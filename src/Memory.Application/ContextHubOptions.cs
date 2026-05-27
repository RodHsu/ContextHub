namespace Memory.Application;

public sealed class ContextHubOptions
{
    public const string SectionName = "ContextHub";
    public string InstanceId { get; set; } = string.Empty;
    public ContextHubSecurityOptions Security { get; set; } = new();
}

public sealed class ContextHubSecurityOptions
{
    public bool RequireAuthentication { get; set; } = true;
    public string BootstrapToken { get; set; } = string.Empty;
    public string BootstrapTenantSlug { get; set; } = "system";
    public string BootstrapUsername { get; set; } = "dashboard-service";
    public string BootstrapAllowedProjectIds { get; set; } = string.Empty;
}
