# ContextHub Feature Inventory

> 2026-07-12 Quiet Signal vNext baseline. Purpose: expand the dashboard site information architecture into page/function levels and mark each item as a page or function.
>
> Design source: [context-hub-quiet-signal-vnext.md](/w:/Repositories/WJCY/ContextHub/docs/design/context-hub-quiet-signal-vnext.md).

## 1. 標註規則

| 標註 | 意義 | 判斷方式 |
| --- | --- | --- |
| Page | 可直接進入的 route、邊界頁、主要頁面容器 | 有 `@page` route，或是登入、錯誤、權限、NotFound 這類頁面 |
| Function | 頁面內工作區、表格、表單、操作、狀態、共用元件、卡控流程 | 沒有獨立 route，但使用者會互動、閱讀、確認、切換、複製或觸發 |
| State | Function 的狀態顯示或狀態轉換 | loading、empty、error、stale、disabled、pending、applied 等 |
| Guardrail | 防呆或卡控規則 | confirmation、disabled reason、permission denied、secret reveal、audit trail 等 |

## 2. 全站功能樹

```text
ContextHub Dashboard
├── 共用 App Shell
├── 維運監控
│   ├── 總覽 /
│   ├── 狀態監控 /monitoring
│   ├── 執行參數 /runtime
│   ├── 日誌 /logs
│   ├── 工作佇列 /jobs
│   └── 效能 /performance
├── 知識工作
│   ├── 記憶圖譜 /graph
│   ├── 記憶資料 /memories
│   ├── 記憶整理 /retention
│   └── 資料來源 /sources
├── 審核治理
│   ├── 收件匣 /inbox
│   ├── 治理 /governance
│   ├── 評估 /evaluation
│   └── ChatGPT /chatgpt-proposals
├── 管理設定
│   ├── 偏好 /preferences
│   ├── 資料庫檢視 /storage
│   ├── 安全管理 /security
│   └── 系統設定 /settings
├── 個人帳號
│   └── Token /account/tokens
└── 邊界頁
    ├── /login
    ├── /forbidden
    ├── /not-found
    └── /Error
```

## 3. 共用 Shell 與跨頁能力

| 層級 | 類型 | 名稱 | Scope / Route | 功能說明 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Function | App shell | 全站 | Sidebar、topbar、content scroll host、主要 layout contract | `02-app-shell-navigation-command.svg` |
| 2 | Function | Sidebar task groups | 全站 | 維運監控、知識工作、審核治理、管理設定、個人帳號 | `02-app-shell-navigation-command.svg` |
| 3 | Function | Sidebar RWD mode | 全站 | Desktop expanded、tablet 64px icon rail、mobile overlay drawer | `10-rwd-loading-refresh-motion.svg` |
| 3 | Function | Topbar account chrome | 全站 | 使用者名稱、theme switcher、logout form | `02-app-shell-navigation-command.svg` |
| 3 | Function | Command palette | 全站 | Route jump、job id、memory lookup、logs、proposal search | `02-app-shell-navigation-command.svg` |
| 3 | Function | Theme switching | 全站 | Dark/light mode menu and persistence | `02-app-shell-navigation-command.svg` |
| 3 | Function | Refresh status | 多數 dashboard page | Last updated、is refreshing、polling interval | `01-ui-system-states-motion.svg` |
| 3 | Function | Client-local time | 全站 | 所有使用者可見時間預設顯示 client-local | `01-ui-system-states-motion.svg` |
| 3 | Function | Toast | 全站 | Success、error、undo where safe、rollback notice | `01-ui-system-states-motion.svg` |
| 3 | Function | Info popover | 全站 | KPI、風險、disabled reason、metric definition | `01-ui-system-states-motion.svg` |
| 3 | State | App loading | 全站 | Route top progress、panel skeleton、table skeleton、code skeleton | `10-rwd-loading-refresh-motion.svg` |
| 3 | Guardrail | Scroll ownership | 全站 | Body no horizontal scroll；table/code/diff local horizontal scroll only | `10-rwd-loading-refresh-motion.svg` |
| 3 | Guardrail | Selectability | 全站 | Chrome non-selectable；IDs、JSON、code、diff、log lines selectable | `01-ui-system-states-motion.svg` |

