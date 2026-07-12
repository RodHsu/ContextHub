# ContextHub Quiet Signal vNext

> Product Design 與 Stitch 共同產出的全站 UI、UX、CIS 與 RWD 設計基準。此文件獨立於既有 `DESIGN.md`，在實作切換完成前不取代 production contract。

## 設計來源

- Product Design 選定方向：方案一 `Quiet Signal`
- Stitch project：`projects/952128967416801377`
- Stitch design system：`assets/afb02499c2fe4a4ebfc0f224d9d7f67a`
- 總覽與 design-system session：`4344313716496177182`
- Tab S7 記憶資料 session：`6710494304141279476`
- 維運監控 session：`16526291107183662673`
- 知識與審核 session：`14964968641656466704`
- 管理與邊界 session：`9265654853941835915`
- 補充治理與 RWD session：`10674733677898668959`

## CIS

`Quiet Signal` 的產品性格是安靜、精準、可信任與快速。介面不再使用霓虹 cyberpunk、終端機式密集外框或卡片牆。

- Canvas：Ink `#0B0F14`
- Surface：Graphite `#141A21`
- Primary：Teal `#2DD4BF`，只用於主要操作、選取與 focus
- Text：Fog `#E8EDF2`
- Success：Mint；Warning：Amber；Danger：Coral；Info：Sky
- UI 與內文使用 Inter；ID、JSON、log、timestamp 才使用 monospace
- 內文預設 14–16px；page title 24px；letter spacing 不得為負值
- 容器最大 8px radius；陰影只用於 menu、drawer 與 dialog
- 狀態必須使用 icon、文字與色彩共同表達，不得只靠顏色

## App Shell 與 RWD

| Viewport | Navigation | Content composition |
| --- | --- | --- |
| `>= 1440` | 248px expanded sidebar | 12-column grid；master-detail 可用 7/5 split |
| `1025–1365` | 72px icon rail + tooltip | 收斂次要欄位；detail 使用 bounded drawer |
| `768–1024` | 64px rail 或 overlay drawer | chart、queue、list、detail 改為單欄或 route transition |
| `< 768` | top app bar + overlay navigation | list-to-detail；不擠壓桌面 table |

硬性規則：

- Document 與 page body 不得出現水平捲軸。
- Table、JSON、code、diff、payload 與 log 只能在自己的 named frame 內水平捲動。
- 一個 component 最多擁有一個 scroll axis；只有專用 reader 可以有局部垂直捲動。
- Reader 必須有 visible scrollbar、stable gutter、selectable text、copy action 與 wrap toggle。
- Tablet 與 mobile 的底部操作不可 fixed 覆蓋內容；應留在正常 flow 或使用有對應 safe-area padding 的 sticky region。
- Sidebar 在中寬自動收折，不得展開成整頁 menu。

## 共用元件 Contract

- `AppShell`：sidebar、rail、drawer 三態，共用單一 navigation model。
- `PageHeader`：title、context、local updated time、primary action；禁止 hero 化。
- `MetricStrip`：同一 surface 內以 divider 分組，不使用獨立 KPI 卡片牆。
- `DataGrid`：sticky header、44px row、column priority、local horizontal overflow、row action menu。
- `MasterDetail`：desktop 7/5；tablet 與 mobile 使用 drawer 或 full-width detail route。
- `ContentReader`：selectable、copy、wrap、visible scrollbar、stable gutter、bounded height。
- `FormSection`：label above control、inline validation、dirty state、save guard、disabled explanation。
- `RefreshState`：保留現有資料並顯示 inline progress；成功後更新 client-local timestamp。
- `DestructiveDialog`：顯示 object name、impact preview 與 consequence；高風險操作使用 typed confirmation。
- `AsyncState`：default、loading、stale、empty、error、retry、success 都必須保留版面結構。

## Route 對照

| Route | Stitch 設計組 | 主要 archetype |
| --- | --- | --- |
| `/` | Overview | metric strip + health trend + incident queue + service table |
| `/monitoring` | 維運監控 | dominant chart + annotated events + service table |
| `/runtime` | 管理與邊界 | effective/source config + diff + rollback |
| `/logs` | 維運監控 | dedicated selectable log reader |
| `/jobs` | 維運監控、補充 RWD | queue table + bounded detail drawer |
| `/performance` | 維運監控 | KPI strip + percentile/throughput + endpoint table |
| `/graph` | 知識與審核 | unframed graph workspace + node drawer |
| `/memories` | 知識與審核、Tab S7、mobile | list + content/revision/chunk readers |
| `/sources` | 知識與審核 | connector list + sync history + payload reader |
| `/retention` | 知識與審核、Tab S7 | candidate list + guarded decision detail |
| `/inbox` | 知識與審核 | prioritized proposal queue + diff review |
| `/chatgpt-proposals` | 知識與審核 | AI proposal queue + before/after + JSON |
| `/governance` | 補充治理與 RWD | policy list + enforcement + publish guard |
| `/evaluation` | 管理與邊界 | evaluation runs + evidence reader |
| `/settings` | 管理與邊界 | grouped form + dirty save bar |
| `/security` | 管理與邊界 | policy/session controls + danger zone |
| `/storage` | 管理與邊界 | capacity + namespace table + cleanup impact |
| `/preferences` | 補充治理與 RWD | personal settings + local-time preview |
| `/account/tokens` | 管理與邊界 | token lifecycle + one-time secret reveal |
| `/login` | 管理與邊界、mobile | focused authentication + validation/recovery |
| `/forbidden` | 管理與邊界 | permission explanation + recovery action |
| `/not-found` | 管理與邊界 | route recovery + search/home action |
| `/Error` | 管理與邊界 | retry + selectable diagnostic ID |

## 關鍵 Mockups

本機保存稿位於 [`mockups/quiet-signal`](mockups/quiet-signal)。Stitch 仍是完整 canvas 與 HTML prototype 的 authoritative source。

- [`overview-desktop.png`](mockups/quiet-signal/overview-desktop.png)
- [`monitoring-desktop.png`](mockups/quiet-signal/monitoring-desktop.png)
- [`performance-desktop.png`](mockups/quiet-signal/performance-desktop.png)
- [`memories-desktop.png`](mockups/quiet-signal/memories-desktop.png)
- [`memories-tablet-list.png`](mockups/quiet-signal/memories-tablet-list.png)
- [`memories-tablet-detail.png`](mockups/quiet-signal/memories-tablet-detail.png)
- [`chatgpt-proposals-desktop.png`](mockups/quiet-signal/chatgpt-proposals-desktop.png)
- [`governance-desktop.png`](mockups/quiet-signal/governance-desktop.png)
- [`settings-desktop.png`](mockups/quiet-signal/settings-desktop.png)
- [`boundary-states-desktop.png`](mockups/quiet-signal/boundary-states-desktop.png)
- [`login-mobile.png`](mockups/quiet-signal/login-mobile.png)

## 實作 Gate

在 production UI 取代前，每個 route 必須通過：

1. `1920x1080`、`1366x768`、`1280x800`、`1024x768`、`800x1280`、`390x844` screenshot QA。
2. Body 與 document 無水平 overflow。
3. Sidebar 在規定 breakpoint 自動切換 sidebar、rail、drawer。
4. Table、reader、drawer 與 sticky action 不重疊、不穿出 surface。
5. Light/dark、loading、empty、error、stale、disabled、focus、destructive confirmation 均有可操作狀態。
6. Local time、selectable technical value、copy feedback、keyboard focus 與 reduced-motion 行為符合 contract。

