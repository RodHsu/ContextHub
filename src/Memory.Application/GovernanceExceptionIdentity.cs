using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

public static class GovernanceExceptionIdentity
{
    public static bool RequiresHumanDecision(GovernanceReviewItem item)
        => item.RequiresExplicitApproval || !Enum.TryParse<GovernanceBatchActionType>(item.RecommendedAction, out var action) || !Enum.IsDefined(action);

    public static string NormalizeKey(string key)
    {
        var parts = key.Split(':');
        // Only known legacy candidate identities had a trailing update timestamp.
        // Execution ItemKey/cursors remain version-bound and are never normalized.
        if (parts.Length == 4 && parts[0] == "candidate" &&
            parts[1] is "finding" or "insight" && Guid.TryParse(parts[2], out var id) &&
            long.TryParse(parts[3], out _))
            return $"candidate:{parts[1]}:{id:N}";
        return key;
    }

    public static GovernanceExceptionStateResult FromCandidate(GovernanceReviewItem item)
        => new(NormalizeKey($"candidate:{item.ItemKey}"), item.ItemKind.ToString(), "RequiresUserDecision", 2)
        {
            EvidenceFingerprint = Hash(new
            {
                item.ProjectId,
                item.Classification,
                item.RecommendedAction,
                item.RiskLevel,
                item.RequiresExplicitApproval,
                item.AuthorityResourceId,
                resources = item.RelatedResourceIds.Order().ToArray(),
                reasons = item.ReasonCodes.Order().ToArray(),
                item.SemanticEvidenceHash
            })
        };

    public static string MemoryEvidence(MemoryItem item)
        => Hash(new
        {
            item.Id,
            item.ProjectId,
            item.Content,
            item.Summary,
            item.Title,
            item.Status,
            item.SourceType,
            item.SourceRef,
            item.ExternalKey,
            item.MetadataJson,
            item.MemoryType,
            item.Importance,
            item.Confidence,
            item.IsReadOnly,
            tags = item.Tags.Order().ToArray(),
            item.AuthorityState,
            item.SupersedesId,
            item.SupersededById,
            item.SuccessorEvidenceId,
            item.SuccessorEvidenceRef,
            item.ValidFrom,
            item.ValidUntil
        });

    public static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
