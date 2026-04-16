# ContextHub

ContextHub 是一套本機部署、純 Docker Compose 的 MCP knowledge system。它把長期 memory、runtime logs、reindex jobs、向量檢索與 user preference 管理收斂在同一套系統內，讓 Codex 可以透過 MCP 在需要時查詢，而不是把所有上下文硬塞進 prompt。

這版重點：

- 純 `.NET 10 + Docker Compose`
- `MCP + REST` 同服提供
- `DB-first logs`
- `PostgreSQL + pgvector + Redis`
- 自建 `ONNX embedding-service`
- `NginxUI` 風格 dark dashboard
- 顯式 `user preference` 記憶
- `EMBEDDING_PROFILE` 單一主參數

延伸文件：

- `docs/architecture.md`
- `docs/mcp-usage-guide.md`

## 容器用途

| Container | 主要責任 | 對外介面 | 持久化 |
|---|---|---|---|
| `embedding-service` | 第一次啟動下載 ONNX 模型、tokenization、ONNX inference、`query:/passage:` prefix、向量正規化 | 內網 `GET /health/*`、`GET /info`、`POST /embed` | `embedding-model-cache` volume |
| `mcp-server` | `POST/GET /mcp`、REST 查詢 API、health、performance probe、user preference API | `8080` | 無，狀態進 DB/Redis |
| `dashboard` | NginxUI 風格管理主控台、登入、runtime 參數、Docker metrics、memory/log/job/storage explorer | `8088` | `dashboard-data-protection` volume |
| `worker` | reindex、chunk vectors、background jobs、log promotion 後續處理 | 無對外 port | 無，狀態進 DB/Redis |
| `postgres` | durable memory、revisions、chunks、vectors、jobs、runtime logs | `5432` | `postgres-data` volume |
| `redis` | cache version、search/context cache、job signal | `6379` | 無持久化 |

## 專案結構

- `src/Memory.Domain`
- `src/Memory.Application`
- `src/Memory.Dashboard`
- `src/Memory.Infrastructure`
- `src/Memory.EmbeddingServer`
- `src/Memory.McpServer`
- `src/Memory.Worker`
- `tests/Memory.UnitTests`
- `tests/Memory.IntegrationTests`
- `tests/Memory.ApiContractTests`
- `tests/Memory.McpProtocolTests`
- `tests/Memory.ComposeSmokeTests`
- `tests/Memory.DashboardTests`

## 對外介面

### MCP

- `POST/GET /mcp`

MCP tools：

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

### 在 VS Code 中設定 ContextHub MCP

ContextHub 目前對外提供的是 `HTTP /mcp` 端點，部署與日常使用請優先採用 **Streamable HTTP** 連線。

不建議把 ContextHub 當成 `stdio` server 來設定，原因是：

- `stdio` 適合由 VS Code 直接啟動本機 subprocess
- ContextHub 目前標準部署方式是 Docker Compose / 遠端服務
- repo 內沒有提供專用的 `stdio` 啟動包裝腳本或 `command` 設定範本

實務上：

- **本機 Docker Compose**：用 `http://localhost:8080/mcp`
- **遠端主機**：用 `http://<your-host>:8092/mcp`

#### VS Code 設定檔位置

新版 VS Code 會把 MCP server 設定存到 `mcp.json`，而不是一般 `settings.json`。

常用入口：

- Workspace：`.vscode/mcp.json`
- User Profile：使用 Command Palette 開 `MCP: Open User Configuration`
- Remote Profile：使用 Command Palette 開 `MCP: Open Remote User Configuration`

#### Workspace 範例：連到本機 Docker Compose

```json
{
  "servers": {
    "contextHub": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

#### 如果未來 MCP 端點需要驗證

可在同一份 `mcp.json` 內加 `headers`：

```json
{
  "inputs": [
    {
      "type": "promptString",
      "id": "contextHubToken",
      "description": "ContextHub MCP token",
      "password": true
    }
  ],
  "servers": {
    "contextHub": {
      "type": "http",
      "url": "http://<your-host>:8092/mcp",
      "headers": {
        "Authorization": "Bearer ${input:contextHubToken}"
      }
    }
  }
}
```

#### VS Code 端要怎麼判斷 transport

對 `type: "http"` 的 MCP server，VS Code 會先嘗試 **HTTP Stream / Streamable HTTP**；若對方不支援，才回退到舊式 SSE 相容模式。

因此對 ContextHub：

- 設定請使用 `type: "http"`
- `url` 直接指向 `/mcp`
- 不需要寫 `command` / `args`
- 不需要用 `stdio`

#### 設定完成後怎麼驗證

1. 在 VS Code Command Palette 執行 `MCP: List Servers`
2. 確認 `contextHub` 可以正常 Start / Restart
3. 在 agent mode 中確認能列出 ContextHub tools，例如：
   - `build_working_context`
   - `memory_search`
   - `log_search`
   - `enqueue_reindex`

若已部署到遠端主機，也可先人工驗證：

- `http://<your-host>:8092/health/live`
- `http://<your-host>:8092/health/ready`
- `http://<your-host>:8092/mcp`

