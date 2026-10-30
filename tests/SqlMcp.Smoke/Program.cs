// 煙霧測試：以真正的 stdio 子程序啟動 SqlMcp；設定連線字串時另執行 SQL Server 整合測試。
// 執行方式：先 dotnet build src/SqlMcp，再於儲存庫根目錄執行 dotnet run --project tests/SqlMcp.Smoke。
// 任何檢查失敗都會丟出例外使程序以非 0 結束；整合測試未設定時會印出 SKIP。
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Diagnostics;

// 整個測試最多 30 秒，避免子程序卡住時測試永遠不結束。
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
// 以 dotnet 啟動建置好的 SqlMcp.dll；設定全部經由環境變數傳入，模擬 .mcp.json 的 env 區塊。
// erp／crm 驗證前置限制，offline 驗證通過限制後的連線失敗。
var transportOptions = new StdioClientTransportOptions
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
        // offline：指向一定拒絕連線的位址（127.0.0.1 的 port 1）且連線逾時 1 秒，用來驗證「通過前置檢查後才會失敗於連線」的情況。
        // MaxRows 刻意小於 100，驗證 read_records 不帶 limit 時不會被舊的寫死預設值擋下。
        ["SQLMCP_Connections__offline__ConnectionString"] = "Server=127.0.0.1,1;Database=ERP;Connect Timeout=1;Encrypt=True",
        ["SQLMCP_Connections__offline__UserName"] = "offline_reader",
        ["SQLMCP_Connections__offline__MaxRows"] = "50",
        ["SQLMCP_Connections__offline__Entities__products__Table"] = "Products",
        ["SQLMCP_Connections__offline__Entities__products__Columns__0"] = "Id",
    }
};
// 選擇性整合測試：查詢既有結構、操作區域資料表變數及呼叫回傳常數的系統程序。
var liveConnectionString = Environment.GetEnvironmentVariable("SQLMCP_SMOKE_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(liveConnectionString))
{
    transportOptions.EnvironmentVariables!["SQLMCP_Connections__live__ConnectionString"] = liveConnectionString;
    transportOptions.EnvironmentVariables["SQLMCP_Connections__live_write__ConnectionString"] = liveConnectionString;
    transportOptions.EnvironmentVariables["SQLMCP_Connections__live_write__AllowWrite"] = "true";
}
var transport = new StdioClientTransport(transportOptions);
// 取出工具結果的文字內容，用來比對錯誤訊息，區分「被前置檢查擋下」與「連線資料庫時失敗」。
static string TextOf(CallToolResult result) => string.Join("", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
// 用固定的協定版本 2026-07-28 連線，確認伺服器支援該版本。
await using var client = await McpClient.CreateAsync(transport,
    new McpClientOptions { ProtocolVersion = "2026-07-28" }, cancellationToken: timeout.Token);
// 檢查工具探索：所有預期的工具都必須存在（新增或改名工具時要同步更新這份清單）。
var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
if (!new[] { "list_connections", "list_entities", "read_records", "list_databases", "list_tables", "describe_table", "execute_sql" }.All(n => tools.Any(t => t.Name == n)))
    throw new Exception("工具清單不完整");
if (!tools.Single(t => t.Name == "list_tables").Description!.Contains("欄位資訊請用 describe_table")
    || !tools.Single(t => t.Name == "execute_sql").Description!.Contains("向使用者回報"))
    throw new Exception("工具說明未正確指引欄位查詢或阻擋回報");
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
    if (result.IsError != true || !TextOf(result).Contains("查詢已阻擋，未執行 SQL"))
        throw new Exception("未明確回報無效或未授權操作已阻擋、未執行");
}
// read_records 不帶 limit：應以連線的 MaxRows（50）為上限通過檢查，進到連線階段才因伺服器不存在而失敗，
// 且必須回報資料庫錯誤，而不是 limit 範圍錯誤。
{
    var result = await client.CallToolAsync("read_records",
        new Dictionary<string, object?> { ["connection"] = "offline", ["entity"] = "products" }, cancellationToken: timeout.Token);
    if (result.IsError != true || !TextOf(result).Contains("資料庫操作失敗"))
        throw new Exception($"read_records 未以 MaxRows 作為預設 limit：{TextOf(result)}");
}
// 檢查 execute_sql 的唯讀防護：沒有 allowWrite 的連線必須拒絕 DROP／DELETE／UPDATE 與空白指令，
// 包括用區塊註解（含巢狀註解）或單行註解把 SELECT 放在前面來偽裝的情況，
// 以及以 SELECT 開頭、但用 COMMIT／ROLLBACK 結束交易或用 EXEC 執行動態 SQL 來逃出 rollback 的批次。
// 這些都在連線資料庫之前就被擋下，所以不需要真實資料庫；比對錯誤訊息含 allowWrite（空白指令則是「不可為空」），
// 確認是被前置檢查擋下而非連線失敗。
foreach (var query in new[]
{
    "DROP TABLE x", "/* SELECT */ DELETE FROM x", "-- SELECT\nUPDATE x SET a = 1", "",
    "/* /* */ SELECT */ DELETE FROM x",
    "SELECT 1; COMMIT; DELETE FROM x",
    "SELECT 1; ROLLBACK; DELETE FROM x",
    "SELECT 1; SAVE TRANSACTION point",
    "SELECT 1; EXEC('DELETE FROM x')",
    "WITH a AS (SELECT 1 AS n) SELECT n FROM a; execute sp_who",
    // 預存程序在批次首句可省略 EXEC；後面的 SELECT 不可讓它通過檢查。
    "[dbo].[DoWork]; SELECT 1",
    "\"dbo\".\"DoWork\"; SELECT 1",
    "/* comment */ [dbo].[DoWork] @select = 1",
    "[SELECT]; SELECT 1",
    "SELECT$DoWork; SELECT 1",
    // 允許變數名稱不代表可以漏掉真正的交易控制。
    "SELECT 1; DECLARE @commit int = 1; COMMIT;",
    "SELECT 1; DECLARE @rollback int = 1; ROLLBACK;",
})
{
    var result = await client.CallToolAsync("execute_sql",
        new Dictionary<string, object?> { ["connection"] = "erp", ["query"] = query }, cancellationToken: timeout.Token);
    if (result.IsError != true || !TextOf(result).Contains("查詢已阻擋，未執行 SQL")
        || !(TextOf(result).Contains("allowWrite") || TextOf(result).Contains("不可為空")))
        throw new Exception($"唯讀連線未阻擋寫入指令：{query}");
    if (query.Length > 0 && !TextOf(result).Contains("請向使用者回報原因"))
        throw new Exception("唯讀阻擋未提供 agent 回報指引");
}
// 反向檢查：關鍵字只出現在字串常值、引號識別字或註解內時不可誤擋；也接受 ;WITH 開頭。
// 這些查詢會通過前置檢查、進到連線階段才失敗，所以錯誤訊息不可含 allowWrite。
foreach (var query in new[]
{
    "SELECT 'commit' AS [exec], \"rollback\" FROM t -- EXEC x",
    "SELECT '--' + 'x' /* COMMIT /* nested */ */",
    ";WITH a AS (SELECT 1 AS n) SELECT n FROM a",
    "SELECT 1; DECLARE @commit int = 1; SELECT @commit;",
    "SELECT 1; DECLARE @rollback int = 1, @save int = 2, @exec int = 3, @execute int = 4; SELECT @rollback, @save, @exec, @execute;",
    "SELECT 1 AS n INTO #commit; SELECT n FROM #commit;",
})
{
    var result = await client.CallToolAsync("execute_sql",
        new Dictionary<string, object?> { ["connection"] = "offline", ["query"] = query }, cancellationToken: timeout.Token);
    if (result.IsError != true || !TextOf(result).Contains("SQL 錯誤"))
        throw new Exception($"合法查詢未通過檢查進入連線階段：{query}：{TextOf(result)}");
}
// 不完整括號或超過兩段的名稱不得默默查詢錯誤物件。
foreach (var table in new[] { "[dbo].[Orders", "dbo.Orders.extra", "", "[]", "dbo..Orders" })
{
    var result = await client.CallToolAsync("describe_table",
        new Dictionary<string, object?> { ["connection"] = "offline", ["table"] = table }, cancellationToken: timeout.Token);
    if (result.IsError != true || !TextOf(result).Contains("查詢已阻擋，未執行 SQL：table"))
        throw new Exception($"未阻擋無效資料表名稱：{table}");
}
foreach (var table in new[] { "Orders", "dbo.Orders", "[dbo].[Orders]", "[schema.with.dot].[name]]with.dot]" })
{
    var result = await client.CallToolAsync("describe_table",
        new Dictionary<string, object?> { ["connection"] = "offline", ["table"] = table }, cancellationToken: timeout.Token);
    if (result.IsError != true || !TextOf(result).Contains("SQL 錯誤"))
        throw new Exception($"合法資料表名稱未通過解析：{table}：{TextOf(result)}");
}

// 以獨立程序驗證啟動失敗，不讓解析例外中的密碼或設定內容流入 stderr／stdout。
foreach (var connectionString in new[] { "Server=localhost;Password=secret-do-not-print;InvalidKeyword=value", "Server=\"unterminated" })
{
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    start.ArgumentList.Add(Path.GetFullPath("src/SqlMcp/bin/Debug/net10.0/SqlMcp.dll"));
    foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("SQLMCP_", StringComparison.OrdinalIgnoreCase)).ToArray())
        start.Environment.Remove(key);
    start.Environment["SQLMCP_Connections__invalid__ConnectionString"] = connectionString;
    using var process = Process.Start(start)!;
    process.StandardInput.Close();
    var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
    try { await process.WaitForExitAsync(timeout.Token); }
    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 1 || stdout.Length != 0
        || stderr.Trim() != "啟動失敗。請檢查 MCP 設定中的 SQLMCP_Connections__* 環境變數。")
        throw new Exception("無效連線字串未在啟動時安全地失敗");
}

