using Microsoft.AspNetCore.Components.Routing;

namespace Memory.Dashboard.Services;

public sealed record DashboardNavigationItem(string Mark, string Label, string Href, string Description, NavLinkMatch Match = NavLinkMatch.Prefix, bool RequiresAdmin = true);
public sealed record DashboardNavigationGroup(string Label, IReadOnlyList<DashboardNavigationItem> Items);
public sealed record DomainSurfaceLink(string Label, string Href, string Purpose);

public static class DashboardNavigation
{
    public static readonly IReadOnlyList<DashboardNavigationGroup> Groups =
    [
        new("總覽", [new("O", "總覽", "/", "健康、風險與待決事項", NavLinkMatch.All)]),
        new("專案與協作", [
            new("P", "專案工作區", "/project-information", "專案背景與生命週期"),
            new("H", "專案拓撲", "/project-tree", "階層、關係與權限影響"),
            new("W", "專案待辦", "/project-work-items", "可執行工作與 checklist"),
            new("D", "跨專案討論", "/discussions", "參與者範圍內的協作")
        ]),
        new("知識與檔案", [
            new("F", "受管檔案", "/files", "檔案、版本、隔離與權限"),
            new("M", "記憶資料", "/memories", "搜尋、內容與 revisions", RequiresAdmin: false),
            new("G", "記憶圖譜", "/graph", "關係與整合視圖", RequiresAdmin: false),
            new("S", "資料來源", "/sources", "來源與同步狀態"),
            new("R", "記憶整理", "/retention", "保留、封存與治理候選")
        ]),
        new("Agent 自動化", [
            new("A", "Agent 執行", "/agent-executions", "Queue、lease、retry 與 evidence"),
            new("K", "Agent Skills", "/skills", "Skills lifecycle 與 telemetry"),
            new("C", "連線狀態", "/connectivity", "Agent connectivity telemetry")
        ]),
        new("治理與安全", [
            new("Z", "Secrets 與 MFA", "/secrets", "Credential inventory、assurance 與稽核"),
            new("I", "治理建議", "/inbox", "檢視與處理系統產生的建議"),
            new("V", "治理檢查", "/governance", "政策、例外與 receipts"),
            new("Q", "ChatGPT 寫入審核", "/chatgpt-proposals", "提案 review 與 read-back"),
            new("E", "評估驗證", "/evaluation", "評估 runs 與 evidence"),
            new("X", "安全管理", "/security", "角色、session 與稽核")
        ]),
        new("營運與監控", [
            new("N", "營運中心", "/operations", "Authority、projection 與背景工作"),
            new("O", "即時監控", "/monitoring", "服務健康、資源與趨勢"),
            new("C", "執行環境", "/runtime", "服務設定、依賴與執行狀態"),
            new("W", "背景工作", "/jobs", "佇列、執行結果與維護"),
            new("L", "日誌與診斷", "/logs", "可搜尋 runtime logs"),
            new("F", "效能", "/performance", "受控 performance probes"),
            new("B", "資料庫檢視", "/storage", "受限 storage diagnostics"),
            new("T", "MCP 與 API", "/mcp-tools", "產品介面與工具說明")
        ]),
        new("設定與帳戶", [
            new("Y", "系統設定", "/settings", "平台設定與更新頻率"),
            new("U", "偏好", "/preferences", "個人顯示與行為", RequiresAdmin: false),
            new("N", "存取權杖", "/account/tokens", "個人 token lifecycle", RequiresAdmin: false)
        ])
    ];

    public static IEnumerable<DashboardNavigationGroup> VisibleGroups(bool isAdmin)
        => Groups.Select(group => new DashboardNavigationGroup(group.Label, group.Items.Where(item => isAdmin || !item.RequiresAdmin).ToArray()))
            .Where(group => group.Items.Count > 0);

    public static DashboardNavigationGroup? FindGroup(string href)
        => Groups.FirstOrDefault(group => group.Items.Any(item => IsCurrent(item, href)));

    public static bool IsCurrent(DashboardNavigationItem item, string href)
    {
        var path = href.Split('?', '#')[0].TrimEnd('/');
        return string.Equals(item.Href, path.Length == 0 ? "/" : path, StringComparison.OrdinalIgnoreCase);
    }
}
