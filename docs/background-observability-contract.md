# 背景工作與監控契約

ContextHub 共用 `audit.authority_outbox_events`、`monitoring` projection 與 background run 語意，讓各 domain 保留自己的業務生命週期。REST 與 MCP 沿用 Application use cases，不另建 monitoring 或 memory API 服務。

## 資料責任

| 資料 | 權威與恢復方式 | 不能推論的事項 |
| --- | --- | --- |
| Authority audit、outbox | 業務交易內可靠寫入、append-only；delivery 狀態另存，不抽樣 | monitoring 成功不等於業務提交成功，亦不能替代安全 audit |
| Activity projection | 依 outbox/cursor/generation 可重播、重建 | 最近一次成功不是最新業務邊界 |
| Graph projection | global scope、revision、lease/fence 發布，增量與 full reconciliation | metadata/explicit edges 仍有全 scope 讀取，不代表所有成本皆增量 |
| Operational cache 分鐘 metrics | 程序內有界緩衝、instance/boot 冪等落盤 | crash 未 flush 部分未知；省略 raw 的數字不能保證重建 |
| Redis snapshot、counter | 加速與自啟動觀測，可過期或中斷 | 不是 durable authority、全副本 inventory 或期間歷史的唯一來源 |

## 共用欄位與入口

| 範圍 | 現行 producer 與欄位 | Canonical surface |
| --- | --- | --- |
| Authorization | `AuthorityOutboxCapture` 的 Authorization category，revision、project、tenant、security-critical | Operations authority/projection/background runs |
| Storage、Files | ManagedFiles category；managed object、transfer lifecycle 與 access event | Operations logical storage，Files 管理入口 |
| Secrets、SSH | Secrets category，lease/version/grant/policy/audit；Use 與 Reveal 分離 | Secrets、SSH domain surface；Operations activity 摘要 |
| Skills | Skills category，resolution/version/reindex 與 telemetry 各自保留 domain lifecycle | Skills 管理入口；Operations activity |
| AgentExecution | AgentExecution category，execution/event/resolution；業務狀態不轉成 background run 狀態 | Operations queue、AgentExecution domain surface |
| Graph、Knowledge | migration 053 的 KnowledgeRevision event、durable revisions、global Graph generation/lease/dirty state | Graph refresh status endpoint、cache telemetry、Monitoring |
| Governance | governance run/receipt/provenance、完整 scope 與可靠性各自有權威契約 | Governance domain surface；不能只靠 generic projection 判定治理完成 |

共用 Activity run 提供 scope、mode、generation、authority boundary、cursor、expected/scanned、coverage、stale/drift/repaired/rebuilt/failed、attempt/max attempts、last success、eligible time 與 terminal status。Domain 有專用 receipt 或安全資訊時，維持專用契約；未提供的欄位不能捏造為零或 Complete。

`GET /api/dashboard/operations` 是受保護的共用入口。Projection 的 `AuthoritySequence` 保留 projector 保存的邊界；新增 `ObservedAuthoritySequence` 表示本次觀測的同 tenant/project authority 邊界。Lag、IsStale 依該新邊界與 cursor 判定，因此新提交、尚未投影的事件不會被顯示為 Current。舊客戶端仍能讀舊欄位；新 UI 使用 observed 值，缺少時相容回舊欄位。Lag 是 sequence distance，不能當作精確待處理事件數或秒數。

## 健康與覆蓋率

- alive、ready 由既有 health probes 判定。Dependency degraded 需對應依賴觀測；projection stale 表示尚未達 authority 邊界；這些都不能由命中率代替。
- background run 的 Completed 是該 run 的終態，不能表示下一個業務提交也已投影。
- `Observed` 只描述已觀測程序；`ExpectedInventoryStatus=Unknown` 表示沒有權威部署副本清單，不能宣稱全副本完整。
- replica/boot 心跳缺口、drop、unclean shutdown 與歷史窗口不足顯示 Partial。預期副本與擴縮/退役對帳仍需獨立的部署 inventory；不從 Redis key 數量猜測。
- 靜態 MCP schema、server/runtime、authenticated UI、fresh ChatGPT host 與自然排程 provenance 各自出證，不互相替代。

## 容量與保存政策

`cache_metric_minutes`、`cache_metric_coverage`、`cache_metric_boots` 沒有自動 TTL 或 partition。24H/3D/7D/14D/30D 是查詢窗口，不能當作已核准保存期限。

分鐘列估算為 active process-minutes × active kind/traffic pairs。固定詞彙最多 13 × 4 pairs；單一持續 process 的 30D 理論上限為 2,246,400 列，coverage 約 43,200 列。實際 boot 重疊、UPSERT/WAL、row-lock、索引、dead tuples/autovacuum 及查詢成本需量測。安全 audit、raw diagnostics 與 aggregates 不能直接套用同一個 cleanup 政策。

[唯讀容量 SQL](../tools/performance/cache-capacity.sql) 只查 relation/statistics/schema 與 query plan，不掃描 30D 資料、不刪除或修改保存政策。SQL 有 10 秒 statement timeout、2 秒 lock timeout；必須透過核准的 PostgreSQL session 使用，秘密不得放進 command line。資料表缺少時不得把部分輸出當作 release PASS。

## 重跑與發布

`CacheReleaseAcceptanceTests` 驗證 cache-disabled 回退不讀舊 payload、不寫 replacement，仍拒絕超出 project grant 的查詢且保留 durable revision；另外核對 migrations receipt replay 與唯讀容量 SQL。

`CacheReleaseMigrationTests` 使用隔離資料庫核對 052 fixture 在 053–055 前後兩種安裝順序及重播。Fixture 是相容性測試快照，不把人工裁決產品實作加入此發布；其實際 source 若漂移須重新核對。

同序列 cold/warm HTTP 測試預設 32 samples、1 run；以 `CONTEXTHUB_CACHE_BENCHMARK_SAMPLES=1000`、`CONTEXTHUB_CACHE_BENCHMARK_RUNS=3` 可收集更多逐請求樣本。counter 與 origin 以每個已登記 request 的 AsyncLocal frame 歸屬，背景請求不混入；回應完成 callback 收集 server duration，另保留 client elapsed。p99 少於 10,000 samples 標示 Exploratory。資料是單文件、deterministic embedding 的隔離 authenticated TestServer；沒有網路 RTT、代表性正式流量或受控 Graph on/off，不能拿這些結果宣稱 Production p95 改善。

安全回退可使用同一新版映像設定 `Memory__RedisCache__Enabled=false`，Docker Compose 對應 `REDIS_CACHE_ENABLED=false`，停用物件快取 read/set 而保留新版授權與 durable revisions。開關預設 true；所有 Memory 使用者一致設定，既有受保護的正式 compose/env 必須先對照，不用開發範本取代。這是物件快取開關，不是停用 Redis 服務，也不會清除既有 key；其吞吐成本須在切換前測量。不能改用已知不安全的舊 reader 作為安全回退。

相關契約：[快取一致性與監控](cache-consistency-and-monitoring.md)。
