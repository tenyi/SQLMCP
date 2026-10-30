---
name: sqlmcp
description: 安裝、設定或使用 SQLMCP 連接 SQL Server；適用於列出連線與資料庫、探索資料表結構、讀取白名單資料、執行 T-SQL，以及處理 AllowWrite 唯讀限制。若 sqlmcp MCP 工具尚未出現，先依本 skill 的設定指引安裝 server 並連接 agent。
---

# SQLMCP

SQLMCP 是本機 stdio MCP server。Skill 只提供操作指引，不包含執行檔、連線或資料庫權限；必須同時完成 server 安裝與 agent 的 MCP 設定。

## 首次使用：先完成安裝

首次部署或目前看不到 SQLMCP 工具時，不要直接嘗試呼叫工具。完整閱讀 [references/setup.md](references/setup.md)，依序完成：

1. 在 Windows x64 電腦安裝 .NET 10 SDK，從本儲存庫建置並安裝 `sqlmcp.exe`。
2. 在 agent 的 MCP 設定中註冊 `sqlmcp` stdio server，填入執行檔路徑及至少一組 SQL Server 連線。
3. 將整個 `skills/sqlmcp` 資料夾安裝到 agent 的 skill 目錄；必須連同 `references/setup.md` 一起複製。
4. 重新啟動 agent，使 MCP 設定與 skill 生效。
5. 確認七個工具都存在，再呼叫 `list_connections` 驗證連線。

只有目前環境已能看到下列七個工具時，才可略過上述安裝流程。

## 每次使用前

先確認目前環境是否已有下列工具；工具可能帶有 `mcp__sqlmcp__` 等命名空間前綴：

- `list_connections`
- `list_entities`
- `read_records`
- `list_databases`
- `list_tables`
- `describe_table`
- `execute_sql`

工具不存在、server 啟動失敗，或使用者要求安裝／設定時，回到「首次使用」流程。不要假設安裝 skill 就會自動安裝或啟動 MCP server。

## 選擇工具

| 目的 | 工具 | 重要限制 |
| --- | --- | --- |
| 確認可用連線 | `list_connections` | 只回傳連線名稱，不含帳密 |
| 查看受控讀取範圍 | `list_entities(connection)` | 只列出 `read_records` 白名單 |
| 讀取白名單資料 | `read_records(connection, entity, limit?)` | 不能自行加篩選或排序；省略 limit 時使用 MaxRows |
| 列出資料庫 | `list_databases(connection)` | 可見範圍由資料庫帳號決定 |
| 列出表與檢視表 | `list_tables(connection, database?)` | 回傳 schema、名稱與類型，不含欄位 |
| 查看欄位 | `describe_table(connection, table, database?)` | `table` 可用 `name`、`schema.name` 或方括號名稱 |
| 自訂查詢或寫入 | `execute_sql(connection, query, database?)` | 是否可寫入由該連線的 AllowWrite 決定 |

## 查詢流程

1. 使用者未指定連線時，先呼叫 `list_connections`。只有一個連線才可直接選用；有多個時依使用者意圖選擇，無法判斷時詢問。
2. 只需白名單資料時，先用 `list_entities`，再用 `read_records`。不要把實際 table 名稱當成 entity 別名。
3. 需要自訂 SQL 時，先用 `list_databases`、`list_tables`、`describe_table` 確認實際結構。SQL 中的資料表與欄位名稱不確定時，回到探索工具查證，不要猜。
4. 使用 T-SQL。透過 `database` 參數切換資料庫，SQL 內優先使用 `schema.table`；名稱含句點或特殊字元時使用方括號，名稱中的 `]` 寫成 `]]`。
5. 每個結果集最多回傳 MaxRows。`truncated=true` 代表結果不完整，改用 `TOP`、`WHERE`、分頁或彙總縮小結果，並在回答中揭露截斷。

## 唯讀、寫入與預存程序

- `AllowWrite=false` 是預設值。`execute_sql` 只接受 SELECT／WITH 開頭，禁止 COMMIT、ROLLBACK、SAVE、EXEC／EXECUTE，並在成功讀取結果後回滾交易。
- 唯讀檢查會阻擋批次首句省略 EXEC 的預存程序呼叫。預存程序只能透過 `AllowWrite=true` 連線執行；目前不會讀取程序內容判定是否唯讀。
- `rolledBack=true` 表示變更已回滾，此時 `rowsAffected=null`；不得描述為資料已寫入。
- `AllowWrite=true` 時，SQLMCP 不額外建立回滾交易。寫入與 DDL 依 SQL Server 權限及 SQL 本身的交易控制生效。
- 唯讀檢查不是完整的 T-SQL 安全分析器，也不能防止所有外部副作用；資料庫帳號必須使用符合用途的最小權限。

執行會保留資料變更的 INSERT、UPDATE、DELETE、MERGE、DDL 或預存程序前，先確認使用者已授權該操作，並在可行時用相同條件的 SELECT 或 COUNT 說明影響範圍。若工具回報「查詢已阻擋，未執行 SQL」，向使用者回報原始原因；不要自行改用 AllowWrite 連線、修改設定或提高權限。

## 解讀結果與錯誤

- `resultSets` 依 SQL 產生順序排列；每個結果集各自包含 `rows` 與 `truncated`。
- `rowsAffected` 只代表寫入模式下 SQL Server 回報的影響筆數；沒有影響筆數時可能是 null。
- SQL 語法或物件錯誤會回傳 `SQL 錯誤 <編號>：<訊息>`。依訊息修正查詢；物件或欄位不存在時重新探索結構。
- 未知連線、未授權 entity、limit 超限與唯讀限制會在送出 SQL 前回傳工具錯誤。
- 不在回答、日誌或 SQL 中重述連線字串、密碼或其他敏感設定。
