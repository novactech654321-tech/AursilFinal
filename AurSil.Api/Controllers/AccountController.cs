using System.Text.Json;
using System.Text.RegularExpressions;
using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public partial class AccountController(IConfiguration config, PriceService prices, IHttpClientFactory httpFactory, ActivityLog log) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [GeneratedRegex(@"^[A-Z]{4}0[A-Z0-9]{6}$")]
    private static partial Regex IfscRegex();

    [GeneratedRegex(@"^\d{9,18}$")]
    private static partial Regex AccountRegex();

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        await using var db = new SqlConnection(_connectionString);
        var user = await db.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM Users WHERE UserId = @id", new { id = User.UserId() });
        return user is null ? NotFound() : Ok(user.ToProfile());
    }

    [HttpGet("portfolio")]
    public async Task<IActionResult> Portfolio()
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = (await db.QueryAsync<(string Metal, decimal Grams, decimal InvestedAmount)>(
            "SELECT Metal, Grams, InvestedAmount FROM Holdings WHERE UserId = @id", new { id = User.UserId() })).ToList();

        var holdings = Metals.All.Select(metal =>
        {
            var h = rows.FirstOrDefault(r => r.Metal == metal);
            var sellRate = prices.Get(metal)?.SellRate ?? 0;
            var value = Math.Round(h.Grams * sellRate, 2);
            var pl = value - h.InvestedAmount;
            var plPct = h.InvestedAmount > 0 ? Math.Round(pl / h.InvestedAmount * 100, 2) : 0;
            return new HoldingDto(metal, h.Grams, h.InvestedAmount, value, pl, plPct, sellRate);
        }).ToList();

        var invested = holdings.Sum(h => h.Invested);
        var total = holdings.Sum(h => h.CurrentValue);
        var profit = total - invested;
        return Ok(new PortfolioResponse(holdings, invested, total, profit,
            invested > 0 ? Math.Round(profit / invested * 100, 2) : 0));
    }

    [HttpGet("bank")]
    public async Task<IActionResult> GetBank()
    {
        await using var db = new SqlConnection(_connectionString);
        var bank = await db.QuerySingleOrDefaultAsync<BankAccountRow>("SELECT * FROM BankAccounts WHERE UserId = @id", new { id = User.UserId() });
        return bank is null ? NoContent() : Ok(bank.ToDto());
    }

    [HttpPut("bank")]
    public async Task<IActionResult> SaveBank(BankAccountRequest req)
    {
        var holder = req.HolderName?.Trim() ?? "";
        var account = req.AccountNumber?.Trim() ?? "";
        var ifsc = req.Ifsc?.Trim().ToUpperInvariant() ?? "";

        if (holder.Length < 3)
            return BadRequest(new ApiMessage("Enter the account holder name as per bank records."));
        if (!AccountRegex().IsMatch(account))
            return BadRequest(new ApiMessage("Account number must be 9 to 18 digits."));
        if (!IfscRegex().IsMatch(ifsc))
            return BadRequest(new ApiMessage("Enter a valid 11-character IFSC code (e.g. HDFC0001234)."));

        var info = await LookupIfscAsync(ifsc);
        if (info is null)
            return BadRequest(new ApiMessage("IFSC code not found. Please check it."));

        await using var db = new SqlConnection(_connectionString);
        await db.ExecuteAsync(
            """
            MERGE BankAccounts AS b USING (SELECT @userId AS UserId) AS s ON b.UserId = s.UserId
            WHEN MATCHED THEN UPDATE SET HolderName = @holder, AccountNumber = @account, Ifsc = @ifsc, BankName = @bankName, Branch = @branch, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (UserId, HolderName, AccountNumber, Ifsc, BankName, Branch) VALUES (@userId, @holder, @account, @ifsc, @bankName, @branch);
            """,
            new { userId = User.UserId(), holder, account, ifsc, bankName = info.BankName, branch = $"{info.Branch}, {info.City}" });

        log.Write("BANK_SAVED", User.UserId(), $"Bank account saved: {info.BankName}, IFSC {ifsc}, a/c ending {account[^4..]}");
        var saved = await db.QuerySingleAsync<BankAccountRow>("SELECT * FROM BankAccounts WHERE UserId = @id", new { id = User.UserId() });
        return Ok(saved.ToDto());
    }

    [HttpGet("bank/ifsc/{ifsc}")]
    public async Task<IActionResult> Ifsc(string ifsc)
    {
        ifsc = ifsc.Trim().ToUpperInvariant();
        if (!IfscRegex().IsMatch(ifsc))
            return BadRequest(new ApiMessage("Invalid IFSC format."));
        var info = await LookupIfscAsync(ifsc);
        return info is null ? NotFound(new ApiMessage("IFSC code not found.")) : Ok(info);
    }

    private async Task<IfscInfo?> LookupIfscAsync(string ifsc)
    {
        try
        {
            using var response = await httpFactory.CreateClient("prices").GetAsync($"https://ifsc.razorpay.com/{ifsc}");
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            return new IfscInfo(ifsc, r.GetProperty("BANK").GetString() ?? "", r.GetProperty("BRANCH").GetString() ?? "",
                r.GetProperty("CITY").GetString() ?? "");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
