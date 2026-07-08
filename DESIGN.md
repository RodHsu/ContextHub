# ContextHub Design Notes

## 1. 目的

本文件定義 `ContextHub` 目前 dashboard / graph explorer 的設計基準、測試資料落點規則，以及遠端部署後的最小驗證標準。

它不是取代 [architecture.md](/w:/Repositories/WJCY/ContextHub/docs/architecture.md)，而是補足：

- UI / UX 基準要看哪裡
- 新頁面要怎麼同步確認
- 測試產物應該放哪裡
- 遠端升級後怎麼判斷系統真的正常

## 2. 設計來源

### 2.1 Active Product Design + Stitch baseline

- `ContextHub Product Design + Stitch UI/UX Design Language` 是 2026-07-08 起全站 UI/UX 的唯一 active 設計稿來源。舊版 full-site / scroll-shell / early operations-console 設計稿已退休，不再作為新 UI 決策依據。
  - project：`projects/4533056430393435785`
  - active design system：`assets/33c0c23e5fc4475e8852b7906cfe6bc6` (`ContextHub Operations`)
  - 設計語言文件：[context-hub-operations-console-design-language.md](/w:/Repositories/WJCY/ContextHub/docs/design/context-hub-operations-console-design-language.md)
  - SVG 保存版設計稿目錄：[/docs/design/svg/](/w:/Repositories/WJCY/ContextHub/docs/design/svg/)
  - 使用範圍：全站 dashboard route、共用 app shell、sidebar/topbar、command palette、page header、panel、table/code/diff scroll shell、detail drawer、modal、popover、toast、empty/loading/error state、refresh state、disabled reason、confirmation modal、token one-time reveal、RWD、animation 與 reduced-motion 行為。
  - 設計方向：現代 SaaS operations console；高資訊密度但不厚重，使用 tonal layering、hairline、refined dot chips、短導覽文字、清楚狀態語彙與防呆卡控。避免老派 enterprise admin、NOC wall、滿版 menu、巢狀卡片、裝飾性 gradient/blob、body horizontal scroll 與雙重垂直 scrollbar。
  - 參考來源轉譯：採 shadcn-admin 的 command search / grouped sidebar、Tabler 的 responsive dashboard hierarchy、TailAdmin / Flowbite Admin Dashboard 的 CRUD/forms/tables/drawers/auth surfaces，以及 Grafana / OpenObserve 類 observability workflow；只作為 UX pattern reference，不複製其 branding。
  - RWD 合約：`body` 不得出現水平捲軸；表格、JSON、code、diff、log cells 只能在自身 frame 內水平捲動；`641px-1024px` sidebar 自動收折到 `64px` icon rail；`<640px` 使用 overlay drawer；content area 擁有主要垂直捲動；sidebar、panel、table 不得產生雙重垂直捲軸；popover/menu/tooltip 不得被 overflow clipping；detail panel 在 tablet 疊到主內容下方，不遮蓋 table frame。
  - motion / loading / refresh：hover/focus 120ms、menu/popover 180ms、drawer 220ms、route transition 240ms、live-tail row fade 700ms；loading 使用 app top progress、panel/table/code skeleton；refresh 使用 live pulse、paused chip、stale banner、retry flow 與 request/correlation id。
  - 全站 route 對應：
    - Product Design strategy：`projects/4533056430393435785/screens/27112231d8494e448ad6e7fdf42eed86`，保存版 `docs/design/svg/00-product-design-strategy.svg`。
    - Modern visual direction：`projects/4533056430393435785/screens/d14425b3d81c4f299ed15f5b901628a7`，保存版 `docs/design/svg/00-modern-visual-direction.svg`。
    - UI system / states / motion：`projects/4533056430393435785/screens/d6284134138544659d04b026954d010f`，保存版 `docs/design/svg/01-ui-system-states-motion.svg`。
    - App shell / navigation / command：`projects/4533056430393435785/screens/116b0b12c50d4a31b8aa4025c10f7d9a`，保存版 `docs/design/svg/02-app-shell-navigation-command.svg`。
    - Auth / boundary pages：`projects/4533056430393435785/screens/11e2b475d8b541db953b6fd6b2b710ce`，對應 `/login`、`/forbidden`、`/not-found`、`/Error`，保存版 `docs/design/svg/03-auth-boundary-pages.svg`。
    - Operations overview / monitoring：`projects/4533056430393435785/screens/454b419a89264cc29d07e005029bda83`，對應 `/`、`/monitoring`，保存版 `docs/design/svg/04-operations-overview-monitoring.svg`。
    - Runtime / logs / jobs / performance：`projects/4533056430393435785/screens/80b6642ae1b249b3ab921d3ab67129e8`，對應 `/runtime`、`/logs`、`/jobs`、`/performance`，保存版 `docs/design/svg/05-runtime-logs-jobs-performance.svg`。
    - Knowledge graph / memories：`projects/4533056430393435785/screens/baa8629daf8741ef8469d0939d3fc0d8`，對應 `/graph`、`/memories`，保存版 `docs/design/svg/06-knowledge-graph-memory.svg`。
    - Sources / inbox / retention：`projects/4533056430393435785/screens/6d5ffa8ae48748ff8832e8a3f8d312fb`，對應 `/sources`、`/inbox`、`/retention`，保存版 `docs/design/svg/07-sources-inbox-retention.svg`。
    - Review / governance / evaluation：`projects/4533056430393435785/screens/8b98c7ea4f0645b2b8da732e7c6fa89a`，對應 `/chatgpt-proposals`、`/governance`、`/evaluation`，保存版 `docs/design/svg/08-review-governance-evaluation.svg`。
    - Admin / account：`projects/4533056430393435785/screens/992f81554dc64034807a9753b0523024`，對應 `/preferences`、`/storage`、`/security`、`/settings`、`/account/tokens`，保存版 `docs/design/svg/09-admin-settings-account-tokens.svg`。
    - RWD / loading / refresh / motion validation：`projects/4533056430393435785/screens/17e2fea5a4b347b9bbd3a8e485b73fe1`，對應全站 viewport 與 motion/loading/refresh 驗收，保存版 `docs/design/svg/10-rwd-loading-refresh-motion.svg`。
    - Feature Design Atlas：`projects/4533056430393435785/screens/450468dcfc354e9b9e706085e4cf86b2`，逐頁逐功能 route matrix，保存版 `docs/design/svg/11-feature-design-atlas.svg`。
    - Operations Feature Atlas：`projects/4533056430393435785/screens/8950c9e0f9a14b93ac4bf5b7283e6f90`，對應 `/`、`/monitoring`、`/runtime`、`/logs`、`/jobs`、`/performance`，保存版 `docs/design/svg/12-operations-feature-atlas.svg`。
    - Knowledge Feature Atlas：`projects/4533056430393435785/screens/79ba882411d44e13b94cfc6d1de72013`，對應 `/graph`、`/memories`、`/retention`、`/sources`，保存版 `docs/design/svg/13-knowledge-feature-atlas.svg`。
    - Review and Governance Feature Atlas：`projects/4533056430393435785/screens/f296315d38f5483b8845d1352d75d3c5`，對應 `/inbox`、`/governance`、`/evaluation`、`/chatgpt-proposals`，保存版 `docs/design/svg/14-review-governance-feature-atlas.svg`。
    - Admin, Account and Boundary Feature Atlas：`projects/4533056430393435785/screens/b80a7c7dbf8f4c2f91151dde97db5a38`，對應 `/preferences`、`/storage`、`/security`、`/settings`、`/account/tokens`、`/login`、`/forbidden`、`/not-found`、`/Error`，保存版 `docs/design/svg/15-admin-boundary-feature-atlas.svg`。
  - 注意：目前 Stitch MCP 提供 screen/design generation 與讀取工具，但沒有 expose 原生 SVG export；`docs/design/svg/` 中的 SVG 是依 Product Design + Stitch screens 與設計合約製作的 repo 保存版 summary board，不宣稱為 Stitch 原生匯出。

