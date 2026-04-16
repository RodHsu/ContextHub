namespace Memory.Application;

public sealed class ContextHubOptions
{
    public const string SectionName = "ContextHub";
    public string InstanceId { get; set; } = string.Empty;
}
