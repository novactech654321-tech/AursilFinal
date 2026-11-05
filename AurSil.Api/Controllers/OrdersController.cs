using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public class OrdersController(IConfiguration config, PriceService prices, OrderService orders, StockService stock, RazorpayService razorpay,
    ActivityLog log, ILogger<OrdersController> logger) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [HttpPost("buy")]
    public async Task<IActionResult> Buy(BuyRequest req)
    {
        var result = await orders.CreateBuyAsync(User.UserId(), req.Metal, req.AmountInr, req.Grams);
        return result.Response is { } ok
            ? Ok(ok)
            : StatusCode(result.Status, new ApiMessage(result.Error!));
    }

    [HttpPost("sell")]
    public async Task<IActionResult> Sell(SellRequest req)
    {
        if (!Metals.IsValid(req.Metal))
            return BadRequest(new ApiMessage("Unknown metal."));
        if (prices.Get(req.Metal) is not { } quote)
            return StatusCode(503, new ApiMessage("Live prices are loading. Please try again."));

        var grams = Math.Round(req.Grams, 4);
        if (grams <= 0)
            return BadRequest(new ApiMessage("Enter the grams to sell."));

        var userId = User.UserId();
        await using var db = new SqlConnection(_connectionString);
        await db.OpenAsync();

        var bank = await db.QuerySingleOrDefaultAsync<BankAccountRow>("SELECT * FROM BankAccounts WHERE UserId = @userId", new { userId });
        if (bank is null)
        {
            log.Write("SELL_REJECTED", userId, $"Tried to sell {grams:0.####} g {req.Metal} but has no bank account");
            return BadRequest(new { message = "Please add your bank account to receive the sale amount.", needsBank = true });
        }

        await using var tx = await db.BeginTransactionAsync();
        var holding = await db.QuerySingleOrDefaultAsync<(decimal Grams, decimal InvestedAmount)>(
            "SELECT Grams, InvestedAmount FROM Holdings WITH (UPDLOCK) WHERE UserId = @userId AND Metal = @Metal",
            new { userId, req.Metal }, tx);

        if (holding.Grams < grams)
        {
            await tx.RollbackAsync();
            log.Write("SELL_REJECTED", userId, $"Tried to sell {grams:0.####} g {req.Metal} but holds only {holding.Grams:0.####} g");
            return BadRequest(new ApiMessage($"You only have {holding.Grams:0.####} g of {Metals.Display(req.Metal)}."));
        }

        var amount = Math.Round(grams * quote.SellRate, 2);
        if (amount < 1)
        {
            await tx.RollbackAsync();
            return BadRequest(new ApiMessage("Amount is too small to sell."));
        }

        var investedReduction = Math.Round(holding.InvestedAmount * grams / holding.Grams, 2);
        await db.ExecuteAsync(
            "UPDATE Holdings SET Grams = Grams - @grams, InvestedAmount = InvestedAmount - @investedReduction, UpdatedAt = SYSUTCDATETIME() WHERE UserId = @userId AND Metal = @Metal",
            new { grams, investedReduction, userId, req.Metal }, tx);
        // Metal sold back by the customer returns to company stock
        await StockService.AddAsync(db, tx, req.Metal, grams);

        // Bank payout is simulated: the order completes immediately and the amount is shown as paid to the linked account
        var orderNo = OrderService.NewOrderNo();
        await db.ExecuteAsync(
            """
            INSERT INTO Orders (OrderNo, UserId, Metal, Side, Grams, RatePerGram, Amount, Gst, Total, Status, PaymentGateway, GatewayPaymentId, BankAccountId, CompletedAt, InvoiceNo)
            VALUES (@orderNo, @userId, @Metal, 'Sell', @grams, @rate, @amount, 0, @amount, 'Completed', 'bank', @payoutRef, @bankId, SYSUTCDATETIME(),
                'INV-' + CAST(YEAR(SYSUTCDATETIME()) AS VARCHAR(4)) + '-' + RIGHT('000000' + CAST(NEXT VALUE FOR InvoiceSeq AS VARCHAR(10)), 6))
            """,
            new { orderNo, userId, req.Metal, grams, rate = quote.SellRate, amount, bankId = bank.Id, payoutRef = "PAYOUT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() }, tx);
        await tx.CommitAsync();
        log.Write("SELL_COMPLETED", userId, $"Order {orderNo}: SOLD {grams:0.####} g {req.Metal} @ {quote.SellRate} = {OrderService.Inr(amount)}, paid out to bank account {bank.BankName}");

        var order = (await orders.GetAsync(orderNo))!;
        await orders.SendOrderEmailAsync(order);
        return Ok(order.ToDto());
    }

    [HttpGet]
    public async Task<IActionResult> List(int take = 50)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.QueryAsync<OrderRow>(
            """
            SELECT TOP (@take) o.*, b.BankName, b.AccountNumber FROM Orders o
            LEFT JOIN BankAccounts b ON b.Id = o.BankAccountId
            WHERE o.UserId = @userId AND o.Status <> 'Created'
            ORDER BY o.CreatedAt DESC
            """,
            new { take = Math.Clamp(take, 1, 200), userId = User.UserId() });
        return Ok(rows.Select(r => r.ToDto()));
    }

    [HttpGet("{orderNo}")]
    public async Task<IActionResult> Get(string orderNo)
    {
        var order = await orders.GetAsync(orderNo);
        if (order is null || order.UserId != User.UserId())
            return NotFound(new ApiMessage("Order not found."));
        return Ok(order.ToDto());
    }
}