## 4. 維運監控

### 4.1 總覽 `/`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 總覽 | Dashboard landing route，提供系統健康與待處理摘要 | loading、error、stale | `04-operations-overview-monitoring.svg` |
| 2 | Function | Health summary | MCP、embedding、DB、Redis、Docker host 摘要 | unhealthy 顯示原因與導向 | `04-operations-overview-monitoring.svg` |
| 2 | Function | Snapshot freshness | 背景快照時間、collector 狀態 | stale banner blocks misleading actions | `04-operations-overview-monitoring.svg` |
| 2 | Function | Token savings summary | Token 節省量、樣本量、視窗摘要 | tooltip 說明 exact/estimated coverage | `04-operations-overview-monitoring.svg` |
| 2 | Function | Recent activity | 最近 jobs、maintenance、proposal、retention 訊號 | empty state | `04-operations-overview-monitoring.svg` |
| 2 | Function | Alert summary | Active alerts、acknowledgement entry | ack requires audit context | `04-operations-overview-monitoring.svg` |

### 4.2 狀態監控 `/monitoring`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 狀態監控 | 深入檢視 runtime snapshot 與 monitoring metrics | loading、no snapshot、stale、error | `04-operations-overview-monitoring.svg` |
| 2 | Function | Time range controls | 1h、6h、24h、7d 等時間視窗 | disabled if snapshot unavailable | `04-operations-overview-monitoring.svg` |
| 2 | Function | Refresh cadence | 自動刷新間隔與 last refresh | paused/stale indicators | `04-operations-overview-monitoring.svg` |
| 2 | Function | Metric cards | dependency health、resource、collector、traffic | metric delta pulse | `04-operations-overview-monitoring.svg` |
| 2 | Function | Detail drawer | 點擊 metric 後開啟細節 | drawer 220ms；tablet stacked panel | `04-operations-overview-monitoring.svg` |
| 2 | State | No snapshot | 背景快照尚未產生 | retry and diagnostic hint | `04-operations-overview-monitoring.svg` |

### 4.3 執行參數 `/runtime`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 執行參數 | 顯示服務、依賴、設定與 runtime 狀態 | loading、degraded、permission denied | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Service cards | mcp-server、dashboard、worker、embedding service | healthy/degraded/failed chips | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Dependency map | 服務與依賴關係視覺化 | reduced-motion fallback | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Runtime settings view | 顯示可觀測設定、polling、limits | secret values redacted | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Runtime detail drawer | 點選服務後顯示 uptime、latency、version | local scroll for long values | `05-runtime-logs-jobs-performance.svg` |

### 4.4 日誌 `/logs`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 日誌 | 查詢與檢視系統 logs | loading、empty、error、live tail paused | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Query bar | Keyword、level、service、time range | validation error | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Severity filter | Trace/debug/info/warn/error | active filter chips | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Live tail toggle | 即時追蹤新 log lines | new row highlight 700ms fade | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Log table | Timestamp、level、source、message | sticky header；local horizontal scroll | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Copy log line | 複製單列 log 或選取範圍 | selectable log text only | `05-runtime-logs-jobs-performance.svg` |

### 4.5 工作佇列 `/jobs`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 工作佇列 | 檢視背景工作、狀態、失敗原因與重試 | loading、empty、failed retry、permission denied | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Jobs table | Job ID、type、status、created local time | local horizontal scroll；sticky header | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Job detail drawer | Metadata、payload、logs、result | tablet stacked detail panel | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Retry job | 重新排入失敗 job | disabled reason、optimistic state | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Cancel job | 取消 running/pending job | confirmation required | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Copy job result | 複製原始結果或錯誤摘要 | code block selectable | `05-runtime-logs-jobs-performance.svg` |

### 4.6 效能 `/performance`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 效能 | 執行 performance probe 與閱讀 measurement | loading、validation error、measurement failed | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Probe form | Endpoint、duration、sample options | validation and disabled reason | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Measurement chart | Latency、throughput、error ratio | responsive chart resize | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Payload/code block | 顯示 request/response 或 raw result | local horizontal scroll | `05-runtime-logs-jobs-performance.svg` |
| 2 | Function | Retry measurement | 重新執行 probe | retry flow with correlation id | `05-runtime-logs-jobs-performance.svg` |

