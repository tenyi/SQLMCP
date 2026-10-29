// 本程式是一個以 stdio 為傳輸層的 MCP（Model Context Protocol）伺服器，讓 AI agent 能透過工具存取 SQL Server。
// 整體結構（由上而下）：
//   1. 頂層程式：解析命令列、從環境變數與命令列載入並驗證設定、啟動 MCP 主機。
//   2. Settings／Database／Entity：設定對應的資料模型。
//   3. SqlTools：對外公開的 MCP 工具（list_connections、list_entities、read_records、
//      list_databases、list_tables、describe_table、execute_sql）。
// 注意：stdout 專供 MCP 通訊使用，任何日誌或錯誤都只能寫到 stderr，否則會破壞協定。
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// 最外層 try：任何啟動階段的失敗（設定缺漏、設定值不合法）
// 都統一在底下的 catch 回報，並以結束碼 1 離開。
try
{
    // 所有設定都來自啟動本程序時的 MCP 設定（.mcp.json 的 env 與 args），不讀任何設定檔。
    // 這裡傳入 args，所以命令列參數（--Connections:名稱:ConnectionString=...）也會成為設定來源。
    // 建立 .NET Generic Host，用來承載 MCP 伺服器與相依性注入。
    var builder = Host.CreateApplicationBuilder(args);
    // 加入前綴為 SQLMCP_ 的環境變數作為設定來源，前綴會被去掉，雙底線 __ 代表階層。
    // 例如 SQLMCP_Connections__localhost__ConnectionString 對應設定鍵 Connections:localhost:ConnectionString；
    // 陣列用索引，例如 SQLMCP_Connections__erp__Entities__products__Columns__0=Id。
    builder.Configuration.AddEnvironmentVariables("SQLMCP_");
    // 把設定綁定到 Settings；完全沒有設定時得到空物件，交由 Validate 拒絕。
    var settings = builder.Configuration.Get<Settings>() ?? new Settings();
    // 驗證設定值（至少一個連線、連線字串、maxRows／timeoutSeconds 範圍、實體欄位等），
    // 有問題就在啟動時失敗，不要等到呼叫工具時才發現。
    settings.Validate();
    // 清除預設的日誌提供者，再把所有等級的日誌導向 stderr（LogToStandardErrorThreshold = Trace），
    // 確保 stdout 完全留給 MCP 協定使用。
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    // 把已驗證的設定註冊為單例，讓 SqlTools 的建構函式可以注入取得。
    builder.Services.AddSingleton(settings);
    // 註冊 MCP 伺服器：使用 stdio 傳輸（由 agent 啟動本程序並透過標準輸入／輸出溝通），
    // 並從 SqlTools 類別掃描要公開的工具。
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SqlTools>();
    // 啟動主機並持續執行，直到 stdin 關閉或程序被終止。
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception)
{
    // 原始解析例外可能包含機密，不直接輸出。
    // 例外訊息可能含有連線字串或密碼片段，所以這裡只輸出固定的提示文字，不輸出例外內容與堆疊。
    Console.Error.WriteLine("啟動失敗。請檢查 MCP 設定中的 SQLMCP_Connections__* 環境變數。");
    return 1;
}

