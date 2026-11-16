# ProjectId 相容發布與回退

ProjectId 保留首次接受的拼寫；重複檢查、查詢與引用使用 ProjectId v1 的固定等價關係。回退版本必須理解同一個等價關係，不能依拼寫把已接受的 alias 資料分開。

## 發布鏈路

```text
原版本（exact reader）
  → 相容 bridge（v1 reader、migration 056、相容 Worker）
  → candidate → alias writes
  → 相容 bridge → read / replay / restart
  → candidate（roll-forward）
```

- bridge 從原版本基底回移 ProjectId reader、授權引用、cache/revision scope、projection 與 reindex Worker 相容修正；不是原 exact reader image。
- bridge 的 embedding runtime、provider、profile 與設定維持原版本。Tokenizer 正式切換、model generation 變更與 reindex 是獨立發布議題。
- migration 056 保留原始 ProjectId 與 revision spelling rows。回退不移除 migration、不重寫資料、不重置 revision，也不清除 Production Redis。
- candidate 與 bridge 使用相同的固定 case map、資料庫 identity function、cache namespace 與 revision 聚合契約。Tenant、owner、grant、model key 與其他 identity 的原有比對規則維持不變。
- 已接受 alias writes 後，原 exact reader 不可作為 application-only rollback。Full DB restore / PITR 是較高層 recovery；不取代相容 application rollback。

## 必要驗收

1. 完整 solution tests、linter、API/MCP 契約與 tenant/owner/grant 安全回歸。
2. 獨立 bridge 與 candidate 的實際 image：candidate 寫入不同拼寫、bridge 回退後所有 alias 讀到完整資料，保留 record ID、拼寫與獨立外部識別。
3. Worker 重播、重啟與故障恢復保持既有 authority boundary、cursor、generation、model key 與資料隔離；失敗交易不得留下部分寫入。
4. Roll-forward 保留 accepted data、revision 與 authority ledger。原 exact reader 僅用作負向控制。
5. 正式發布前，bridge 與 candidate 都須具有通過驗收且位於 `origin/main` 的不可變 commit/image 座標。保存相容 fallback 的整組服務、設定與 key recovery 準備，避免只換 MCP 而留下不相容 Worker。

本機 deterministic、受控 fixture 與 server-side 驗證各自記錄，不代表 Production authenticated UI、fresh ChatGPT host A1/A2/B 或 NaturalSchedule 通過。
