# 有界請求到達／併發觀測

`RequestArrivalObservations` 為預設關閉的診斷功能，不改變 retrieval、cache、授權或 tokenizer 契約。啟用需設定 `Enabled=true` 與固定的 `UntilUtc`（UTC，啟動時未來 24 小時內）。重啟沿用同一個截止時間，不重新延長視窗。過期、缺少或不合理的截止時間會關閉 capture。

範例使用一般設定結構；截止時間由受控發布設定提供：

```json
{
  "RequestArrivalObservations": {
    "Enabled": false,
    "CaptureSchemaVersion": 1,
    "UntilUtc": null,
    "Capacity": 2048,
    "MaximumSamples": 100000
  }
}
```

- HTTP arrival 邊界為 MCP server 與 chat-agent Gateway 的 ASP.NET application pipeline，位於 origin/authentication 檢查前；MCP server 涵蓋 `/api` 與 `/mcp`，Gateway 涵蓋內部 `/mcp`。公開 `/mcp-chat` 由 reverse proxy 轉送，其路由對應須另行驗證。不宣稱量到 socket arrival、proxy 排隊或 client RTT；每個 Gateway surface／副本均需獨立納入 inventory。
- MCP tool dispatch 另記一層。工具樣本以 process 內產生的 HTTP sequence 關聯；HTTP 與 tool 不可相加當成獨立請求數。
- 記錄實際開始 UTC、單調 timestamp、開始時同層 active count、完成 UTC、單調 duration 與固定分類。不得從 `created_at-duration` 推算開始時間。
- 不記錄 query、arguments、ProjectId、tenant、owner、grant、headers、cookies、credentials、cache key、原始 path 或 exception payload。未知操作一律分類為 `other`。Boot 使用隨機 ID，不使用 hostname。
- 記憶體 staging 有界；DB writer 每兩秒異步寫入新的 monitoring 表，失敗保留待重試的樣本，不阻塞 request。樣本版本避免 start acknowledgement 消除同時到達的 completion；DB upsert 支援重試。
- 每 boot 最多接受設定的樣本總量，超限仍記 coverage/drop 計數。已入庫 start 若遺失 completion，保持 `inflight`，不能當完整 latency 樣本。Hard crash、缺少 heartbeat、unfinished、drops、未量測副本與跨副本時鐘偏差都必須明示為 coverage 限制。
- Migration 057 只新增表／索引；ProjectId v1 bridge 可以忽略此診斷資料。Application rollback 不刪除表，也不重置已有樣本。不新增自動刪除既有資料的 retention job。

啟用的 process 啟動時，`Memory.Infrastructure.RequestArrivalObservationFlushService` 會嘗試發出一次 Information 事件 `5701 / RequestArrivalCaptureStarted`。欄位只包含此次 capture 的 `SchemaVersion`、隨機 `ObservationBootId`、固定 `StartedAtUtc`／`UntilUtc`、`TimestampFrequency` 與 process-local `ProcessId`。關閉或不合法的 capture 不發出事件；此啟動事件的 logger 失敗不影響 capture，但來源對應證據因此不足。事件不包含 caller-controlled 值、container 名稱或服務設定。

來源對應必須以受控 container 的結構化 console 紀錄，搭配獨立的 container/image/process inventory receipt 驗證。捕捉期間可使用既有 `Logging:Console:FormatterName=json` 與該 category 的 Information 設定；必須讀回實際 runtime 設定與紀錄，不能由 repo 設定推定已生效。以固定 category、EventId 與欄位集合解析完整 JSON record，不以純文字片段比對。Docker JSON log driver 本身不代表 application 使用 JSON formatter。DB log、可搜尋文字或呼叫者提供的工具名稱均不可當成來源 authority；缺少、重複、格式錯誤或有歧義的 boot 對應維持 `NotVerified`。這是受控證據鏈，不是事件本身的密碼學認證。