// 設定的根物件，對應設定來源（環境變數／命令列）最外層的 Connections。
// Connections 的 key 是「連線名稱」（agent 呼叫工具時用的 connection 參數），value 是該連線的設定。
public sealed class Settings
{
    public Dictionary<string, Database> Connections { get; set; } = [];
    // 逐一檢查每個連線與其實體白名單；任何一項不合法就丟出 ArgumentException。
    // 訊息刻意保持簡短、不含設定內容，避免洩漏機密。
    public void Validate()
    {
        // 沒有任何連線通常代表 MCP 設定漏寫或環境變數名稱打錯，直接在啟動時失敗。
        if (Connections.Count == 0) throw new ArgumentException("未設定任何連線");
        foreach (var (name, db) in Connections)
        {
            // 連線層級檢查：名稱與連線字串不可為空；maxRows 限制 1~10000、timeoutSeconds 限制 1~300，
            // 避免一次回傳過多資料或查詢永不逾時。
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(db.ConnectionString)
                || db.MaxRows is < 1 or > 10000 || db.TimeoutSeconds is < 1 or > 300)
                throw new ArgumentException("無效連線設定");
            // 實體層級檢查：schema、table 不可為空，至少要有一個欄位，欄位名稱不可空白且不可重複（不分大小寫）。
            // 這些名稱之後會被組進 SQL，所以在啟動時就先把關。
            foreach (var entity in db.Entities.Values)
                if (string.IsNullOrWhiteSpace(entity.Schema) || string.IsNullOrWhiteSpace(entity.Table)
                    || entity.Columns.Length == 0 || entity.Columns.Any(string.IsNullOrWhiteSpace)
                    || entity.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entity.Columns.Length)
                    throw new ArgumentException("無效實體設定");
        }
    }
}
// 單一 SQL Server 連線的設定（對應 connections 底下的一個項目）。
public sealed class Database
{
    // 基本連線字串（伺服器、資料庫、加密選項等）。帳號密碼建議分開放在下面的欄位，不要寫進這裡。
    public string ConnectionString { get; set; } = "";
    // SQL 驗證的使用者名稱。有設定時會關閉 Integrated Security（Windows 驗證）。
    public string? UserName { get; set; }
    // 明文密碼。優先順序低於 PasswordEnvironmentVariable；不建議使用，也不要提交到 Git。
    public string? Password { get; set; }
    // 存放密碼的環境變數名稱（例如 SQLMCP_ERP_PASSWORD）。設定後優先於 Password。
    public string? PasswordEnvironmentVariable { get; set; }
    // 單次查詢最多回傳的資料列數（預設 100）。read_records 的 limit 與 execute_sql 每個結果集都受此限制。
    public int MaxRows { get; set; } = 100;
    // 單一指令的逾時秒數（預設 30），套用在 SqlCommand.CommandTimeout。
    public int TimeoutSeconds { get; set; } = 30;
    // 是否允許 execute_sql 執行寫入／DDL 指令；預設 false，僅允許 SELECT／WITH 且一律 rollback。
    public bool AllowWrite { get; set; } = false;
    // 允許 read_records 讀取的實體白名單：key 是對外公開的別名，value 是實際的資料表與欄位。
    // 空白代表 read_records 不能讀任何東西（但不影響 execute_sql 等其他工具）。
    public Dictionary<string, Entity> Entities { get; set; } = [];
    // 組出實際用來連線的字串：把 UserName／密碼套用到設定的 ConnectionString 上。
    // 每次呼叫都重新讀環境變數，密碼只在記憶體中短暫存在，也不會出現在 list_connections 的輸出。
    public string BuildConnectionString()
    {
        // 使用建構器而不是字串拼接，可正確處理跳脫與重複的鍵。
        var b = new SqlConnectionStringBuilder(ConnectionString);
        // 有指定使用者名稱時改用 SQL 驗證：關閉 Windows 整合驗證再設定帳號。
        if (UserName is not null) { b.IntegratedSecurity = false; b.UserID = UserName; }
        // 密碼來源優先順序：環境變數 > 設定中的明文密碼。環境變數不存在時丟出 InvalidOperationException，
        // 由工具轉成「連線設定或密碼環境變數無效」的通用訊息。
        if (PasswordEnvironmentVariable is not null)
            b.Password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable)
                ?? throw new InvalidOperationException();
        else if (Password is not null) b.Password = Password;
        return b.ConnectionString;
    }
}
// 可被 read_records 讀取的資料表描述。只有列在 Columns 的欄位會被選取，用來限制 agent 能看到的欄位。
public sealed class Entity
{
    // SQL Server 的 schema，預設 dbo。
    public string Schema { get; set; } = "dbo";
    // 資料表（或檢視表）名稱。
    public string Table { get; set; } = "";
    // 公開給 agent 的欄位名稱清單；順序即為查詢與回傳的欄位順序。
    public string[] Columns { get; set; } = [];
}
// MCP 工具集合。類別上的標記讓 SDK 掃描其中標有 [McpServerTool] 的方法並公開為工具；
// Settings 透過主要建構函式由相依性注入取得。
// 各工具的 Description 是給 AI agent 看的說明文字，修改時要同時考慮 agent 會如何理解它。
[McpServerToolType]
public sealed class SqlTools(Settings settings)
{
    // 列出設定中所有連線的名稱。只回傳名稱（Keys），不含連線字串、帳號或密碼。
    [McpServerTool(Name = "list_connections", ReadOnly = true), Description("列出可用 SQL Server 連線名稱，不包含機密。")]
    public string ListConnections() => JsonSerializer.Serialize(settings.Connections.Keys);

