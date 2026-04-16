namespace Memory.Dashboard.Services;

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPasswordHash = "AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==";

    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string InstanceId { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = DefaultAdminUsername;
    public string AdminPasswordHash { get; set; } = DefaultAdminPasswordHash;
    public int SessionTimeoutMinutes { get; set; } = 480;
    public string ComposeProject { get; set; } = "contexthub";
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";
    public int DockerSnapshotCacheSeconds { get; set; } = 3;
    public int DockerSnapshotTimeoutSeconds { get; set; } = 4;
    public string DataProtectionPath { get; set; } = "/var/lib/contexthub-dashboard/keys";
    public DashboardPollingOptions Polling { get; set; } = new();
}

public sealed class DashboardPollingOptions
{
    public int OverviewSeconds { get; set; } = 10;
    public int MetricsSeconds { get; set; } = 3;
    public int JobsSeconds { get; set; } = 8;
    public int LogsSeconds { get; set; } = 10;
    public int PerformanceSeconds { get; set; } = 30;
}

public sealed record DashboardLoginForm(
    string Username,
    string Password,
    string? ReturnUrl);
