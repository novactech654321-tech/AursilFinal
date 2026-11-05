using AurSil.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Services;

public class AutopayPlanRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Metal { get; set; } = "";
    public decimal AmountInr { get; set; }
    public decimal? TotalLimitInr { get; set; }
    public decimal TotalSpent { get; set; }
    public int RunHour { get; set; }
    public string Status { get; set; } = "";
    public DateTime NextRunAt { get; set; }
}

// Runs the daily buy plans. Plans are rows in AutopayPlans; this checks for due ones every minute.
// Test mode (no Razorpay keys): the buy completes straight away, no real money. With Razorpay keys a card or UPI cannot be
// charged without a saved mandate, so the order is created and the user gets an email with the payment link.
public class AutopayService(IConfiguration config, OrderService orders, EmailSender email, PriceService prices,
    ActivityLog log, ILogger<AutopayService> logger) : BackgroundService
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    // Next time (UTC) the given India-time hour comes round
    public static DateTime NextRunUtc(DateTime nowUtc, int runHour)
    {
        var india = nowUtc + IndiaOffset;
        var candidate = india.Date.AddHours(runHour);
        if (candidate <= india) candidate = candidate.AddDays(1);
        return candidate - IndiaOffset;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        var every = TimeSpan.FromSeconds(Math.Max(2, config.GetValue("Autopay:CheckSeconds", 60)));
        logger.LogInformation("Autopay worker started, checking every {Seconds}s", every.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(); }
            catch (Exception ex) { logger.LogWarning("Autopay check failed: {Message}", ex.Message); }
            await Task.Delay(every, stoppingToken);
        }
    }

    private async Task RunDueAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        var due = (await db.QueryAsync<AutopayPlanRow>(
            "SELECT * FROM AutopayPlans WHERE Status = 'Active' AND NextRunAt <= SYSUTCDATETIME()")).ToList();

        foreach (var plan in due)
        {
            // Claim the run first, so a second check or instance cannot buy twice. A long outage still means one buy, not many.
            var claimed = await db.ExecuteAsync(
                "UPDATE AutopayPlans SET NextRunAt = @next, LastRunAt = SYSUTCDATETIME() WHERE Id = @Id AND NextRunAt <= SYSUTCDATETIME() AND Status = 'Active'",
                new { next = NextRunUtc(DateTime.UtcNow, plan.RunHour), plan.Id });
            if (claimed != 1) continue;
            logger.LogInformation("Autopay running plan {PlanId}: {Metal} for {User}", plan.Id, plan.Metal, plan.UserId);
            log.Write("AUTOPAY_RUN", plan.UserId, $"Plan {plan.Id}: daily {plan.Metal} buy of {OrderService.Inr(plan.AmountInr)} started");
            await ExecutePlanAsync(db, plan);
        }
    }

    private async Task ExecutePlanAsync(SqlConnection db, AutopayPlanRow plan)
    {
        var charge = plan.AmountInr;
        if (plan.TotalLimitInr is { } limit)
        {
            var remaining = limit - plan.TotalSpent;
            if (remaining < prices.Options.MinOrderInr)
            {
                await db.ExecuteAsync("UPDATE AutopayPlans SET Status = 'Completed' WHERE Id = @Id", new { plan.Id });
                await RecordAsync(db, plan.Id, null, 0, "Failed", "Total limit reached. Autopay finished.");
                return;
            }
            charge = Math.Min(charge, remaining);
        }

        var result = await orders.CreateBuyAsync(plan.UserId, plan.Metal, charge, null);
        if (result.Response is not { } buy)
        {
            await RecordAsync(db, plan.Id, null, charge, "Failed", result.Error);
            return;
        }

        var order = buy.Order;
        if (buy.PaymentMode == "simulated")
        {
            var ok = await orders.CompleteBuyAsync(order.OrderNo, "autopay", "AUTO-" + Guid.NewGuid().ToString("N")[..10]);
            if (!ok)
            {
                await RecordAsync(db, plan.Id, order.OrderNo, charge, "Failed", $"{Metals.Display(plan.Metal)} ran out of stock.");
                return;
            }

            var spent = plan.TotalSpent + order.Total;
            var finished = plan.TotalLimitInr is { } l && l - spent < prices.Options.MinOrderInr;
            await db.ExecuteAsync(
                "UPDATE AutopayPlans SET TotalSpent = @spent, Status = CASE WHEN @finished = 1 THEN 'Completed' ELSE Status END WHERE Id = @Id",
                new { spent, finished, plan.Id });
            await RecordAsync(db, plan.Id, order.OrderNo, order.Total, "Success",
                $"Bought {order.Grams:0.####} g {Metals.Display(plan.Metal)}" + (finished ? ". Limit reached, autopay finished." : ""));
            return;
        }

        await RecordAsync(db, plan.Id, order.OrderNo, order.Total, "AwaitingPayment", "Payment link sent by email.");
        var user = await db.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM Users WHERE UserId = @UserId", new { plan.UserId });
        if (user is not null)
        {
            var link = (config["App:PublicUrl"] ?? "http://localhost:5168").TrimEnd('/') + buy.PayPath;
            await email.SendAsync(user.Email, $"AurSil autopay: pay for {order.Grams:0.####} g {Metals.Display(plan.Metal)}",
                $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.Name)},</p><p>Your daily autopay order for <b>{order.Grams:0.####} g {Metals.Display(plan.Metal)}</b> " +
                $"({OrderService.Inr(order.Total)}) is ready.</p><p><a href=\"{link}\">Pay now</a></p>");
        }
    }

    private async Task RecordAsync(SqlConnection db, int planId, string? orderNo, decimal amount, string status, string? message)
    {
        log.Write("AUTOPAY_" + status, null, $"Plan {planId}: order={orderNo ?? "-"} amount={OrderService.Inr(amount)} {message}");
        await db.ExecuteAsync(
            "INSERT INTO AutopayRuns (PlanId, OrderNo, Amount, Status, Message) VALUES (@planId, @orderNo, @amount, @status, @message)",
            new { planId, orderNo, amount, status, message });
    }
}