此最小捕捉可提供 arrival／同副本 overlap 證據。代表性效能驗收仍需同窗口的 replica inventory、時鐘精度、actor/grants 與資料 snapshot、query shape／大小、mutation／model／Graph 背景量與完整 coverage 對照；僅有這些 timing 樣本不代表整個 Production workload 已被重建。Production 啟用依原 release authority 與相容 rollback 流程進行。

## v2 的 RAW clock 與分類

`CaptureSchemaVersion=2` 僅改變此診斷功能；業務的 `TimeProvider`、timer、授權期限、tokenizer 與 embedding profile 保持既有設定。v2 的正式執行環境為 Linux x64。啟用前須驗證 native `clock_gettime` 與固定 clock domain；其他平台或 native clock 失敗會拒絕完整量測，不使原 HTTP／MCP 請求失敗。未啟用與 v1 不呼叫 native clock。

v2 在原 UTC／Stopwatch 欄位之外，保留 request begin／finish 的 `CLOCK_MONOTONIC_RAW` 前後讀取界限，並記錄 kernel boot ID、time namespace、MONOTONIC／BOOTTIME namespace offsets 與 clocksource。原有 UTC／Stopwatch 值不補寫、不校正。截止仍為固定 `UntilUtc`，並額外限制啟動時決定的 RAW 最大 span；UTC 回退不能延長 capture，UTC 前跳造成的截短不能算完整視窗。

DB writer 每 0.5 秒檢查 domain 與 clock，按 RAW 秒最多留一個 clock anchor，另保留啟動與所有 in-flight 完成後的 terminal anchor。每 boot 最多 86,402 個 anchors，staging queue 與 sample admission 仍有界；clock read、domain、RAW order、suspend 或 queue／anchor 超限均留下 coverage failure／drop。BOOTTIME 相對 MONOTONIC 的變化用於偵測 suspend；UTC 相對 RAW 的不連續另行記錄。不同物理時間與 wall clock 品質不能互相代替。

Migration 058 僅新增 v2 欄位與 `monitoring.request_arrival_clock_anchors`；057 的欄位、分類約束與 migration checksum 保持不變。v1 rows 不回填 RAW。v2 boot revision 為 HTTP／tool starts 與 completions、window-closed、clock checks、clock failures 的合計；anchor sequence 僅在建立 anchor admission 時增加，同一 RAW 秒的檢查不增加 sequence。容量不足時保留 drop 與 sequence 缺口，不能判定為完整量測。

分類僅使用固定的 API method／matched endpoint template 或已知 MCP tool registry，保存 allowlisted request family、method、宣告 `Content-Length` 的大小區間。未知路由、工具或缺少長度維持 `unknown`；不讀取 request body、不保存 route template、原 path、query、arguments、actor 或其他敏感資料。MCP tool 的 arguments 大小不能以 HTTP envelope 長度代替。舊 `operation=other` 與 v2 `request_family=unknown` 仍代表未能分類的工作，不能自行當成 health traffic 排除。

跨副本物理 arrival 驗收還需整窗同 domain 的獨立校準，例如 Windows QPC 對 RAW 的雙側 anchors；來源 inventory、native read brackets、最大 gap、rate drift、suspend、terminal flush 與 event mapping uncertainty 都須驗證。首尾兩點、短期 feasibility、wall time 正確或僅有同副本 overlap 不足以證明完整跨副本對齊。安全分類與大小區間也不足以重建 payload／query shape；代表性 replay 仍須以受控資料集與明確工作模板驗收。

相容 application rollback 先停止 admission、完成 drain／flush 並移除 capture 設定，再切回含 migration 056 reader／worker 相容能力的 bridge。bridge 保留 alias 與 v2 rows，但不宣稱繼續執行 v2 capture。獨立 read-only export 可讀取保留的完整 v2 ledger；不得以 database restore 取代此正常 application rollback 能力。
