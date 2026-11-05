using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

public record VendorRequest(string Name, string? Phone, string? Email, string? Address);

public record StockPurchaseRequest(int VendorId, string Metal, decimal Grams, decimal CostPerGram);

[ApiController]
[Authorize(Roles = UserRole.Admin)]
[Route("api/admin")]
public class AdminController(IConfiguration config, StockService stock, ActivityLog log) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [HttpGet("vendors")]
    public async Task<IActionResult> Vendors()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            "SELECT Id, Name, Phone, Email, Address, IsActive, CreatedAt FROM Vendors ORDER BY Name"));
    }

    [HttpPost("vendors")]
    public async Task<IActionResult> AddVendor(VendorRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length < 2)
            return BadRequest(new ApiMessage("Enter the vendor name."));

        await using var db = new SqlConnection(_connectionString);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Vendors WHERE Name = @name", new { name }) > 0)
            return Conflict(new ApiMessage("A vendor with this name already exists."));

        var id = await db.ExecuteScalarAsync<int>(
            """
            INSERT INTO Vendors (Name, Phone, Email, Address) OUTPUT INSERTED.Id
            VALUES (@name, @phone, @email, @address)
            """,
            new { name, phone = Clean(req.Phone), email = Clean(req.Email), address = Clean(req.Address) });
        log.Write("ADMIN_VENDOR", User.UserId(), $"Vendor added: {name} (id {id})");
        return Ok(new { id, message = "Vendor added." });
    }

    [HttpPost("stock/purchase")]
    public async Task<IActionResult> PurchaseStock(StockPurchaseRequest req)
    {
        var (ok, message) = await stock.PurchaseAsync(User.UserId(), req.VendorId, req.Metal, req.Grams, req.CostPerGram);
        if (!ok) return BadRequest(new ApiMessage(message));
        log.Write("ADMIN_STOCK", User.UserId(), $"Stock purchase: {req.Grams:0.####} g {req.Metal} from vendor {req.VendorId} at {req.CostPerGram}/g");
        return Ok(new { message, available = await stock.AvailableAsync(req.Metal) });
    }

    // Total stock per metal, plus where it came from (per vendor)
    [HttpGet("stock")]
    public async Task<IActionResult> Stock()
    {
        await using var db = new SqlConnection(_connectionString);

        var metals = await db.QueryAsync(
            """
            SELECT s.Metal,
                   s.Grams AS AvailableGrams,
                   ISNULL((SELECT SUM(Grams) FROM StockPurchases p WHERE p.Metal = s.Metal), 0) AS PurchasedGrams,
                   ISNULL((SELECT SUM(TotalCost) FROM StockPurchases p WHERE p.Metal = s.Metal), 0) AS PurchaseCost,
                   ISNULL((SELECT SUM(Grams) FROM Orders o WHERE o.Metal = s.Metal AND o.Side = 'Buy' AND o.Status = 'Completed'), 0) AS SoldToCustomersGrams,
                   ISNULL((SELECT SUM(Grams) FROM Orders o WHERE o.Metal = s.Metal AND o.Side = 'Sell' AND o.Status = 'Completed'), 0) AS BoughtBackGrams
            FROM StockInventory s
            ORDER BY CASE s.Metal WHEN 'gold' THEN 1 WHEN 'silver' THEN 2 ELSE 3 END
            """);

        var vendors = await db.QueryAsync(
            """
            SELECT v.Id AS VendorId, v.Name AS VendorName, p.Metal, SUM(p.Grams) AS Grams, SUM(p.TotalCost) AS TotalCost
            FROM StockPurchases p JOIN Vendors v ON v.Id = p.VendorId
            GROUP BY v.Id, v.Name, p.Metal
            ORDER BY v.Name, p.Metal
            """);

        var purchases = await db.QueryAsync(
            """
            SELECT TOP 50 p.Id, v.Name AS VendorName, p.Metal, p.Grams, p.CostPerGram, p.TotalCost, p.PurchasedBy, p.PurchasedAt
            FROM StockPurchases p JOIN Vendors v ON v.Id = p.VendorId
            ORDER BY p.PurchasedAt DESC, p.Id DESC
            """);

        return Ok(new { metals, vendors, purchases });
    }

    // What each customer currently holds
    [HttpGet("holdings")]
    public async Task<IActionResult> Holdings()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            """
            SELECT u.UserId, u.Name, u.Email,
                   ISNULL(SUM(CASE WHEN h.Metal = 'gold' THEN h.Grams END), 0) AS Gold,
                   ISNULL(SUM(CASE WHEN h.Metal = 'silver' THEN h.Grams END), 0) AS Silver,
                   ISNULL(SUM(CASE WHEN h.Metal = 'platinum' THEN h.Grams END), 0) AS Platinum
            FROM Users u JOIN Holdings h ON h.UserId = u.UserId
            WHERE h.Grams > 0
            GROUP BY u.UserId, u.Name, u.Email
            ORDER BY u.Name
            """));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

[ApiController]
[Route("api/stock")]
public class StockController(StockService stock) : ControllerBase
{
    // Public: lets the buy screen show what can be purchased right now
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await stock.GetAllAsync());
}
