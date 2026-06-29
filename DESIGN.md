# ContextHub Design Notes

## 1. 目的

本文件定義 `ContextHub` 目前 dashboard / graph explorer 的設計基準、測試資料落點規則，以及遠端部署後的最小驗證標準。

它不是取代 [architecture.md](/w:/Repositories/WJCY/ContextHub/docs/architecture.md)，而是補足：

- UI / UX 基準要看哪裡
- 新頁面要怎麼同步確認
- 測試產物應該放哪裡
- 遠端升級後怎麼判斷系統真的正常

## 2. 設計來源

### 2.1 Stitch baseline

- `Full-site UI/UX refresh` 以 Stitch 專案 `ContextHub Full Site UI UX Refresh` 作為 2026-06-29 起的全站基準：
  - project：`projects/952128967416801377`
  - design system：`assets/c68e3953f5af4004b3e2e7e72e91568c` (`ContextHub Admin`)
  - 使用範圍：所有 dashboard 可達 route，包含主導覽頁、管理頁、Token 頁、ChatGPT 寫入審核、Login、Forbidden、NotFound、Error。
  - 設計方向：dark dense admin console、短導覽 label、任務分組 sidebar、共用 page header / panel / table / form / empty state / detail panel / scroll shell；不做 landing page、decorative gradient/orb 或大型 marketing whitespace。
  - RWD 驗收：以 `1920x1080`、`1366x768`、`1280x800`、`1024x768`、`800x1280`、`390x844` 為主要 viewport；連續 resize `1920 -> 1600 -> 1366 -> 1280 -> 1024 -> 800 -> 768 -> 390` 不得出現 body horizontal overflow、panel overlap、scroll trap 或 tooltip/menu clipping。
- 全站 route 對應 Stitch 設計稿：
  - Auth / boundary pages：`projects/952128967416801377/screens/d8431ed48c0d4b2a9af1bd9650481541`，對應 `/login`、`/forbidden`、`/not-found`、`/Error`。
  - Operations Monitoring：`projects/952128967416801377/screens/1916647a3e6e48cfa2c4e85007dc6e75`，對應 `/`、`/monitoring`、`/runtime`、`/logs`、`/jobs`、`/performance`。
  - Knowledge Work / Review：`projects/952128967416801377/screens/301434fd263547918f18a6e772217a53`，對應 `/graph`、`/memories`、`/retention`、`/sources`、`/inbox`、`/governance`、`/evaluation`、`/chatgpt-proposals`。
  - Admin Settings / Personal Account：`projects/952128967416801377/screens/ce85635f0d76482d8cd249835acfecbf`，對應 `/preferences`、`/storage`、`/security`、`/settings`、`/account/tokens`。
- `Memory Graph` 相關畫面以 Stitch 專案 `ContextHub Memory Graph` 為主基準。
- `Graph` refined explorer screen 是目前已確認可對照的主要設計稿。
- `Token 節省量` compact dashboard baseline 已補 Stitch artifact：
  - project：`projects/2525745562578529015` (`ContextHub Context Savings Compact Dashboard`)
  - screen：`projects/2525745562578529015/screens/6ae43256689f4181876132939a41b4d5`
  - design system：`assets/96724f4c496e4ceba84d3765d600da5d`
  - 使用範圍：首頁 `Token 節省量` compact insight strip、`/monitoring` Token 節省量 card；維持 dark dense admin console，不做大型 landing-style panel。
  - 指標語彙：以 token 為計算單位，標題與欄位使用 `Token 節省量`，各視窗合併呈現 `24H/3D/7D/30D 節省量 / 樣本量`，避免使用容易誤解為一般對話數量的 `對話節省量`。
  - token 口徑：卡片主畫面保持 compact，只顯示節省率、節省 token 與樣本量；`精準 token` / `估算 token` / exact coverage 放在 tooltip，避免 8 欄視窗在 app-browser 寬度下被文字撐高。
- 2026-06-21 browser feedback pass 期間，Codex 可用工具沒有 Stitch connector；該輪只依既有 baseline 修正。2026-06-29 已補 full-site Stitch project，後續 UI 變更應優先對照上述全站 screens。

### 2.2 Repo 內文件角色

- 架構與服務邊界：看 [architecture.md](/w:/Repositories/WJCY/ContextHub/docs/architecture.md)
- MCP 操作方式：看 [mcp-usage-guide.md](/w:/Repositories/WJCY/ContextHub/docs/mcp-usage-guide.md)
- UI / 驗證 / 部署檢查基準：看本文件

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
