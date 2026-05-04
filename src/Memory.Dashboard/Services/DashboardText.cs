using Memory.Domain;

namespace Memory.Dashboard.Services;

public static class DashboardText
{
    public static string Label(MemoryScope value)
        => value switch
        {
            MemoryScope.User => "使用者",
            MemoryScope.Repo => "儲存庫",
            MemoryScope.Project => "專案",
            MemoryScope.Task => "任務",
            _ => value.ToString()
        };

    public static string Label(MemoryType value)
        => value switch
        {
            MemoryType.Fact => "事實",
            MemoryType.Decision => "決策",
            MemoryType.Episode => "事件",
            MemoryType.Artifact => "產物",
            MemoryType.Summary => "摘要",
            MemoryType.Preference => "偏好",
            _ => value.ToString()
        };

    public static string Label(MemoryStatus value)
        => value switch
        {
            MemoryStatus.Active => "啟用",
            MemoryStatus.Stale => "過期",
            MemoryStatus.Superseded => "已取代",
            MemoryStatus.Archived => "封存",
            _ => value.ToString()
        };

    public static string Label(UserPreferenceKind value)
        => value switch
        {
            UserPreferenceKind.CommunicationStyle => "溝通風格",
            UserPreferenceKind.EngineeringPrinciple => "工程原則",
            UserPreferenceKind.ToolingPreference => "工具偏好",
            UserPreferenceKind.Constraint => "限制條件",
            UserPreferenceKind.AntiPattern => "反模式",
            _ => value.ToString()
        };

    public static string Label(MemoryJobType value)
        => value switch
        {
            MemoryJobType.Reindex => "重新索引",
            MemoryJobType.Cleanup => "清理",
            MemoryJobType.RefreshSummary => "重建共用綜合層",
            MemoryJobType.IngestConversation => "整理對話 checkpoint",
            MemoryJobType.PromoteConversationInsights => "提升對話 insights",
            MemoryJobType.SyncSource => "同步來源",
            MemoryJobType.AnalyzeGovernance => "分析治理檢查",
            MemoryJobType.RunEvaluation => "執行評測",
            MemoryJobType.ExecuteSuggestedAction => "執行建議",
            _ => value.ToString()
        };

    public static string Label(MemoryJobStatus value)
        => value switch
        {
            MemoryJobStatus.Pending => "等待中",
            MemoryJobStatus.Running => "執行中",
            MemoryJobStatus.Completed => "已完成",
            MemoryJobStatus.Failed => "失敗",
            _ => value.ToString()
        };

    public static string Label(ChunkKind value)
        => value switch
        {
            ChunkKind.Document => "文件",
            ChunkKind.Code => "程式碼",
            ChunkKind.Log => "日誌",
            _ => value.ToString()
        };

    public static string Status(string status)
        => status.ToLowerInvariant() switch
        {
            "healthy" or "ready" => "正常",
            "running" => "執行中",
            "degraded" or "warning" => "降級",
            "error" or "critical" or "failed" or "unhealthy" => "異常",
            "pending" => "等待中",
            "completed" => "已完成",
            "active" => "啟用",
            "archived" => "封存",
            "exited" => "已停止",
            "n/a" => "未提供",
            _ => status
        };

    public static string Service(string name)
        => name.ToLowerInvariant() switch
        {
            "mcp-server" => "MCP 伺服器",
            "postgres" => "PostgreSQL",
            "redis" => "Redis",
            "embedding-service" or "embeddings" => "向量服務",
            "dashboard" => "主控台",
            "worker" => "背景工作器",
            _ => name
        };

    public static string LogLevel(string level)
        => level.ToLowerInvariant() switch
        {
            "trace" => "追蹤",
            "debug" => "偵錯",
            "information" or "info" => "資訊",
            "warning" or "warn" => "警告",
            "error" => "錯誤",
            "critical" or "fatal" => "嚴重",
            _ => level
        };

    public static string PerformanceMetric(string name)
        => name switch
        {
            "Chunking" => "切塊 (Chunking)",
            "Query Embedding" => "查詢向量化 (Query Embedding)",
            "Document Embedding" => "文件向量化 (Document Embedding)",
            "Keyword Search" => "關鍵字搜尋 (Keyword Search)",
            "Vector Search" => "向量搜尋 (Vector Search)",
            "Hybrid Search" => "混合搜尋 (Hybrid Search)",
            _ => name
        };

    public static string PreferenceTag(string tag)
        => tag switch
        {
            "user-preference" => "使用者偏好",
            nameof(UserPreferenceKind.CommunicationStyle) => "溝通風格",
            nameof(UserPreferenceKind.EngineeringPrinciple) => "工程原則",
            nameof(UserPreferenceKind.ToolingPreference) => "工具偏好",
            nameof(UserPreferenceKind.Constraint) => "限制條件",
            nameof(UserPreferenceKind.AntiPattern) => "反模式",
            _ => tag
        };

    public static string Label(SourceKind value)
        => value switch
        {
            SourceKind.LocalRepo => "本機 Repo",
            SourceKind.LocalDocs => "本機文件",
            SourceKind.RuntimeLogRule => "Runtime Log Rule",
            SourceKind.GitHubPull => "GitHub Pull",
            _ => value.ToString()
        };

    public static string Label(SourceSyncStatus value)
        => value switch
        {
            SourceSyncStatus.Pending => "等待中",
            SourceSyncStatus.Running => "同步中",
            SourceSyncStatus.Completed => "已完成",
            SourceSyncStatus.Failed => "失敗",
            _ => value.ToString()
        };

    public static string Label(GovernanceFindingType value)
        => value switch
        {
            GovernanceFindingType.DuplicateCandidate => "重複候選",
            GovernanceFindingType.ConflictCandidate => "衝突候選",
            GovernanceFindingType.StaleSource => "來源過期",
            GovernanceFindingType.MissingSource => "來源遺失",
            GovernanceFindingType.ReindexRequired => "需要重建索引",
            _ => value.ToString()
        };

    public static string Label(GovernanceFindingStatus value)
        => value switch
        {
            GovernanceFindingStatus.Open => "待處理",
            GovernanceFindingStatus.Accepted => "已接受",
            GovernanceFindingStatus.Dismissed => "已忽略",
            GovernanceFindingStatus.Resolved => "已解決",
            _ => value.ToString()
        };

    public static string Label(SuggestedActionType value)
        => value switch
        {
            SuggestedActionType.SyncSourceNow => "立即同步來源",
            SuggestedActionType.ArchiveStaleMemory => "封存過期記憶",
            SuggestedActionType.MergeDuplicateCandidate => "處理重複候選",
            SuggestedActionType.ReviewConflictCandidate => "檢閱衝突候選",
            SuggestedActionType.ReindexProject => "重新索引專案",
            SuggestedActionType.RefreshSharedSummary => "更新綜合層",
            _ => value.ToString()
        };

    public static string Label(SuggestedActionStatus value)
        => value switch
        {
            SuggestedActionStatus.Pending => "待處理",
            SuggestedActionStatus.Accepted => "已接受",
            SuggestedActionStatus.Dismissed => "已忽略",
            SuggestedActionStatus.Executed => "已執行",
            SuggestedActionStatus.Failed => "失敗",
            _ => value.ToString()
        };
}
