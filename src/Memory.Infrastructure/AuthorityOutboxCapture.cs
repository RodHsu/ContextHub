using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Memory.Infrastructure;

internal static class AuthorityOutboxCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AuthorityOutboxEvent? TryCreate(EntityEntry entry, DateTimeOffset occurredAt)
    {
        var entity = entry.Entity;
        var category = Category(entity);
        if (category is null) return null;
        if (Identity(entry).Length == 0) return null;
        var projectId = ReadString(entry, "ProjectId") ?? ReadString(entry, "TargetProjectId") ?? string.Empty;
        // Project Skill bindings are handled separately with the owning root's tenant.
        // Other unscoped children must never become global monitoring events.
        if (projectId.Length == 0) return null;
        return Create(entry, occurredAt, category, projectId, ReadGuid(entry, "TenantId"));
    }

    internal sealed record ProjectBindingScope(EntityEntry Entry, Guid SkillId, string ProjectId, bool Original);

    public static IEnumerable<ProjectBindingScope> ProjectBindingScopes(EntityEntry<SkillBinding> entry)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) yield break;
        if (entry.State != EntityState.Deleted && entry.Entity.Scope == SkillBindingScope.Project)
            yield return new(entry, entry.Entity.SkillId, RequireProject(entry.Entity.ScopeValue), false);
        if (entry.State != EntityState.Added && entry.Property(x => x.Scope).OriginalValue == SkillBindingScope.Project)
            yield return new(entry, entry.Property(x => x.SkillId).OriginalValue, RequireProject(entry.Property(x => x.ScopeValue).OriginalValue), true);
    }

    public static AuthorityOutboxEvent CreateProjectBinding(ProjectBindingScope scope, Guid? tenantId, DateTimeOffset occurredAt)
        => Create(scope.Entry, occurredAt, "Skills", scope.ProjectId, tenantId);

    private static string RequireProject(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Project Skill binding requires an explicit project scope.")
            : value.Trim();

    private static AuthorityOutboxEvent Create(EntityEntry entry, DateTimeOffset occurredAt, string category, string projectId, Guid? tenantId)
    {
        var aggregateType = entry.Entity.GetType().Name;
        var aggregateId = Identity(entry);
        if (aggregateId.Length == 0)
            throw new InvalidOperationException("Authority outbox requires an aggregate identity.");
        var revision = ReadLong(entry, "Revision") ?? ReadLong(entry, "ClassificationRevision") ??
            ReadLong(entry, "LeaseVersion") ?? ReadLong(entry, "AuthorityRevision") ?? 0;
        var eventType = entry.State.ToString();
        var securityCritical = category is "Authorization" or "ManagedFiles" or "Secrets" or "Connections" or "Mfa" or "AgentExecution" or "Skills";
        return new AuthorityOutboxEvent
        {
            TenantId = tenantId,
            ProjectId = projectId,
            Category = category,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            AuthorityRevision = revision,
            SecurityCritical = securityCritical,
            PayloadJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                category,
                aggregateType,
                aggregateId,
                change = eventType,
                authorityRevision = revision,
                securityCritical
            }, JsonOptions),
            OccurredAt = occurredAt
        };
    }

    private static string? Category(object entity) => entity switch
    {
        ProjectHierarchy or ProjectSecurityRevision or ProjectAuthorizationPolicy or ProjectExplicitGrant => "Authorization",
        FileAsset or FileVersion or FileRelation or FileAccessEvent or FileSecurityFinding or FileDeletionRecord => "ManagedFiles",
        Secret or SecretVersion or SecretRelation or SecretGrant or SecretPolicy or SecretLease or SecretAccessEvent or
            SshCertificateLease or SshRevocationRecord => "Secrets",
        SourceConnection => "Connections",
        StepUpAssertion or StepUpAuthenticationAttempt or MfaAuthorityState or TotpFactor or MfaRecoveryCode or
            WebAuthnCredential or WebAuthnCeremony or MfaSecurityEvent => "Mfa",
        AgentExecution or AgentExecutionEvent or AgentExecutionResolutionSnapshot or AgentExecutionResolutionItem or
            AgentExecutionResourceApproval => "AgentExecution",
        Skill or SkillVersion or SkillVersionDependency or SkillBinding or SkillResolution or SkillResolutionPin or
            SkillTelemetryEvent or SkillMaterialization => "Skills",
        _ => null
    };

    private static string Identity(EntityEntry entry)
    {
        var primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey is null) return string.Empty;
        return string.Join(':', primaryKey.Properties.Select(property =>
        {
            var value = entry.Property(property.Name).CurrentValue ?? entry.Property(property.Name).OriginalValue;
            return value switch
            {
                Guid id => id.ToString("D"),
                null => string.Empty,
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            };
        }));
    }

    private static long? ReadLong(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null) return null;
        var value = entry.Property(propertyName).CurrentValue ?? entry.Property(propertyName).OriginalValue;
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadString(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        return property is null ? null : Convert.ToString(entry.Property(propertyName).CurrentValue ?? entry.Property(propertyName).OriginalValue)?.Trim();
    }

    private static Guid? ReadGuid(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null) return null;
        var value = entry.Property(propertyName).CurrentValue ?? entry.Property(propertyName).OriginalValue;
        return value is Guid id ? id : null;
    }
}
