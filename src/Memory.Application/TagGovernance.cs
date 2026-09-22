using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public static class CanonicalTagReasonCodes
{
    public const string ResultSelected = "ResultSelected";
    public const string UserRejected = "UserRejected";
    public const string IntentMismatch = "IntentMismatch";
    public const string FilterApplied = "FilterApplied";
    public const string SearchCandidateShown = "SearchCandidateShown";
    public const string HighImpressionLowSelection = "HighImpressionLowSelection";
    public const string StaleUnused = "StaleUnused";
    public const string DuplicateAlias = "DuplicateAlias";
    public const string Overbroad = "Overbroad";
    public const string TooSpecific = "TooSpecific";
    public const string TaxonomyConflict = "TaxonomyConflict";

    public static bool IsValid(string value) => value is
        ResultSelected or UserRejected or IntentMismatch or FilterApplied or SearchCandidateShown or
        HighImpressionLowSelection or StaleUnused or DuplicateAlias or Overbroad or TooSpecific or TaxonomyConflict;
}

public sealed record RecordTagTelemetryRequest(
    Guid DefinitionId,
    string ProjectId,
    CanonicalTagTelemetryKind Kind,
    string ReasonCode,
    string? RawQuery,
    string ResourceType,
    string ActorType);

public sealed record CanonicalTagQualityWindow(
    Guid DefinitionId,
    int Days,
    long Impressions,
    long FilterUses,
    long Selections,
    long Rejections,
    long Mismatches,
    decimal SelectionRate,
    decimal RejectionRate,
    decimal MismatchRate,
    decimal PrecisionProxy,
    IReadOnlyList<string> Signals,
    bool MeetsGovernanceThreshold);

public sealed record CanonicalTagMergePreview(Guid SourceId, Guid TargetId, int AffectedBindings, int ConflictingBindings, IReadOnlyList<string> SampleResourceIds);
public sealed record CanonicalTagSplitPreview(Guid SourceId, IReadOnlyList<string> CandidateResourceIds, int TotalBindings);
public sealed record CanonicalTagLifecycleRequest(Guid DefinitionId, CanonicalTagGovernanceProposalKind Kind, string Value, long ExpectedRevision);

public interface ICanonicalTagGovernanceService
{
    Task RecordAsync(RecordTagTelemetryRequest request, CancellationToken cancellationToken);
    Task<int> ReconcileAsync(string projectId, DateOnly? day, CancellationToken cancellationToken);
    Task<IReadOnlyList<CanonicalTagQualityWindow>> GetQualityAsync(string projectId, int days, int minimumSamples, CancellationToken cancellationToken);
    Task<CanonicalTagMergePreview> PreviewMergeAsync(string projectId, Guid sourceId, Guid targetId, CancellationToken cancellationToken);
    Task ApplyMergeAsync(string projectId, Guid sourceId, Guid targetId, long expectedSourceRevision, CancellationToken cancellationToken);
    Task<CanonicalTagSplitPreview> PreviewSplitAsync(string projectId, Guid sourceId, IReadOnlyList<string> candidateResourceIds, CancellationToken cancellationToken);
    Task ApplyLifecycleAsync(string projectId, CanonicalTagLifecycleRequest request, CancellationToken cancellationToken);
}

