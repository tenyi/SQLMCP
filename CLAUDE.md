# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 指令

~~~powershell
dotnet build src/SqlMcp                 # 建置（煙霧測試依賴 Debug 輸出）
dotnet run --project tests/SqlMcp.Smoke # 唯一的測試；須在儲存庫根目錄執行，印出 PASS 才算通過
./scripts/install.ps1                   # 發佈 self-contained 單檔 sqlmcp.exe 到 ~/.local/bin
~~~

- 沒有測試框架，沒有單一測試可挑選執行；煙霧測試是一支依序檢查的程式，先 build 再跑，否則測到的是舊的 `SqlMcp.dll`。
- 煙霧測試預設不需真實資料庫，驗證工具探索、機密不外洩、白名單、筆數上限與 execute_sql 唯讀拒絕。`offline` 連線指向 127.0.0.1:1 並設 1 秒逾時，用來驗證「通過前置檢查、失敗於連線」的路徑；實際 SQL Server 行為由選擇性整合測試驗證。
- Claude Code 執行中會鎖住 `~/.local/bin/sqlmcp.exe`，`install.ps1` 會失敗；需請使用者關閉 Claude Code 後安裝並重啟，新版工具才會生效。

## 架構

- 整個伺服器在 `src/SqlMcp/Program.cs` 單一檔案：啟動、設定模型（`Settings`/`Database`/`Entity`）、所有 MCP 工具與 `RunQuery` 輔助函式。新增工具時要同步更新煙霧測試裡的工具清單。
- stdio transport：stdout 專供 MCP 協定，任何日誌或輸出都必須走 stderr，否則會破壞協定。
- 設定只來自環境變數（前綴 `SQLMCP_`，`__` 代表階層，例如 `SQLMCP_Connections__localhost__AllowWrite`）或命令列參數，不讀任何設定檔。這是刻意的設計：所有參數都寫在 agent 的 `.mcp.json` 的 `env` 區塊。不要重新引入獨立的 JSON 設定檔。
- 沒有任何連線時 `Validate()` 會讓程序啟動即失敗（exit code 1）。
- 兩條讀取路徑：
  - `list_entities` / `read_records`：受 `Entities` 白名單限制的受控讀取。
  - `list_databases` / `list_tables` / `describe_table` / `execute_sql`：不受白名單限制，只受資料庫帳號權限限制。
- `execute_sql` 在 `AllowWrite=false`（預設）時：以 `StripCommentsAndLiterals`（逐字元掃描，支援巢狀註解）去除註解與字串常值後，只接受 SELECT/WITH 開頭且不含 COMMIT/ROLLBACK/SAVE/EXEC/EXECUTE，並在交易內執行後一律 rollback（交易若已被結束則回報錯誤）；`AllowWrite=true` 時不包交易直接執行。每個結果集最多 `MaxRows` 筆並標示 `truncated`；`SqlException` 轉成保留錯誤編號的 `McpException`。
- 回傳的 `rolledBack=true` 代表交易已回滾，`rowsAffected` 必須為 null，不可描述成已儲存的變更；寫入模式才回報影響筆數。
- 查詢遭前置檢查阻擋時，工具必須回傳 `isError=true` 與「查詢已阻擋，未執行 SQL」及原因。agent 必須向使用者回報，不可自動改用寫入連線或提高權限繞過。
- `describe_table` 支援一般及方括號名稱，包括括號內句點和 `]]` 跳脫，名稱一律以 SQL 參數傳入。
- 連線字串語法在啟動時驗證，設定失敗與主機執行期故障分開回報，兩者都不直接輸出例外內容。
- 可設定 `SQLMCP_SMOKE_CONNECTION_STRING` 執行 SQL Server 整合測試（目標資料庫須有可見資料表或檢視表）；只查詢既有結構與操作區域資料表變數，不留下變更。
- 版本固定：ModelContextProtocol C# SDK 2.3.0、MCP 協定 2026-07-28（煙霧測試以此版本連線）。

## 慣例

- 程式碼註解使用詳細的繁體中文，與現有程式一致。
- 根目錄的 `.mcp.json` 會提交到 Git，不得寫入密碼；用 `${變數}` 展開（如 `${ERP_PASSWORD}`）。
- 主幹 main，GitHub Flow：短期工作分支，驗證後合併。
