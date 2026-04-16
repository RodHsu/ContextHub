# ContextHub Architecture

## 1. 目標與定位

ContextHub 是一套給 Codex 使用的外部知識系統。它的目標不是把所有歷史資料直接塞進 prompt，而是讓 Codex 在需要時透過 MCP 或 REST 查詢：

- 長期記憶
- 專案事實與設計決策
- 程式碼 / 文件 / 錯誤脈絡
- 使用者顯式偏好
- 部署後的 runtime logs
- 工作上下文與建議測試

它的設計原則是：

- 單機部署
- 純 Docker Compose
- 無外部 embedding API
- PostgreSQL 作為唯一永久資料來源
- Redis 只做快取與協調
- MCP 與 REST 共用同一組 application use cases

## 2. 高階拓樸

```text
Codex / REST Client / Dashboard User
    |
    v
Memory.McpServer
  - /mcp
  - /api/*
  - /health/*
  - /api/performance/measure
    |
    +--> Memory.Application
    |      - memory use cases
    |      - working context builder
    |      - user preference orchestration
    |      - log promotion
    |      - reindex dispatch
    |
    +--> PostgreSQL + pgvector
    |      - memory items
    |      - revisions
    |      - chunks
    |      - vectors
    |      - jobs
    |      - runtime logs
    |
    +--> Redis
    |      - cache version
    |      - search/context cache
    |      - job signal
    |
    +--> embedding-service
           - ONNX runtime
           - tokenizer
           - /embed
           - /info

Memory.Worker
    |
    +--> PostgreSQL jobs
    +--> embedding-service
    +--> Redis signal

Memory.Dashboard
    |
    +--> Memory.McpServer REST APIs
    +--> Docker socket (read-only)
```

## 3. 服務分層

### 3.1 `Memory.McpServer`

職責：

- 提供 MCP endpoint：`/mcp`
- 提供 REST 診斷與查詢 API：`/api/*`
- 提供 health endpoints：`/health/live`、`/health/ready`
- 提供 runtime status 與 performance probe
- 做 request validation、transport mapping、ProblemDetails

不負責：

- 實作核心業務邏輯
- 直接操作向量或背景 job 流程

### 3.2 `Memory.Application`

職責：

- `memory_upsert` / `memory_update` / `memory_get`
- hybrid search orchestration
- `build_working_context`
- `user preference` 顯式管理
- `promote_log_slice_to_memory`
- `enqueue_reindex`
- performance probe orchestration

這層是系統的用例核心，REST 與 MCP 都共用這一層。

### 3.3 `Memory.Domain`

職責：

- 定義核心 enum 與 entity
- 提供 memory scope/type、job type/status、vector status 等共識

目前核心 enum 包含：

- `MemoryScope`
  - `User`
  - `Repo`
  - `Project`
  - `Task`
- `MemoryType`
  - `Fact`
  - `Decision`
  - `Episode`
  - `Artifact`
  - `Summary`
  - `Preference`
- `UserPreferenceKind`
  - `CommunicationStyle`
  - `EngineeringPrinciple`
  - `ToolingPreference`
  - `Constraint`
  - `AntiPattern`

### 3.4 `Memory.Infrastructure`

職責：

- EF Core `DbContext`
- PostgreSQL / pgvector 資料存取
- Redis cache / signal
- health checks
- embedding provider adapter
- database log sink

### 3.5 `Memory.EmbeddingServer`

職責：

- 解析 `EMBEDDING_PROFILE`
- 第一次啟動下載 ONNX 模型資產
- 載入 tokenizer 與 ONNX session
- 提供內網 `/embed` API
- 提供 `/info` 給部署驗證

這個 service 是正式部署的 embedding 主路徑。

### 3.6 `Memory.Worker`

職責：

- 消費 PostgreSQL durable jobs
- 重建 chunk vectors
- 切換 model/profile 後重跑 embedding
- 與 `embedding-service` 協作完成 reindex

### 3.7 `Memory.Dashboard`

職責：

- 提供 NginxUI 風格 dark dashboard
- 以 cookie auth 提供單一 admin 登入
- 顯示 runtime 參數、Docker / Compose 資源、memory/log/job/storage explorer
- 透過 `mcp-server` 的 REST API 讀取業務資料
- 透過唯讀 Docker socket 顯示容器即時狀態

## 4. 容器與基礎設施

### 4.1 `embedding-service`

用途：

- 模型下載
- tokenization
- ONNX inference
- `query:` / `passage:` prefix 注入
- mean pooling + L2 normalization

