// 煙霧測試：以真正的 stdio 子程序啟動 SqlMcp，用 MCP 用戶端走一遍協定，驗證不需要真實資料庫的行為。
// 執行方式：先 dotnet build src/SqlMcp，再於儲存庫根目錄執行 dotnet run --project tests/SqlMcp.Smoke。
// 任何檢查失敗都會丟出例外使程序以非 0 結束，最後印出 PASS 才代表全部通過。
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

// 整個測試最多 30 秒，避免子程序卡住時測試永遠不結束。
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
// 以 dotnet 啟動建置好的 SqlMcp.dll；設定全部經由環境變數傳入，模擬 .mcp.json 的 env 區塊。
// 共兩個連線：erp（含 products 實體、maxRows 100、未啟用 allowWrite）與 crm。
// 連線字串指向不存在的伺服器也沒關係，因為下面的檢查都會在連線資料庫之前就被擋下。
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = [Path.GetFullPath("src/SqlMcp/bin/Debug/net10.0/SqlMcp.dll")],
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["SQLMCP_Connections__erp__ConnectionString"] = "Server=localhost;Database=ERP;Encrypt=True",
        ["SQLMCP_Connections__erp__UserName"] = "erp_reader",
        ["SQLMCP_Connections__erp__Password"] = "not-a-real-password",
        ["SQLMCP_Connections__erp__MaxRows"] = "100",
        ["SQLMCP_Connections__erp__Entities__products__Schema"] = "dbo",
        ["SQLMCP_Connections__erp__Entities__products__Table"] = "Products",
        ["SQLMCP_Connections__erp__Entities__products__Columns__0"] = "Id",
        ["SQLMCP_Connections__erp__Entities__products__Columns__1"] = "Name",
        ["SQLMCP_Connections__crm__ConnectionString"] = "Server=localhost;Database=CRM;Encrypt=True",
        ["SQLMCP_Connections__crm__UserName"] = "crm_reader",
    }
});
// 用固定的協定版本 2026-07-28 連線，確認伺服器支援該版本。
await using var client = await McpClient.CreateAsync(transport,
    new McpClientOptions { ProtocolVersion = "2026-07-28" }, cancellationToken: timeout.Token);
// 檢查工具探索：所有預期的工具都必須存在（新增或改名工具時要同步更新這份清單）。
var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
if (!new[] { "list_connections", "list_entities", "read_records", "list_databases", "list_tables", "describe_table", "execute_sql" }.All(n => tools.Any(t => t.Name == n)))
    throw new Exception("工具清單不完整");
// 檢查連線清單：必須包含 erp、crm，且不得洩漏帳號（reader）或密碼相關字樣。
var connections = await client.CallToolAsync("list_connections", cancellationToken: timeout.Token);
var text = string.Join("", connections.Content.OfType<TextContentBlock>().Select(x => x.Text));
if (!text.Contains("erp") || !text.Contains("crm") || text.Contains("reader") || text.Contains("Password"))
    throw new Exception("連線清單錯誤或洩漏機密");
// 檢查 read_records 的存取限制，三種情況都必須回傳錯誤：
//   1. 不存在的連線；2. 白名單以外的實體；3. limit 超過連線的 MaxRows。
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
// 未啟用 allowWrite 的連線必須拒絕非 SELECT／WITH 的指令（在連線資料庫之前就擋下）。
// 檢查 execute_sql 的唯讀防護：沒有 allowWrite 的連線必須拒絕 DROP／DELETE／UPDATE 與空白指令，
// 包括用區塊註解或單行註解把 SELECT 放在前面來偽裝的情況。這些都在連線資料庫之前就被擋下，所以不需要真實資料庫。
foreach (var query in new[] { "DROP TABLE x", "/* SELECT */ DELETE FROM x", "-- SELECT\nUPDATE x SET a = 1", "" })
{
    var result = await client.CallToolAsync("execute_sql",
        new Dictionary<string, object?> { ["connection"] = "erp", ["query"] = query }, cancellationToken: timeout.Token);
    if (result.IsError != true) throw new Exception($"唯讀連線未阻擋寫入指令：{query}");
}
Console.WriteLine("PASS: MCP 2026-07-28 stdio、工具探索、機密隔離與讀取限制、execute_sql 唯讀防護。");