若 `/health/live` 與 `/health/ready` 正常，通常再回 VS Code 重新載入 MCP server 即可。

### 讓其他使用者建立相同規則

如果希望其他使用者或其他 Codex 使用者也能沿用這套 ContextHub 工作流，建議不要只給 MCP 連線資訊，還要一起建立「開場讀 context、收尾寫回」規則。

建議拆成兩層：

1. 全域規則：放在 Codex 使用者自己的全域規則檔
2. Repo 規則：放在該 repo 的 `AGENTS.md` / `.agent/AGENTS.md`

#### 1. 全域規則

建議位置：

- Windows：`%USERPROFILE%\.codex\AGENT_RULES.md`
- 其他環境：`$CODEX_HOME/AGENT_RULES.md`

至少補上這些原則：

```md
## ContextHub MCP

- 進入任何 repo 後，在開始分析、修改、測試前，先用既有 `projectId` 執行 `build_working_context`。
- 若 repo 尚未明確指定 `projectId`，以 repo root 目錄名稱作為 `projectId`。
- 後續所有 ContextHub 查詢與寫入都要明確帶入同一個 `projectId`。

## 知識庫同步

- 新對話開始：先讀取 working context。
- 對話告一段落、或完成具重用價值的任務：先做 `conversation_ingest`。
- 若本次有穩定可重用的 `Decision`、`Fact`、`Artifact`、`Preference`，再補 `memory_upsert`、`memory_update`、`user_preference_upsert`。
- 固定排程 automation 只作為補漏 / 對帳，不作為主流程。
```

#### 2. Repo 規則

每個 repo 應在 `AGENTS.md` 裡明確指定：

- 固定 `ProjectId`
- 開場要先讀 `build_working_context(projectId=...)`
- 收尾要先做 `conversation_ingest`
- 該 repo 是否允許寫入 `user_preference`
- 是否還有 `includedProjectIds` 慣例

可直接參考這個 repo 的寫法：

- 根目錄 [AGENTS.md](/w:/Repositories/WJCY/ContextHub/AGENTS.md)
- 專案規則 [`.agent/AGENTS.md`](/w:/Repositories/WJCY/ContextHub/.agent/AGENTS.md)

最小範例：

```md
## ContextHub

- 本 repo 的 `ProjectId` 固定使用 `MyRepo`。
- 開始工作時先執行 `build_working_context(projectId = MyRepo)`。
- 對話收尾時，若有高訊號知識，至少執行 `conversation_ingest`。
- 若需要跨 repo 參考，明確指定 `includedProjectIds`。
```

#### 3. 不建議直接用固定時程當主流程

不建議把 `ContextHub Sync` 做成每天固定時間執行的主要機制，原因是：

- 新對話開始時拿不到最新 context
- 任務剛完成時也不會立即寫回
- 容易把低價值的檔案變動一起掃進知識庫

比較實務的做法是：

- 主要流程：靠全域規則 + repo 規則，在對話開始 / 收尾時執行
- 補漏機制：若真的需要，再額外加一條 automation 做 reconciliation

若要加 automation，建議定位成：

- `PAUSED` 預設，手動開啟時才跑
- 或 thread / idle 型補漏機制
- 不要取代 agent 在任務收尾時的即時寫回

### REST

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

## Dashboard

Dashboard 是獨立的 `Blazor Web App` 容器，不直接碰 PostgreSQL；業務資料一律透過 `mcp-server` REST API 讀取，只有 Docker / Compose runtime metrics 直接從唯讀 Docker socket 取得。

主要頁面：

- `/login`
- `/`
- `/runtime`
- `/memories`
- `/preferences`
- `/logs`
- `/jobs`
- `/storage`
- `/performance`

登入方式：