對外介面：

- `GET /health/live`
- `GET /health/ready`
- `GET /info`
- `POST /embed`

### 4.2 `mcp-server`

用途：

- Codex MCP 入口
- REST 查詢與診斷入口
- 整體系統的主要 API gateway

對外介面：

- `POST/GET /mcp`
- `GET /api/status`
- `GET /api/memories/search`
- `GET /api/logs/search`
- `POST /api/context/build`
- `POST /api/performance/measure`
- `GET /api/user/preferences`
- `POST /api/user/preferences`
- `PATCH /api/user/preferences/{id}`

### 4.3 `worker`

用途：

- 處理 `memory_jobs`
- reindex vectors
- model switch 後補齊資料

### 4.4 `dashboard`

用途：

- 管理主控台
- Overview / Runtime / Memories / Preferences / Logs / Jobs / Storage / Performance
- Docker host / compose stack 即時監控

對外介面：

- `GET /health/live`
- `GET /health/ready`
- `/login`
- `/`
- `/runtime`
- `/memories`
- `/preferences`
- `/logs`
- `/jobs`
- `/storage`
- `/performance`

### 4.5 `postgres`

用途：

- 永久資料存放
- pgvector 向量檢索
- FTS keyword 檢索
- durable jobs
- runtime logs

### 4.6 `redis`

用途：

- cache version
- search/context cache
- job signal

限制：

- 不保存永久記憶
- 不是 background job source of truth

## 5. 資料模型

### 5.1 `memory_items`

系統中的記憶主體，代表一個可管理的 knowledge object。

主要欄位概念：

- `id`
- `external_key`
- `scope`
- `memory_type`
- `title`
- `content`
- `summary`
- `tags`
- `source_type`
- `source_ref`
- `importance`
- `confidence`
- `version`
- `status`
- `metadata_json`
- `created_at`
- `updated_at`

### 5.2 `memory_item_revisions`

用來記錄每次更新後的版本快照。

用途：

- provenance
- 歷史追蹤
- rollback 線索
- archive/restore 變更痕跡

### 5.3 `memory_item_chunks`

檢索單位，不直接拿整份文件做單一 embedding。

主要欄位概念：

- `memory_item_id`
- `chunk_kind`
- `chunk_index`
- `chunk_text`
- `metadata_json`
- `content_tsv`

### 5.4 `memory_chunk_vectors`

每個 chunk 的向量版本表。

主要欄位概念：

- `chunk_id`
- `model_key`
- `dimension`
- `status`
- `embedding`
- `created_at`

這個設計允許：

- 不同 model key 共存
- model switch 後保留舊向量一段時間
- 逐步平滑切換

### 5.5 `memory_jobs`

durable background jobs。

主要用途：

- `Reindex`
- `Cleanup`

### 5.6 `runtime_log_entries`

DB-first logs 的主表。

主要欄位概念：

- `service_name`
- `category`
- `level`
- `message`
- `exception`
- `trace_id`
- `request_id`
- `payload_json`
- `created_at`

### 5.7 `memory_links`

預留的關聯表，用來表達 item 與 item 之間的語意關係。

可用於未來擴充：

- `depends_on`
- `related_bug`
- `derived_from`
- `related_decision`

## 6. User Preference 設計

這是 ContextHub 用來「顯式了解使用者」的正式能力。

### 6.1 設計原則

- 不做自動偷存
- 不做背景自動人格抽取
- 只有 MCP tool / REST API 顯式寫入才算正式偏好

### 6.2 存放方式

user preference 仍然存進既有 memory tables，不新增獨立 preference table。

固定規則：

- `scope = User`
- `memory_type = Preference`
- `source_type = user-preference`
- `source_ref = stable preference key`

`metadata_json` 目前至少包含：

- `kind`
- `rationale`

### 6.3 為什麼這樣設計

優點：

- 可控
- 可稽核
- 不容易產生髒記憶
- 維護成本低

代價：

- 需要顯式整理偏好
- 不會自動從對話推斷新偏好

### 6.4 在 working context 裡的作用

`build_working_context` 會固定帶回：

- `userPreferences`

排序規則：

1. 先對 user preference 做 hybrid search
2. 若 query 完全沒有命中，再用 `importance + confidence + updated_at` fallback

## 7. Chunking 策略

ContextHub 的檢索不是以整份文件為單位，而是以 chunk 為單位。

### 7.1 原因

- 避開模型 token 上限
- 提高搜尋精度
- 降低無關內容污染
- citation 更精準

### 7.2 目前策略

