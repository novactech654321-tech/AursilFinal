using System.Collections.Concurrent;
using System.Text.Json;
using AurSil.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Services;

public class PricingOptions
{
    public int RefreshSeconds { get; set; } = 60;
    public int HistoryEveryMinutes { get; set; } = 5;
    public decimal ImportDutyPercent { get; set; } = 6;
    public decimal SpreadPercent { get; set; } = 1.5m;
    public decimal GstPercent { get; set; } = 3;
    public decimal MinOrderInr { get; set; } = 10;
    public decimal MaxOrderInr { get; set; } = 200000;
}

// Holds the latest live quote per metal; PriceUpdater keeps it fresh.
public class PriceService(PricingOptions options, IConfiguration config)
{
    private const decimal GramsPerTroyOunce = 31.1034768m;
    private readonly ConcurrentDictionary<string, MetalQuote> _latest = new();
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    public PricingOptions Options => options;

    public bool HasPrices => _latest.Count == Metals.All.Length;

    public MetalQuote? Get(string metal) => _latest.GetValueOrDefault(metal);

    public IReadOnlyList<MetalQuote> All() => Metals.All.Select(m => _latest.GetValueOrDefault(m)).OfType<MetalQuote>().ToList();

    public (decimal Buy, decimal Sell) ToInrRates(decimal spotUsdPerOz, decimal usdInr)
    {
        var landed = spotUsdPerOz * usdInr / GramsPerTroyOunce * (1 + options.ImportDutyPercent / 100);
        return (Math.Round(landed * (1 + options.SpreadPercent / 100), 2),
                Math.Round(landed * (1 - options.SpreadPercent / 100), 2));
    }

    public void Set(MetalQuote quote) => _latest[quote.Metal] = quote;

    public async Task<IReadOnlyList<HistoryPoint>> HistoryAsync(string metal, string range)
    {
        var from = DateTime.UtcNow - range switch
        {
            "1W" => TimeSpan.FromDays(7),
            "1M" => TimeSpan.FromDays(30),
            "1Y" => TimeSpan.FromDays(365),
            _ => TimeSpan.FromDays(1),
        };

        await using var db = new SqlConnection(_connectionString);
        var rows = (await db.QueryAsync<HistoryPoint>(
            "SELECT RecordedAt AS T, BuyRate AS Buy, SellRate AS Sell FROM PriceHistory WHERE Metal = @metal AND RecordedAt >= @from ORDER BY RecordedAt",
            new { metal, from })).ToList();

        // 1D falls back to the last 24 recorded points when the market was closed
        if (rows.Count < 2 && range == "1D")
            rows = (await db.QueryAsync<HistoryPoint>(
                "SELECT TOP 48 RecordedAt AS T, BuyRate AS Buy, SellRate AS Sell FROM PriceHistory WHERE Metal = @metal ORDER BY RecordedAt DESC",
                new { metal })).Reverse().ToList();

        if (Get(metal) is { } live)
            rows.Add(new HistoryPoint(live.UpdatedAt, live.BuyRate, live.SellRate));

        return Downsample(rows, 60);
    }

    public async Task<decimal> ChangePercentAsync(string metal, decimal currentBuy)
    {
        await using var db = new SqlConnection(_connectionString);
        var dayAgo = await db.QueryFirstOrDefaultAsync<decimal?>(
            "SELECT TOP 1 BuyRate FROM PriceHistory WHERE Metal = @metal AND RecordedAt <= @t ORDER BY RecordedAt DESC",
            new { metal, t = DateTime.UtcNow.AddHours(-24) });
        return dayAgo is > 0 ? Math.Round((currentBuy - dayAgo.Value) / dayAgo.Value * 100, 2) : 0;
    }

    private static List<HistoryPoint> Downsample(List<HistoryPoint> points, int max)
    {
        if (points.Count <= max)
            return points;
        var step = (double)points.Count / max;
        var result = Enumerable.Range(0, max).Select(i => points[(int)(i * step)]).ToList();
        result[^1] = points[^1];
        return result;
    }
}

