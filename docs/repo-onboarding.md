# ContextHub Repo 接入指南

## 1. 目的

這份文件給「剛拿到一個 repo，想把它接到 ContextHub」的人使用，重點不是介紹 ContextHub 內部架構，而是把日常操作流程固定下來：

- 如何安裝並啟動 ContextHub
- 如何確認 ContextHub 可用
- 如何在 MCP client 綁定 ContextHub MCP Server
- 如何在目標 repo 新增 `AGENTS.md`
- 如何使用 ContextHub 知識庫
- 什麼時候該更新知識，什麼時候該同步工作上下文

若要看 MCP tool 的細部使用方式，請搭配 [mcp-usage-guide.md](/w:/Repositories/WJCY/ContextHub/docs/mcp-usage-guide.md)。

## 2. 接入流程總覽

```text
拿到 repo
  -> 確認 ProjectId
  -> 啟動 ContextHub
  -> 綁定 MCP Server
  -> 新增或補齊 AGENTS.md
  -> 開場讀 build_working_context
  -> 工作中按需查 memory / logs
  -> 收尾寫 conversation_ingest
  -> 高訊號結論再寫 memory / preference
```

## 3. 安裝與啟動 ContextHub

### 3.1 前置需求

本機需要：

- Docker Desktop 或可用的 Docker Engine
- Docker Compose
- `.NET 10 SDK`，只在本機開發或跑測試時需要

第一次拿到 ContextHub repo 後，先建立環境檔：

```powershell
Copy-Item .env.example .env
```

正式使用前，至少要檢查 `.env` 內這些值：

- `DASHBOARD_ADMIN_USERNAME`
- `DASHBOARD_ADMIN_PASSWORD_HASH`
- `EMBEDDING_PROFILE`

日常建議先用 `EMBEDDING_PROFILE=compact`，資源壓力較小；若要提高檢索品質，再切到 `balanced`，切換後必須重建 vectors。

### 3.2 啟動服務

```powershell
docker compose up -d --build
```

第一次啟動時，`embedding-service` 會下載 ONNX 模型到 Docker named volume，時間會比一般重啟更久。

停止但保留資料：

```powershell
docker compose down
```

刪除資料與模型 cache：

```powershell
docker compose down -v
```

### 3.3 基礎驗證

本機 Docker Compose 預設可檢查：

- `http://localhost:8080/health/live`
- `http://localhost:8080/health/ready`
- `http://localhost:8080/api/status`
- `http://localhost:8088/login`

若部署在遠端主機，請改用實際對外 port；目前慣例是：

- MCP / REST：`http://<your-host>:8092`
- Dashboard：`http://<your-host>:8091`

不要只看 container 是否 running。至少要確認 `/health/ready`、`/api/status` 與 MCP tools list 都正常，才算 MCP Server 可用。

## 4. 綁定 MCP Server

ContextHub 對外提供 `HTTP /mcp` endpoint，MCP client 應使用 Streamable HTTP，不要設定成 `stdio`。

### 4.1 VS Code workspace 設定

建議在目標 repo 放 `.vscode/mcp.json`：

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

遠端部署範例：

```json
{
  "servers": {
    "contextHub": {
      "type": "http",
      "url": "http://<your-host>:8092/mcp"
    }
  }
}
```

若未來 MCP endpoint 需要 token，可加入 `inputs` 與 `headers`：

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

### 4.2 綁定後驗證

1. 在 VS Code Command Palette 執行 `MCP: List Servers`。
2. 確認 `contextHub` 可以 Start 或 Restart。
3. 在 agent mode 中確認可看到 ContextHub tools：
   - `build_working_context`
   - `memory_search`
   - `memory_get`
   - `conversation_ingest`
   - `memory_upsert`
   - `memory_update`
   - `log_search`
   - `log_read`
   - `user_preference_list`
   - `user_preference_upsert`
   - `enqueue_reindex`

若 tools list 不完整，先確認 client 連到的是 `/mcp`，不是 service root。

## 5. 在 repo 新增 `AGENTS.md`

### 5.1 檔名與放置位置

標準檔名使用 `AGENTS.md`，放在 repo root。

若 repo 已有 `.agent/` 資料夾，可再分層：

- `AGENTS.md`：repo 專屬規則與 ContextHub 接入規則
- `.agent/AGENTS.md`：可攜式或團隊共用規則
- `.agent/CONTEXT.md`：長篇背景，若專案需要才建立
- `.agent/RULES.md`：更細的操作限制，若專案需要才建立
- `.agent/local/`：本機輔助檔與 TODO，不放需要共享的正式規則

如果某些工具只認 `AGENT.md`，仍建議以 `AGENTS.md` 為主；需要相容時，讓 `AGENT.md` 只保留指向 `AGENTS.md` 的簡短說明，避免兩份規則分歧。

### 5.2 `ProjectId` 決定規則

每個 repo 都要有固定 `ProjectId`。決定順序：

1. 若 repo 規則、設定檔或既有 ContextHub 記錄已指定 `ProjectId`，沿用該值。
2. 若尚未指定，使用 repo root 目錄名稱原樣作為 `ProjectId`。
3. 一旦開始使用，不要自行更名；若真的要改，需明確規劃資料搬移或重建。

所有 ContextHub MCP 查詢與寫入都必須帶入同一個 `projectId`，不要落回 `default`。

### 5.3 最小 `AGENTS.md` 範本

