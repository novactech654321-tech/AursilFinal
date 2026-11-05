using System.Net;
using System.Text.Json;
using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

// HTML pages the mobile app opens in a WebView. The order number is random and unguessable;
// payments are only credited after the Razorpay signature is verified on the server.
[ApiController]
[Route("pay")]
public class PaymentPageController(IConfiguration config, OrderService orders, RazorpayService razorpay, ActivityLog log) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [HttpGet("{orderNo}")]
    public async Task<IActionResult> Page(string orderNo)
    {
        var order = await orders.GetAsync(orderNo);
        if (order is null || order.Side != "Buy")
            return Html(Layout("Order not found", "<p>This payment link is not valid.</p>"));
        if (order.Status == "Completed")
            return Redirect($"/pay/result?status=success&orderNo={orderNo}");

        await using var db = new SqlConnection(_connectionString);
        var user = await db.QuerySingleAsync<UserRow>("SELECT * FROM Users WHERE UserId = @UserId", new { order.UserId });

        var summary = $"""
            <div class="card">
              <div class="metal">{Metals.Display(order.Metal)}</div>
              <div class="grams">{order.Grams:0.####} g</div>
              <table>
                <tr><td>Rate</td><td>{OrderService.Inr(order.RatePerGram)} / g</td></tr>
                <tr><td>Metal value</td><td>{OrderService.Inr(order.Amount)}</td></tr>
                <tr><td>GST</td><td>{OrderService.Inr(order.Gst)}</td></tr>
                <tr class="total"><td>Total</td><td>{OrderService.Inr(order.Total)}</td></tr>
              </table>
              <div class="no">Order {order.OrderNo}</div>
            </div>
            """;

        if (order.PaymentGateway == "razorpay" && razorpay.IsConfigured)
        {
            var options = JsonSerializer.Serialize(new
            {
                key = razorpay.KeyId,
                amount = (long)Math.Round(order.Total * 100),
                currency = "INR",
                name = "AurSil",
                description = $"{order.Grams:0.####} g {Metals.Display(order.Metal)}",
                order_id = order.GatewayOrderId,
                prefill = new { name = user.Name, email = user.Email, contact = user.Phone },
                theme = new { color = "#F5B82E" },
            });

            return Html(Layout("Pay securely", $$"""
                {{summary}}
                <button class="btn" id="pay">Pay {{OrderService.Inr(order.Total)}}</button>
                <a class="cancel" href="/pay/result?status=cancelled&orderNo={{orderNo}}">Cancel</a>
                <p class="note">🔒 Secured by Razorpay (test mode)</p>
                <script src="https://checkout.razorpay.com/v1/checkout.js"></script>
                <script>
                  const options = {{options}};
                  options.handler = async (r) => {
                    document.getElementById('pay').textContent = 'Verifying...';
                    const res = await fetch('/pay/{{orderNo}}/razorpay', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify({ razorpayPaymentId: r.razorpay_payment_id, razorpayOrderId: r.razorpay_order_id, razorpaySignature: r.razorpay_signature }) });
                    location.href = '/pay/result?status=' + (res.ok ? 'success' : 'failed') + '&orderNo={{orderNo}}';
                  };
                  options.modal = { ondismiss: () => {} };
                  const rzp = new Razorpay(options);
                  rzp.on('payment.failed', () => {});
                  document.getElementById('pay').onclick = () => rzp.open();
                  rzp.open();
                </script>
                """));
        }

        return Html(Layout("Test payment", $"""
            {summary}
            <form method="post" action="/pay/{orderNo}/simulate">
              <button class="btn" type="submit">Pay {OrderService.Inr(order.Total)} (Test)</button>
            </form>
            <a class="cancel" href="/pay/result?status=cancelled&orderNo={orderNo}">Cancel</a>
            <p class="note">⚠️ Test mode: no real money is charged. Add Razorpay test keys in appsettings.json for the real checkout screen.</p>
            """));
    }

    [HttpPost("{orderNo}/razorpay")]
    public async Task<IActionResult> VerifyRazorpay(string orderNo, RazorpayVerifyRequest req)
    {
        var order = await orders.GetAsync(orderNo);
        if (order is null || order.GatewayOrderId != req.RazorpayOrderId)
            return BadRequest(new ApiMessage("Order mismatch."));
        if (!razorpay.VerifySignature(req.RazorpayOrderId, req.RazorpayPaymentId, req.RazorpaySignature))
        {
            log.Write("PAYMENT_FAILED", order.UserId, $"Order {orderNo}: invalid Razorpay signature");
            return BadRequest(new ApiMessage("Payment signature is invalid."));
        }

        log.Write("PAYMENT", order.UserId, $"Order {orderNo}: Razorpay payment {req.RazorpayPaymentId} verified");
        await orders.CompleteBuyAsync(orderNo, "razorpay", req.RazorpayPaymentId);
        return Ok(new ApiMessage("Payment verified."));
    }

    [HttpPost("{orderNo}/simulate")]
    public async Task<IActionResult> Simulate(string orderNo)
    {
        var order = await orders.GetAsync(orderNo);
        // Only orders created without a gateway can be completed this way
        if (order is null || order.PaymentGateway != "simulated")
            return Redirect($"/pay/result?status=failed&orderNo={orderNo}");

        log.Write("PAYMENT", order.UserId, $"Order {orderNo}: test payment submitted");
        var ok = await orders.CompleteBuyAsync(orderNo, "simulated", "SIM-" + Guid.NewGuid().ToString("N")[..10]);
        return Redirect($"/pay/result?status={(ok ? "success" : "failed")}&orderNo={orderNo}");
    }

    [HttpGet("result")]
    public IActionResult Result(string status, string orderNo)
    {
        var (icon, title) = status switch
        {
            "success" => ("✅", "Payment successful"),
            "cancelled" => ("↩️", "Payment cancelled"),
            _ => ("❌", "Payment failed"),
        };
        return Html(Layout(title, $"<div class='result'>{icon}</div><h2>{title}</h2><p class='no'>Order {WebUtility.HtmlEncode(orderNo)}</p><a class='cancel' href='/'>Back to AurSil</a>"));
    }

    private ContentResult Html(string html) => Content(html, "text/html; charset=utf-8");

    private static string Layout(string title, string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}}</title>
        <style>
          body{margin:0;font-family:Roboto,Segoe UI,Arial,sans-serif;background:#F4F6FA;color:#0B1F3A;text-align:center}
          header{background:#0B1F3A;padding:22px 16px 46px;color:#fff} header b{color:#F5B82E;font-size:24px} header span{color:#C0C7D1;font-size:24px;font-weight:700}
          h1{font-size:18px;margin:6px 0 0;font-weight:500;color:#AEB8CC}
          main{padding:0 18px 24px;margin-top:-30px}
          .card{background:#fff;border-radius:18px;padding:20px;box-shadow:0 6px 18px rgba(0,0,0,.1);text-align:left}
          .metal{color:#6B7280;font-size:14px} .grams{font-size:30px;font-weight:700;margin:4px 0 12px}
          table{width:100%;border-collapse:collapse;font-size:15px} td{padding:7px 0} td:last-child{text-align:right}
          .total td{border-top:1px solid #E5E7EB;font-weight:700;font-size:17px;padding-top:10px}
          .no{color:#9CA3AF;font-size:12px;margin-top:10px}
          .btn{width:100%;height:54px;border:none;border-radius:12px;background:#F5B82E;color:#0B1F3A;font-size:17px;font-weight:700;margin-top:18px}
          .cancel{display:block;margin-top:14px;color:#6B7280;font-size:14px}
          .note{color:#6B7280;font-size:12px;margin-top:16px} .result{font-size:64px;margin-top:40px}
        </style></head>
        <body><header><b>Aur</b><span>Sil</span><h1>{{title}}</h1></header><main>{{body}}</main></body></html>
        """;
}
