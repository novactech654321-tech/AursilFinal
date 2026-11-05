using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AurSil.Api.Models;
using AurSil.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AurSil.Api.Controllers;

public record TokenRequest(string Name);

// Personal access tokens, created from the website by a logged-in user
[ApiController]
[Authorize]
[Route("api/tokens")]
public class TokensController(IConfiguration config) : ControllerBase
{
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await using var db = new SqlConnection(_connectionString);
        return Ok(await db.QueryAsync("SELECT Id, Name, CreatedAt, LastUsedAt FROM ApiTokens WHERE UserId = @userId ORDER BY Id DESC",
            new { userId = User.UserId() }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(TokenRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 60)
            return BadRequest(new ApiMessage("Give the token a name (e.g. Claude Desktop)."));

        var userId = User.UserId();
        await using var db = new SqlConnection(_connectionString);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApiTokens WHERE UserId = @userId", new { userId }) >= 10)
            return BadRequest(new ApiMessage("You can have up to 10 tokens. Delete one first."));

        var token = "aursil_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await db.ExecuteAsync("INSERT INTO ApiTokens (UserId, Name, TokenHash) VALUES (@userId, @name, @hash)",
            new { userId, name, hash = McpController.Hash(token) });
        return Ok(new { token, message = "Copy this token now. It is shown only once." });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = new SqlConnection(_connectionString);
        var rows = await db.ExecuteAsync("DELETE FROM ApiTokens WHERE Id = @id AND UserId = @userId", new { id, userId = User.UserId() });
        return rows == 0 ? NotFound(new ApiMessage("Token not found.")) : Ok(new ApiMessage("Token deleted."));
    }
}

// MCP server (Streamable HTTP, stateless). Any MCP-capable AI app can add https://<host>/mcp as a connector and
// call the same actions as the website chat, as the user who owns the token.
[ApiController]
[Route("mcp")]
public class McpController(IConfiguration config, ChatTools tools) : ControllerBase
{
    private static readonly string[] SupportedVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private readonly string _connectionString = config.GetConnectionString("Aursil")!;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [HttpGet]
    public IActionResult Get() => StatusCode(405);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonNode? message)
    {
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
        await using var db = new SqlConnection(_connectionString);
        var user = token.Length == 0 ? null : await db.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT u.* FROM ApiTokens t JOIN Users u ON u.UserId = t.UserId
            WHERE t.TokenHash = @hash AND u.Status = 'Active'
            """, new { hash = Hash(token) });
        if (user is null)
        {
            Response.Headers.WWWAuthenticate = "Bearer realm=\"AurSil\"";
            return Unauthorized(new { error = "Missing or invalid access token." });
        }
        await db.ExecuteAsync("UPDATE ApiTokens SET LastUsedAt = SYSUTCDATETIME() WHERE TokenHash = @hash", new { hash = Hash(token) });

        if (message is not JsonObject msg || msg["method"]?.GetValue<string>() is not { } method)
            return Rpc(null, error: (-32600, "Invalid request."));

        var id = msg["id"]?.DeepClone();
        // Notifications have no id and get no reply body
        if (id is null) return Accepted();

        var isAdmin = user.Role == UserRole.Admin;
        switch (method)
        {
            case "initialize":
            {
                var asked = msg["params"]?["protocolVersion"]?.GetValue<string>();
                return Rpc(id, result: new JsonObject
                {
                    ["protocolVersion"] = asked is not null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "aursil", ["version"] = "1.0.0" },
                    ["instructions"] = "AurSil: buy and sell gold, silver and platinum. buy_metal only creates an order; the user pays on the returned link.",
                });
            }
            case "ping":
                return Rpc(id, result: new JsonObject());

            case "tools/list":
                return Rpc(id, result: new JsonObject
                {
                    ["tools"] = new JsonArray(ChatTools.Definitions(isAdmin).Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t!["name"]!.DeepClone(),
                        ["description"] = t["description"]!.DeepClone(),
                        ["inputSchema"] = t["input_schema"]!.DeepClone(),
                    }).ToArray()),
                });

            case "tools/call":
            {
                var name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                if (!ChatTools.Definitions(isAdmin).Any(t => t!["name"]!.GetValue<string>() == name))
                    return Rpc(id, error: (-32602, $"Unknown tool '{name}'."));

                var outcome = await tools.RunAsync(name, msg["params"]?["arguments"], user.UserId, isAdmin);
                var text = outcome.Text;
                if (outcome.Action is not null)
                {
                    var url = System.Text.Json.JsonSerializer.SerializeToNode(outcome.Action)?["url"]?.GetValue<string>();
                    if (url is not null) text += $"\nThe user must pay here to complete the order: {Request.Scheme}://{Request.Host}{url}";
                }
                return Rpc(id, result: new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = outcome.IsError,
                });
            }
            default:
                return Rpc(id, error: (-32601, $"Method not found: {method}"));
        }
    }

    private ContentResult Rpc(JsonNode? id, JsonNode? result = null, (int Code, string Message)? error = null)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is { } e) body["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else body["result"] = result;
        return Content(body.ToJsonString(), "application/json");
    }
}