public sealed class CanonicalTagGovernanceService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock) : ICanonicalTagGovernanceService
{
    public async Task RecordAsync(RecordTagTelemetryRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        if (!CanonicalTagReasonCodes.IsValid(request.ReasonCode)) throw new InvalidOperationException("Tag telemetry reason code is not supported.");
        var definition = await Scope(dbContext.CanonicalTagDefinitions, actor).SingleOrDefaultAsync(x => x.Id == request.DefinitionId && x.ProjectId == projectId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Canonical tag is not available.");
        var now = clock.UtcNow;
        await dbContext.CanonicalTagTelemetryEvents.AddAsync(new CanonicalTagTelemetryEvent
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            DefinitionId = definition.Id,
            ProjectId = projectId,
            Kind = request.Kind,
            ReasonCode = request.ReasonCode,
            QueryHash = string.IsNullOrWhiteSpace(request.RawQuery) ? string.Empty : HashQuery(request.RawQuery),
            ResourceType = RequireText(request.ResourceType, nameof(request.ResourceType), 100),
            ActorType = RequireText(request.ActorType, nameof(request.ActorType), 100),
            CreatedAt = now
        }, cancellationToken);
        definition.LastUsedAt = now;
        definition.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> ReconcileAsync(string projectId, DateOnly? day, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecurityManage);
        var normalized = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalized, write: true);
        var targetDay = day ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var start = new DateTimeOffset(targetDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);
        var groups = await Scope(dbContext.CanonicalTagTelemetryEvents, actor)
            .Where(x => x.ProjectId == normalized && x.CreatedAt >= start && x.CreatedAt < end)
            .GroupBy(x => x.DefinitionId)
            .Select(x => new
            {
                DefinitionId = x.Key,
                SearchImpressions = x.LongCount(y => y.Kind == CanonicalTagTelemetryKind.SearchImpression),
                FilterUses = x.LongCount(y => y.Kind == CanonicalTagTelemetryKind.FilterUse),
                Selections = x.LongCount(y => y.Kind == CanonicalTagTelemetryKind.Selection),
                Rejections = x.LongCount(y => y.Kind == CanonicalTagTelemetryKind.Rejection),
                Mismatches = x.LongCount(y => y.Kind == CanonicalTagTelemetryKind.Mismatch)
            })
            .ToArrayAsync(cancellationToken);
        foreach (var group in groups)
        {
            var aggregate = await Scope(dbContext.CanonicalTagDailyAggregates, actor).SingleOrDefaultAsync(x => x.ProjectId == normalized && x.DefinitionId == group.DefinitionId && x.Day == targetDay, cancellationToken);
            var isNew = aggregate is null;
            aggregate ??= new CanonicalTagDailyAggregate { TenantId = actor.TenantId, OwnerUserId = actor.UserId, DefinitionId = group.DefinitionId, ProjectId = normalized, Day = targetDay };
            aggregate.SearchImpressions = group.SearchImpressions;
            aggregate.FilterUses = group.FilterUses;
            aggregate.Selections = group.Selections;
            aggregate.Rejections = group.Rejections;
            aggregate.Mismatches = group.Mismatches;
            aggregate.UpdatedAt = clock.UtcNow;
            if (isNew) await dbContext.CanonicalTagDailyAggregates.AddAsync(aggregate, cancellationToken);
        }
        var definitions = await Scope(dbContext.CanonicalTagDefinitions, actor).Where(x => x.ProjectId == normalized).ToArrayAsync(cancellationToken);
        foreach (var definition in definitions) definition.LastValidatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return groups.Length;
    }

    public async Task<IReadOnlyList<CanonicalTagQualityWindow>> GetQualityAsync(string projectId, int days, int minimumSamples, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var normalized = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalized, write: false);
        if (days is not 7 and not 30 and not 90) throw new InvalidOperationException("Tag quality window must be 7, 30, or 90 days.");
        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.AddDays(-(days - 1)));
        var aggregates = await Scope(dbContext.CanonicalTagDailyAggregates, actor).Where(x => x.ProjectId == normalized && x.Day >= since).ToArrayAsync(cancellationToken);
        return aggregates.GroupBy(x => x.DefinitionId).Select(group =>
        {
            var impressions = group.Sum(x => x.SearchImpressions);
            var filterUses = group.Sum(x => x.FilterUses);
            var selections = group.Sum(x => x.Selections);
            var rejections = group.Sum(x => x.Rejections);
            var mismatches = group.Sum(x => x.Mismatches);
            var selectionRate = Rate(selections, impressions);
            var rejectionRate = Rate(rejections, Math.Max(1, selections + rejections));
            var mismatchRate = Rate(mismatches, Math.Max(1, impressions));
            var precision = Math.Clamp(selectionRate * (1m - rejectionRate) * (1m - mismatchRate), 0m, 1m);
            var signals = new List<string>();
            if (impressions >= minimumSamples && selectionRate < 0.05m) signals.Add(CanonicalTagReasonCodes.HighImpressionLowSelection);
            if (rejectionRate >= 0.25m) signals.Add(CanonicalTagReasonCodes.Overbroad);
            if (mismatchRate >= 0.20m) signals.Add(CanonicalTagReasonCodes.TaxonomyConflict);
            if (impressions == 0) signals.Add(CanonicalTagReasonCodes.StaleUnused);
            return new CanonicalTagQualityWindow(group.Key, days, impressions, filterUses, selections, rejections, mismatches, selectionRate, rejectionRate, mismatchRate, precision, signals, impressions >= minimumSamples);
        }).ToArray();
    }

    public async Task<CanonicalTagMergePreview> PreviewMergeAsync(string projectId, Guid sourceId, Guid targetId, CancellationToken cancellationToken)
    {
        if (sourceId == targetId) throw new InvalidOperationException("A tag cannot merge into itself.");
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var normalized = ProjectContext.Normalize(projectId);
        await LoadDefinitionsAsync(normalized, sourceId, targetId, actor, cancellationToken);
        var sourceBindings = await Scope(dbContext.CanonicalTagBindings, actor).Where(x => x.ProjectId == normalized && x.DefinitionId == sourceId && x.Status == "Active").ToArrayAsync(cancellationToken);
        var targetKeys = await Scope(dbContext.CanonicalTagBindings, actor).Where(x => x.ProjectId == normalized && x.DefinitionId == targetId && x.Status == "Active")
            .Select(x => x.ResourceType + "\u001f" + x.ResourceId).ToArrayAsync(cancellationToken);
        var set = targetKeys.ToHashSet(StringComparer.Ordinal);
        var conflicts = sourceBindings.Count(x => set.Contains(x.ResourceType + "\u001f" + x.ResourceId));
        return new(sourceId, targetId, sourceBindings.Length, conflicts, sourceBindings.Take(25).Select(x => x.ResourceType + ":" + x.ResourceId).ToArray());
    }

    public async Task ApplyMergeAsync(string projectId, Guid sourceId, Guid targetId, long expectedSourceRevision, CancellationToken cancellationToken)
    {
        _ = await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var actor = RequireActor(SecurityScopes.SecurityManage);
            var normalized = ProjectContext.Normalize(projectId);
            var (source, target) = await LoadDefinitionsAsync(normalized, sourceId, targetId, actor, ct);
            if (source.Revision != expectedSourceRevision) throw new DbUpdateConcurrencyException("Canonical tag revision conflict; reload before retrying.");
            if (source.Status != CanonicalTagStatus.Active || target.Status != CanonicalTagStatus.Active) throw new InvalidOperationException("Only active canonical tags can be merged.");
            var sourceBindings = await Scope(dbContext.CanonicalTagBindings, actor).Where(x => x.ProjectId == normalized && x.DefinitionId == sourceId && x.Status == "Active").ToArrayAsync(ct);
            var targetKeys = (await Scope(dbContext.CanonicalTagBindings, actor).Where(x => x.ProjectId == normalized && x.DefinitionId == targetId && x.Status == "Active").ToArrayAsync(ct))
                .ToDictionary(x => x.ResourceType + "\u001f" + x.ResourceId, StringComparer.Ordinal);
            foreach (var binding in sourceBindings)
            {
                var key = binding.ResourceType + "\u001f" + binding.ResourceId;
                if (!targetKeys.ContainsKey(key))
                {
                    await dbContext.CanonicalTagBindings.AddAsync(new CanonicalTagBinding
                    {
                        TenantId = actor.TenantId,
                        OwnerUserId = actor.UserId,
                        DefinitionId = targetId,
                        ProjectId = normalized,
                        ResourceType = binding.ResourceType,
                        ResourceId = binding.ResourceId,
                        Source = "GovernanceMerge",
                        Confidence = binding.Confidence,
                        EvidenceRef = $"merge:{sourceId:D}",
                        Status = "Active",
                        CreatedAt = clock.UtcNow,
                        LastValidatedAt = clock.UtcNow
                    }, ct);
                }
                binding.Status = "Superseded";
                binding.LastValidatedAt = clock.UtcNow;
            }
            source.Status = CanonicalTagStatus.Superseded;
            source.RedirectToId = targetId;
            source.Revision++;
            source.LastValidatedAt = clock.UtcNow;
            source.UpdatedAt = clock.UtcNow;
            await dbContext.CanonicalTagAliases.AddAsync(new CanonicalTagAlias
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                DefinitionId = targetId,
                ProjectId = normalized,
                Alias = source.CanonicalName,
                NormalizedAlias = source.NormalizedName
            }, ct);
            await dbContext.CanonicalTagGovernanceProposals.AddAsync(new CanonicalTagGovernanceProposal
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = normalized,
                Kind = CanonicalTagGovernanceProposalKind.Merge,
                Status = CanonicalTagGovernanceProposalStatus.Applied,
                SourceDefinitionId = sourceId,
                TargetDefinitionId = targetId,
                ProposedValue = target.CanonicalName,
                ReasonCode = CanonicalTagReasonCodes.DuplicateAlias,
                AffectedBindingCount = sourceBindings.Length,
                Confidence = 1m,
                CandidateResourceIdsJson = JsonSerializer.Serialize(sourceBindings.Select(x => x.ResourceId)),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
                AppliedAt = clock.UtcNow
            }, ct);
            await dbContext.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<CanonicalTagSplitPreview> PreviewSplitAsync(string projectId, Guid sourceId, IReadOnlyList<string> candidateResourceIds, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecurityManage);
        var normalized = ProjectContext.Normalize(projectId);
        _ = await Scope(dbContext.CanonicalTagDefinitions, actor).SingleOrDefaultAsync(x => x.ProjectId == normalized && x.Id == sourceId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Canonical tag is not available.");
        var candidates = candidateResourceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Take(5000).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Tag split requires an explicit affected-resource candidate set.");
        var total = await Scope(dbContext.CanonicalTagBindings, actor).CountAsync(x => x.ProjectId == normalized && x.DefinitionId == sourceId && x.Status == "Active", cancellationToken);
        await dbContext.CanonicalTagGovernanceProposals.AddAsync(new CanonicalTagGovernanceProposal
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = normalized,
            Kind = CanonicalTagGovernanceProposalKind.Split,
            Status = CanonicalTagGovernanceProposalStatus.Pending,
            SourceDefinitionId = sourceId,
            ProposedValue = string.Empty,
            ReasonCode = CanonicalTagReasonCodes.TooSpecific,
            CandidateResourceIdsJson = JsonSerializer.Serialize(candidates),
            AffectedBindingCount = candidates.Length,
            Confidence = 0m,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(sourceId, candidates, total);
    }

    public async Task ApplyLifecycleAsync(string projectId, CanonicalTagLifecycleRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecurityManage);
        var normalized = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalized, write: true);
        if (request.Kind is not CanonicalTagGovernanceProposalKind.Rename and not CanonicalTagGovernanceProposalKind.Deprecate) throw new InvalidOperationException("Only rename and deprecate lifecycle changes are supported here.");
        var definition = await Scope(dbContext.CanonicalTagDefinitions, actor).SingleOrDefaultAsync(x => x.ProjectId == normalized && x.Id == request.DefinitionId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Canonical tag is not available.");
        if (definition.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Canonical tag revision conflict; reload before retrying.");
        var previousName = definition.CanonicalName;
        if (request.Kind == CanonicalTagGovernanceProposalKind.Rename)
        {
            var value = RequireText(request.Value, nameof(request.Value), 200);
            var normalizedValue = CanonicalTagResolver.NormalizeTag(value);
            var collision = await Scope(dbContext.CanonicalTagDefinitions, actor).AnyAsync(x => x.ProjectId == normalized && x.Id != definition.Id && x.NormalizedName == normalizedValue && x.Status == CanonicalTagStatus.Active, cancellationToken);
            if (collision) throw new InvalidOperationException("Canonical tag rename collides with an active definition.");
            await dbContext.CanonicalTagAliases.AddAsync(new CanonicalTagAlias { TenantId = actor.TenantId, OwnerUserId = actor.UserId, DefinitionId = definition.Id, ProjectId = normalized, Alias = previousName, NormalizedAlias = definition.NormalizedName }, cancellationToken);
            definition.CanonicalName = value;
            definition.NormalizedName = normalizedValue;
        }
        else
        {
            definition.Status = CanonicalTagStatus.Deprecated;
        }
        definition.Revision++;
        definition.LastValidatedAt = clock.UtcNow;
        definition.UpdatedAt = clock.UtcNow;
        await dbContext.CanonicalTagGovernanceProposals.AddAsync(new CanonicalTagGovernanceProposal
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = normalized,
            Kind = request.Kind,
            Status = CanonicalTagGovernanceProposalStatus.Applied,
            SourceDefinitionId = definition.Id,
            ProposedValue = request.Kind == CanonicalTagGovernanceProposalKind.Rename ? definition.CanonicalName : "Deprecated",
            ReasonCode = request.Kind == CanonicalTagGovernanceProposalKind.Rename ? CanonicalTagReasonCodes.DuplicateAlias : CanonicalTagReasonCodes.StaleUnused,
            AffectedBindingCount = await Scope(dbContext.CanonicalTagBindings, actor).CountAsync(x => x.ProjectId == normalized && x.DefinitionId == definition.Id && x.Status == "Active", cancellationToken),
            Confidence = 1m,
            CandidateResourceIdsJson = "[]",
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            AppliedAt = clock.UtcNow
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(CanonicalTagDefinition Source, CanonicalTagDefinition Target)> LoadDefinitionsAsync(string projectId, Guid sourceId, Guid targetId, ContextHubRequestActor actor, CancellationToken cancellationToken)
    {
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: actor.HasScope(SecurityScopes.MemoryWrite));
        var definitions = await Scope(dbContext.CanonicalTagDefinitions, actor).Where(x => x.ProjectId == projectId && (x.Id == sourceId || x.Id == targetId)).ToArrayAsync(cancellationToken);
        return (definitions.SingleOrDefault(x => x.Id == sourceId) ?? throw new UnauthorizedAccessException("Source tag is not available."), definitions.SingleOrDefault(x => x.Id == targetId) ?? throw new UnauthorizedAccessException("Target tag is not available."));
    }

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
        => !actor.HasUser ? query : actor.IsServiceActor || actor.IsAdmin
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);

    private static string HashQuery(string query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().Normalize(NormalizationForm.FormKC)))).ToLowerInvariant();
    private static decimal Rate(long numerator, long denominator) => denominator <= 0 ? 0m : decimal.Divide(numerator, denominator);
    private static string RequireText(string? value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new InvalidOperationException($"{name} is required and must not exceed {maxLength} characters.");
        return normalized;
    }
}

