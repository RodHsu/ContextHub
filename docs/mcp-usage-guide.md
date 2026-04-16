# ContextHub MCP 使用指南

## 1. 目的

這份文件回答三件事：

- 在新對話開始時，什麼情況應該先呼叫 ContextHub
- 在對話進行中，什麼資訊值得查、值得寫回
- 如何判斷 ContextHub 不只是「有在跑」，而是真的達到開發時規劃的效果

ContextHub 的定位不是把所有歷史對話全文塞進 prompt，而是讓代理在需要時，透過 MCP 主動拉取：

- 長期事實
- 設計決策
- 顯式 user preferences
- runtime logs
- 結構化 working context

## 2. 核心使用原則

### 2.1 先查再做，不要先猜

開始一個新任務時，先用 `build_working_context` 建立任務上下文，再決定要不要補 `memory_search`、`memory_get` 或 `log_search`。

### 2.2 顯式寫入，不靠偷存

只有明確確認要跨對話保留的資訊，才寫進 ContextHub。特別是 user preferences，只能用 `user_preference_*` 系列工具管理，不要混成一般 memory。

### 2.3 寫入的是「可重用知識」，不是每一句對話

適合寫入：

- 穩定的 repo 規則
- 經確認的架構決策
- 值得後續追蹤的 incident 摘要
- 長期有效的 user preference

不適合寫入：

- 臨時猜測
- 尚未定案的 brainstorm
- 一次性執行細節
- 可以從程式碼直接讀到、且沒有摘要價值的原文搬運

### 2.4 以 query 為中心，不要用模糊關鍵字

`build_working_context` 與 `memory_search` 的 query 應該接近你現在的任務描述，而不是只丟單字。

好例子：

- `請依照目前 repo 規則實作 API 變更並列出必要測試`
- `ContextHub embedding profile 切換後的正式操作流程`
- `最近 mcp-server 的 user preference 寫入錯誤`

差的例子：

- `test`
- `memory`
- `bug`

## 3. 對話生命週期怎麼用

### 3.1 開始新對話

目標：快速把同 repo 的長期脈絡帶回來，避免重新蒐集一次。

建議流程：

```text
新對話開始
  -> build_working_context(任務描述)
  -> 若命中不足，再 memory_search(更精準主題)
  -> 若已有明確 item，再 memory_get(id)
```

建議先呼叫：

- `build_working_context`

適合查出的內容：

- 既有事實 `facts`
- 已採納決策 `decisions`
- 近期 relevant logs `recentLogs`
- 顯式 user preferences `userPreferences`
- 建議測試 `suggestedTests`
- citations

建議對代理這樣下指令：

```text
開始前先用 ContextHub 的 build_working_context，
query 用「<目前任務描述>」，
整理 repo 規則、既有決策、相關偏好與建議測試。
```

什麼時候還要補 `memory_search`：

- `build_working_context` 回來的內容太少
- 你要追某個明確主題，例如特定 API、特定決策、特定文件
- 你想知道是否已經有同類 incident 或既有 trade-off 記錄

### 3.2 對話進行中

目標：在需要時補查，而不是每一輪都查。

適合補查的時機：

- 代理開始對 repo 規則或架構做出假設時
- 需要確認某個名詞、流程、決策是否已經被記錄
- 正在除錯，需要對照 runtime 事實

建議流程：

```text
正在實作 / 討論
  -> 發現缺脈絡
  -> memory_search / log_search
  -> 命中單一項目時再 memory_get / log_read
```

工具選擇：

- 查長期知識：`memory_search`
- 讀單一 memory 全文：`memory_get`
- 查 runtime logs：`log_search`
- 讀指定 log 條目：`log_read`

### 3.3 討論設計或 trade-off

目標：先找既有決策，再形成新決策。

建議流程：

```text
討論設計
  -> build_working_context(本次設計題)
  -> memory_search(特定設計主題)
  -> 形成結論
  -> memory_upsert / memory_update 寫回決策摘要
```

這個階段最有價值的不是把整段討論存進去，而是把最終決策整理成：

- 決策內容
- 適用範圍
- trade-off
- 風險與後續條件

### 3.4 對話結束前

目標：只把「下次還值得拿回來」的內容寫入。

適合寫回的內容：

- 新增 repo 規則
- 新的 architecture decision
- 值得長期保留的 incident summary
- 已確認的 user preference

不適合寫回的內容：

- 尚未驗證的猜測
- 一次性 debugging 過程全文
- 與長期上下文無關的暫時安排

## 4. 什麼時候該呼叫哪個 Tool

| 情境 | Tool | 什麼時候用 | 期待結果 |
|---|---|---|---|
| 新對話剛開始 | `build_working_context` | 需要快速恢復任務脈絡 | 結構化 facts / decisions / preferences / citations |
| 想查某個主題是否已有記錄 | `memory_search` | 需要補精準知識 | 命中相關 memory 摘要 |
| 想看某個 memory 全文 | `memory_get` | 已知 item id | 完整 memory document |
| 確定要新增長期知識 | `memory_upsert` | 新事實、決策、artifact 需要保存 | 建立或覆蓋 external key 對應 item |
| 已有 memory 需要修正 | `memory_update` | 補充內容或調整摘要 | 更新既有 item |
| 確認使用者長期偏好 | `user_preference_upsert` | 例如回覆語言、工程原則、限制 | 顯式可查詢偏好 |
| 想列出既有偏好 | `user_preference_list` | 檢查目前已存的偏好 | 偏好清單 |
| 偏好失效或要停用 | `user_preference_archive` | 舊偏好不再適用 | archived / restore |
| 除錯時查 runtime 事實 | `log_search` | 用 service、level、traceId、關鍵字查詢 | 命中 log 清單 |
| 看某段 log 細節 | `log_read` | 需要進一步讀已命中的事件 | 指定 log slice / 條件結果 |
| incident 值得長期保留 | `promote_log_slice_to_memory` | 將一段 log 變成可檢索知識 | durable memory |
| 切換 embedding 模型或 profile | `enqueue_reindex` | `EMBEDDING_PROFILE` / model 變更後 | 背景重建 vectors |