public class PriceUpdater(PriceService prices, PricingOptions options, IConfiguration config,
    IHttpClientFactory httpFactory, ILogger<PriceUpdater> logger) : BackgroundService
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;
    private decimal _usdInr;
    private DateTime _fxFetchedAt = DateTime.MinValue;
    private DateTime _lastSavedAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadLastKnownAsync();
        try { await BackfillIfEmptyAsync(stoppingToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Price history backfill failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Live price refresh failed: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, options.RefreshSeconds)), stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config["Nebula:ApiKey"]))
        {
            await RefreshFromNebulaAsync(ct);
            return;
        }

        var http = httpFactory.CreateClient("prices");

        if (_usdInr == 0 || DateTime.UtcNow - _fxFetchedAt > TimeSpan.FromHours(1))
        {
            using var fx = JsonDocument.Parse(await http.GetStringAsync("https://open.er-api.com/v6/latest/USD", ct));
            _usdInr = fx.RootElement.GetProperty("rates").GetProperty("INR").GetDecimal();
            _fxFetchedAt = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var save = now - _lastSavedAt >= TimeSpan.FromMinutes(options.HistoryEveryMinutes);

        foreach (var metal in Metals.All)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.gold-api.com/price/{Metals.SpotSymbol(metal)}", ct));
            var spot = doc.RootElement.GetProperty("price").GetDecimal();
            var (buy, sell) = prices.ToInrRates(spot, _usdInr);
            var change = await SafeChangePercentAsync(metal, buy);
            prices.Set(new MetalQuote(metal, buy, sell, spot, _usdInr, change, now));

            if (save)
            {
                try
                {
                    await using var db = new SqlConnection(_connectionString);
                    await db.ExecuteAsync(
                        "INSERT INTO PriceHistory (Metal, SpotUsdPerOz, UsdInr, BuyRate, SellRate, RecordedAt) VALUES (@metal, @spot, @usdInr, @buy, @sell, @now)",
                        new { metal, spot, usdInr = _usdInr, buy, sell, now });
                }
                catch (Exception ex) when (IsDbUnavailable(ex))
                {
                    logger.LogWarning(ex, "Price save skipped because SQL Server is unavailable.");
                }
            }
        }

        if (save)
            _lastSavedAt = now;
    }

    // Nebula Metals returns Indian-market INR prices per gram. basePricePerGram already includes import duty and the
    // domestic premium but NOT GST (GST is added at checkout by AurSil), so it is used directly as the buy rate and the
    // sell rate is the buy rate less the configured spread.
    private async Task RefreshFromNebulaAsync(CancellationToken ct)
    {
        var baseUrl = (config["Nebula:BaseUrl"] ?? "https://workspace.nebulaapi.com").TrimEnd('/');
        var city = Uri.EscapeDataString(config["Nebula:City"] ?? "Chennai");
        var http = httpFactory.CreateClient("prices");

        var endpoints = new Dictionary<string, (string Path, string Key)>
        {
            [Metals.Gold] = ($"/gold_price?city={city}", "gold24k"),
            [Metals.Silver] = ($"/silver_price?city={city}", "silver"),
            [Metals.Platinum] = ("/platinum_price", "platinum"),
        };

        var now = DateTime.UtcNow;
        var save = now - _lastSavedAt >= TimeSpan.FromMinutes(options.HistoryEveryMinutes);

        foreach (var metal in Metals.All)
        {
            var (path, key) = endpoints[metal];

            // Live call first. If Nebula refuses (no credits, network down...) use the file saved by scripts/get-prices.sh.
            string json;
            var fromFile = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                request.Headers.Add("x-api-key", config["Nebula:ApiKey"]);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Nebula {path} returned {(int)response.StatusCode}: {body}");
                }
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                var saved = ReadSavedNebulaFile(metal);
                if (saved is null) throw;
                logger.LogWarning("Nebula live call failed ({Message}); using saved file for {Metal}", ex.Message, metal);
                json = saved;
                fromFile = true;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // A saved file keeps the time Nebula produced it, so the page shows the real age of the price
            var asOf = fromFile && root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t.ToUniversalTime() : now;
            var buy = Math.Round(root.GetProperty("prices").GetProperty(key).GetProperty("basePricePerGram").GetDecimal(), 2);
            var sell = Math.Round(buy * (1 - options.SpreadPercent / 100), 2);

            decimal spot = 0, usdInr = _usdInr;
            if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("source_details", out var sd) &&
                sd.TryGetProperty("engine", out var engine))
            {
                if (engine.TryGetProperty("spot_usd_oz", out var s1)) spot = s1.GetDecimal();
                if (engine.TryGetProperty("usd_inr", out var s2)) usdInr = s2.GetDecimal();
            }
            _usdInr = usdInr;

            var change = await SafeSpotChangePercentAsync(metal, spot);
            prices.Set(new MetalQuote(metal, buy, sell, spot, usdInr, change, asOf));

            if (save && !fromFile)
            {
                try
                {
                    await using var db = new SqlConnection(_connectionString);
                    await db.ExecuteAsync(
                        "INSERT INTO PriceHistory (Metal, SpotUsdPerOz, UsdInr, BuyRate, SellRate, RecordedAt) VALUES (@metal, @spot, @usdInr, @buy, @sell, @now)",
                        new { metal, spot, usdInr, buy, sell, now });
                }
                catch (Exception ex) when (IsDbUnavailable(ex))
                {
                    logger.LogWarning(ex, "Price save skipped because SQL Server is unavailable.");
                }
            }
        }

        if (save)
            _lastSavedAt = now;
    }

    // The three files written by scripts/get-prices.sh (gold.json, silver.json, platinum.json)
    private string? ReadSavedNebulaFile(string metal)
    {
        try
        {
            var dir = Path.GetFullPath(config["Nebula:FilesDirectory"] ?? "../scripts/output", Directory.GetCurrentDirectory());
            var file = Path.Combine(dir, metal + ".json");
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            // Ignore a file holding an error message instead of prices
            return text.Contains("\"success\":true") ? text : null;
        }
        catch { return null; }
    }

    // 24h change measured on the USD spot price, so it stays correct even though older history rows were priced with
    // a different formula than Nebula's.
    private async Task<decimal> SafeSpotChangePercentAsync(string metal, decimal spotNow)
    {
        if (spotNow <= 0) return 0;
        try
        {
            await using var db = new SqlConnection(_connectionString);
            var dayAgo = await db.QueryFirstOrDefaultAsync<decimal?>(
                "SELECT TOP 1 SpotUsdPerOz FROM PriceHistory WHERE Metal = @metal AND RecordedAt <= @t ORDER BY RecordedAt DESC",
                new { metal, t = DateTime.UtcNow.AddHours(-24) });
            return dayAgo is > 0 ? Math.Round((spotNow - dayAgo.Value) / dayAgo.Value * 100, 2) : 0;
        }
        catch (Exception ex) when (IsDbUnavailable(ex))
        {
            return 0m;
        }
    }

    private async Task LoadLastKnownAsync()
    {
        try
        {
            await using var db = new SqlConnection(_connectionString);
            foreach (var metal in Metals.All)
            {
                var r = await db.QueryFirstOrDefaultAsync<LastPriceRow>(
                    "SELECT TOP 1 SpotUsdPerOz, UsdInr, BuyRate, SellRate, RecordedAt FROM PriceHistory WHERE Metal = @metal ORDER BY RecordedAt DESC",
                    new { metal });
                if (r is not null)
                {
                    prices.Set(new MetalQuote(metal, r.BuyRate, r.SellRate, r.SpotUsdPerOz, r.UsdInr, 0, r.RecordedAt));
                    _usdInr = r.UsdInr;
                }
            }
        }
        catch (Exception ex) when (IsDbUnavailable(ex))
        {
            logger.LogWarning(ex, "SQL Server not available; waiting for the first live price instead.");
        }
    }

    private async Task BackfillIfEmptyAsync(CancellationToken ct)
    {
        try
        {
            await using var db = new SqlConnection(_connectionString);
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PriceHistory") > 0)
                return;

            logger.LogInformation("Backfilling price history from Yahoo Finance...");
            var ranges = new[] { ("1y", "1d"), ("1mo", "60m"), ("5d", "15m"), ("1d", "5m") };

            var fx = new SortedList<DateTime, decimal>();
            foreach (var (range, interval) in ranges)
                foreach (var (t, v) in await YahooSeriesAsync("INR=X", range, interval, ct))
                    fx[t] = v;
            if (fx.Count == 0)
                return;

            var rows = new List<object>();
            foreach (var metal in Metals.All)
            {
                var series = new SortedList<DateTime, decimal>();
                foreach (var (range, interval) in ranges)
                    foreach (var (t, v) in await YahooSeriesAsync(Metals.YahooSymbol(metal), range, interval, ct))
                        series[t] = v;

                foreach (var (t, spot) in series)
                {
                    var usdInr = NearestValue(fx, t);
                    var (buy, sell) = prices.ToInrRates(spot, usdInr);
                    rows.Add(new { metal, spot, usdInr, buy, sell, t });
                }
            }

            await db.ExecuteAsync(
                "INSERT INTO PriceHistory (Metal, SpotUsdPerOz, UsdInr, BuyRate, SellRate, RecordedAt) VALUES (@metal, @spot, @usdInr, @buy, @sell, @t)",
                rows);
            logger.LogInformation("Backfilled {Count} price points", rows.Count);
        }
        catch (Exception ex) when (IsDbUnavailable(ex))
        {
            logger.LogWarning(ex, "Price history backfill skipped because SQL Server is unavailable.");
        }
    }

    private async Task<List<(DateTime, decimal)>> YahooSeriesAsync(string symbol, string range, string interval, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("prices");
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        if (!result.TryGetProperty("timestamp", out var times))
            return [];
        var closes = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close");

        var list = new List<(DateTime, decimal)>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (closes[i].ValueKind != JsonValueKind.Number)
                continue;
            list.Add((DateTimeOffset.FromUnixTimeSeconds(times[i].GetInt64()).UtcDateTime, closes[i].GetDecimal()));
        }
        return list;
    }

    private async Task<decimal> SafeChangePercentAsync(string metal, decimal currentBuy)
    {
        try
        {
            await using var db = new SqlConnection(_connectionString);
            var dayAgo = await db.QueryFirstOrDefaultAsync<decimal?>(
                "SELECT TOP 1 BuyRate FROM PriceHistory WHERE Metal = @metal AND RecordedAt <= @t ORDER BY RecordedAt DESC",
                new { metal, t = DateTime.UtcNow.AddHours(-24) });
            return dayAgo is > 0 ? Math.Round((currentBuy - dayAgo.Value) / dayAgo.Value * 100, 2) : 0;
        }
        catch (Exception ex) when (IsDbUnavailable(ex))
        {
            logger.LogWarning(ex, "Skipping change calculation because SQL Server is unavailable.");
            return 0m;
        }
    }

    private static bool IsDbUnavailable(Exception ex) => ex is SqlException || ex is InvalidOperationException || ex is TimeoutException;

    private class LastPriceRow
    {
        public decimal SpotUsdPerOz { get; set; }
        public decimal UsdInr { get; set; }
        public decimal BuyRate { get; set; }
        public decimal SellRate { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    private static decimal NearestValue(SortedList<DateTime, decimal> series, DateTime t)
    {
        var keys = series.Keys;
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (keys[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return series.Values[lo];
    }
}
