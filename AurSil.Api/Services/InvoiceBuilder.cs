using System.Net;
using AurSil.Api.Models;

namespace AurSil.Api.Services;

// Seller details printed on invoices. Fill Company:* in appsettings.json; blank lines are left out.
public record CompanyInfo(string Name, string Address, string Gstin, string Email)
{
    public static CompanyInfo From(IConfiguration config) => new(
        config["Company:Name"] ?? "AurSil", config["Company:Address"] ?? "", config["Company:Gstin"] ?? "", config["Company:Email"] ?? "");
}

// One invoice layout, used both in the email and in the Invoices section of the website
public static class InvoiceBuilder
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public static string Hsn(string metal) => metal switch { Metals.Gold => "7108", Metals.Silver => "7106", _ => "7110" };

    public static string Purity(string metal) => metal switch { Metals.Gold => "24K, 99.9% pure", Metals.Silver => "99.9% pure", _ => "99.95% pure" };

    public static string Build(OrderRow o, UserRow user, CompanyInfo company)
    {
        var buy = o.Side == "Buy";
        var metal = Metals.Display(o.Metal);
        var when = (o.CompletedAt ?? o.CreatedAt) + India;
        var gstPercent = o.Amount > 0 ? Math.Round(o.Gst / o.Amount * 100, 2) : 0;
        Func<string?, string> e = s => WebUtility.HtmlEncode(s) ?? "";
        var inr = OrderService.Inr;

        string Line(string label, string value, bool bold = false) =>
            $"<tr><td style='padding:5px 0;color:#666'>{label}</td><td style='padding:5px 0;text-align:right'>{(bold ? "<b>" : "")}{value}{(bold ? "</b>" : "")}</td></tr>";

        var sellerLines = string.Join("<br>", new[] { company.Address, company.Gstin.Length > 0 ? "GSTIN: " + company.Gstin : "", company.Email }
            .Where(s => s.Length > 0).Select(e));

        var totals = buy
            ? Line("Taxable value", inr(o.Amount)) +
              Line($"GST @ {gstPercent:0.##}%", inr(o.Gst)) +
              "<tr><td colspan='2'><hr style='border:0;border-top:1px solid #ddd;margin:6px 0'></td></tr>" +
              Line("Total paid", inr(o.Total), true)
            : Line("Amount paid out", inr(o.Total), true) +
              Line("Paid to", e($"{o.BankName} {BankMask.Mask(o.AccountNumber ?? "")}".Trim()));

        var mode = o.PaymentGateway switch
        {
            "autopay" => "Autopay",
            "razorpay" => "Razorpay (online)",
            "simulated" => "Test payment",
            "bank" => "Bank transfer",
            _ => o.PaymentGateway ?? "-",
        };

        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto;border:1px solid #e5e5e5;border-radius:12px;overflow:hidden;color:#222;background:#fff">
              <div style="background:#0B1F3A;padding:18px 22px;color:#fff">
                <table style="width:100%"><tr>
                  <td><span style="color:#F5B82E;font-size:24px;font-weight:700">{e(company.Name)}</span><div style="font-size:12px;color:#cfd6e4;line-height:1.5;margin-top:4px">{sellerLines}</div></td>
                  <td style="text-align:right;vertical-align:top"><div style="font-size:15px;font-weight:700">{(buy ? "TAX INVOICE" : "SALE RECEIPT")}</div><div style="font-size:12px;color:#cfd6e4">{e(o.InvoiceNo ?? "-")}</div></td>
                </tr></table>
              </div>
              <div style="padding:20px 22px">
                <table style="width:100%;font-size:13px;border-collapse:collapse">
                  <tr><td style="vertical-align:top;width:50%"><div style="color:#888">{(buy ? "Billed to" : "Paid to")}</div><b>{e(user.Name)}</b><br>{e(user.Email)}<br>{e(user.Phone)}<br>User ID: {e(user.UserId)}</td>
                      <td style="vertical-align:top;text-align:right;line-height:1.7"><div><span style="color:#888">Invoice no:</span> <b>{e(o.InvoiceNo ?? "-")}</b></div><div><span style="color:#888">Date:</span> {when:dd MMM yyyy, hh:mm tt} IST</div><div><span style="color:#888">Order ID:</span> {e(o.OrderNo)}</div><div><span style="color:#888">Transaction ID:</span> {e(o.GatewayPaymentId ?? "-")}</div><div><span style="color:#888">Payment mode:</span> {e(mode)}</div></td></tr>
                </table>
                <table style="width:100%;font-size:13px;border-collapse:collapse;margin-top:16px">
                  <tr style="background:#F4F6FA;text-align:left"><th style="padding:8px">Description</th><th style="padding:8px">HSN</th><th style="padding:8px;text-align:right">Qty</th><th style="padding:8px;text-align:right">Rate / g</th></tr>
                  <tr><td style="padding:8px;border-bottom:1px solid #eee">{metal} ({Purity(o.Metal)})</td><td style="padding:8px;border-bottom:1px solid #eee">{Hsn(o.Metal)}</td><td style="padding:8px;border-bottom:1px solid #eee;text-align:right">{o.Grams:0.####} g</td><td style="padding:8px;border-bottom:1px solid #eee;text-align:right">{inr(o.RatePerGram)}</td></tr>
                </table>
                <table style="width:100%;font-size:14px;border-collapse:collapse;margin-top:12px">{totals}</table>
                <p style="font-size:11px;color:#999;margin-top:18px">This is a computer generated invoice and does not need a signature.</p>
              </div>
            </div>
            """;
    }
}
