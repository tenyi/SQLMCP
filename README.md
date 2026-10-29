# SQL MCP

.NET 10 本機 SQL Server MCP server。使用官方 C# SDK 2.3.0 的 stdio transport，支援 MCP 2026-07-28。由 AI agent 啟動子程序，stdout 僅供 MCP，日誌寫入 stderr。不呼叫其他 MCP。

## 安裝

Windows x64，需要 .NET 10 SDK 建置，發佈後不需另外安裝 runtime：

~~~powershell
./scripts/install.ps1
~~~

預設安裝到 HOME 環境變數指定目錄的 .local/bin；未設定 HOME 時使用 Windows 使用者目錄。可用 -Destination 指定路徑。

## 設定

複製 sqlmcp.example.json 至使用者目錄的 .config/sqlmcp/sqlmcp.json，或啟動時傳入 --config 絕對路徑。設定檔不依目前工作目錄搜尋。

connections 可設定多組 SQL Server 連線。每組各自設定 connectionString、userName、password 或 passwordEnvironmentVariable；環境變數優先於明文 password。也可在 connectionString 使用 Integrated Security。不要將真實帳密提交到 Git，並限制設定檔的檔案存取權限。

entities 是允許公開的實體與欄位清單；只公開業務需要的欄位。使用只有 SELECT 權限的資料庫帳號。每個啟動此程序的 agent 都能存取該設定檔列出的所有連線；不同使用者的存取範圍應透過不同設定檔與作業系統權限隔離。stdio 不需要也不另設 HTTP/OAuth 或 MCP 登入帳密。

## Agent 設定

在支援 stdio 的 agent 中設定，例如（路徑請替換成實際絕對路徑，不依賴 agent 展開 HOME）：

~~~json
{
  "mcpServers": {
    "sql": {
      "command": "C:\\Users\\you\\.local\\bin\\sqlmcp.exe",
      "args": ["--config", "C:\\Users\\you\\.config\\sqlmcp\\sqlmcp.json"],
      "env": {
        "SQLMCP_ERP_PASSWORD": "<ERP 密碼>",
        "SQLMCP_CRM_PASSWORD": "<CRM 密碼>"
      }
    }
  }
}
~~~

工具：

- list_connections：只回傳連線名稱。
- list_entities(connection)：回傳允許的實體別名與欄位。
- read_records(connection, entity, limit=100)：讀取指定實體，最多 MaxRows 筆，預設 100；不保證排序。支援取消與查詢逾時（timeoutSeconds，預設 30）。

第一版僅提供受控讀取，不包含任意 SQL、篩選、分頁、寫入或預存程序。這些是產品範圍，並非 MCP 協定的限制。資料型別沿用 SqlClient 的 JSON 序列化表示；二進位資料為 Base64。

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
