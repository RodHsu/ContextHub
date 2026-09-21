namespace Memory.Domain;

public enum AuthorizationEffect
{
    Allow,
    Deny
}

public enum AuthorizationRuleSource
{
    PolicyDerived,
    ExplicitGrant
}

public sealed class ProjectSecurityRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public long TopologyRevision { get; set; }
    public long PolicyRevision { get; set; }
    public long GrantRevision { get; set; }
    public long TagRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectAuthorizationPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string Right { get; set; } = string.Empty;
    public AuthorizationEffect Effect { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string EvidenceRef { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectExplicitGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string Right { get; set; } = string.Empty;
    public AuthorizationEffect Effect { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string EvidenceRef { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CanonicalTagDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CanonicalTagAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DefinitionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public CanonicalTagDefinition? Definition { get; set; }
}

public sealed class CanonicalTagRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceDefinitionId { get; set; }
    public Guid TargetDefinitionId { get; set; }
    public string RelationType { get; set; } = string.Empty;
}

public sealed class CanonicalTagBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DefinitionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CanonicalTagSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string SuggestedName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
