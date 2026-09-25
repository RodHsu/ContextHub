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
        var aggregateType = entity.GetType().Name;
        var aggregateId = Identity(entry);
        if (aggregateId.Length == 0) return null;
        var revision = ReadLong(entry, "Revision") ?? ReadLong(entry, "ClassificationRevision") ??
            ReadLong(entry, "LeaseVersion") ?? ReadLong(entry, "AuthorityRevision") ?? 0;
        var projectId = ReadString(entry, "ProjectId") ?? ReadString(entry, "TargetProjectId") ?? string.Empty;
        // Child evidence without its own project identity is represented by the root authority
        // mutation in the same unit of work. Never emit an unscoped event that could merge
        // otherwise isolated projects in monitoring.
        if (projectId.Length == 0) return null;
        var tenantId = ReadGuid(entry, "TenantId");
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
