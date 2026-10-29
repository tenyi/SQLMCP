using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = [Path.GetFullPath("src/SqlMcp/bin/Debug/net10.0/SqlMcp.dll"), "--config", Path.GetFullPath("sqlmcp.example.json")]
});
await using var client = await McpClient.CreateAsync(transport,
    new McpClientOptions { ProtocolVersion = "2026-07-28" }, cancellationToken: timeout.Token);
var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
if (!new[] { "list_connections", "list_entities", "read_records" }.All(n => tools.Any(t => t.Name == n)))
    throw new Exception("工具清單不完整");
var connections = await client.CallToolAsync("list_connections", cancellationToken: timeout.Token);
var text = string.Join("", connections.Content.OfType<TextContentBlock>().Select(x => x.Text));
if (!text.Contains("erp") || !text.Contains("crm") || text.Contains("reader") || text.Contains("Password"))
    throw new Exception("連線清單錯誤或洩漏機密");
foreach (var arguments in new[]
{
    new Dictionary<string, object?> { ["connection"] = "missing", ["entity"] = "products" },
    new Dictionary<string, object?> { ["connection"] = "erp", ["entity"] = "forbidden" },
    new Dictionary<string, object?> { ["connection"] = "erp", ["entity"] = "products", ["limit"] = 101 }
})
{
    var result = await client.CallToolAsync("read_records", arguments, cancellationToken: timeout.Token);
    if (result.IsError != true) throw new Exception("未阻擋無效或未授權操作");
}
Console.WriteLine("PASS: MCP 2026-07-28 stdio、工具探索、機密隔離與讀取限制。");

