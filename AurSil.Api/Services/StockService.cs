using System.Data.Common;
using AurSil.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Services;

// Company stock per metal. Grams are only ever changed inside the caller's transaction.
public class StockService(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    public async Task<Dictionary<string, decimal>> GetAllAsync()
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.QueryAsync<(string Metal, decimal Grams)>("SELECT Metal, Grams FROM StockInventory");
        var map = rows.ToDictionary(r => r.Metal, r => r.Grams);
        foreach (var m in Metals.All) map.TryAdd(m, 0);
        return map;
    }

    public async Task<decimal> AvailableAsync(string metal)
    {
        await using var db = new SqlConnection(_connectionString);
        return await db.ExecuteScalarAsync<decimal?>("SELECT Grams FROM StockInventory WHERE Metal = @metal", new { metal }) ?? 0;
    }

    // Atomic: succeeds only if enough grams are left, so stock can never go below zero
    public static async Task<bool> TryDeductAsync(DbConnection db, DbTransaction tx, string metal, decimal grams) =>
        await db.ExecuteAsync(
            "UPDATE StockInventory SET Grams = Grams - @grams, UpdatedAt = SYSUTCDATETIME() WHERE Metal = @metal AND Grams >= @grams",
            new { metal, grams }, tx) == 1;

    public static Task AddAsync(DbConnection db, DbTransaction tx, string metal, decimal grams) =>
        db.ExecuteAsync(
            "UPDATE StockInventory SET Grams = Grams + @grams, UpdatedAt = SYSUTCDATETIME() WHERE Metal = @metal",
            new { metal, grams }, tx);

    // Buy stock from a vendor: records the purchase and adds the grams to inventory in one transaction
    public async Task<(bool Ok, string Message)> PurchaseAsync(string adminUserId, int vendorId, string metal, decimal gramsIn, decimal costPerGram)
    {
        if (!Metals.IsValid(metal))
            return (false, "Choose gold, silver or platinum.");
        var grams = Math.Round(gramsIn, 4);
        if (grams <= 0 || grams > 1_000_000)
            return (false, "Enter a valid quantity in grams.");
        var cost = Math.Round(costPerGram, 2);
        if (cost <= 0)
            return (false, "Enter the vendor's price per gram.");

        await using var db = new SqlConnection(_connectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        var vendorActive = await db.ExecuteScalarAsync<bool?>("SELECT IsActive FROM Vendors WHERE Id = @vendorId", new { vendorId }, tx);
        if (vendorActive != true)
        {
            await tx.RollbackAsync();
            return (false, "Choose a valid vendor.");
        }

        await db.ExecuteAsync(
            """
            INSERT INTO StockPurchases (VendorId, Metal, Grams, CostPerGram, TotalCost, PurchasedBy)
            VALUES (@vendorId, @metal, @grams, @cost, @total, @adminUserId)
            """,
            new { vendorId, metal, grams, cost, total = Math.Round(grams * cost, 2), adminUserId }, tx);
        await AddAsync(db, tx, metal, grams);
        await tx.CommitAsync();

        return (true, $"Added {grams:0.####} g of {Metals.Display(metal)} to stock.");
    }
}