- 文件
  - 以段落 / heading 分段
- 程式碼
  - 以 function / class / block 為優先
- 日誌
  - 以時間窗、incident window、關聯訊息為主

### 7.3 檢索粒度

```text
source document / code / logs
    -> chunking
    -> memory_item_chunks
    -> FTS + vector retrieval
    -> regroup to memory_items
```

## 8. Hybrid Search 流程

```text
query
  -> keyword search on chunks
  -> query embedding
  -> vector search on chunk vectors
  -> merge score
  -> regroup to memory items
  -> build working context
```

### 8.1 Keyword Search

- 使用 PostgreSQL FTS
- 搜尋目標是 `memory_item_chunks.content_tsv`

### 8.2 Semantic Search

- query 先轉 embedding
- 對 `memory_chunk_vectors.embedding` 做 pgvector nearest-neighbor search

### 8.3 Merge Score

目前是簡單加權融合：

- keyword score
- semantic score
- item importance / confidence

### 8.4 Regroup

chunk 命中後再 regroup 成 item，避免同一份 memory 因多個 chunk 重複灌入結果。

## 9. Working Context Builder

`build_working_context` 不回傳單一自由文字 blob，而是回傳結構化結果。

輸出欄位：

- `facts`
- `decisions`
- `episodes`
- `artifacts`
- `recentLogs`
- `userPreferences`
- `suggestedTests`
- `citations`

### 9.1 為什麼要結構化

優點：

- Codex 可以穩定消費
- 容易做前後版本比較
- 不會把所有上下文揉成一團

## 10. MCP 與 REST 合約

### 10.1 MCP

入口：

- `POST/GET /mcp`

主要 tools：

- `memory_search`
- `memory_get`
- `memory_upsert`
- `memory_update`
- `build_working_context`
- `enqueue_reindex`
- `log_read`
- `log_search`
- `promote_log_slice_to_memory`
- `user_preference_upsert`
- `user_preference_list`
- `user_preference_archive`

### 10.2 REST

用途：

- 存活檢查
- 黑箱測試
- Portainer / probe / external automation
- 人工查詢與除錯

主要 endpoints：

- `GET /health/live`
- `GET /health/ready`
- `GET /api/status`
- `GET /api/dashboard/overview`
- `GET /api/dashboard/runtime`
- `GET /api/memories`
- `GET /api/memories/search`
- `GET /api/memories/{id}`
- `GET /api/memories/{id}/details`
- `GET /api/logs/search`
- `GET /api/logs/{id}`
- `POST /api/context/build`
- `POST /api/performance/measure`
- `GET /api/jobs`
- `GET /api/jobs/{id}`
- `POST /api/jobs/reindex`
- `GET /api/storage/tables`
- `GET /api/storage/{table}`
- `GET /api/user/preferences`
- `POST /api/user/preferences`
- `PATCH /api/user/preferences/{id}`

### 10.3 MCP 與 REST 的關係

- 不拆第二套 `memory-api`
- REST 與 MCP 共用 `Memory.Application`
- 這能降低契約分裂與維護成本

## 11. Background Jobs

### 11.1 基本流程

```text
memory_upsert / memory_update / enqueue_reindex
    -> create memory_jobs row
    -> publish Redis signal
    -> worker polls pending jobs
    -> worker calls embedding-service
    -> worker writes memory_chunk_vectors
```

### 11.2 為什麼 source of truth 在 PostgreSQL

因為 Redis 不適合當永久 job ledger。

目前分工：

- PostgreSQL
  - durable state
  - status transition
  - error record
- Redis
  - job wake-up signal
  - cache invalidation 協調

## 12. DB-first Logs

ContextHub 不依賴大量實體 log files 作為主要查詢來源。

### 12.1 流程

```text
app structured logs
  -> in-memory queue
  -> background batch writer
  -> runtime_log_entries
  -> log_search / log_read
  -> promote_log_slice_to_memory
```

### 12.2 好處

- 適合讓 Codex 查詢事實
- 不需要去 tail 不同容器的原始檔案
- 可以直接以 `service / level / trace_id / request_id / time range` 做查詢

### 12.3 角色分工

- Docker container logs
  - 短期保底
  - rotation
- PostgreSQL runtime logs
  - 正式查詢來源
  - 可被 MCP / REST 使用

## 13. Embedding 架構

### 13.1 正式主路徑

正式路徑是：

- `mcp-server` / `worker`
  - 使用 `HttpEmbeddingProvider`
- `embedding-service`
  - 實際執行 ONNX inference

