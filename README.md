# SQL MCP

.NET 10 SQL Server MCP 伺服器，提供資料庫結構探索、白名單資料讀取與 SQL 執行。使用官方 C# SDK 2.3.0 的 stdio transport，支援 MCP 2026-07-28。

由 AI agent 啟動子程序，透過連線字串存取 SQL Server。stdout 僅供 MCP 協定使用，日誌寫入 stderr。

## 安裝

Windows x64，需要 .NET 10 SDK 建置，發佈後不需另外安裝 runtime：

~~~powershell
./scripts/install.ps1
~~~

預設安裝到 HOME 環境變數指定目錄的 .local/bin；未設定 HOME 時使用 Windows 使用者目錄。可用 -Destination 指定路徑。

~~~powershell
./scripts/install.ps1 -Destination "$env:USERPROFILE\.local\bin"
~~~

更新程式時需重新執行安裝，再重新啟動 agent 的 MCP 連線。若執行檔正被使用，先停止使用它的 MCP 子程序再安裝。

## 設定

建議將設定寫在 agent 的 MCP 設定（例如 .mcp.json）中，以 `SQLMCP_` 前綴環境變數傳入，雙底線 `__` 代表階層。程式也會載入 .NET Generic Host 的預設設定來源，包括 appsettings 檔案、未加前綴的環境變數及命令列參數；`SQLMCP_` 來源後加入，遇到相同設定鍵時優先：

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

啟動時會驗證連線字串語法；格式無效即以結束碼 1 離開，不輸出連線字串或密碼。主機執行期間的故障會另行回報，不會一律歸因於設定。

只公開業務需要的欄位，並使用最小權限的資料庫帳號。每個啟動此程序的 agent 都能存取設定中列出的所有連線。.mcp.json 若會提交到 Git，不要直接寫入密碼；Claude Code 的 .mcp.json 支援 `${變數}` 展開，可寫成 `"SQLMCP_Connections__erp__Password": "${ERP_PASSWORD}"`。stdio 不需要也不另設 HTTP/OAuth 或 MCP 登入帳密。

## Agent 設定

儲存庫已包含 `.mcp.json`，供 Claude Code 以 `${USERPROFILE}` 展開 Windows 使用者目錄。此本機開發設定使用 Windows 驗證連到 localhost/master，並明確啟用 `AllowWrite=true`；實際寫入權限沿用啟動 agent 的 Windows 帳號。

以下範例包含可寫入的本機連線與預設唯讀的 ERP 連線。請將執行檔路徑與資料庫設定改為實際值；其他 agent 的環境變數展開方式依其設定格式為準。

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

## 工具

- list_connections：只回傳連線名稱。
- list_entities(connection)：回傳允許的實體別名與欄位。
- read_records(connection, entity, limit?)：讀取指定實體，最多 MaxRows 筆，limit 省略時為 MaxRows；不保證排序。支援取消與查詢逾時（timeoutSeconds，預設 30）。
- list_databases(connection)：列出該 SQL Server 執行個體上的所有資料庫。
- list_tables(connection, database?)：列出指定資料庫的資料表與檢視表，回傳 schema、名稱與類型。欄位資訊請用 describe_table。
- describe_table(connection, table, database?)：列出資料表欄位、型別與是否可為 NULL。table 支援 name（預設 dbo）、schema.name、[schema].[name]，包含方括號內的句點與 `]]` 跳脫。
- execute_sql(connection, query, database?)：執行任意 SQL（可用 database 切換資料庫），多個結果集會分開回傳，每個結果集最多 MaxRows 筆（超過會標示 truncated）。`rolledBack=true` 表示交易已回滾，此時 `rowsAffected=null`；寫入模式為 `rolledBack=false`，`rowsAffected` 為影響筆數（無筆數時為 null）。SQL 錯誤會回傳錯誤編號與訊息。

前置檢查阻擋查詢時，工具回傳 MCP `isError=true`，訊息明確包含「查詢已阻擋，未執行 SQL」及原因。agent 應向使用者回報原因，不可靜默改用寫入連線或提高權限重試。

## 唯讀與寫入模式

`AllowWrite` 只控制 `execute_sql`，以每個連線的設定為準：

