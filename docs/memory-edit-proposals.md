# Memory 版本化編輯提案

送出 `memory_edit` 提案時，使用 `MemoryEditCommand` 並指定與提案一致的 `ProjectId`。目前擁有者須提供最初讀取的正整數 `ExpectedVersion` 和 `RequestId`。重送同一編輯意圖會回傳既有提案。同一範圍內若沿用相同 `RequestId` 送出不同意圖，會回傳衝突。

核准時會鎖定提案並重新核對原擁有者、專案、目標、請求識別碼與版本。Memory、版本紀錄、chunks、索引工作、提案狀態、回執及核准稽核會在同一交易提交。核准結果不明時，使用同一提案 ID 重試。若提案已過期，或歷史提案缺少原始版本，請讀取目前的 Memory，重新審查編輯意圖後另送提案。

`/mcp` 的 `memory_edit` 使用 `MemoryWrite`，直接提交編輯並回傳回執。`/mcp-chat` 的同名工具使用原始編輯意圖建立提案，核准後才會套用。若提案建立結果不明，原樣重送可找回既有提案。核准結果則用原提案 ID 查核。

兩個入口的 `memory_edit_receipt_get` 皆使用 `MemoryRead` 與目前的資源存取權限，查詢直接編輯的回執。請沿用原始 `ProjectId`、`MemoryId`、`RequestId`、`ExpectedRequestHash` 與 `HashVersion`（目前為 `MemoryEditIntent/v1`）。若尚未拿到直接編輯回執，可原樣重送編輯取得同一結果。遇版本衝突時，保留草稿並重新比對目前版本。

既有 `memory_update` 提案與直接更新工具在本階段維持相容。停用未帶版本的寫入前，需完成呼叫端切換及既有待審提案的重新審查。`GovernanceBatchExecutor` 的重複記憶合併須保存兩筆 Memory 的原始版本，在改動任一筆前共同核對，並依原始配對恢復操作。正式啟用與回滾皆需驗證舊版程式與呼叫端的寫入控管。