- 打包版本不內建預設管理員帳號 / 密碼
- 複製 `.env.example` 後，必須自行設定 `DASHBOARD_ADMIN_USERNAME` 與 `DASHBOARD_ADMIN_PASSWORD_HASH`

正式使用前請更換：

- `DASHBOARD_ADMIN_USERNAME`
- `DASHBOARD_ADMIN_PASSWORD_HASH`
- `DASHBOARD_SESSION_TIMEOUT_MINUTES`

若未設定 `DASHBOARD_ADMIN_USERNAME` 或 `DASHBOARD_ADMIN_PASSWORD_HASH`，`dashboard` 會在啟動驗證階段直接失敗，避免以共通預設值上線。

Polling 參數：

- `DASHBOARD_POLLING_OVERVIEW_SECONDS`
- `DASHBOARD_POLLING_METRICS_SECONDS`
- `DASHBOARD_POLLING_JOBS_SECONDS`
- `DASHBOARD_POLLING_LOGS_SECONDS`
- `DASHBOARD_POLLING_PERFORMANCE_SECONDS`

安全邊界：

- Dashboard 只做單一 admin cookie auth，不是完整 RBAC
- Docker socket 以唯讀方式掛載，這是高權限能力，應只在本機 / 內網可信環境使用
- Data Protection keys 持久化在 `dashboard-data-protection` volume，避免 container 重啟後 session 全失效

## User Preference 設計

ContextHub 不會自動偷讀對話替你建立人格記憶。正式會被長期保留的「了解使用者」能力，只來自顯式工具/API 寫入。

規則：

- `scope = User`
- `memory_type = Preference`
- `source_type = user-preference`
- `source_ref = stable preference key`

偏好種類：

- `CommunicationStyle`
- `EngineeringPrinciple`
- `ToolingPreference`
- `Constraint`
- `AntiPattern`

`build_working_context` 會固定帶回：

- `facts`
- `decisions`
- `episodes`
- `artifacts`
- `recentLogs`
- `userPreferences`
- `suggestedTests`
- `citations`

## Embedding Profile

一般情況只改一個參數：

- `EMBEDDING_PROFILE`

支援的 profile：

| Profile | Model | 維度 | Token 上限 | 預設 threads | 預設 batch | 建議 Docker host memory | 適用場景 |
|---|---|---:|---:|---:|---:|---|---|
| `compact` | `intfloat/multilingual-e5-small` | 384 | 512 | 2 | 4 | `8 GB` | 本機開發預設，資源較省 |
| `balanced` | `intfloat/multilingual-e5-base` | 768 | 512 | 4 | 2 | `12 GB+` | 檢索品質優先 |

覆寫參數：

- `EMBEDDING_MODEL_ID`
- `EMBEDDING_DIMENSIONS`
- `EMBEDDING_MAX_TOKENS`
- `EMBEDDING_INFERENCE_THREADS`
- `EMBEDDING_BATCH_SIZE`

規則：

- `EMBEDDING_PROFILE` 先決定預設模型、維度、token 上限與推論設定
- 有覆寫值就覆寫 profile 預設
- 若 `EMBEDDING_DIMENSIONS` 跟模型實際輸出不一致，`embedding-service` 會啟動失敗
- 切換 profile 或 model 之後，必須執行 `enqueue_reindex`

## 記憶體調整到底改哪裡

這裡要分三層，不要混在一起。

### 1. Docker host memory 上限

這是 Docker Desktop / WSL2 給整個 Docker engine 的總 RAM。  
如果這層太小，任何 profile 都可能起不來。

例如 Windows 可在 `C:\Users\<you>\.wslconfig` 設：

```ini
[wsl2]
memory=8GB
processors=4
```

這不是專案參數。

### 2. 應用層資源策略

這是本專案真正的共用主參數：

- `EMBEDDING_PROFILE`

它會一起影響：

- 模型大小
- 向量維度
- 推論 threads
- 預設 batch
- 整體 CPU/RAM 壓力

如果你想「改一個值就調整記憶體使用與模型維度」，改這個就好。

### 3. Container hard limit

這是 Compose 的 `mem_limit`。  
它不是主要調整手段，只是保護某個 container 不要把整個 Docker host 吃滿。

本 repo 目前沒有預設設硬上限；先用 `EMBEDDING_PROFILE` 調整即可。

## 啟動方式

第一次建議先複製設定：

```powershell
Copy-Item .env.example .env
```

預設啟動：

```powershell
docker compose up -d --build
```

停止但保留資料：

```powershell
docker compose down
```