    // 列出指定連線的實體白名單（別名與欄位），agent 以此得知 read_records 可以讀什麼。
    [McpServerTool(Name = "list_entities", ReadOnly = true), Description("列出指定連線允許讀取的實體別名與公開欄位。")]
    public string ListEntities(string connection) => JsonSerializer.Serialize(GetDatabase(connection).Entities.Select(
        x => new { name = x.Key, columns = x.Value.Columns }));

    // 受控讀取：只能讀設定白名單內的實體與欄位，且筆數不超過 MaxRows。
    // 不接受任何自訂 SQL、篩選或排序，所以即使 agent 被誘導也無法讀到白名單外的資料。
    // cancellationToken 由 SDK 提供，用戶端取消請求時會中止查詢。
    [McpServerTool(Name = "read_records", ReadOnly = true), Description("讀取允許的實體與欄位。limit 不可超過連線的 MaxRows；結果不保證排序。")]
    public async Task<string> ReadRecords(string connection, string entity, int limit = 100, CancellationToken cancellationToken = default)
    {
        // 先驗證連線名稱存在，再驗證實體別名與 limit 範圍，全部通過才會真正連線資料庫。
        var db = GetDatabase(connection);
        if (!db.Entities.TryGetValue(entity, out var source)) throw new McpException("未知或未授權的實體。");
        if (limit < 1 || limit > db.MaxRows) throw new McpException($"limit 必須介於 1 與 {db.MaxRows}。");
        try
        {
            // 每次呼叫都建立新連線並在結束時釋放（連線池會重複使用底層連線）。
            await using var sql = new SqlConnection(db.BuildConnectionString());
            await sql.OpenAsync(cancellationToken);
            await using var cmd = sql.CreateCommand();
            // 設定指令逾時，避免長時間查詢佔住連線。
            cmd.CommandTimeout = db.TimeoutSeconds;
            // 識別字僅從設定白名單取得並跳脫；筆數使用參數。
            cmd.CommandText = $"SELECT TOP (@limit) {string.Join(",", source.Columns.Select(Quote))} FROM {Quote(source.Schema)}.{Quote(source.Table)}";
            cmd.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;
            // 以資料讀取器逐列讀取；欄位索引 i 對應 source.Columns[i]（查詢時依相同順序選取）。
            // DBNull 轉為 JSON null，其餘值沿用 SqlClient 的 GetValue（二進位資料序列化後為 Base64）。
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[source.Columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows);
        }
        // 資料庫錯誤只回傳通用訊息，不洩漏伺服器位置、帳號等細節。
        catch (SqlException) { throw new McpException("資料庫操作失敗，請檢查連線、實體設定與權限。"); }
        catch (InvalidOperationException) { throw new McpException("連線設定或密碼環境變數無效。"); }
    }
    // 列出整個 SQL Server 執行個體上的資料庫（sys.databases）。實際可見範圍取決於登入帳號的權限。
    // 傳給 RunQuery 的 database 為 null，表示沿用連線字串指定的資料庫。
    [McpServerTool(Name = "list_databases", ReadOnly = true), Description("列出指定連線所在 SQL Server 執行個體上的所有資料庫名稱。")]
    public async Task<string> ListDatabases(string connection, CancellationToken cancellationToken = default)
        => await RunQuery(GetDatabase(connection), null,
            "SELECT name FROM sys.databases ORDER BY name", readOnly: true, cancellationToken);

