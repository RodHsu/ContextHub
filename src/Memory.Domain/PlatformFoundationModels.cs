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

public enum ManagedObjectSecurityDomain
{
    ManagedFile,
    Secret
}

public enum ManagedObjectState
{
    Staged,
    Ready,
    Orphaned,
    Missing,
    Corrupt,
    Tombstoned
}

public enum ManagedTransferOperation
{
    Upload,
    Download
}

public enum ManagedTransferSessionState
{
    Active,
    Completed,
    Revoked,
    Expired
}

public sealed class ManagedObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public ManagedObjectSecurityDomain SecurityDomain { get; set; } = ManagedObjectSecurityDomain.ManagedFile;
    public ManagedObjectState State { get; set; } = ManagedObjectState.Staged;
    public string StorageId { get; set; } = string.Empty;
    public long PlaintextLength { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public int EncryptionSchemaVersion { get; set; } = 1;
    public int EncryptionGeneration { get; set; } = 1;
    public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
    public string KeyId { get; set; } = string.Empty;
    public byte[] WrappedDek { get; set; } = [];
    public byte[] WrapNonce { get; set; } = [];
    public byte[] WrapTag { get; set; } = [];
    public string? PlaintextSha256 { get; set; }
    public DateTimeOffset StagedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TombstonedAt { get; set; }
    public ICollection<ManagedObjectChunk> Chunks { get; set; } = [];
}

public sealed class ManagedObjectChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagedObjectId { get; set; }
    public int ChunkIndex { get; set; }
    public long PlaintextOffset { get; set; }
    public int PlaintextLength { get; set; }
    public int CiphertextLength { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public string PlaintextSha256 { get; set; } = string.Empty;
    public string CiphertextSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ManagedObject? ManagedObject { get; set; }
}

public sealed class ManagedTransferSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagedObjectId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string? AgentId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid CapabilityId { get; set; } = Guid.NewGuid();
    public string CapabilityHash { get; set; } = string.Empty;
    public ManagedTransferOperation Operation { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public long MaxBytes { get; set; }
    public long UsedBytes { get; set; }
    public int MaxConcurrency { get; set; }
    public long Revision { get; set; } = 1;
    public int EncryptionGeneration { get; set; } = 1;
    public ManagedTransferSessionState State { get; set; } = ManagedTransferSessionState.Active;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ManagedObject? ManagedObject { get; set; }
    public ICollection<ManagedTransferOperationRecord> Operations { get; set; } = [];
}

public sealed class ManagedTransferOperationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public long BytesTransferred { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ManagedTransferSession? Session { get; set; }
}
