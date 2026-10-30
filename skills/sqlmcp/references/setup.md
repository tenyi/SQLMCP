# 安裝與連接 SQLMCP

SQLMCP 的可用狀態包含三個彼此獨立的部分：

1. `sqlmcp.exe`：實際提供工具的 stdio MCP server。
2. Agent MCP 設定：告訴 agent 如何啟動執行檔，並傳入 SQL Server 連線設定。
3. `sqlmcp` skill：教 agent 如何選擇及安全使用工具。

只安裝 skill 不會提供 MCP 工具；只安裝 server 而未設定 agent，也不會看到工具。

## 1. 建置並安裝 server

支援環境為 Windows x64。從 SQLMCP 儲存庫根目錄執行：

```powershell
./scripts/install.ps1
```

需要 .NET 10 SDK 進行建置。腳本會產生 self-contained 單檔執行檔；預設位置是 `$env:HOME\.local\bin\sqlmcp.exe`，若未設定 `HOME` 則使用 Windows 使用者目錄。可指定位置：

```powershell
./scripts/install.ps1 -Destination "$env:USERPROFILE\.local\bin"
```

若執行檔正由 agent 使用，先停止該 MCP server 或關閉 agent，再重新安裝。

## 2. 設定 agent 的 MCP server

將以下設定加入 agent 的 MCP 設定檔，並替換執行檔路徑與連線內容。不同 agent 的設定檔位置及環境變數展開語法不同；不確定時查閱該 agent 的 stdio MCP 文件。

```json
{
  "mcpServers": {
    "sqlmcp": {
      "type": "stdio",
      "command": "C:\\Users\\you\\.local\\bin\\sqlmcp.exe",
      "args": [],
      "env": {
        "SQLMCP_Connections__localhost__ConnectionString": "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True",
        "SQLMCP_Connections__localhost__MaxRows": "100",
        "SQLMCP_Connections__localhost__TimeoutSeconds": "30",
        "SQLMCP_Connections__localhost__AllowWrite": "false"
      }
    }
  }
}
```

連線名稱是 `Connections__` 後的片段；以上範例的工具參數 `connection` 為 `localhost`。每個 agent 啟動的 SQLMCP 程序都能使用其設定中的所有連線。不要把密碼提交到 Git；使用 agent 支援的秘密或環境變數機制。

至少需要一個 `ConnectionString`。常用的選用設定：

| 設定尾碼 | 用途 | 預設值 |
| --- | --- | --- |
| `UserName` | 設定後改用 SQL 驗證 | 未設定 |
| `Password` | SQL 驗證密碼 | 未設定 |
| `PasswordEnvironmentVariable` | 從指定的程序環境變數讀取密碼，優先於 Password | 未設定 |
| `MaxRows` | 每個結果集最多回傳筆數，範圍 1–10000 | 100 |
| `TimeoutSeconds` | SQL 指令逾時秒數，範圍 1–300 | 30 |
| `AllowWrite` | 允許 `execute_sql` 執行寫入、DDL 與預存程序 | false |

若要使用 `read_records`，還需設定 entity 白名單：

```json
"SQLMCP_Connections__erp__Entities__products__Schema": "dbo",
"SQLMCP_Connections__erp__Entities__products__Table": "Products",
"SQLMCP_Connections__erp__Entities__products__Columns__0": "Id",
"SQLMCP_Connections__erp__Entities__products__Columns__1": "Name"
```

此時 `entity` 參數使用別名 `products`，而 SQL Server 實體是 `dbo.Products`。

## 3. 安裝 skill

下列指令都從 SQLMCP 儲存庫根目錄執行。

### Codex

```powershell
$codexRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$codexSkills = Join-Path $codexRoot 'skills'
New-Item -ItemType Directory -Force -Path $codexSkills | Out-Null
Copy-Item -Recurse -Force -Path '.\skills\sqlmcp' -Destination $codexSkills
```

安裝結果應為 `<Codex 根目錄>\skills\sqlmcp\SKILL.md`。

### Claude Code

```powershell
$claudeSkills = Join-Path $env:USERPROFILE '.claude\skills'
New-Item -ItemType Directory -Force -Path $claudeSkills | Out-Null
Copy-Item -Recurse -Force -Path '.\skills\sqlmcp' -Destination $claudeSkills
```

安裝結果應為 `%USERPROFILE%\.claude\skills\sqlmcp\SKILL.md`。

### 其他 agent

Skill 沒有跨所有 AI agent 的統一安裝標準。若 agent 支援資料夾式 `SKILL.md`，將整個 `skills\sqlmcp` 資料夾複製到它指定的 skills 目錄，保留 `references\setup.md` 相對路徑。若不支援 skills，仍可使用 SQLMCP server；將本 `SKILL.md` 作為該 agent 的專案指引或自訂 instructions，但具體位置依該產品文件為準。

安裝或更新 skill 後重新啟動 agent，使其重新載入指引。

## 4. 驗證

重新啟動 agent 後：

1. 確認名為 `sqlmcp` 的 MCP server 已連線，沒有啟動錯誤。
2. 確認可看到 `list_connections` 等七個工具；工具名稱可能帶有 server 命名空間前綴。
3. 呼叫 `list_connections`，確認回傳預期的連線名稱且不包含連線字串或帳密。
4. 呼叫 `list_databases` 或 `list_entities` 驗證資料庫權限與設定。

若 skill 已載入但沒有工具，檢查的是 server 安裝與 MCP 設定。若工具可用但 agent 不會自動選用，檢查 skill 是否複製到正確目錄並重啟 agent。