## 5. 建議的呼叫方式

## 5.1 新對話的標準開場

優先用這種請求：

```text
先用 ContextHub 的 build_working_context，
query 用「<本次任務描述>」，
回來後再根據不足的部分補查 memory_search。
```

## 5.2 實作中的補查

```text
針對「<主題>」用 ContextHub memory_search 補查，
如果有命中再展開關鍵 item。
```

## 5.3 除錯中的補查

```text
用 ContextHub 查最近跟「<服務 / 錯誤關鍵字 / trace id>」相關的 logs，
先看最近 Error，再整理成可行動的事實。
```

## 5.4 寫回長期知識

```text
把剛確認的結論整理後寫回 ContextHub。
如果是 repo 規則或架構決策，用 memory_upsert；
如果是我的長期偏好，用 user_preference_upsert。
```

## 6. Query 與內容撰寫建議

### 6.1 `build_working_context` / `memory_search` 的 query

把 query 寫成「任務句」，至少包含：

- 主題
- 範圍
- 目標

例如：

- `依目前 repo 規則修改 Minimal API 端點並列出必要測試`
- `ContextHub user preference 設計原則與使用限制`
- `mcp-server 最近的 preference upsert concurrency error`

### 6.2 寫回 memory 的內容

摘要應該能讓下次的人不用重看所有原始資料，就知道：

- 這是什麼
- 為什麼重要
- 適用條件
- 有什麼限制或 trade-off

### 6.3 寫回 user preference 的內容

只存長期、穩定、可操作的偏好，例如：

- 回覆語言與溝通風格
- 工程原則
- tooling preference
- 明確限制
- 不要採用的 anti-pattern

## 7. 如何判斷它有正常運作

不要只看 container 是 `healthy`，至少要同時滿足下面幾點：

### 7.1 基礎可用

- `/health/ready` 正常
- `/api/status` 能回傳目前 embedding provider、profile、model、dimensions
- MCP client 可以 `tools/list`、`tools/call`

### 7.2 查詢可用

- `build_working_context` 能回傳結構化結果，不是空殼
- `memory_search` 能命中已知 repo facts / decisions
- `user_preference_list` 能看到已寫入的偏好
- `log_search` 能查到近期 runtime facts

### 7.3 背景流程可用

- 寫入 memory 後，對應 job 能完成
- 切換模型後，`enqueue_reindex` 建立的 job 能完成
- `/api/jobs` 沒有長時間卡住的 pending / failed

### 7.4 效能可接受

- `/api/performance/measure` 在同機環境下表現穩定
- `queryEmbedding`、`hybridSearch` 沒有明顯異常抖動
- logs 沒有持續出現 embeddings health check 或連線錯誤

## 8. 如何判斷它有達到原本規劃效果

ContextHub 規劃的效果，不是「多一個資料庫」，而是這些行為真的發生：

### 8.1 新對話能快速恢復脈絡

達標表現：

- 開新對話後先呼叫一次 `build_working_context`
- 能立刻拿回 repo 規則、已知決策、user preference、相關 citations
- 不需要每次都重新翻 README、架構文件、舊 incident

### 8.2 討論不再只靠即時上下文

達標表現：

- 設計討論時能先找既有決策
- 新決策可整理寫回，供下次任務使用
- user preference 不會因換新對話而消失

### 8.3 除錯不再只看容器 console

達標表現：

- 可以用 `log_search` 依 service / level / trace id 找到事件
- 可將重要 incident 提升成 memory，供後續檢索

### 8.4 embedding 與檢索是可維運的

達標表現：

- `EMBEDDING_PROFILE` 改完後知道要 `enqueue_reindex`
- `/api/status` 與 `/api/performance/measure` 能驗證設定與效能

## 9. 建議的日常操作節奏

```text
開始新任務
  -> build_working_context

需要補資料
  -> memory_search / log_search

確認重要結論
  -> memory_upsert / memory_update

確認長期偏好
  -> user_preference_upsert

收尾驗證
  -> jobs / logs / performance
```

## 10. 常見誤用

- 把每次對話全文都寫進 memory，導致檢索噪音升高
- 明明是 user preference，卻用一般 memory 寫入
- `build_working_context` 沒先用，就開始憑印象推斷
- query 寫得太短，造成檢索品質差
- 改模型後忘記 `enqueue_reindex`
- 只看 health check，不看 query 命中品質與 jobs 狀態

## 11. 建議的驗收清單

在你的整合完成後，至少手動驗一次下面幾項：

1. 新對話用 `build_working_context` 查 `目前任務描述`，確認能拿回 repo 規則與 citations。
2. 用 `memory_search` 查一個你已知存在的設計決策，確認前幾筆結果正確。
3. 用 `user_preference_list` 確認長期偏好存在，再用 `build_working_context` 確認偏好會被帶回。
4. 用 `log_search` 查最近 `mcp-server` 或 `worker` 的事件，確認可用於除錯。
5. 跑一次 `/api/performance/measure`，記錄目前 baseline。
6. 如果剛改過 profile 或模型，確認 `enqueue_reindex` 建出的 job 已完成。

通過這份清單，才算是「接進來且有實際發揮效果」，而不是只有 endpoint 能連通。
