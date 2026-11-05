using System.Text;

namespace AurSil.Api.Services;

// Writes one plain-text file per day: Logs/2026-11-05.log
// Line format:  2026-11-05 14:03:22 | LOGIN          | user=suman | Logged in from 127.0.0.1
// A new file starts automatically at midnight (server local time). Never log passwords, OTPs or tokens here.
public class ActivityLog
{
    private readonly string _dir;
    private readonly object _lock = new();

    public ActivityLog(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["ActivityLog:Directory"];
        _dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "Logs")
            : Path.GetFullPath(configured, env.ContentRootPath);
        Directory.CreateDirectory(_dir);
    }

    public void Write(string eventName, string? userId, string message)
    {
        try
        {
            var now = DateTime.Now;
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" | ").Append(eventName.ToUpperInvariant().PadRight(14))
                .Append(" | user=").Append(string.IsNullOrEmpty(userId) ? "-" : Clean(userId))
                .Append(" | ").Append(Clean(message))
                .Append(Environment.NewLine)
                .ToString();

            var path = Path.Combine(_dir, now.ToString("yyyy-MM-dd") + ".log");
            lock (_lock)
                File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch
        {
            // Logging must never break a request or the autopay worker
        }
    }

    // One event per line
    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
}