| 設定 | SQL 前置檢查 | 執行方式 |
| --- | --- | --- |
| `false`（預設） | 只接受 SELECT／WITH 開頭，禁止 COMMIT、ROLLBACK、SAVE、EXEC、EXECUTE | 開啟交易，成功讀取所有結果後回滾 |
| `true` | 僅檢查 query 不可為空，不套用唯讀檢查 | 不額外包交易；未自行控制交易時，依 SQL Server 自動提交規則執行 |

唯讀檢查支援前導分號、左括號與巢狀註解，例如 `;WITH ...`、`(SELECT ...)`。字串常值與引號識別字中的禁止關鍵字不會觸發阻擋；`@commit`、`@rollback` 等變數，以及 `#commit` 等暫存表名稱，也不會被誤認為交易控制指令。

預存程序目前須使用 `AllowWrite=true` 的連線執行。唯讀模式同時阻擋明確的 `EXEC`／`EXECUTE` 與批次首句省略 EXEC 的程序呼叫，例如 `[dbo].[DoWork]; SELECT 1`。目前未實作讀取程序定義並判定其是否唯讀的功能。

此檢查不是完整的 T-SQL 語法分析器。像 `SELECT 1; DELETE FROM ...` 仍可能通過前置檢查，再由交易回滾；外部副作用也不能單靠回滾防止。因此實際授權仍須由資料庫帳號權限限制，`AllowWrite=false` 不會將帳號本身改成唯讀。需要寫入時才啟用 `AllowWrite=true`，並搭配最小權限帳號。

`list_databases`、`list_tables`、`describe_table` 不受 Entities 白名單限制，可查詢帳號有權限檢視的資料庫結構；`read_records` 無論 AllowWrite 為何，都受實體與欄位白名單限制。

`read_records` 僅提供受控讀取，不含篩選與分頁；自訂查詢請用 `execute_sql`，需要保留寫入結果或執行預存程序時須啟用 `AllowWrite=true`。資料型別沿用 SqlClient 的 JSON 序列化表示；二進位資料為 Base64。

## 驗證

在儲存庫根目錄執行：

~~~powershell
dotnet build src/SqlMcp
dotnet run --project tests/SqlMcp.Smoke
~~~

需要執行 SQL Server 整合測試時，設定下列環境變數。以下使用 Windows 驗證連線到 `localhost` 的 `Test` 資料庫；目標資料庫須有至少一個可見、可用一般 `schema.table` 名稱表示的資料表或檢視表，且帳號須能執行測試使用的 `sys.sp_executesql`：

~~~powershell
dotnet build src/SqlMcp
if ($LASTEXITCODE -ne 0) { throw '建置失敗' }
$previousConnection = $env:SQLMCP_SMOKE_CONNECTION_STRING
try {
    $env:SQLMCP_SMOKE_CONNECTION_STRING = 'Server=localhost;Database=Test;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5'
    dotnet run --project tests/SqlMcp.Smoke
    if ($LASTEXITCODE -ne 0) { throw '整合測試失敗' }
}
finally {
    $env:SQLMCP_SMOKE_CONNECTION_STRING = $previousConnection
}
~~~

整合測試使用同一連線字串建立唯讀與寫入兩組測試連線，驗證：

- 一般名稱與方括號名稱查出的欄位一致。
- 唯讀回滾與寫入模式的 `rolledBack`、`rowsAffected` 及多結果集回報。
- 省略 EXEC 的程序呼叫在唯讀模式遭阻擋，在寫入模式可執行並回傳正確結果。
- `@commit`、`@rollback`、`@save`、`@exec`、`@execute` 等變數可實際執行並回傳正確結果。

測試只讀取既有結構、操作區域資料表變數，以及呼叫只回傳常數的 `sys.sp_executesql`，不留下永久資料變更。上述整合測試已於 2026-10-30 在 `localhost/Test` 通過。未設定環境變數時會明確印出 SKIP；煙霧測試通過不代表已執行 SQL Server 整合測試。

煙霧測試透過真正的 stdio 子程序，以固定 2026-07-28 版本連線，檢查工具探索、連線清單不含機密、未授權實體與筆數上限、預設 limit、唯讀查詢阻擋回報、資料表名稱解析，以及無效連線字串在啟動時失敗且不洩漏機密。預設不需真實資料庫；offline 連線會嘗試連到不存在的伺服器，以區分前置阻擋與連線失敗。

## 開發流程

主幹名稱 main，採 GitHub Flow：從 main 建立短期工作分支，驗證後透過 Pull Request 合併。

規範來源：[MCP 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)

SDK：[Model Context Protocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