### 13.2 為什麼不讓每個服務自己載 ONNX

如果 `mcp-server` 和 `worker` 都各自載模型：

- 容器更重
- 模型切換更複雜
- 記憶體重複消耗
- 部署驗證更分散

因此採單一 `embedding-service` 是較穩妥的 production 方案。

### 13.3 `EMBEDDING_PROFILE`

主要主參數：

- `compact`
  - `intfloat/multilingual-e5-small`
  - 384 維
  - 512 tokens
- `balanced`
  - `intfloat/multilingual-e5-base`
  - 768 維
  - 512 tokens

覆寫參數：

- `EMBEDDING_MODEL_ID`
- `EMBEDDING_DIMENSIONS`
- `EMBEDDING_MAX_TOKENS`
- `EMBEDDING_INFERENCE_THREADS`
- `EMBEDDING_BATCH_SIZE`

### 13.4 為什麼只推薦平常改 `EMBEDDING_PROFILE`

因為它能同時改變：

- 模型
- 維度
- 預設 token 上限
- 推論 threads
- 批次策略
- 整體 CPU / RAM 壓力

這是最符合團隊維運成本的主入口。

## 14. 模型切換與 reindex

切換 profile / model 時的正式流程：

1. 修改 `.env`
2. 重啟 `embedding-service`、`mcp-server`、`worker`
3. 驗證 `/api/status` 與 `/info`
4. 執行 `enqueue_reindex`
5. 等 worker 重建向量

### 14.1 為什麼一定要重跑向量

因為模型一旦換掉，向量空間就變了。

即使：

- 維度相同
- 模型名稱很像

舊向量也不能安全當成新模型的檢索基礎。

## 15. 配置與運維

### 15.1 三層資源概念

#### Docker host memory

這是 Docker Desktop / WSL2 的總 RAM 配額，不是專案參數。

#### 應用層資源策略

這是 ContextHub 的主入口：

- `EMBEDDING_PROFILE`

#### Container hard limit

Compose `mem_limit` 是可選保護機制，不是主要調整手段。

### 15.2 一般建議

- 先調整 Docker host memory
- 再用 `EMBEDDING_PROFILE` 決定整體 embedding 資源壓力
- 最後才考慮 per-container hard limit

## 16. 安全邊界

### 16.1 目前系統邊界

- `embedding-service` 只透過 compose 內網呼叫
- `mcp-server` 是唯一外部 API 入口
- `dashboard` 是外部管理 UI 入口，但業務資料仍回到 `mcp-server`
- `dashboard` 透過 read-only Docker socket 讀取 compose metrics
- PostgreSQL / Redis 由 compose 管理

### 16.2 尚未納入的進階議題

目前沒有重點實作：

- 多租戶 auth
- row-level isolation
- secret rotation
- API rate limiting / quota

這些可視需求在後續版本再加入。

## 17. 測試策略

### 17.1 Unit Tests

驗證：

- chunking
- deterministic embedding
- profile resolver

### 17.2 Integration Tests

驗證：

- memory workflow
- user preferences
- reindex / model key switch
- DB log ingestion

### 17.3 MCP Protocol Tests

驗證：

- `/mcp` Streamable HTTP
- `tools/list`
- `tools/call`
- `user_preference_*`
- memory / log / context flow

### 17.4 Compose Smoke Tests

驗證：

- `docker compose up`
- `health/ready`
- `/api/status`
- `/api/performance/measure`
- `dashboard` login / overview

## 18. 目前值得現在做與不值得現在做的事

### 值得現在做

- 明確 user preference 管理
- 自建 ONNX embedding service
- `EMBEDDING_PROFILE` 單一配置入口
- DB-first logs
- MCP + REST 共用用例層
- Dashboard 管理台

### 建議延後

- 多租戶
- 自動從對話抽取人格偏好
- 複雜 reranker
- 額外拆獨立 `memory-api`
- 完整 RBAC / 多管理者後台

原因很直接：

- 現在先做這些，會增加 deploy、auth、telemetry、維運複雜度
- 對當前單機 Codex integration 的直接價值不高

## 19. 架構結論

ContextHub 的核心價值不在於「多一個資料庫」，而在於把這些能力收斂成可被 Codex 使用的穩定系統：

- 明確的 memory model
- 結構化 working context
- 可查詢的 runtime facts
- 顯式的 user preference
- 可演進的 embedding runtime
- 單機可落地的 Docker Compose 拓樸

這讓它更像一個可維護的外部記憶系統，而不是一堆散落的 prompt 與腳本。