    // 列出指定資料庫（database 省略時為連線字串的資料庫）的資料表與檢視表。
    // 查詢固定寫死在程式內，database 只用來切換連線的 Initial Catalog，所以沒有 SQL 注入的疑慮。
    [McpServerTool(Name = "list_tables", ReadOnly = true), Description("列出指定連線（可指定 database，預設為連線字串的資料庫）中的使用者資料表與檢視表，含 schema 與欄位資訊。")]
    public async Task<string> ListTables(string connection, string? database = null, CancellationToken cancellationToken = default)
        => await RunQuery(GetDatabase(connection), database,
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME",
            readOnly: true, cancellationToken);

    // 查詢單一資料表的欄位結構。table 可寫 name（預設 schema 為 dbo）或 schema.name。
    [McpServerTool(Name = "describe_table", ReadOnly = true), Description("列出指定資料表的欄位名稱、型別、長度與是否可為 NULL。table 可寫成 name 或 schema.name。")]
    public async Task<string> DescribeTable(string connection, string table, string? database = null, CancellationToken cancellationToken = default)
    {
        // 拆成 schema 與 table；以參數傳入，不拼接進 SQL。
        // 拆出 schema 與資料表名稱；它們以 SQL 參數（@schema、@table）傳入，不會拼進 SQL 字串。
        // 限制：資料表名稱本身含 "." 時無法正確拆分。
        var parts = table.Split('.', 2);
        var (schema, name) = parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
        return await RunQuery(GetDatabase(connection), database,
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION",
            readOnly: true, cancellationToken,
            ("@schema", schema), ("@table", name));
    }

    // 執行任意 SQL，是本伺服器中權限最大的工具，所以標記 Destructive = true 提醒用戶端需要小心。
    // 是否允許寫入由連線的 AllowWrite 決定，預設只准 SELECT／WITH。
    [McpServerTool(Name = "execute_sql", Destructive = true), Description("在指定連線執行任意 SQL 指令（database 可切換資料庫）。連線未設定 allowWrite 時僅允許 SELECT／WITH 開頭的查詢。查詢結果最多回傳連線的 MaxRows 筆；多個結果集會分開回傳。")]
    public async Task<string> ExecuteSql(string connection, string query, string? database = null, CancellationToken cancellationToken = default)
    {
        var db = GetDatabase(connection);
        // 空白指令直接拒絕，不送到資料庫。
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query 不可為空。");
        // 唯讀連線：只接受 SELECT／WITH；真正的保障仍是資料庫帳號權限，這裡再加 rollback 作為第二道防線。
        // readOnly 同時控制兩件事：(1) 事前檢查指令開頭；(2) 執行時包在交易內並一律 rollback。
        var readOnly = !db.AllowWrite;
        if (readOnly && !IsReadOnlyQuery(query))
            throw new McpException("此連線未啟用 allowWrite，只允許 SELECT／WITH 查詢。");
        return await RunQuery(db, database, query, readOnly, cancellationToken);
    }