## 5. 知識工作

### 5.1 記憶圖譜 `/graph`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 記憶圖譜 | 脈絡、hub、鄰域、similarity、explicit link 視覺化 | loading、empty、stale index | `06-knowledge-graph-memory.svg` |
| 2 | Function | Graph filter rail | Project、type、confidence、relationship layer | filter no-results state | `06-knowledge-graph-memory.svg` |
| 2 | Function | Graph canvas | 節點、關係、pan/zoom、selected node focus | reduced-motion snap-to-fit | `06-knowledge-graph-memory.svg` |
| 2 | Function | Relationship toggles | inference/reference/ownership/similarity layers | disabled if unavailable | `06-knowledge-graph-memory.svg` |
| 2 | Function | Zoom controls | zoom in/out/center/fit | keyboard accessible | `06-knowledge-graph-memory.svg` |
| 2 | Function | Node inspect drawer | Metadata、neighbors、payload | drawer offsets canvas; no overlap | `06-knowledge-graph-memory.svg` |
| 2 | Function | Enqueue reindex | 重新建立 graph index | gated action, audit trail | `06-knowledge-graph-memory.svg` |

### 5.2 記憶資料 `/memories`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 記憶資料 | 查詢、篩選、檢視 memory items | loading、empty、no results、conflict | `06-knowledge-graph-memory.svg` |
| 2 | Function | Search and filter row | Project、type、status、tag、source、query | filter chips and reset | `06-knowledge-graph-memory.svg` |
| 2 | Function | Memories table | ID、project、type/status、tags/source、timestamps | local horizontal scroll | `06-knowledge-graph-memory.svg` |
| 2 | Function | Memory detail drawer | Metadata、source context、links、findings、revisions、chunks | tablet stacked panel；no overlap | `06-knowledge-graph-memory.svg` |
| 3 | Function | Detail content reader | Markdown/source text/value body | framed reader；local x/y scroll；copy action；selectable text | `06-knowledge-graph-memory.svg` |
| 3 | Function | Revision summary cards | Version、actor、title、summary、created time | preview clamped；must not overlap chunks | `06-knowledge-graph-memory.svg` |
| 3 | Function | Chunk/log readers | Chunk text、log text、vector count、metadata | framed reader；visible scrollbar gutter；copy action；selectable text | `06-knowledge-graph-memory.svg` |
| 2 | Function | Merge memory | 合併或連結相關 memory | disabled reason, conflict state | `06-knowledge-graph-memory.svg` |
| 2 | Function | Delete/archive memory | 刪除或封存 | destructive confirmation | `06-knowledge-graph-memory.svg` |

### 5.3 記憶整理 `/retention`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 記憶整理 | 檢視 retention run、候選項目與整理決策 | loading、empty、blocked、busy | `07-sources-inbox-retention.svg` |
| 2 | Function | Retention summary | 最近執行、auto-delete、review、blocked、status | client-local completed time | `07-sources-inbox-retention.svg` |
| 2 | Function | Generate review list | 產生整理清單 | disabled while busy | `07-sources-inbox-retention.svg` |
| 2 | Function | Preview delete | 預覽 auto-delete 結果 | preview required before apply | `07-sources-inbox-retention.svg` |
| 2 | Function | Apply auto-delete | 套用刪除 | destructive confirmation required | `07-sources-inbox-retention.svg` |
| 2 | Function | Candidate table | 清單、記憶、訊號、決策 | local horizontal scroll | `07-sources-inbox-retention.svg` |
| 2 | Function | Decision detail panel | Action、review note、reason codes、blocked reasons | detail stacks on tablet | `07-sources-inbox-retention.svg` |
| 2 | Function | Copy review plan/result | 複製整理計畫或原始結果 | copyable text selectable | `07-sources-inbox-retention.svg` |

