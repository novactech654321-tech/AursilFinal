using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

public record AutopayRequest(string Metal, decimal AmountInr, decimal? TotalLimitInr, int RunHour);

public record AutopayStatusRequest(string Status);

[ApiController]
[Authorize]
[Route("api/autopay")]
public class AutopayController(IConfiguration config, PriceService prices, ActivityLog log) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            """
            SELECT Id, Metal, AmountInr, TotalLimitInr, TotalSpent, RunHour, Status, NextRunAt, LastRunAt, CreatedAt
            FROM AutopayPlans WHERE UserId = @userId ORDER BY Id DESC
            """, new { userId = User.UserId() }));
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            """
            SELECT TOP 20 r.Id, p.Metal, r.OrderNo, r.Amount, r.Status, r.Message, r.RunAt
            FROM AutopayRuns r JOIN AutopayPlans p ON p.Id = r.PlanId
            WHERE p.UserId = @userId ORDER BY r.Id DESC
            """, new { userId = User.UserId() }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(AutopayRequest req)
    {
        if (!Metals.IsValid(req.Metal))
            return BadRequest(new ApiMessage("Choose gold, silver or platinum."));
        var amount = Math.Round(req.AmountInr, 2);
        if (amount < prices.Options.MinOrderInr || amount > prices.Options.MaxOrderInr)
            return BadRequest(new ApiMessage($"Daily amount must be between {OrderService.Inr(prices.Options.MinOrderInr)} and {OrderService.Inr(prices.Options.MaxOrderInr)}."));
        decimal? limit = req.TotalLimitInr is > 0 ? Math.Round(req.TotalLimitInr.Value, 2) : null;
        if (limit is not null && limit < amount)
            return BadRequest(new ApiMessage("The total limit must be at least the daily amount."));
        if (req.RunHour is < 0 or > 23)
            return BadRequest(new ApiMessage("Choose an hour between 0 and 23."));

        var userId = User.UserId();
        await using var db = new SqlConnection(_connectionString);
        if (await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM AutopayPlans WHERE UserId = @userId AND Metal = @Metal AND Status IN ('Active', 'Paused')",
                new { userId, req.Metal }) > 0)
            return Conflict(new ApiMessage($"You already have an autopay for {Metals.Display(req.Metal)}. Pause or delete it first."));

        await db.ExecuteAsync(
            """
            INSERT INTO AutopayPlans (UserId, Metal, AmountInr, TotalLimitInr, RunHour, NextRunAt)
            VALUES (@userId, @Metal, @amount, @limit, @RunHour, @next)
            """,
            new { userId, req.Metal, amount, limit, req.RunHour, next = AutopayService.NextRunUtc(DateTime.UtcNow, req.RunHour) });
        log.Write("AUTOPAY_CREATE", userId, $"Autopay set: {OrderService.Inr(amount)} of {req.Metal} daily at {req.RunHour}:00 IST, total limit {(limit is null ? "none" : OrderService.Inr(limit.Value))}");
        return Ok(new ApiMessage($"Autopay set: {OrderService.Inr(amount)} of {Metals.Display(req.Metal)} every day."));
    }

    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, AutopayStatusRequest req)
    {
        if (req.Status is not ("Active" or "Paused"))
            return BadRequest(new ApiMessage("Status must be Active or Paused."));

        await using var db = new SqlConnection(_connectionString);
        var plan = await db.QuerySingleOrDefaultAsync<AutopayPlanRow>(
            "SELECT * FROM AutopayPlans WHERE Id = @id AND UserId = @userId", new { id, userId = User.UserId() });
        if (plan is null) return NotFound(new ApiMessage("Autopay not found."));
        if (plan.Status == "Completed") return BadRequest(new ApiMessage("This autopay has finished."));

        // Resuming schedules from now, so a long pause does not trigger an instant buy
        await db.ExecuteAsync("UPDATE AutopayPlans SET Status = @Status, NextRunAt = @next WHERE Id = @id",
            new { req.Status, id, next = AutopayService.NextRunUtc(DateTime.UtcNow, plan.RunHour) });
        log.Write(req.Status == "Paused" ? "AUTOPAY_PAUSE" : "AUTOPAY_RESUME", User.UserId(), $"Autopay plan {id} ({plan.Metal}) set to {req.Status}");
        return Ok(new ApiMessage(req.Status == "Paused" ? "Autopay paused." : "Autopay resumed."));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.ExecuteAsync("DELETE FROM AutopayPlans WHERE Id = @id AND UserId = @userId", new { id, userId = User.UserId() });
        if (rows > 0) log.Write("AUTOPAY_DELETE", User.UserId(), $"Autopay plan {id} deleted");
        return rows == 0 ? NotFound(new ApiMessage("Autopay not found.")) : Ok(new ApiMessage("Autopay deleted."));
    }
}
