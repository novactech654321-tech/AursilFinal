using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using AurSil.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Services;

public record BuyResult(BuyResponse? Response, int Status, string? Error);

public class OrderService(IConfiguration config, EmailSender email, PriceService prices, StockService stock, RazorpayService razorpay,
    ActivityLog log, ILogger<OrderService> logger)
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    public static string NewOrderNo() =>
        "AS" + DateTime.UtcNow.ToString("yyMMdd") + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));

    public async Task<OrderRow?> GetAsync(string orderNo)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.QuerySingleOrDefaultAsync<OrderRow>(
            """
            SELECT o.*, b.BankName, b.AccountNumber FROM Orders o
            LEFT JOIN BankAccounts b ON b.Id = o.BankAccountId
            WHERE o.OrderNo = @orderNo
            """, new { orderNo });
    }

    // Creates an unpaid buy order after checking the price, limits and stock. The grams only leave stock when it is paid.
    public async Task<BuyResult> CreateBuyAsync(string userId, string metal, decimal? amountInr, decimal? gramsIn)
    {
        if (!Metals.IsValid(metal))
            return new(null, 400, "Unknown metal.");
        if (prices.Get(metal) is not { } quote)
            return new(null, 503, "Live prices are loading. Please try again.");

        var gstRate = prices.Options.GstPercent / 100;
        decimal grams, amount, gst, total;

        if (amountInr is > 0 and var inr)
        {
            // User pays exactly the amount entered; GST is carved out of it
            total = Math.Round(inr, 2);
            amount = Math.Round(total / (1 + gstRate), 2);
            gst = total - amount;
            grams = Math.Floor(amount / quote.BuyRate * 10000) / 10000;
        }
        else if (gramsIn is > 0 and var g)
        {
            grams = Math.Round(g, 4);
            amount = Math.Round(grams * quote.BuyRate, 2);
            gst = Math.Round(amount * gstRate, 2);
            total = amount + gst;
        }
        else
        {
            return new(null, 400, "Enter an amount in ₹ or grams.");
        }

        if (total < prices.Options.MinOrderInr)
            return new(null, 400, $"Minimum purchase is {Inr(prices.Options.MinOrderInr)}.");
        if (total > prices.Options.MaxOrderInr)
            return new(null, 400, $"Maximum purchase per order is {Inr(prices.Options.MaxOrderInr)}.");
        if (grams < 0.0001m)
            return new(null, 400, "Amount is too small to buy any metal.");

        var available = await stock.AvailableAsync(metal);
        if (available < grams)
            return new(null, 409, available <= 0
                ? $"{Metals.Display(metal)} is out of stock right now."
                : $"Only {available:0.####} g of {Metals.Display(metal)} is in stock. Please reduce the quantity.");

        var orderNo = NewOrderNo();
        string? gatewayOrderId = null;
        var mode = "simulated";
        if (razorpay.IsConfigured)
        {
            try
            {
                gatewayOrderId = await razorpay.CreateOrderAsync(total, orderNo);
                mode = "razorpay";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Razorpay order creation failed");
                return new(null, 502, "Payment gateway is unavailable. Please try again.");
            }
        }

        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync(
            """
            INSERT INTO Orders (OrderNo, UserId, Metal, Side, Grams, RatePerGram, Amount, Gst, Total, Status, PaymentGateway, GatewayOrderId)
            VALUES (@orderNo, @userId, @metal, 'Buy', @grams, @rate, @amount, @gst, @total, 'Created', @mode, @gatewayOrderId)
            """,
            new { orderNo, userId, metal, grams, rate = quote.BuyRate, amount, gst, total, mode, gatewayOrderId });

        var order = (await GetAsync(orderNo))!;
        log.Write("BUY_CREATED", userId, $"Order {orderNo}: {grams:0.####} g {metal} @ {quote.BuyRate} = {Inr(total)} (GST {Inr(gst)}), payment={mode}, waiting for payment");
        return new(new BuyResponse(order.ToDto(), mode, $"/pay/{orderNo}"), 200, null);
    }

    // Marks a paid buy order complete and credits the grams. Safe to call twice.
    public async Task<bool> CompleteBuyAsync(string orderNo, string gateway, string? paymentId)
    {
        await using var db = new SqlConnection(_connectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var order = await db.QuerySingleOrDefaultAsync<OrderRow>(
            "SELECT * FROM Orders WITH (UPDLOCK) WHERE OrderNo = @orderNo AND Side = 'Buy'", new { orderNo }, tx);
        if (order is null || order.Status != "Created")
        {
            await tx.RollbackAsync();
            return order?.Status == "Completed";
        }

        // Take the grams out of company stock first; if another buyer got there first the order fails
        if (!await StockService.TryDeductAsync(db, tx, order.Metal, order.Grams))
        {
            await db.ExecuteAsync("UPDATE Orders SET Status = 'Failed' WHERE Id = @Id", new { order.Id }, tx);
            await tx.CommitAsync();
            log.Write("BUY_FAILED", order.UserId, $"Order {orderNo}: paid but {order.Metal} ran out of stock ({order.Grams:0.####} g)");
            return false;
        }

        await db.ExecuteAsync(
            """
            UPDATE Orders SET Status = 'Completed', PaymentGateway = @gateway, GatewayPaymentId = @paymentId,
                CompletedAt = SYSUTCDATETIME(),
                InvoiceNo = 'INV-' + CAST(YEAR(SYSUTCDATETIME()) AS VARCHAR(4)) + '-' + RIGHT('000000' + CAST(NEXT VALUE FOR InvoiceSeq AS VARCHAR(10)), 6) WHERE Id = @Id
            """, new { gateway, paymentId, order.Id }, tx);

        await db.ExecuteAsync(
            """
            MERGE Holdings AS h
            USING (SELECT @UserId AS UserId, @Metal AS Metal) AS s ON h.UserId = s.UserId AND h.Metal = s.Metal
            WHEN MATCHED THEN UPDATE SET Grams = h.Grams + @Grams, InvestedAmount = h.InvestedAmount + @Total, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (UserId, Metal, Grams, InvestedAmount) VALUES (@UserId, @Metal, @Grams, @Total);
            """, new { order.UserId, order.Metal, order.Grams, order.Total }, tx);

        await tx.CommitAsync();
        log.Write("BUY_COMPLETED", order.UserId, $"Order {orderNo}: BOUGHT {order.Grams:0.####} g {order.Metal} for {Inr(order.Total)}, via {gateway}, payment id {paymentId}");
        await SendOrderEmailAsync((await GetAsync(orderNo))!);
        return true;
    }

    // Emails the invoice for a completed order
    public async Task SendOrderEmailAsync(OrderRow order)
    {
        try
        {
            await using var db = new SqlConnection(_connectionString);
            var user = await db.QuerySingleAsync<UserRow>("SELECT * FROM Users WHERE UserId = @UserId", new { order.UserId });
            var metal = Metals.Display(order.Metal);
            var verb = order.Side == "Buy" ? (order.PaymentGateway == "autopay" ? "Autopay: bought" : "You bought") : "You sold";

            var html = $"""
                <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto;color:#222">
                  <p>Hi {WebUtility.HtmlEncode(user.Name)},</p>
                  <p>{verb} <b>{order.Grams:0.####} g of {metal}</b> for <b>{Inr(order.Total)}</b>. Your invoice is below, and you can find it any time under Invoices on AurSil.</p>
                </div>
                {InvoiceBuilder.Build(order, user, CompanyInfo.From(config))}
                """;
            log.Write("EMAIL", order.UserId, $"Invoice {order.InvoiceNo} emailed for order {order.OrderNo}");
            await email.SendAsync(user.Email, $"AurSil invoice {order.InvoiceNo}: {metal} {order.Side.ToLowerInvariant()} (order {order.OrderNo})", html);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Order email failed for {OrderNo}", order.OrderNo);
        }
    }

    public static string Inr(decimal amount) => "₹" + amount.ToString("N2", India);
}