    // 判斷指令移除註解後，第一個關鍵字是否為 SELECT 或 WITH（沿用 mssql_mcp_server 的判斷方式並補上 WITH）。
    // 注意這個判斷只看「第一個關鍵字」，並不是完整的 SQL 解析：
    // 例如 "SELECT 1; DROP TABLE x" 會通過檢查，這時靠 rollback 與資料庫帳號權限擋下。
    // 因此連線務必使用最小權限的帳號。
    private static bool IsReadOnlyQuery(string query)
    {
        // 先移除區塊註解 /* ... */（Singleline 讓 . 可以比對換行），避免用註解把真正的指令藏在後面。
        var cleaned = System.Text.RegularExpressions.Regex.Replace(query, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        // 再移除單行註解 -- 到行尾。
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"--[^\r\n]*", "");
        // 以空白字元切開並取第一個字；若指令只剩註解或空白則視為空字串（不通過）。
        var first = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase);
    }

    // 共用的執行流程：開連線（可覆寫資料庫）、執行、把每個結果集轉成 JSON。
    // readOnly 為 true 時包在交易內並一律 rollback；結果集每個最多讀 MaxRows 筆，超過則標示 truncated。
    // 所有查詢工具共用的執行流程：連線 → 執行 → 把每個結果集轉成 JSON。
    // 參數說明：
    //   db         連線設定。
    //   database   要切換的資料庫，null 表示沿用連線字串。
    //   sqlText    要執行的 SQL。
    //   readOnly   true 時包在交易中並一律 rollback。
    //   parameters SQL 參數（名稱, 值），用來避免字串拼接。
    // 回傳格式：{ "resultSets": [ { "rows": [...], "truncated": bool } ... ], "rowsAffected": int? }
    private static async Task<string> RunQuery(Database db, string? database, string sqlText, bool readOnly,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        try
        {
            // 從已套用帳密的連線字串建立建構器，才能在其上覆寫資料庫。
            var builder = new SqlConnectionStringBuilder(db.BuildConnectionString());
            // 資料庫名稱經由連線字串建構器設定，會被正確跳脫，不會被當成額外的連線參數。
            if (!string.IsNullOrWhiteSpace(database)) builder.InitialCatalog = database;
            await using var sql = new SqlConnection(builder.ConnectionString);
            await sql.OpenAsync(cancellationToken);
            // 唯讀時開啟交易（結束時若未提交，Dispose 也會自動 rollback）；寫入模式則不使用交易，
            // 指令各自自動提交，這與原本 Python 版 mssql_mcp_server 的行為一致。
            await using var tx = readOnly ? (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken) : null;
            await using var cmd = sql.CreateCommand();
            // 使用交易時，指令必須明確指定該交易，否則 SqlClient 會報錯。
            cmd.Transaction = tx;
            cmd.CommandTimeout = db.TimeoutSeconds;
            cmd.CommandText = sqlText;
            // 加入 SQL 參數（可為 0 個）。
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            // 一個批次可能包含多個陳述式而產生多個結果集（例如 "SELECT 1; SELECT 2"），所以用 do-while 依序讀完。
            var resultSets = new List<object>();
            do
            {
                // FieldCount 為 0 代表此段是 INSERT／UPDATE／DDL 等不回傳資料列的陳述式。
                if (reader.FieldCount == 0) continue;
                var rows = new List<Dictionary<string, object?>>();
                // 每個結果集各自限制 MaxRows 筆；超過就停止讀取並標示 truncated，避免一次載入過大的結果。
                var truncated = false;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count >= db.MaxRows) { truncated = true; break; }
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        // 欄位名稱取自結果集本身（因為 execute_sql 的欄位事先未知）。注意：若 SQL 有重複的欄位別名，後者會覆蓋前者。
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                resultSets.Add(new { rows, truncated });
            } while (await reader.NextResultAsync(cancellationToken));
            await reader.CloseAsync(); // RecordsAffected 要在 reader 結束後才有值。
            // RecordsAffected 是整個批次中 INSERT／UPDATE／DELETE 影響的列數總和；沒有這類陳述式時為 -1，轉成 null。
            // 單純的 SELECT 也可能回報 -1。
            var affected = reader.RecordsAffected;
            // 唯讀模式明確 rollback，確保即使指令偷偷寫入也不會留下變更。
            if (tx is not null) await tx.RollbackAsync(cancellationToken); // 唯讀模式不保留任何變更
            return JsonSerializer.Serialize(new { resultSets, rowsAffected = affected < 0 ? (int?)null : affected });
        }
        // 執行 SQL 的錯誤訊息（語法錯誤、物件不存在等）對使用者除錯是必要的，所以此處回傳訊息本身。
        // 與 read_records 不同，這裡刻意回傳錯誤編號與訊息，否則 agent 無法修正自己寫錯的 SQL。
        // 代價是訊息可能含伺服器端細節，所以請避免讓不受信任的對象使用這支程式。
        catch (SqlException ex) { throw new McpException($"SQL 錯誤 {ex.Number}：{ex.Message}"); }
        catch (InvalidOperationException) { throw new McpException("連線設定或密碼環境變數無效。"); }
    }

    // 依連線名稱取得設定；找不到時回報「未知或未授權」，不區分兩者以免洩漏哪些名稱存在。
    private Database GetDatabase(string name) => settings.Connections.TryGetValue(name, out var db)
        ? db : throw new McpException("未知或未授權的連線。");
    // 把識別字（欄位、資料表、schema）包成 [名稱] 並把名稱中的 ] 轉成 ]]，防止識別字注入。
    // 僅用於來自設定白名單的名稱，不用於使用者輸入。
    private static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
}
