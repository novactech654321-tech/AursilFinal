using System.Text.Json.Nodes;
using AurSil.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Services;

public record ToolOutcome(string Text, bool IsError, object? Action = null);

// Actions the chat assistant can perform. Every call runs as the logged-in user, so the same rules as the website apply.
public class ChatTools(IConfiguration config, PriceService prices, StockService stock, OrderService orders)
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    private static readonly JsonObject MetalProp = new()
    {
        ["type"] = "string", ["enum"] = new JsonArray("gold", "silver", "platinum"),
    };

    private static JsonObject Tool(string name, string description, JsonObject? props = null, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props ?? new JsonObject(),
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    public static JsonArray Definitions(bool isAdmin)
    {
        var tools = new JsonArray
        {
            Tool("get_prices", "Current buy and sell price per gram (INR) for gold, silver and platinum."),
            Tool("get_stock", "Grams of each metal currently in stock and available to buy."),
            Tool("get_portfolio", "The logged-in user's holdings in grams, amount invested and current value."),
            Tool("buy_metal",
                "Create a buy order for the logged-in user. Give grams OR amount_inr. The order still has to be paid by the user on the payment page; this does not charge anything.",
                new JsonObject
                {
                    ["metal"] = MetalProp.DeepClone(),
                    ["grams"] = new JsonObject { ["type"] = "number", ["description"] = "Quantity in grams" },
                    ["amount_inr"] = new JsonObject { ["type"] = "number", ["description"] = "Rupee amount to spend, GST included" },
                }, "metal"),
        };

        if (isAdmin)
        {
            tools.Add(Tool("list_vendors", "List the vendors stock can be bought from."));
            tools.Add(Tool("add_vendor", "Add a new vendor.",
                new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["phone"] = new JsonObject { ["type"] = "string" },
                }, "name"));
            tools.Add(Tool("add_stock",
                "ADMIN ONLY. Buy stock from a vendor and add it to company inventory. Changes the database immediately.",
                new JsonObject
                {
                    ["vendor_name"] = new JsonObject { ["type"] = "string", ["description"] = "Name of an existing vendor" },
                    ["metal"] = MetalProp.DeepClone(),
                    ["grams"] = new JsonObject { ["type"] = "number" },
                    ["cost_per_gram"] = new JsonObject { ["type"] = "number", ["description"] = "Vendor price per gram in INR" },
                }, "vendor_name", "metal", "grams", "cost_per_gram"));
        }
        return tools;
    }

    public async Task<ToolOutcome> RunAsync(string name, JsonNode? input, string userId, bool isAdmin)
    {
        try
        {
            switch (name)
            {
                case "get_prices":
                    return Ok(prices.All().Select(q => new
                    {
                        metal = q.Metal, buy_per_gram_inr = q.BuyRate, sell_per_gram_inr = q.SellRate, gst_percent = prices.Options.GstPercent,
                    }));

                case "get_stock":
                    return Ok(await stock.GetAllAsync());

                case "get_portfolio":
                    return await PortfolioAsync(userId);

                case "buy_metal":
                {
                    var result = await orders.CreateBuyAsync(userId, Str(input, "metal"), Num(input, "amount_inr"), Num(input, "grams"));
                    if (result.Response is not { } buy) return new(result.Error!, true);
                    var o = buy.Order;
                    return new(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = "order created, waiting for the user to pay",
                        order_no = o.OrderNo, metal = o.Metal, grams = o.Grams, rate_per_gram = o.RatePerGram, total_inr = o.Total,
                    }), false, new { type = "pay", url = buy.PayPath, label = $"Pay {OrderService.Inr(o.Total)} for {o.Grams:0.####} g {Metals.Display(o.Metal)}" });
                }
            }

            if (!isAdmin && name is "list_vendors" or "add_vendor" or "add_stock")
                return new("Only an admin can do this.", true);

            switch (name)
            {
                case "list_vendors":
                {
                    await using var db = new SqlConnection(_connectionString);
                    return Ok(await db.QueryAsync("SELECT Name, Phone FROM Vendors WHERE IsActive = 1 ORDER BY Name"));
                }

                case "add_vendor":
                {
                    var vendor = Str(input, "name").Trim();
                    if (vendor.Length < 2) return new("Vendor name is too short.", true);
                    await using var db = new SqlConnection(_connectionString);
                    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Vendors WHERE Name = @vendor", new { vendor }) > 0)
                        return new("A vendor with this name already exists.", true);
                    await db.ExecuteAsync("INSERT INTO Vendors (Name, Phone) VALUES (@vendor, @phone)",
                        new { vendor, phone = string.IsNullOrWhiteSpace(Str(input, "phone")) ? null : Str(input, "phone").Trim() });
                    return new($"Vendor '{vendor}' added.", false);
                }

                case "add_stock":
                {
                    var vendorName = Str(input, "vendor_name").Trim();
                    await using var db = new SqlConnection(_connectionString);
                    var vendorId = await db.ExecuteScalarAsync<int?>(
                        "SELECT Id FROM Vendors WHERE Name = @vendorName AND IsActive = 1", new { vendorName });
                    if (vendorId is null)
                        return new($"No vendor named '{vendorName}'. Use list_vendors, or add_vendor first.", true);

                    var (ok, message) = await stock.PurchaseAsync(userId, vendorId.Value, Str(input, "metal"),
                        Num(input, "grams") ?? 0, Num(input, "cost_per_gram") ?? 0);
                    if (!ok) return new(message, true);
                    return new($"{message} Stock now: {await stock.AvailableAsync(Str(input, "metal")):0.####} g.", false);
                }
            }
            return new($"Unknown tool '{name}'.", true);
        }
        catch (Exception ex)
        {
            return new("The action failed: " + ex.Message, true);
        }
    }

    private async Task<ToolOutcome> PortfolioAsync(string userId)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.QueryAsync<(string Metal, decimal Grams, decimal InvestedAmount)>(
            "SELECT Metal, Grams, InvestedAmount FROM Holdings WHERE UserId = @userId AND Grams > 0", new { userId });
        return Ok(rows.Select(r => new
        {
            metal = r.Metal, grams = r.Grams, invested_inr = r.InvestedAmount,
            current_value_inr = Math.Round(r.Grams * (prices.Get(r.Metal)?.SellRate ?? 0), 2),
        }));
    }

    private static ToolOutcome Ok(object value) => new(System.Text.Json.JsonSerializer.Serialize(value), false);

    private static string Str(JsonNode? input, string key) => input?[key]?.GetValue<string>() ?? "";

    private static decimal? Num(JsonNode? input, string key) =>
        input?[key] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;
}