public interface ICanonicalTagBackgroundReconciler
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public sealed class CanonicalTagBackgroundReconciler(IApplicationDbContext dbContext, IClock clock) : ICanonicalTagBackgroundReconciler
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-90);
        var events = await dbContext.CanonicalTagTelemetryEvents.Where(x => x.CreatedAt >= since).ToArrayAsync(cancellationToken);
        var groups = events.GroupBy(x => new { x.TenantId, x.OwnerUserId, x.ProjectId, x.DefinitionId, Day = DateOnly.FromDateTime(x.CreatedAt.UtcDateTime) }).ToArray();
        foreach (var group in groups)
        {
            var aggregate = await dbContext.CanonicalTagDailyAggregates.SingleOrDefaultAsync(x => x.TenantId == group.Key.TenantId && x.OwnerUserId == group.Key.OwnerUserId && x.ProjectId == group.Key.ProjectId && x.DefinitionId == group.Key.DefinitionId && x.Day == group.Key.Day, cancellationToken);
            var isNew = aggregate is null;
            aggregate ??= new CanonicalTagDailyAggregate { TenantId = group.Key.TenantId, OwnerUserId = group.Key.OwnerUserId, ProjectId = group.Key.ProjectId, DefinitionId = group.Key.DefinitionId, Day = group.Key.Day };
            aggregate.SearchImpressions = group.LongCount(x => x.Kind == CanonicalTagTelemetryKind.SearchImpression);
            aggregate.FilterUses = group.LongCount(x => x.Kind == CanonicalTagTelemetryKind.FilterUse);
            aggregate.Selections = group.LongCount(x => x.Kind == CanonicalTagTelemetryKind.Selection);
            aggregate.Rejections = group.LongCount(x => x.Kind == CanonicalTagTelemetryKind.Rejection);
            aggregate.Mismatches = group.LongCount(x => x.Kind == CanonicalTagTelemetryKind.Mismatch);
            aggregate.UpdatedAt = clock.UtcNow;
            if (isNew) await dbContext.CanonicalTagDailyAggregates.AddAsync(aggregate, cancellationToken);
        }
        var staleCutoff = clock.UtcNow.AddDays(-90);
        var staleSuggestions = await dbContext.CanonicalTagSuggestions.Where(x => x.Status == "Pending" && x.UpdatedAt < staleCutoff).ToArrayAsync(cancellationToken);
        foreach (var suggestion in staleSuggestions) suggestion.Status = "Rejected";
        await dbContext.SaveChangesAsync(cancellationToken);
        return groups.Length + staleSuggestions.Length;
    }
}
