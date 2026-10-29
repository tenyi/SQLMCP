# SQL MCP

.NET 10 本機 SQL Server MCP server。使用官方 C# SDK 2.3.0 的 stdio transport，支援 MCP 2026-07-28。由 AI agent 啟動子程序，stdout 僅供 MCP，日誌寫入 stderr。不呼叫其他 MCP。

## 安裝

Windows x64，需要 .NET 10 SDK 建置，發佈後不需另外安裝 runtime：

~~~powershell
./scripts/install.ps1
~~~

預設安裝到 HOME 環境變數指定目錄的 .local/bin；未設定 HOME 時使用 Windows 使用者目錄。可用 -Destination 指定路徑。

## 設定

所有設定都寫在 agent 的 MCP 設定（例如 .mcp.json）裡，以環境變數傳入，不另外讀取設定檔。環境變數一律以 `SQLMCP_` 開頭，雙底線 `__` 代表階層：

| 環境變數 | 說明 |
| --- | --- |
| `SQLMCP_Connections__<名稱>__ConnectionString` | 必填。SQL Server 連線字串，可使用 Integrated Security |
| `SQLMCP_Connections__<名稱>__UserName` | SQL 驗證帳號；設定後改用 SQL 驗證 |
| `SQLMCP_Connections__<名稱>__Password` | 密碼 |
| `SQLMCP_Connections__<名稱>__PasswordEnvironmentVariable` | 改從另一個環境變數讀取密碼，優先於 Password |
| `SQLMCP_Connections__<名稱>__MaxRows` | 每個結果集最多回傳筆數，1~10000，預設 100 |
| `SQLMCP_Connections__<名稱>__TimeoutSeconds` | 指令逾時秒數，1~300，預設 30 |
| `SQLMCP_Connections__<名稱>__AllowWrite` | `true` 時 execute_sql 可執行寫入與 DDL，預設 `false` |
| `SQLMCP_Connections__<名稱>__Entities__<別名>__Schema` / `__Table` / `__Columns__0`、`__Columns__1`… | read_records 的實體白名單 |

`<名稱>` 就是工具的 connection 參數，可設定多組連線。至少要有一組連線，否則程序啟動即失敗。也可改用命令列參數傳入同樣的設定，例如 `--Connections:localhost:ConnectionString=...`。

只公開業務需要的欄位，並使用最小權限的資料庫帳號。每個啟動此程序的 agent 都能存取設定中列出的所有連線。.mcp.json 若會提交到 Git，不要直接寫入密碼；Claude Code 的 .mcp.json 支援 `${變數}` 展開，可寫成 `"SQLMCP_Connections__erp__Password": "${ERP_PASSWORD}"`。stdio 不需要也不另設 HTTP/OAuth 或 MCP 登入帳密。

## Agent 設定

在支援 stdio 的 agent 中設定，例如（路徑請替換成實際絕對路徑，不依賴 agent 展開 HOME）：

~~~json
{
  "mcpServers": {
    "sql": {
      "type": "stdio",
      "command": "C:\\Users\\you\\.local\\bin\\sqlmcp.exe",
      "args": [],
      "env": {
        "SQLMCP_Connections__localhost__ConnectionString": "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True",
        "SQLMCP_Connections__localhost__AllowWrite": "true",
        "SQLMCP_Connections__erp__ConnectionString": "Server=erp-db;Database=ERP;Encrypt=True",
        "SQLMCP_Connections__erp__UserName": "erp_reader",
        "SQLMCP_Connections__erp__Password": "${ERP_PASSWORD}",
        "SQLMCP_Connections__erp__Entities__products__Table": "Products",
        "SQLMCP_Connections__erp__Entities__products__Columns__0": "Id",
        "SQLMCP_Connections__erp__Entities__products__Columns__1": "Name"
      }
    }
  }
}
~~~

工具：

- list_connections：只回傳連線名稱。
- list_entities(connection)：回傳允許的實體別名與欄位。
- read_records(connection, entity, limit=100)：讀取指定實體，最多 MaxRows 筆，預設 100；不保證排序。支援取消與查詢逾時（timeoutSeconds，預設 30）。
- list_databases(connection)：列出該 SQL Server 執行個體上的所有資料庫。
- list_tables(connection, database?)：列出指定資料庫的資料表與檢視表。
- describe_table(connection, table, database?)：列出資料表欄位、型別與是否可為 NULL。
- execute_sql(connection, query, database?)：執行任意 SQL（可用 database 切換資料庫），多個結果集會分開回傳，每個結果集最多 MaxRows 筆（超過會標示 truncated），並回傳 rowsAffected。SQL 錯誤會回傳錯誤編號與訊息。

execute_sql 預設唯讀：連線未設定 `"allowWrite": true` 時，只接受 SELECT／WITH 開頭的指令，且在交易內執行後一律 rollback。這只是第二道防線，真正的保障仍是資料庫帳號權限；需要 INSERT／UPDATE／DDL 時才為該連線開啟 allowWrite，並搭配最小權限帳號。list_databases、list_tables、describe_table 不受 entities 白名單限制，會看到帳號有權限檢視的所有資料庫與資料表。

read_records 僅提供受控讀取（不含篩選、分頁）；任意 SQL、寫入與預存程序請用 execute_sql（需 allowWrite）。資料型別沿用 SqlClient 的 JSON 序列化表示；二進位資料為 Base64。

## 驗證

在儲存庫根目錄執行：

~~~powershell
dotnet build src/SqlMcp
dotnet run --project tests/SqlMcp.Smoke
~~~

煙霧測試透過真正的 stdio 子程序，以固定 2026-07-28 版本連線，檢查工具探索、連線清單不含機密，以及未授權實體與筆數上限。尚未對實際 SQL Server 驗證登入與讀取。

## 開發流程

主幹名稱 main，採 GitHub Flow：從 main 建立短期工作分支，驗證後透過 Pull Request 合併。

規範來源：https://modelcontextprotocol.io/specification/2026-07-28
SDK：https://github.com/modelcontextprotocol/csharp-sdk
