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

- `Memory Graph` 相關畫面以 Stitch 專案 `ContextHub Memory Graph` 為主基準。
- `Graph` refined explorer screen 是目前已確認可對照的主要設計稿。
- `Sources`、`Governance`、`Evaluation`、`Inbox` 目前仍缺正式 Stitch screen artifact，屬於已知缺口；新增 UI 變更時不能只看本機 screenshot，後續需補齊設計稿來源。

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

### 3.3 新頁面同步確認

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