if (!string.IsNullOrWhiteSpace(liveConnectionString))
{
    var tables = await client.CallToolAsync("list_tables",
        new Dictionary<string, object?> { ["connection"] = "live" }, cancellationToken: timeout.Token);
    if (tables.IsError == true) throw new Exception($"整合測試無法列出資料表：{TextOf(tables)}");
    using var tableResult = JsonDocument.Parse(TextOf(tables));
    var firstTable = tableResult.RootElement.GetProperty("resultSets")[0].GetProperty("rows").EnumerateArray()
        .First(r => !r.GetProperty("TABLE_SCHEMA").GetString()!.Contains('.') && !r.GetProperty("TABLE_NAME").GetString()!.Contains('.'));
    var schema = firstTable.GetProperty("TABLE_SCHEMA").GetString()!;
    var name = firstTable.GetProperty("TABLE_NAME").GetString()!;
    var plain = await client.CallToolAsync("describe_table",
        new Dictionary<string, object?> { ["connection"] = "live", ["table"] = $"{schema}.{name}" }, cancellationToken: timeout.Token);
    var bracketed = await client.CallToolAsync("describe_table",
        new Dictionary<string, object?> { ["connection"] = "live", ["table"] = $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]" }, cancellationToken: timeout.Token);
    using var columns = JsonDocument.Parse(TextOf(plain));
    if (plain.IsError == true || bracketed.IsError == true || TextOf(plain) != TextOf(bracketed)
        || columns.RootElement.GetProperty("resultSets")[0].GetProperty("rows").GetArrayLength() == 0)
        throw new Exception("方括號名稱查出的欄位與一般名稱不一致");

    foreach (var connection in new[] { "live", "live_write" })
    {
        var result = await client.CallToolAsync("execute_sql", new Dictionary<string, object?>
        {
            ["connection"] = connection,
            ["query"] = "SELECT 1 AS n; DECLARE @items TABLE (id int); INSERT INTO @items VALUES (1); SELECT id FROM @items;"
        }, cancellationToken: timeout.Token);
        if (result.IsError == true) throw new Exception($"整合查詢失敗：{TextOf(result)}");
        using var json = JsonDocument.Parse(TextOf(result));
        var root = json.RootElement;
        var readOnly = connection == "live";
        if (root.GetProperty("rolledBack").GetBoolean() != readOnly
            || (readOnly ? root.GetProperty("rowsAffected").ValueKind != JsonValueKind.Null : root.GetProperty("rowsAffected").GetInt32() != 1)
            || root.GetProperty("resultSets").GetArrayLength() != 2)
            throw new Exception("回滾狀態、影響筆數或多結果集回報錯誤");
    }
    // 用只回傳常數的系統程序驗證 P1：同一批次在唯讀模式必須阻擋，寫入模式則可正常呼叫。
    const string implicitProcedure = "[sys].[sp_executesql] N'SELECT 42 AS n'; SELECT 1 AS tail;";
    var blockedProcedure = await client.CallToolAsync("execute_sql", new Dictionary<string, object?>
    {
        ["connection"] = "live", ["query"] = implicitProcedure
    }, cancellationToken: timeout.Token);
    if (blockedProcedure.IsError != true || !TextOf(blockedProcedure).Contains("查詢已阻擋，未執行 SQL"))
        throw new Exception("唯讀連線未阻擋隱含預存程序呼叫");
    var allowedProcedure = await client.CallToolAsync("execute_sql", new Dictionary<string, object?>
    {
        ["connection"] = "live_write", ["query"] = implicitProcedure
    }, cancellationToken: timeout.Token);
    if (allowedProcedure.IsError == true) throw new Exception($"寫入連線無法呼叫預存程序：{TextOf(allowedProcedure)}");
    using var procedureJson = JsonDocument.Parse(TextOf(allowedProcedure));
    var procedureSets = procedureJson.RootElement.GetProperty("resultSets");
    if (procedureSets.GetArrayLength() != 2
        || procedureSets[0].GetProperty("rows")[0].GetProperty("n").GetInt32() != 42
        || procedureSets[1].GetProperty("rows")[0].GetProperty("tail").GetInt32() != 1
        || procedureJson.RootElement.GetProperty("rolledBack").GetBoolean())
        throw new Exception("隱含預存程序的結果或寫入模式狀態不正確");

    // P2 必須實際執行成功，不能只驗證通過前置檢查；同時涵蓋所有禁止關鍵字的變數名稱。
    var variables = await client.CallToolAsync("execute_sql", new Dictionary<string, object?>
    {
        ["connection"] = "live",
        ["query"] = "SELECT 1 AS initial; DECLARE @commit int = 1, @rollback int = 2, @save int = 3, @exec int = 4, @execute int = 5; SELECT @commit + @rollback + @save + @exec + @execute AS total;"
    }, cancellationToken: timeout.Token);
    if (variables.IsError == true) throw new Exception($"關鍵字變數查詢失敗：{TextOf(variables)}");
    using var variableJson = JsonDocument.Parse(TextOf(variables));
    if (variableJson.RootElement.GetProperty("resultSets")[1].GetProperty("rows")[0].GetProperty("total").GetInt32() != 15
        || !variableJson.RootElement.GetProperty("rolledBack").GetBoolean()
        || variableJson.RootElement.GetProperty("rowsAffected").ValueKind != JsonValueKind.Null)
        throw new Exception("關鍵字變數查詢的結果或唯讀回滾狀態不正確");
    Console.WriteLine("PASS: SQL Server 整合測試，方括號欄位查詢、回滾與寫入模式的影響筆數、多結果集、P1 隱含程序呼叫與 P2 關鍵字變數。");
}
else Console.WriteLine("SKIP: 未設定 SQLMCP_SMOKE_CONNECTION_STRING，略過 SQL Server 整合測試。");
Console.WriteLine("PASS: MCP 2026-07-28 stdio、工具探索、機密隔離與讀取限制、預設 limit、唯讀阻擋回報、資料表名稱與啟動設定驗證。");

