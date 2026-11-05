using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

public record ChatMessage(string Role, string Content);

public record ChatRequest(List<ChatMessage> Messages);

public record McpServerRequest(string Name, string Url, string? AuthToken);

// AI chat. Users add remote MCP servers (like the Connectors screen in Claude); Anthropic's MCP connector
// calls those servers, so the URL has to be public https.
[ApiController]
[Authorize]
[Route("api/chat")]
public partial class ChatController(IConfiguration config, IHttpClientFactory httpFactory, IDataProtectionProvider dataProtection,
    ChatTools chatTools, ILogger<ChatController> logger) : ControllerBase
{
    private const string McpBeta = "mcp-client-2025-11-20";
    private const int MaxServersPerUser = 10;
    private const int MaxRequestsPerHour = 30;

    private readonly string _connectionString = config.GetConnectionString("Aursil")!;
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AurSil.McpServers.AuthToken");

    // Per-user request counter so one account cannot run up the Anthropic bill
    private static readonly ConcurrentDictionary<string, List<DateTime>> RecentRequests = new();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ServerNameRegex();

    [HttpGet("mcp")]
    public async Task<IActionResult> ListServers()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync(
            "SELECT Id, Name, Url, CAST(CASE WHEN AuthToken IS NULL THEN 0 ELSE 1 END AS BIT) AS HasToken FROM McpServers WHERE UserId = @userId ORDER BY Name",
            new { userId = User.UserId() }));
    }

    [HttpPost("mcp")]
    public async Task<IActionResult> AddServer(McpServerRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (!ServerNameRegex().IsMatch(name))
            return BadRequest(new ApiMessage("Name must be 1-40 letters, numbers, dash or underscore."));
        if (!Uri.TryCreate(req.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host == "localhost")
            return BadRequest(new ApiMessage("Enter the public https URL of a remote MCP server (e.g. https://example.com/mcp)."));

        var userId = User.UserId();
        await using var db = new SqlConnection(_connectionString);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM McpServers WHERE UserId = @userId", new { userId }) >= MaxServersPerUser)
            return BadRequest(new ApiMessage($"You can add up to {MaxServersPerUser} MCP servers."));
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM McpServers WHERE UserId = @userId AND Name = @name", new { userId, name }) > 0)
            return Conflict(new ApiMessage("You already have a server with this name."));

        var token = string.IsNullOrWhiteSpace(req.AuthToken) ? null : _protector.Protect(req.AuthToken.Trim());
        await db.ExecuteAsync(
            "INSERT INTO McpServers (UserId, Name, Url, AuthToken) VALUES (@userId, @name, @url, @token)",
            new { userId, name, url = uri.ToString(), token });
        return Ok(new ApiMessage("MCP server added."));
    }

    [HttpDelete("mcp/{id:int}")]
    public async Task<IActionResult> DeleteServer(int id)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.ExecuteAsync("DELETE FROM McpServers WHERE Id = @id AND UserId = @userId", new { id, userId = User.UserId() });
        return rows == 0 ? NotFound(new ApiMessage("Server not found.")) : Ok(new ApiMessage("MCP server removed."));
    }

    [HttpPost]
    public async Task<IActionResult> Chat(ChatRequest req)
    {
        var apiKey = config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, new ApiMessage("AI chat is not configured. Add Anthropic:ApiKey in appsettings."));

        var messages = req.Messages ?? [];
        if (messages.Count is 0 or > 30 ||
            messages.Any(m => m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > 4000) ||
            messages[^1].Role != "user")
            return BadRequest(new ApiMessage("Invalid conversation."));

        var userId = User.UserId();
        if (!UnderRateLimit(userId))
            return StatusCode(429, new ApiMessage($"Limit of {MaxRequestsPerHour} messages per hour reached. Try again later."));

        await using var db = new SqlConnection(_connectionString);
        var servers = (await db.QueryAsync<(string Name, string Url, string? AuthToken)>(
            "SELECT Name, Url, AuthToken FROM McpServers WHERE UserId = @userId", new { userId })).ToList();

        var isAdmin = User.IsInRole(UserRole.Admin);
        var mcpServers = new JsonArray();
        var tools = ChatTools.Definitions(isAdmin);
        foreach (var s in servers)
        {
            var def = new JsonObject { ["type"] = "url", ["url"] = s.Url, ["name"] = s.Name };
            if (s.AuthToken is not null)
            {
                try { def["authorization_token"] = _protector.Unprotect(s.AuthToken); }
                catch { logger.LogWarning("Could not decrypt token for MCP server {Name}", s.Name); }
            }
            mcpServers.Add(def);
            tools.Add(new JsonObject { ["type"] = "mcp_toolset", ["mcp_server_name"] = s.Name });
        }

        var system = """
            You are the AurSil assistant. AurSil lets people buy and sell gold, silver and platinum. Prices are in Indian rupees per gram.
            You can act for the logged-in user with your tools: look up prices, stock and their portfolio, and create buy orders.
            - "buy 2 gram gold" or "buy gold worth 5000": call buy_metal. It only creates the order; tell the user to press the Pay button shown under your reply. Never say a purchase is complete.
            - If stock is too low, the tool says so; pass that on and suggest a smaller quantity.
            - For admins, add_stock records stock bought from a vendor and changes the database. Before calling it, the vendor name, metal, grams and the vendor's price per gram must all be given by the user. Ask for anything missing and never guess a vendor or price. Then repeat the details back and ask the user to confirm, and call add_stock only after they confirm.
            - Use tool results for facts. Do not invent prices, stock or account data. Keep answers short.
            """;
        var model = config["Anthropic:Model"] ?? "claude-opus-5-5";
        var baseUrl = (config["Anthropic:BaseUrl"] ?? "https://api.anthropic.com").TrimEnd('/');

        var apiMessages = new JsonArray(messages
            .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());
        var toolCalls = new List<string>();
        var actions = new List<object>();
        using var http = httpFactory.CreateClient("anthropic");

        // Each round: ask Claude; if it wants our tools, run them as this user and send the results back
        for (var round = 0; round < 6; round++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 4096,
                ["system"] = system,
                ["messages"] = apiMessages.DeepClone(),
                ["tools"] = tools.DeepClone(),
            };
            if (servers.Count > 0) body["mcp_servers"] = mcpServers.DeepClone();

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages") { Content = JsonContent.Create(body) };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("anthropic-beta", McpBeta);

            JsonNode? result;
            try
            {
                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogError("Anthropic API returned {Status}: {Body}", (int)response.StatusCode, text);
                    var detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>();
                    return StatusCode(502, new ApiMessage(detail ?? "The AI service returned an error."));
                }
                result = JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Anthropic API call failed");
                return StatusCode(502, new ApiMessage("Could not reach the AI service."));
            }

            var stopReason = result?["stop_reason"]?.GetValue<string>();
            if (stopReason == "refusal")
                return Ok(new { reply = "I can't help with that request.", toolCalls, actions });

            var content = result?["content"]?.AsArray() ?? [];
            foreach (var block in content)
                if (block?["type"]?.GetValue<string>() == "mcp_tool_use")
                    toolCalls.Add($"{block["name"]?.GetValue<string>()} ({block["server_name"]?.GetValue<string>()})");

            if (stopReason is "tool_use" or "pause_turn")
            {
                // Send the assistant turn back unchanged, plus our tool results
                apiMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                if (stopReason == "pause_turn") continue;

                var results = new JsonArray();
                foreach (var block in content)
                {
                    if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                    var name = block["name"]!.GetValue<string>();
                    var outcome = await chatTools.RunAsync(name, block["input"], userId, isAdmin);
                    toolCalls.Add(name);
                    if (outcome.Action is not null) actions.Add(outcome.Action);
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = block["id"]!.GetValue<string>(),
                        ["content"] = outcome.Text,
                        ["is_error"] = outcome.IsError,
                    });
                }
                apiMessages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                continue;
            }

            var reply = string.Concat(content.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]?.GetValue<string>() ?? ""));
            return Ok(new { reply = reply.Trim(), toolCalls, actions });
        }

        return Ok(new { reply = "That took too many steps. Please try again with a simpler request.", toolCalls, actions });
    }

    private static bool UnderRateLimit(string userId)
    {
        var now = DateTime.UtcNow;
        var list = RecentRequests.GetOrAdd(userId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (list.Count >= MaxRequestsPerHour) return false;
            list.Add(now);
            return true;
        }
    }
}