```md
# Agent Notes

## ContextHub

- 本 repo 的 `ProjectId` 固定使用 `<RepoName>`。
- 進入此 repo 開始工作時，在分析、修改、測試前，先執行 `build_working_context(projectId = <RepoName>)`。
- 後續所有 ContextHub MCP 查詢與寫入都必須明確帶入 `projectId = <RepoName>`。
- 若任務需要跨 repo 參考，才額外指定 `includedProjectIds`。
- 對話告一段落、或完成具重用價值的任務後，先用 `conversation_ingest` 寫入 checkpoint。
- 若有穩定可重用的 `Decision`、`Fact`、`Artifact` 或 `Preference`，再補 `memory_upsert`、`memory_update` 或 `user_preference_upsert`。
- 定時 `ContextHub Sync` automation 只作為補漏或對帳，不作為主要同步流程。
```

### 5.4 建議補充的 repo 規則

除了 ContextHub 規則，`AGENTS.md` 通常也應記錄：

- 技術棧與主要服務入口
- build、test、lint 指令
- 測試資料與本機啟動方式
- release 或部署前檢查
- 文件更新規則
- 資安限制，例如 secret 不可落檔、不可打到 production endpoint

這些內容會被 `build_working_context` 帶入或透過 memory 補強，讓 agent 不需要每次重新猜 repo 慣例。

## 6. 使用 ContextHub 知識庫

### 6.1 開始工作前

每次新對話、切換 repo 或開始新任務時，先呼叫：

```text
build_working_context(projectId = <ProjectId>, query = <本次任務描述>)
```

query 應該像任務句，而不是單字。例如：

- `依目前 repo 規則新增 API 文件並補 README 入口`
- `調查 dashboard 登入失敗與遠端部署健康檢查`
- `評估 embedding profile 切換流程與重建 vectors 風險`

如果回來的內容不足，再用 `memory_search` 補查特定主題。

### 6.2 工作中按需查詢

常見工具選擇：

| 情境 | Tool |
|---|---|
| 查任務整體上下文 | `build_working_context` |
| 查長期事實或設計決策 | `memory_search` |
| 讀單一記憶全文 | `memory_get` |
| 查近期 runtime 事件 | `log_search` |
| 讀指定 log 片段 | `log_read` |
| 查使用者長期偏好 | `user_preference_list` |

工作中不需要每一輪都查。當你準備做架構判斷、遇到不確定規則、或除錯需要 runtime 事實時再查。

### 6.3 寫回知識庫

適合寫入：

- 已採納的架構決策
- 穩定的 repo 規則
- 重複會遇到的 troubleshooting 結論
- 已驗證的部署或測試結果
- 使用者明確確認的長期偏好

不適合寫入：

- 尚未驗證的猜測
- 一次性操作流水帳
- 可直接從程式碼讀出、沒有摘要價值的內容
- 敏感資訊、token、密碼、私鑰

寫回時要保留「下次可直接使用」的摘要，而不是貼完整對話。

## 7. 什麼時候該更新、同步

### 7.1 更新知識

「更新」指的是把穩定可重用內容寫進 memory 或 user preference。

該更新的時機：

- 完成一個具長期價值的決策
- 找到並驗證一個常見問題的根因
- 修改 repo 規則、部署流程、測試策略
- 使用者明確確認長期偏好
- 切換 embedding model 或 profile，並完成 reindex 驗證

建議工具：

- 新增長期知識：`memory_upsert`
- 修正既有知識：`memory_update`
- 寫入使用者偏好：`user_preference_upsert`
- 將重要 log incident 提升為知識：`promote_log_slice_to_memory`

### 7.2 同步工作上下文

「同步」指的是在對話生命週期中讀取或寫回工作進度，讓下次對話可以接續。

主要流程：

```text
新對話 / 新任務開始
  -> build_working_context

對話告一段落 / 任務完成
  -> conversation_ingest

有穩定可重用結論
  -> memory_upsert / memory_update / user_preference_upsert
```

定時 `ContextHub Sync` automation 只能當補漏或對帳，不應取代 agent 在開場與收尾的即時同步。原因是固定排程無法保證新對話開始時有最新 context，也可能把低價值變動掃進知識庫。

### 7.3 建議收尾檢查

每次完成一段有價值的工作前，快速判斷：

1. 這次有沒有下次還會用到的決策或事實？
2. 這次有沒有新增或修正 repo 規則？
3. 這次有沒有值得保留的錯誤根因或部署結果？
4. 這次有沒有使用者明確確認的長期偏好？
5. 若答案都是否，只做 `conversation_ingest` 或不寫入 memory。

## 8. 多 repo 使用注意事項

跨 repo 查詢時才使用 `includedProjectIds`。不要把所有 repo 都塞進同一個 query，否則會提高檢索噪音。

建議做法：

- 每個 repo 固定自己的 `ProjectId`
- 只有在明確需要參考共用 library、部署 repo 或文件 repo 時，才指定 `includedProjectIds`
- 跨 repo 得出的決策，寫回最主要受影響的 repo；若是共用規則，再寫到共用 project 或 user preference

## 9. 驗收清單

接入完成後，至少確認：

- `docker compose up -d --build` 可啟動 ContextHub。
- `/health/ready` 與 `/api/status` 正常。
- MCP client 可列出 ContextHub tools。
- 目標 repo 已有 `AGENTS.md`，且明確指定 `ProjectId`。
- 新對話可用 `build_working_context(projectId = <ProjectId>)` 取回 repo 規則與 user preferences。
- 可用 `memory_search` 查到一筆已知存在的 repo 決策或事實。
- 收尾流程知道何時用 `conversation_ingest`，何時補 `memory_upsert` 或 `user_preference_upsert`。
