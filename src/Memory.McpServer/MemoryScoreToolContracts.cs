using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Memory.Application;
using Memory.Domain;

namespace Memory.McpServer;

/// <summary>
/// MCP-facing request DTOs keep the score range visible in the published
/// input schemas. MCP does not execute DataAnnotations at runtime, so the tool
/// methods also call <see cref="MemoryScoreContract"/> before dispatching.
/// </summary>
public sealed record MemoryUpsertToolRequest(
    string ExternalKey,
    MemoryScope Scope,
    MemoryType MemoryType,
    string Title,
    string Content,
    string Summary,
    string SourceType,
    string SourceRef,
    IReadOnlyList<string> Tags,
    [property: Range(0d, 1d)]
    [property: Description("Canonical importance score in the inclusive [0,1] range; percentage values are invalid.")]
    decimal Importance,
    [property: Range(0d, 1d)]
    [property: Description("Canonical confidence score in the inclusive [0,1] range; percentage values are invalid.")]
    decimal Confidence,
    string MetadataJson = "{}",
    string ProjectId = ProjectContext.DefaultProjectId)
{
    public MemoryUpsertRequest ToApplicationRequest()
        => new(
            ExternalKey,
            Scope,
            MemoryType,
            Title,
            Content,
            Summary,
            SourceType,
            SourceRef,
            Tags,
            Importance,
            Confidence,
            MetadataJson,
            ProjectId);
}

public sealed record MemoryUpdateToolRequest(
    Guid Id,
    string? Title = null,
    string? Content = null,
    string? Summary = null,
    IReadOnlyList<string>? Tags = null,
    [property: Range(0d, 1d)]
    [property: Description("Canonical importance score in the inclusive [0,1] range; null leaves the current value unchanged.")]
    decimal? Importance = null,
    [property: Range(0d, 1d)]
    [property: Description("Canonical confidence score in the inclusive [0,1] range; null leaves the current value unchanged.")]
    decimal? Confidence = null,
    string? MetadataJson = null,
    string? ProjectId = null)
{
    public MemoryUpdateRequest ToApplicationRequest()
        => new(
            Id,
            Title,
            Content,
            Summary,
            Tags,
            Importance,
            Confidence,
            MetadataJson,
            ProjectId);
}

public sealed record UserPreferenceUpsertToolRequest(
    string Key,
    UserPreferenceKind Kind,
    string Title,
    string Content,
    string Rationale,
    IReadOnlyList<string>? Tags = null,
    [property: Range(0d, 1d)]
    [property: Description("Canonical importance score in the inclusive [0,1] range; percentage values are invalid.")]
    decimal Importance = 0.95m,
    [property: Range(0d, 1d)]
    [property: Description("Canonical confidence score in the inclusive [0,1] range; percentage values are invalid.")]
    decimal Confidence = 0.95m)
{
    public UserPreferenceUpsertRequest ToApplicationRequest()
        => new(Key, Kind, Title, Content, Rationale, Tags, Importance, Confidence);
}
