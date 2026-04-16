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
}