刪除資料與模型 cache：

```powershell
docker compose down -v
```

行為說明：

- `embedding-service` 第一次啟動會從 Hugging Face 下載 ONNX 模型資產到 `embedding-model-cache`
- 之後重啟不需要重新下載
- runtime 不會呼叫外部 embedding API
- `embedding-service` 不對外暴露公網用途，`mcp-server` 與 `worker` 透過 compose 內網呼叫它
- `dashboard` 會從 `http://mcp-server:8080` 讀取業務資料，並透過唯讀 Docker socket 顯示 compose stack 狀態

## 如何切換模型 / 維度

最常見做法是改 `.env`：

```env
EMBEDDING_PROFILE=balanced
```

如果你要細部覆寫：

```env
EMBEDDING_PROFILE=compact
EMBEDDING_DIMENSIONS=384
EMBEDDING_MAX_TOKENS=512
EMBEDDING_INFERENCE_THREADS=2
EMBEDDING_BATCH_SIZE=4
```

切換流程：

1. 修改 `.env`
2. `docker compose up -d --build embedding-service mcp-server worker`
3. 驗證 `GET /api/status`
4. 執行 `enqueue_reindex`
5. 等 worker 重建 vectors
6. 用 `memory_search` / `/api/performance/measure` 驗證結果

注意：

- 不論 `compact -> balanced`，或同維度不同模型，向量都要重跑
- 舊向量不能直接當成新模型的語意空間使用

## DB-first Logs

應用程式不自行落大量實體 log 檔。正式流程是：

- app 寫 structured logs
- logs 先進 in-memory queue
- background writer 批次落到 `runtime_log_entries`
- Docker container logs 只做短期輪替保底

這樣 Codex 能透過 MCP 或 REST 查詢：

- 真實 runtime 事件
- exception
- trace id / request id
- 近期部署後事實

再把重要片段提升成 memory。

## 效能量測 API

- `POST /api/performance/measure`

這個端點會用「目前正在跑的 profile / model / DB 狀態」實際量測：

- chunking
- query embedding
- document embedding
- keyword search
- vector search
- hybrid search

範例：

```json
{
  "query": "performance benchmark endpoint",
  "document": "Measure the configured runtime using the current embedding model.",
  "searchLimit": 5,
  "warmupIterations": 1,
  "measurementIterations": 3
}
```

適合在這些時機使用：

- 切換 `EMBEDDING_PROFILE`
- 調整 `EMBEDDING_INFERENCE_THREADS`
- 調整 `EMBEDDING_BATCH_SIZE`
- 切換模型後驗證實際效能

## 測試

```powershell
dotnet test ContextHub.slnx -m:1
dotnet format ContextHub.slnx --verify-no-changes
docker compose config
```

說明：

- `UnitTests` 永遠會跑
- `IntegrationTests`、`ApiContractTests`、`McpProtocolTests` 需要 Docker daemon
- Docker 不可用時，容器測試會明確標為 `Skipped`
- `ComposeSmokeTests` 需設 `RUN_COMPOSE_SMOKE_TESTS=1`
- smoke test 會驗證 `/health/ready`、`/api/status` 與 `/api/performance/measure`
- smoke test 也會驗證 `dashboard` 的 `/health/ready`、登入頁與 overview shell

## 目前 Compose 主要參數

`docker-compose.yml` 會從 `.env` 讀下列 embedding 參數：

- `EMBEDDING_PROFILE`
- `EMBEDDING_MODEL_ID`
- `EMBEDDING_DIMENSIONS`
- `EMBEDDING_MAX_TOKENS`
- `EMBEDDING_INFERENCE_THREADS`
- `EMBEDDING_BATCH_SIZE`

對 `mcp-server`、`worker`、`embedding-service` 來說，這些參數是共用的單一來源，不需要三個 container 分別手改。

Dashboard 相關 `.env` 參數：

- `DASHBOARD_ADMIN_USERNAME`
- `DASHBOARD_ADMIN_PASSWORD_HASH`
- `DASHBOARD_SESSION_TIMEOUT_MINUTES`
- `DASHBOARD_COMPOSE_PROJECT`
- `DASHBOARD_POLLING_OVERVIEW_SECONDS`
- `DASHBOARD_POLLING_METRICS_SECONDS`
- `DASHBOARD_POLLING_JOBS_SECONDS`
- `DASHBOARD_POLLING_LOGS_SECONDS`
- `DASHBOARD_POLLING_PERFORMANCE_SECONDS`
