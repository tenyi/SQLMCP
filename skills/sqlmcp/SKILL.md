---
name: sqlmcp
description: 使用 sqlmcp MCP 工具探索 SQL Server 結構、讀取白名單資料及執行 T-SQL；用於使用者要求操作已設定的 SQL Server 資料庫。
---

# SQL MCP

使用已連接的 sqlmcp MCP 工具。工具若為延遲載入，先依執行環境的工具探索方式載入所需工具；工具名稱和可用連線以實際環境為準。

## 查詢與探索

- 使用 `list_connections` 確認連線；若使用者未指定且只有一個連線，可使用該連線。
- 受控讀取可先用 `list_entities` 查看白名單，再呼叫 `read_records`。省略 `limit` 時使用連線的 MaxRows。
- 需要自訂查詢時，視需要依序使用 `list_databases`、`list_tables`、`describe_table` 確認資料庫、資料表及欄位；不要猜測未知結構。名稱含句點或特殊字元時以方括號表示，右方括號以 `]]` 跳脫。
- 使用 T-SQL，透過 `execute_sql` 的 `database` 參數選擇資料庫。結果集的 `truncated=true` 表示結果不完整；調整篩選、分頁或彙總，並告知使用者結果限制。
- `rolledBack=true` 表示交易已回滾，`rowsAffected=null`，不可將這類操作描述為已保存的變更。寫入模式回傳的 `rowsAffected` 為影響筆數，可能為 null。

## 權限與錯誤

- `AllowWrite=false` 時，`execute_sql` 只接受 SELECT／WITH 開頭，並阻擋 COMMIT、ROLLBACK、SAVE、EXEC／EXECUTE；省略 EXEC 的首句預存程序呼叫也會被阻擋。檢查不是完整 SQL 解析，仍需資料庫帳號限制權限。
- 預存程序需要 `AllowWrite=true`；目前不會讀取程序定義來判斷是否唯讀。被阻擋時向使用者回報原因，不要自行切換寫入連線或提高權限繞過限制。
- `AllowWrite=true` 時不額外包交易，依資料庫權限及 SQL 交易設定執行。依使用者授權操作；寫入前確認目標與影響範圍。
- `list_databases`、`list_tables`、`describe_table` 和 `execute_sql` 不受 `Entities` 白名單限制。SQL 錯誤可用於修正查詢；避免在回報中洩漏連線或敏感資訊。
