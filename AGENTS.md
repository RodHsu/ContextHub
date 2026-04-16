# ContextHub Agent Notes

本檔記錄 `ContextHub` repo 的專案專屬規則與操作重點；通用規則、語氣與本機習慣請以 [`.agent/AGENTS.md`](/w:/Repositories/WJCY/ContextHub/.agent/AGENTS.md) 為準。

## 專案定位

- 技術棧：
  - `.NET 10`
  - `ASP.NET Core Minimal API`
  - `PostgreSQL + pgvector`
  - `Redis`
  - `Docker Compose`
  - `embedding-service`（internal ONNX HTTP）
- 服務入口：
  - MCP：`/mcp`
  - REST：`/api/*`
  - Health：`/health/live`、`/health/ready`
  - Performance probe：`POST /api/performance/measure`

## 開發命令

```powershell
dotnet test ContextHub.slnx
dotnet format ContextHub.slnx --verify-no-changes
docker compose up -d --build
docker compose down
```

## 架構原則

- 不新增獨立 `memory-api` 服務。
- REST 與 MCP 共用同一組 Application use cases。
- `memory_item_chunks` 是檢索單位，不直接對整份文件建向量。
- logs 採 DB-first，不依賴大量實體 log 檔。
- Redis 只做 cache、lock、job signal，不做永久資料來源。

## Embeddings 規則

- 預設部署使用自建 ONNX `embedding-service`。
- 模型在第一次 `docker compose up` 時下載到 Docker named volume。
- `mcp-server` 與 `worker` 透過內網 HTTP 呼叫 embeddings API。
- 本機 `dotnet test` 使用 deterministic provider，避免測試依賴模型下載。
- 切換模型後必須重跑 `enqueue_reindex`。
- 日常調整以 `EMBEDDING_PROFILE` 為主。
- 只有在需要細調時才修改：
  - `EMBEDDING_MODEL_ID`
  - `EMBEDDING_DIMENSIONS`
  - `EMBEDDING_MAX_TOKENS`
  - `EMBEDDING_INFERENCE_THREADS`
  - `EMBEDDING_BATCH_SIZE`

## User Preferences 與 Memory

- 本 repo 的預設 `ProjectId` 固定使用 `ContextHub`。
- 進入此 repo 開始工作時，應優先透過 ContextHub MCP 讀取目前工作上下文；預設先使用 `build_working_context`，並明確帶入 `projectId = ContextHub`。此要求不依賴 `.agent/` 是否存在，且屬於「新對話 / 新任務開場流程」，不是同步寫回。
- 在此 repo 內操作 ContextHub 時，查詢與寫入都應明確帶 `projectId = ContextHub`，避免落回 `default`。
- 使用者偏好只透過顯式 MCP tool 或 REST API 寫入。
- 長期偏好、決策與可重用事實優先使用知識庫維護，不再維護 `.agent/local/LOG.md`。
- `build_working_context` 會固定帶入 `userPreferences`。
- 對話告一段落、或完成具重用價值的任務後，應先以 `conversation_ingest` 寫入 checkpoint；若有穩定可重用的 `Decision`、`Fact`、`Artifact`、`Preference`，再補 `memory_upsert` / `memory_update` / `user_preference_upsert`。
- 若存在定時 `ContextHub Sync` automation，僅可作為補漏 / 對帳，不應作為本 repo 的主要同步流程。
- 常用 MCP tools：
  - `build_working_context`
  - `conversation_ingest`
  - `memory_search`
  - `memory_get`
  - `memory_upsert`
  - `memory_update`
  - `log_search`
  - `log_read`
  - `promote_log_slice_to_memory`
  - `user_preference_list`
  - `user_preference_upsert`
  - `user_preference_archive`
  - `enqueue_reindex`

## 測試原則

- 任何程式碼變更都要跑：
  - `dotnet test ContextHub.slnx`
  - `dotnet format ContextHub.slnx --verify-no-changes`
- 只改 comments 或 documentation 時，可不跑測試與 linter。
- 容器整合測試若本機 Docker daemon 不可用，會自動 `Skip`。
- `McpProtocolTests` 必須保留真實 Streamable HTTP black-box 測試。

## 操作提醒

- 進 repo 後先檢查根目錄 `AGENTS.md` 與 `.agent/`。
- 若 `.agent/` 存在，優先閱讀 `AGENTS.md`；`CONTEXT.md`、`RULES.md` 若不存在可略過。
- 無論 `.agent/` 是否存在，完成規則檔檢查後都應立即讀取 ContextHub MCP 工作上下文；預設先呼叫 `build_working_context(projectId = ContextHub)`，需要補充時再搭配 `memory_search`、`log_search` 等工具。
- 對話收尾時，應先判斷本次是否產生高訊號知識；若有，至少執行 `conversation_ingest`，再視內容補寫對應 memory / preference。
- 每次互動後只維護 [`.agent/local/TODO.md`](/w:/Repositories/WJCY/ContextHub/.agent/local/TODO.md)；不再維護 `.agent/local/LOG.md`。