### 2.2 Repo 內文件角色

- 架構與服務邊界：看 [architecture.md](/w:/Repositories/WJCY/ContextHub/docs/architecture.md)
- MCP 操作方式：看 [mcp-usage-guide.md](/w:/Repositories/WJCY/ContextHub/docs/mcp-usage-guide.md)
- UI / 驗證 / 部署檢查基準：看本文件
- 全站功能盤點與頁面/功能層級：看 [context-hub-feature-inventory.md](/w:/Repositories/WJCY/ContextHub/docs/design/context-hub-feature-inventory.md)

## 3. Dashboard UI 基準

### 3.1 基本原則

- 維持 dashboard 作為內網 admin console 的高資訊密度，不做行銷型 landing page。
- 新頁面必須延續既有 page header、section wrapper、panel spacing 與 dense table/card 語言，不要自行長出另一套 layout。
- 能用現有 page section pattern 解決的問題，不新增特例樣式。

### 3.2 Graph explorer 基準

- 三欄布局要以中間 graph explorer 為重心，左右欄只保留輔助資訊與控制。
- 初始 render 必須先追求可讀性，再追求一次塞進全部空白邊界。
- 小型圖譜不應被過大的 baseline canvas 壓到過度縮放。
- fullscreen 與 normal view 都必須維持節點、連線與側欄的可讀性。
- 空狀態要明確說明是「沒有可繪製節點、篩選無交集、或 graph index 尚未刷新」這類資料狀態；不要只顯示空白 canvas 或模糊錯誤文字。
- 記憶圖譜的定位是脈絡檢視與關聯探索，不是 memory list 的替代品。若使用者需要逐筆檢索與欄位比對，應回到 `Memories` 頁；Graph 頁只承擔 hub、鄰域、explicit link 與 similarity layer 的視覺化。

### 3.3 Dense admin data layout

