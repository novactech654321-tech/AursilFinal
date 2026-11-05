namespace AurSil.Api.Models;

public static class Metals
{
    public const string Gold = "gold";
    public const string Silver = "silver";
    public const string Platinum = "platinum";

    public static readonly string[] All = [Gold, Silver, Platinum];

    // gold-api.com symbols and Yahoo Finance futures tickers
    public static string SpotSymbol(string metal) => metal switch { Gold => "XAU", Silver => "XAG", _ => "XPT" };
    public static string YahooSymbol(string metal) => metal switch { Gold => "GC=F", Silver => "SI=F", _ => "PL=F" };
    public static string Display(string metal) => metal switch { Gold => "Gold", Silver => "Silver", _ => "Platinum" };

    public static bool IsValid(string? metal) => metal is not null && All.Contains(metal);
}

public record MetalQuote(
    string Metal,
    decimal BuyRate,
    decimal SellRate,
    decimal SpotUsdPerOz,
    decimal UsdInr,
    decimal Change24hPercent,
    DateTime UpdatedAt);

public record PricesResponse(IReadOnlyList<MetalQuote> Metals, decimal GstPercent, DateTime UpdatedAt);

public record HistoryPoint(DateTime T, decimal Buy, decimal Sell);

public record BuyRequest(string Metal, decimal? AmountInr, decimal? Grams);

public record SellRequest(string Metal, decimal Grams);

public record OrderDto(
    string OrderNo,
    string Metal,
    string Side,
    decimal Grams,
    decimal RatePerGram,
    decimal Amount,
    decimal Gst,
    decimal Total,
    string Status,
    string? PaymentGateway,
    string? PayoutTo,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? InvoiceNo,
    string? TransactionId);

public record BuyResponse(OrderDto Order, string PaymentMode, string PayPath);

public record HoldingDto(string Metal, decimal Grams, decimal Invested, decimal CurrentValue, decimal ProfitLoss, decimal ProfitLossPercent, decimal SellRate);

public record PortfolioResponse(IReadOnlyList<HoldingDto> Holdings, decimal TotalInvested, decimal TotalValue, decimal ProfitLoss, decimal ProfitLossPercent);

public record BankAccountRequest(string HolderName, string AccountNumber, string Ifsc);

public record BankAccountDto(string HolderName, string AccountNumberMasked, string Ifsc, string BankName, string? Branch);

public record IfscInfo(string Ifsc, string BankName, string Branch, string City);

public record LoginResponse(string Token, UserProfile User);

public record RazorpayVerifyRequest(string RazorpayPaymentId, string RazorpayOrderId, string RazorpaySignature);

public class OrderRow
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Metal { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Grams { get; set; }
    public decimal RatePerGram { get; set; }
    public decimal Amount { get; set; }
    public decimal Gst { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "";
    public string? PaymentGateway { get; set; }
    public string? GatewayOrderId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public string? InvoiceNo { get; set; }
    public int? BankAccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }

    public OrderDto ToDto() => new(OrderNo, Metal, Side, Grams, RatePerGram, Amount, Gst, Total, Status, PaymentGateway,
        AccountNumber is null ? null : $"{BankName} {BankMask.Mask(AccountNumber)}", CreatedAt, CompletedAt, InvoiceNo, GatewayPaymentId);
}

public class BankAccountRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string HolderName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string Ifsc { get; set; } = "";
    public string BankName { get; set; } = "";
    public string? Branch { get; set; }

    public BankAccountDto ToDto() => new(HolderName, BankMask.Mask(AccountNumber), Ifsc, BankName, Branch);
}

public static class BankMask
{
    public static string Mask(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : "XXXX" + accountNumber[^4..];
}