### 5.4 資料來源 `/sources`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 資料來源 | 管理 sources inventory 與 sync status | loading、syncing、paused、failed | `07-sources-inbox-retention.svg` |
| 2 | Function | Source inventory table | Provider、scope、last sync、health、actions | sticky header; local scroll | `07-sources-inbox-retention.svg` |
| 2 | Function | Source detail drawer | Connection status、credentials status、sync history | secret redaction | `07-sources-inbox-retention.svg` |
| 2 | Function | Trigger sync | 手動同步來源 | gated when source unhealthy | `07-sources-inbox-retention.svg` |
| 2 | Function | Pause/resume source | 暫停或恢復來源 | confirmation and audit | `07-sources-inbox-retention.svg` |
| 2 | Function | Failed sync diagnostics | 顯示錯誤與 retry | request id visible | `07-sources-inbox-retention.svg` |

## 6. 審核治理

### 6.1 收件匣 `/inbox`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 收件匣 | Triage incoming items and processing queue | loading、empty、batch progress | `07-sources-inbox-retention.svg` |
| 2 | Function | Triage queue | Multi-select、priority/type/age filters | pending does not look processed | `07-sources-inbox-retention.svg` |
| 2 | Function | Preview pane | Raw data metadata、processing status | stacks below list on tablet/mobile | `07-sources-inbox-retention.svg` |
| 2 | Function | Batch actions | Assign、archive、mark processed | progress bar; undo where safe | `07-sources-inbox-retention.svg` |
| 2 | Function | Processing state | pending/running/failed/completed | failed retry and reason | `07-sources-inbox-retention.svg` |

### 6.2 治理 `/governance`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 治理 | 檢視與管理 policy rules、exceptions、approvals | loading、empty、permission denied | `08-review-governance-evaluation.svg` |
| 2 | Function | Policy rules table | Rule name、scope、severity、status、updated | local scroll for dense columns | `08-review-governance-evaluation.svg` |
| 2 | Function | Exception drawer | Exception detail、scope、expiry、audit trail | approval required | `08-review-governance-evaluation.svg` |
| 2 | Function | Approval workflow | Request、review、approve/reject | pending visually distinct | `08-review-governance-evaluation.svg` |
| 2 | Function | Policy warnings | 違規與風險提示 | blocks unsafe action | `08-review-governance-evaluation.svg` |

### 6.3 評估 `/evaluation`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 評估 | 檢視 evaluation runs、scorecards、failures、regressions | loading、empty、failed run | `08-review-governance-evaluation.svg` |
| 2 | Function | Run selector | 選擇 evaluation run 或 time range | stale result warning | `08-review-governance-evaluation.svg` |
| 2 | Function | Scorecards | Accuracy、quality、coverage、regression summary | metric info popovers | `08-review-governance-evaluation.svg` |
| 2 | Function | Failure table | case、reason、severity、delta | local horizontal scroll | `08-review-governance-evaluation.svg` |
| 2 | Function | Regression detail drawer | Failure evidence、payload、diff、notes | code/diff local scroll | `08-review-governance-evaluation.svg` |

### 6.4 ChatGPT 寫入審核 `/chatgpt-proposals`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | ChatGPT 寫入審核 | Review ChatGPT write proposals before durable writes | loading、empty、failed、stale、blocked | `08-review-governance-evaluation.svg` |
| 2 | Function | Proposal filters | Project、status | reload and apply filter | `08-review-governance-evaluation.svg` |
| 2 | Function | Proposal queue | Proposal list、tool、project、actor、updated time | pending not applied | `08-review-governance-evaluation.svg` |
| 2 | Function | Review surface | Source、payload JSON、errors、applied resource | JSON/code selectable | `08-review-governance-evaluation.svg` |
| 2 | Function | Decision rail/zone | Approve、reject、retry failed apply | confirmation and note modal | `08-review-governance-evaluation.svg` |
| 2 | Function | Audit trail | OAuth subject、actor、created/updated local time | no secrets exposed | `08-review-governance-evaluation.svg` |
| 2 | State | Proposal statuses | Pending、validating、approved、rejected、applied、failed、stale、blocked | color and copy must distinguish actionability | `08-review-governance-evaluation.svg` |

## 7. 管理設定

### 7.1 偏好 `/preferences`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 偏好 | 使用者偏好、顯示與行為設定 | loading、saving、saved、error | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Preference forms | Theme、polling、display、scope preferences | validation errors | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Dirty save bar | 尚未儲存變更提示與儲存/還原 | fixed but not overlapping content | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Autosave disabled reason | 高風險設定不自動儲存的說明 | info popover | `09-admin-settings-account-tokens.svg` |