- 表格欄位過多時，優先合併同類資訊，而不是縮小字級或讓內容硬換行。例如 `Memories` 將 scope 併入 project、status 併入 type、tags 併入 source；`Jobs` 明確保留 job id 並避免換行。
- 工具性 form 不應佔用大型空白 panel；只保留必要欄位、直接 action 與一行操作說明。
- 所有可疑或容易誤解的 runtime / telemetry 指標都應有 `InfoPopover`，同一頁內不能只有部分 KPI 有說明、部分明細沒有說明。
- `InfoPopover` 必須能越過 panel 邊界顯示；新增 scroll shell 或 overflow clipping 時，要同步檢查 tooltip z-index 與 clipping。

### 3.4 新頁面同步確認

新開發頁面至少要做三件事：

1. 對照既有 design language，確認 header、section、action row、table/card 密度一致。
2. 跑 browser / screenshot 驗證，確認 desktop 與較窄 viewport 沒有 overlap、unexpected overflow、失衡留白。
3. 若頁面已穩定且會持續演進，應補對應 Stitch artifact，避免之後只能拿實作互相比對。

## 4. 測試資料與產物規則

### 4.1 允許的落點

測試資料、browser artifacts、暫存輸出只允許放在以下兩類位置：

- 對應 repo 內明確約定的目錄
- 系統暫存目錄，例如 `Path.GetTempPath()`

### 4.2 不允許的做法

- 測試時把暫存檔散落在 repo root
- 把一次性驗證產物寫進不受控的工作資料夾
- 讓測試自己在未知路徑留下 screenshot、db、cache 或 export 檔

### 4.3 目前專案慣例

- Dashboard browser test artifacts 與 Data Protection test path 走系統暫存。
- 若需要保留可追蹤的 repo 內產物，應放進有明確用途的目錄，例如 `docs/`、`deploy/`、`.agent/local/`，不能臨時發明新散落路徑。
- `deploy/release-*` 只用於明確的 release artifacts，不視為一般測試暫存空間。

## 5. 遠端部署驗證基準

### 5.1 目標

遠端部署完成不代表系統可用；至少要確認：

- dashboard UI 可登入
- `mcp-server` 回應正常
- snapshot collector 真的有在寫資料
- dashboard 與 `mcp-server` 沒有版本落差到造成功能表面可開、實際不可用

### 5.2 最小檢查清單

```text
Remote deploy completed
  -> GET /health/ready (dashboard / mcp-server)
  -> GET /api/status
  -> GET /api/dashboard/monitoring
  -> 檢查 Docker / resource / monitoring sections 是否有真正 snapshot
  -> 再做 UI 頁面檢查
```

至少要確認：

- `GET /health/ready`
- `GET /api/status`
- `GET /api/dashboard/monitoring`
- dashboard `/login`

### 5.3 判定正常的條件

`/api/dashboard/monitoring` 中以下 sections 不應長期停在：

- `refreshIntervalSeconds = 0`
- `lastError = "Snapshot unavailable."`
- `warning = "尚未收到背景快照。"`

至少以下 key 應該有有效背景快照：

- `dockerHost`
- `dependencyResources`
- `resourceChart`
- `monitoringStats`

若這些 section 全部是 unavailable，而 `statusCore` / `dependenciesHealth` 正常，優先懷疑：

- 只更新了 dashboard，`mcp-server` 沒有一起更新
- 遠端 `mcp-server` 仍在跑舊 image / 舊 collector
- 部署後實際 compose 沒有完成對應服務替換

### 5.4 2026-04-23 實際觀察

2026-04-23 檢查 `developer02.local` 時：

- `http://developer02.local:8091/health/ready` 正常
- `http://developer02.local:8092/health/ready` 正常
- `http://developer02.local:8092/api/status` 顯示 `buildTimestampUtc = 2026-04-12T08:30:00+08:00`
- `http://developer02.local:8092/api/dashboard/monitoring` 中 `dockerHost`、`dependencyResources`、`resourceChart`、`monitoringStats` 全為 unavailable，且 `refreshIntervalSeconds = 0`
- `deploy/release-20260423/contexthub-images_20260423-0919.manifest.json` 只包含 `dashboard` image

這組訊號代表：

- 本次遠端更新至少沒有完整覆蓋 `mcp-server`
- `Docker host snapshot unavailable.` 目前不能當成單一 docker socket 權限問題來看
- 要先把部署完整性與 image/version 對齊查清楚，再去追 collector / runtime 細節

## 6. 實務決策

### 6.1 目前值不值得拆服務

以目前問題來看，不值得先把 dashboard monitoring 再拆成獨立服務。

原因：

- 現在的主要風險在 observability 與 deployment consistency，不在服務邊界
- 先拆服務只會增加部署與驗證矩陣
- 現階段更需要的是確保同一批 release 的 image、compose 與 post-deploy checks 一致

### 6.2 現在應優先做什麼

1. 讓每次遠端升級後都有固定 post-deploy verification。
2. 確認 release manifest 與實際更新服務一致，不要只看其中一個 image tar。
3. 補齊新頁面的 Stitch artifacts，降低 UI 回歸只能靠肉眼比對的風險。
