using Memory.Domain;

namespace Memory.Application;

/// <summary>
/// Canonical server-side gate for actions exposed by Scheduled Governance.
/// The review plan is still authoritative for what was discovered; this gate
/// is the narrower policy boundary for what the scheduled surface may execute.
/// </summary>
internal static class ScheduledGovernanceAutomationEligibility
{
    internal const string PolicyVersion = "scheduled-automation-eligibility-2026-09-08-v1";

    private static readonly IReadOnlySet<GovernanceBatchActionType> AllowedActions =
        ScheduledGovernanceContract.FixedReversibleActions.ToHashSet();

    internal static ScheduledGovernanceEligibilityResult Evaluate(GovernanceReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var actionText = item.RecommendedAction?.Trim() ?? string.Empty;
        if (item.ItemKind == GovernanceItemKind.WorkItem)
        {
            return RequiresDecision("business-work-item-scheduled-forbidden");
        }

        if (item.ItemKind == GovernanceItemKind.Proposal ||
            actionText.Equals(GovernanceBatchActionType.ProposalApply.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return RequiresDecision("proposal-apply-scheduled-forbidden");
        }

        if (item.ItemKind is GovernanceItemKind.LogPartition or GovernanceItemKind.LogCandidate ||
            actionText is "LogPromote" or "LogArchive" or "LogRetentionProposal")
        {
            return RequiresDecision("raw-log-action-scheduled-forbidden");
        }

        if (item.ItemKind == GovernanceItemKind.Project &&
            IsProjectInformationGap(item))
        {
            return RequiresDecision("project-metadata-requires-human-decision");
        }

        if (!Enum.TryParse<GovernanceBatchActionType>(actionText, ignoreCase: true, out var action) ||
            !AllowedActions.Contains(action))
        {
            return RequiresDecision("unsupported-scheduled-action");
        }

        if (!item.AuthorityResourceId.HasValue || item.AuthorityResourceId.Value == Guid.Empty)
        {
            return RequiresDecision("missing-authoritative-resource");
        }

        if (item.RequiresExplicitApproval)
        {
            return RequiresDecision("explicit-approval-required");
        }

        if (!item.IsReversible)
        {
            return RequiresDecision("non-reversible-action");
        }

        if (item.RiskLevel != GovernanceBatchRiskLevel.Low)
        {
            return RequiresDecision("risk-above-low");
        }

        if (item.SemanticConfidence < 0.90m)
        {
            return RequiresDecision("semantic-confidence-below-threshold");
        }

        var reasonCodes = NormalizeCodes(item.ReasonCodes);
        if (ContainsAuthorityConflict(reasonCodes))
        {
            return RequiresDecision("authority-conflict-or-pending");
        }

        if (!HasDeterministicEvidence(item, action, reasonCodes))
        {
            return RequiresDecision("deterministic-authority-evidence-required");
        }

        return new(ScheduledGovernanceEligibility.AutomationActionable, "deterministic-reversible-authoritative-evidence");
    }

    private static bool IsProjectInformationGap(GovernanceReviewItem item)
    {
        var classification = Normalize(item.Classification);
        return classification is "MISSINGPROJECTINFORMATION" or "INVALIDPROJECTMETADATA";
    }

    private static bool HasDeterministicEvidence(
        GovernanceReviewItem item,
        GovernanceBatchActionType action,
        IReadOnlySet<string> reasonCodes)
    {
        var classification = Normalize(item.Classification);
        return item.ItemKind switch
        {
            GovernanceItemKind.Memory =>
                classification == "SUPERSEDEDMEMORYCANDIDATE" ||
                reasonCodes.Contains("DUPLICATE") ||
                reasonCodes.Contains("ARCHIVEFIRST") ||
                reasonCodes.Contains("DETERMINISTICAUTHORITYWINNER") ||
                reasonCodes.Contains("SUCCESSOREVIDENCE") ||
                reasonCodes.Contains("SUCCESSOREVIDENCESTRONG"),
            GovernanceItemKind.Retention => true,
            GovernanceItemKind.Artifact =>
                action == GovernanceBatchActionType.ArtifactReconcile &&
                (reasonCodes.Contains("ARTIFACTDUPLICATEKEY") ||
                 reasonCodes.Contains("DETERMINISTICAUTHORITYWINNER")),
            GovernanceItemKind.ConversationInsight => IsDeterministicInsight(classification, reasonCodes),
            GovernanceItemKind.Project =>
                action == GovernanceBatchActionType.LifecycleReconcile &&
                (reasonCodes.Contains("PROJECTMETADATAAUTHORITATIVEREADBACK") ||
                 reasonCodes.Contains("PROJECTMETADATADETERMINISTIC")),
            GovernanceItemKind.Discussion =>
                action == GovernanceBatchActionType.DiscussionReconcile &&
                reasonCodes.Contains("DISCUSSIONCLOSEDRETENTION"),
            GovernanceItemKind.UserPreference =>
                action == GovernanceBatchActionType.PreferenceReconcile &&
                reasonCodes.Contains("PREFERENCEDUPLICATEKEY"),
            GovernanceItemKind.SuggestedAction =>
                action == GovernanceBatchActionType.SuggestedActionReconcile &&
                (reasonCodes.Contains("SUGGESTEDACTIONDETERMINISTIC") ||
                 reasonCodes.Contains("REINDEXDETERMINISTIC")),
            _ => false
        };
    }

    private static bool IsDeterministicInsight(
        string classification,
        IReadOnlySet<string> reasonCodes)
    {
        if (classification is
            "ALREADYPROMOTEDCONVERSATIONINSIGHT" or
            "DUPLICATECONVERSATIONINSIGHT" or
            "SUPERSEDEDCONVERSATIONINSIGHT" or
            "STALECONVERSATIONINSIGHT")
        {
            return true;
        }

        return reasonCodes.Contains("INSIGHTALREADYPROMOTED") ||
               reasonCodes.Contains("INSIGHTPROMOTEDREADBACK") ||
               reasonCodes.Contains("INSIGHTEXACTDUPLICATE") ||
               reasonCodes.Contains("INSIGHTDUPLICATE") ||
               reasonCodes.Contains("INSIGHTSUPERSEDED") ||
               reasonCodes.Contains("INSIGHTSTALE");
    }

    private static bool ContainsAuthorityConflict(IReadOnlySet<string> reasonCodes)
        => reasonCodes.Any(code =>
            code.Contains("AUTHORITYCONFLICT", StringComparison.Ordinal) ||
            code.Contains("AUTHORITYAMBIGUOUS", StringComparison.Ordinal) ||
            code.Contains("AUTHORITYPENDING", StringComparison.Ordinal) ||
            code.Contains("CONFLICTINGAUTHORITY", StringComparison.Ordinal));

    private static IReadOnlySet<string> NormalizeCodes(IEnumerable<string>? codes)
        => (codes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string? value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();

    private static ScheduledGovernanceEligibilityResult RequiresDecision(string reasonClass)
        => new(ScheduledGovernanceEligibility.RequiresUserDecision, reasonClass);
}

internal enum ScheduledGovernanceEligibility
{
    AutomationActionable,
    RequiresUserDecision
}

internal sealed record ScheduledGovernanceEligibilityResult(
    ScheduledGovernanceEligibility Eligibility,
    string ReasonClass)
{
    internal bool AutomationActionable => Eligibility == ScheduledGovernanceEligibility.AutomationActionable;
    internal bool RequiresUserDecision => Eligibility == ScheduledGovernanceEligibility.RequiresUserDecision;
}