### 7.2 資料庫檢視 `/storage`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 資料庫檢視 | 檢視 storage / object storage / retention usage | loading、stale、failed test | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Storage usage cards | DB size、object storage、retention、artifacts | info popovers | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Connection test | 測試 object storage 或 backend connection | failed state with error detail | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Storage detail table | buckets、paths、usage、last check | local horizontal scroll | `09-admin-settings-account-tokens.svg` |

### 7.3 安全管理 `/security`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 安全管理 | Roles、permissions、sessions、audit | loading、permission denied、stale audit data | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Roles/permissions matrix | 使用者、角色、權限、scope | dense table local scroll | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Audit log | 安全事件、actor、client-local time、result | sticky header and filters | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Session controls | revoke session、force logout | destructive confirmation | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Remove user | 移除或停用使用者 | typed confirmation and audit | `09-admin-settings-account-tokens.svg` |

### 7.4 系統設定 `/settings`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | 系統設定 | Instance-level behavior settings | loading、saving、validation error、permission denied | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Behavior settings form | Polling、limits、feature toggles、maintenance-related settings | dirty save bar | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Save/revert settings | 儲存或還原設定 | confirmation for high-impact changes | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Settings diagnostics | 顯示 effective config 與 source | secrets redacted | `09-admin-settings-account-tokens.svg` |

## 8. 個人帳號

### 8.1 Token `/account/tokens`

| 層級 | 類型 | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- |
| 1 | Page | Token 管理 | 管理個人 API tokens | loading、empty、permission denied、error | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Token list | Prefix、name、scope、created、expires、last used | secret never displayed | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Create token modal | Name、scope、expiration、submit | validation and disabled reason | `09-admin-settings-account-tokens.svg` |
| 2 | Function | One-time reveal | 只在建立後顯示完整 token 一次 | copy action; warning notice | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Revoke token | 撤銷 token | destructive confirmation | `09-admin-settings-account-tokens.svg` |
| 2 | Function | Expiration warning | 即將過期或已過期提示 | warning/danger dot chips | `09-admin-settings-account-tokens.svg` |

## 9. 邊界頁

| 層級 | 類型 | Route | 名稱 | 功能說明 | 狀態 / 卡控 | 設計稿 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Page | `/login` | 登入 | OIDC/SSO 登入入口 | redirect loading、disabled login reason | `03-auth-boundary-pages.svg` |
| 1 | Page | `/forbidden` | 權限不足 | 說明缺少的權限與 request access | no secret leakage | `03-auth-boundary-pages.svg` |
| 1 | Page | `/not-found` | 找不到頁面 | Route recovery、search、回首頁 | no decorative hero | `03-auth-boundary-pages.svg` |
| 1 | Page | `/Error` | 錯誤頁 | Correlation id、retry、copy diagnostics | IDs selectable; diagnostics redacted | `03-auth-boundary-pages.svg` |

## 10. 目前設計覆蓋狀態

| Area | Coverage | 缺口 / 後續 |
| --- | --- | --- |
| Pages | 所有目前 dashboard `@page` routes 都已列入功能樹 | 若新增 route，需同步補本文件與 Stitch matrix |
| Page functions | 已列出主要工作區、表格、表單、detail drawer、action、state | 實作前可再補 API field-level column spec |
| Safety controls | 已列出 disabled reason、typed confirmation、secret reveal、audit trail、pending/applied 區隔 | 需在 Blazor implementation checklist 轉成元件層驗收 |
| RWD / scroll | 已列出全站 scroll ownership 與 Tab S7 行為 | 需由 browser tests 覆蓋所有 route matrix |
| Loading / refresh / motion | 已列出 loading、refresh、stale、retry 與 motion token | 需在 app CSS/component 實作時對照 `10-rwd-loading-refresh-motion.svg` |

## 11. 逐頁逐功能設計對照

Product Design + Stitch 的 active implementation handoff 統一收斂到 [Quiet Signal vNext](context-hub-quiet-signal-vnext.md)。該文件包含 23 條 route matrix、Stitch project/design-system/session、共用 component contract、desktop/Tab S7/mobile RWD 與實作 gate；舊 Feature Atlas 與 repo SVG summary boards 已退休並移除。
