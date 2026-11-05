using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public class InvoicesController(IConfiguration config, OrderService orders) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    // All of the user's invoices (completed orders), newest first
    [HttpGet]
    public async Task<IActionResult> List()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            """
            SELECT InvoiceNo, OrderNo, Metal, Side, Grams, RatePerGram, Amount, Gst, Total, GatewayPaymentId AS TransactionId, PaymentGateway, CompletedAt
            FROM Orders
            WHERE UserId = @userId AND Status = 'Completed' AND InvoiceNo IS NOT NULL
            ORDER BY CompletedAt DESC, Id DESC
            """, new { userId = User.UserId() }));
    }

    [HttpGet("{invoiceNo}")]
    public async Task<IActionResult> Get(string invoiceNo)
    {
        await using var db = new SqlConnection(_connectionString);
        var orderNo = await db.ExecuteScalarAsync<string?>(
            "SELECT OrderNo FROM Orders WHERE InvoiceNo = @invoiceNo AND UserId = @userId", new { invoiceNo, userId = User.UserId() });
        if (orderNo is null) return NotFound(new ApiMessage("Invoice not found."));

        var order = (await orders.GetAsync(orderNo))!;
        var user = await db.QuerySingleAsync<UserRow>("SELECT * FROM Users WHERE UserId = @UserId", new { order.UserId });
        return Ok(new { invoiceNo, html = InvoiceBuilder.Build(order, user, CompanyInfo.From(config)) });
    }
}
