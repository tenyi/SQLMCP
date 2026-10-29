---
name: sqlmcp
description: SQL Server 查詢與探索，透過 sqlmcp MCP 工具。用於：列出資料庫或資料表、查看資料表欄位結構、執行 SQL（查詢、寫入、DDL）。
---

# sqlmcp

sqlmcp 工具以 `mcp__sqlmcp__*` 為名，可能是 deferred，第一次使用前用 ToolSearch 一次載入全部：
`select:mcp__sqlmcp__list_connections,mcp__sqlmcp__list_databases,mcp__sqlmcp__list_tables,mcp__sqlmcp__describe_table,mcp__sqlmcp__execute_sql`

## 探索順序

由外而內，每一步的輸出就是下一步的參數：

1. `list_connections` → 取得 `connection` 名稱（例如 `localhost`）。只有一個連線時直接用它。
2. `list_databases(connection)` → 取得 `database` 名稱。
3. `list_tables(connection, database)` → 取得 `schema.table`。
4. `describe_table(connection, table, database)` → 取得欄位名稱與型別。
5. `execute_sql(connection, query, database)` → 用上一步確認過的欄位名稱寫 SQL。

完成條件：SQL 裡出現的每個資料表與欄位，都已在步驟 3、4 的輸出中看過。名稱不確定時回到步驟 3、4 查，而不是猜。

## 寫 SQL

- 方言是 **T-SQL**：限制筆數用 `SELECT TOP (n)`，分頁用 `ORDER BY ... OFFSET ... FETCH`，字串串接用 `+` 或 `CONCAT`。
- 用 `database` 參數切換資料庫，查詢裡寫兩段式名稱 `schema.table`。
- 每個結果集最多回傳連線的 MaxRows 筆（預設 100）。`truncated: true` 代表還有更多資料：改用 `TOP`、`WHERE` 或 `COUNT(*)` 縮小範圍，而不是當成完整結果回報。
- 一次可送多個陳述式，結果依序出現在 `resultSets`；`rowsAffected` 是整批寫入影響的列數。
- SQL 錯誤會回傳 `SQL 錯誤 <編號>：<訊息>`，依訊息修正後重試（例如 208 = 物件名稱無效，回到 `list_tables` 確認名稱）。

## 寫入與 DDL

連線設定 `AllowWrite=true` 時，`execute_sql` 會直接提交 INSERT／UPDATE／DELETE／DDL，沒有 rollback。執行前：

- 先用相同條件的 `SELECT COUNT(*)` 確認會影響的列數。
- 把要執行的 SQL 與預估影響列數給使用者確認，得到同意後才執行。
- 測試用的資料表放在 tempdb 或使用 `#暫存表`。

連線未開 AllowWrite 時只接受 SELECT／WITH 開頭的指令；遇到「此連線未啟用 allowWrite」就告訴使用者，由使用者決定是否在 `.mcp.json` 加上 `SQLMCP_Connections__<名稱>__AllowWrite=true`。
