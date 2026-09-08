using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

/// <summary>
/// A small compatibility adapter for authority metadata until the durable B1
/// schema is available.  The adapter deliberately stays at the retrieval
/// boundary: it does not mutate memories or infer a replacement in storage.
/// </summary>
internal static class AuthorityAwareRetrievalReranker
{
    internal const string RankingVersion = "authority-v1";
    internal const string ConflictMarker = "[Authority conflict: competing claims; no winner selected.]";

    private static readonly string[] SupersedesPropertyNames =
    [
        "supersedes",
        "supersedesMemoryId",
        "supersedesMemoryIds",
        "replaces",
        "replacesMemoryId",
        "replacesMemoryIds"
    ];

    private static readonly string[] SupersededByPropertyNames =
    [
        "supersededBy",
        "supersededByMemoryId",
        "supersededByMemoryIds",
        "replacedBy",
        "replacedByMemoryId",
        "replacedByMemoryIds"
    ];

    private static readonly string[] AuthorityStatePropertyNames =
    [
        "authorityState",
        "authorityStatus",
        "authority_state",
        "authority_status",
        "lifecycleStatus",
        "lifecycle_status"
    ];

    private static readonly string[] AuthorityKeyPropertyNames =
    [
        "authorityKey",
        "authority_key",
        "subjectKey",
        "subject_key",
        "canonicalKey",
        "canonical_key",
        "statusKey",
        "status_key",
        "claimKey",
        "claim_key",
        "entityKey",
        "entity_key"
    ];

    private static readonly string[] ValidFromPropertyNames =
    [
        "validFrom",
        "valid_from",
        "validFromUtc",
        "valid_from_utc",
        "effectiveFrom",
        "effective_from"
    ];

    private static readonly string[] ValidUntilPropertyNames =
    [
        "validUntil",
        "valid_until",
        "validUntilUtc",
        "valid_until_utc",
        "effectiveUntil",
        "effective_until"
    ];

    // Resolve B1 members opportunistically so this adapter remains buildable
    // while the durable model migration is being integrated. The metadata and
    // legacy-status paths below remain the fallback for older model binaries.
    private static readonly PropertyInfo? TypedAuthorityStateProperty = typeof(MemoryItem).GetProperty("AuthorityState");
    private static readonly PropertyInfo? TypedSupersedesIdProperty = typeof(MemoryItem).GetProperty("SupersedesId");
    private static readonly PropertyInfo? TypedSupersededByIdProperty = typeof(MemoryItem).GetProperty("SupersededById");
    private static readonly PropertyInfo? TypedValidFromProperty = typeof(MemoryItem).GetProperty("ValidFrom");
    private static readonly PropertyInfo? TypedValidUntilProperty = typeof(MemoryItem).GetProperty("ValidUntil");

    public static IReadOnlyList<RankedRetrievalCandidate> Rerank(
        IReadOnlyList<RetrievalCandidate> candidates,
        int limit,
        DateTimeOffset? now = null)
    {
        if (limit <= 0 || candidates.Count == 0)
        {
            return [];
        }

        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var observations = candidates
            .Select(candidate => Observe(candidate, referenceTime))
            .ToArray();
        var replacementGraph = BuildReplacementGraph(observations);
        var successorCounts = BuildSuccessorCounts(replacementGraph);
        var cycleNodes = FindCycleNodes(replacementGraph);
        var candidateIds = observations
            .Select(x => x.Candidate.Item.Id)
            .ToHashSet();
        var evaluated = observations
            .Select(observation => Evaluate(observation, successorCounts, cycleNodes, candidateIds, referenceTime))
            .ToArray();

        foreach (var group in evaluated.GroupBy(x => x.GroupKey, StringComparer.Ordinal))
        {
            MarkAuthorityConflicts(group);
        }

        return evaluated
            .OrderByDescending(x => x.AuthorityRank)
            .ThenByDescending(x => x.IsAuthorityConflict ? 0 : x.IsExplicitCurrent ? 1 : 0)
            .ThenByDescending(x => x.IsAuthorityConflict ? 0m : x.Candidate.Score)
            .ThenByDescending(x => x.IsAuthorityConflict ? DateTimeOffset.MinValue : x.Evidence.ValidFrom ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.Candidate.Item.UpdatedAt)
            .ThenBy(x => x.Candidate.Item.Id)
            .Take(limit)
            .Select(x => x.ToRankedCandidate())
            .ToArray();
    }

    private static AuthorityObservation Observe(RetrievalCandidate candidate, DateTimeOffset now)
    {
        var evidence = ParseEvidence(candidate.Item);
        var state = ResolveInitialState(candidate.Item, evidence, now);
        var groupKey = BuildGroupKey(candidate.Item, evidence);
        return new AuthorityObservation(candidate, evidence, state, groupKey);
    }

    private static AuthorityEvaluation Evaluate(
        AuthorityObservation observation,
        IReadOnlyDictionary<Guid, int> successorCounts,
        IReadOnlySet<Guid> cycleNodes,
        IReadOnlySet<Guid> candidateIds,
        DateTimeOffset now)
    {
        var item = observation.Candidate.Item;
        var evidence = observation.Evidence;
        var successorCount = successorCounts.GetValueOrDefault(item.Id);
        // A relation is authority evidence only inside the same tenant/owner/
        // project/scope boundary. BuildReplacementGraph already filters links
        // to another candidate in a different boundary; IDs absent from the
        // result set remain valid external evidence and still demote a stale
        // predecessor. This keeps shared/user/project results isolated.
        // Only the typed FK may prove a successor that is outside this result
        // page. Its database trigger enforces tenant/owner/project scope. A
        // legacy metadata GUID cannot be scope-verified when the target is not
        // loaded, so it must fail closed instead of demoting the candidate.
        var externalSuccessorCount = evidence.TypedSupersededById is { } typedSuccessorId &&
                                     !candidateIds.Contains(typedSuccessorId)
            ? 1
            : 0;
        var validSuccessorCount = successorCount + externalSuccessorCount;
        var hasSuccessorEvidence = validSuccessorCount > 0;
        var hasSelfReference = evidence.SupersedesIds.Contains(item.Id) || evidence.SupersededByIds.Contains(item.Id);
        var hasCycle = cycleNodes.Contains(item.Id);
        // MemoryStatus is the visibility/lifecycle contract, while
        // AuthorityState is the claim-precedence contract. An item may be
        // archived for retention or visibility reasons without ceasing to be
        // the current authority. Only replacement evidence contradicts an
        // explicit Current claim; lifecycle status alone must not manufacture
        // an authority conflict.
        var contradictoryLifecycle = evidence.HasExplicitCurrent && hasSuccessorEvidence;
        var contradictoryReplacement =
            hasSuccessorEvidence && evidence.HasExplicitCurrent ||
            evidence.HasExplicitHistorical && evidence.SupersedesIds.Count > 0 ||
            validSuccessorCount > 1;
        var contradictoryWindow = evidence.HasExplicitCurrent &&
                                  (evidence.ValidFrom > now || evidence.ValidUntil <= now);
        var conflicted = evidence.HasContradictoryState ||
                         evidence.HasMalformedWindow ||
                         evidence.HasMalformedRelationship ||
                         hasSelfReference ||
                         hasCycle ||
                         contradictoryLifecycle ||
                         contradictoryReplacement ||
                         contradictoryWindow;

        var state = observation.InitialState;
        if (hasSuccessorEvidence && state == RetrievalAuthorityState.Current)
        {
            state = RetrievalAuthorityState.Superseded;
        }

        if (successorCount > 0 && state == RetrievalAuthorityState.Pending)
        {
            state = RetrievalAuthorityState.Superseded;
        }

        if (conflicted)
        {
            state = RetrievalAuthorityState.Conflicted;
        }

        var rank = state switch
        {
            RetrievalAuthorityState.Current when evidence.HasExplicitCurrent => 500,
            RetrievalAuthorityState.Current => 450,
            RetrievalAuthorityState.Pending => 300,
            RetrievalAuthorityState.Unknown => 200,
            RetrievalAuthorityState.Superseded => 100,
            RetrievalAuthorityState.Historical => 50,
            RetrievalAuthorityState.Conflicted => 420,
            _ => 0
        };

        // An authority conflict remains visible above historical evidence, but
        // the later conflict pass forces all competing claims into one tier so
        // semantic score cannot silently select a winner.
        if (conflicted)
        {
            rank = 420;
        }

        return new AuthorityEvaluation(
            observation,
            state,
            rank,
            evidence.HasExplicitCurrent,
            conflicted,
            hasSuccessorEvidence,
            now);
    }

    private static void MarkAuthorityConflicts(IEnumerable<AuthorityEvaluation> values)
    {
        var group = values.ToArray();
        var unresolvedCurrent = group
            .Where(x => x.State is RetrievalAuthorityState.Current or RetrievalAuthorityState.Conflicted)
            .Where(x => !x.HasSuccessorEvidence)
            .ToArray();

        var hasCompetingCurrentClaims = unresolvedCurrent.Length > 1;
        if (!hasCompetingCurrentClaims && !group.Any(x => x.IsAuthorityConflict))
        {
            return;
        }

        foreach (var evaluation in group)
        {
            if (hasCompetingCurrentClaims && unresolvedCurrent.Contains(evaluation))
            {
                evaluation.IsAuthorityConflict = true;
                evaluation.AuthorityRank = 420;
                continue;
            }

            if (evaluation.IsAuthorityConflict)
            {
                evaluation.AuthorityRank = 420;
            }
        }
    }

    private static IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> BuildReplacementGraph(
        IReadOnlyList<AuthorityObservation> observations)
    {
        var graph = observations.ToDictionary(
            x => x.Candidate.Item.Id,
            _ => (IReadOnlySet<Guid>)new HashSet<Guid>());
        var candidateById = observations.ToDictionary(x => x.Candidate.Item.Id);

        foreach (var observation in observations)
        {
            var successors = (HashSet<Guid>)graph[observation.Candidate.Item.Id];
            foreach (var successorId in observation.Evidence.SupersededByIds)
            {
                if (candidateById.TryGetValue(successorId, out var successor) &&
                    IsSameAuthorityScope(observation.Candidate.Item, successor.Candidate.Item))
                {
                    successors.Add(successorId);
                }
            }

            foreach (var predecessorId in observation.Evidence.SupersedesIds)
            {
                if (candidateById.TryGetValue(predecessorId, out var predecessor) &&
                    IsSameAuthorityScope(observation.Candidate.Item, predecessor.Candidate.Item))
                {
                    ((HashSet<Guid>)graph[predecessorId]).Add(observation.Candidate.Item.Id);
                }
            }
        }

        return graph;
    }

    private static IReadOnlyDictionary<Guid, int> BuildSuccessorCounts(
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> graph)
        => graph.ToDictionary(x => x.Key, x => x.Value.Count);

    private static IReadOnlySet<Guid> FindCycleNodes(IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> graph)
    {
        var colors = new Dictionary<Guid, int>();
        var stack = new List<Guid>();
        var stackIndexes = new Dictionary<Guid, int>();
        var cycleNodes = new HashSet<Guid>();

        bool Visit(Guid id)
        {
            colors[id] = 1;
            stackIndexes[id] = stack.Count;
            stack.Add(id);
            foreach (var successorId in graph[id])
            {
                if (!colors.TryGetValue(successorId, out var color))
                {
                    Visit(successorId);
                }
                else if (color == 1 && stackIndexes.TryGetValue(successorId, out var cycleStart))
                {
                    for (var index = cycleStart; index < stack.Count; index++)
                    {
                        cycleNodes.Add(stack[index]);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            stackIndexes.Remove(id);
            colors[id] = 2;
            return cycleNodes.Contains(id);
        }

        foreach (var id in graph.Keys)
        {
            if (!colors.ContainsKey(id))
            {
                Visit(id);
            }
        }

        return cycleNodes;
    }

    private static RetrievalAuthorityState ResolveInitialState(
        MemoryItem item,
        AuthorityEvidence evidence,
        DateTimeOffset now)
    {
        if (evidence.HasContradictoryState)
        {
            return RetrievalAuthorityState.Conflicted;
        }

        if (evidence.ExplicitState.HasValue)
        {
            return evidence.ExplicitState.Value;
        }

        if (evidence.ValidFrom > now)
        {
            return RetrievalAuthorityState.Pending;
        }

        if (evidence.ValidUntil <= now)
        {
            return RetrievalAuthorityState.Historical;
        }

        return item.Status switch
        {
            MemoryStatus.Active => RetrievalAuthorityState.Current,
            MemoryStatus.Superseded => RetrievalAuthorityState.Superseded,
            // Lifecycle status is not authority evidence. A stale or archived
            // item may still be the current authority until an explicit
            // supersession/history claim is persisted.
            MemoryStatus.Stale => RetrievalAuthorityState.Current,
            MemoryStatus.Archived => RetrievalAuthorityState.Current,
            _ => RetrievalAuthorityState.Unknown
        };
    }

    private static AuthorityEvidence ParseEvidence(MemoryItem item)
    {
        var legacyStates = new HashSet<RetrievalAuthorityState>();
        var supersedesIds = new HashSet<Guid>();
        var supersededByIds = new HashSet<Guid>();
        var authorityKey = string.Empty;
        DateTimeOffset? validFrom = null;
        DateTimeOffset? validUntil = null;
        var malformedWindow = false;
        var malformedRelationship = false;
        var malformedJson = false;

        foreach (var tag in item.Tags ?? [])
        {
            AddTagState(legacyStates, tag);
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(item.MetadataJson) ? "{}" : item.MetadataJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (AuthorityStatePropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        AddMetadataStates(legacyStates, property.Value);
                    }
                    else if (string.Equals(property.Name, "authority", StringComparison.OrdinalIgnoreCase))
                    {
                        AddMetadataStates(legacyStates, property.Value);
                    }

                    if (SupersedesPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        ReadGuids(property.Value, supersedesIds, ref malformedRelationship);
                    }

                    if (SupersededByPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        ReadGuids(property.Value, supersededByIds, ref malformedRelationship);
                    }

                    if (AuthorityKeyPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(authorityKey))
                    {
                        authorityKey = ReadText(property.Value);
                    }

                    if (ValidFromPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        validFrom = ReadDate(property.Value, ref malformedWindow);
                    }

                    if (ValidUntilPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        validUntil = ReadDate(property.Value, ref malformedWindow);
                    }
                }
            }
        }
        catch (JsonException)
        {
            malformedJson = true;
        }

        // B1 is being introduced without removing legacy metadata. A typed
        // authority state is the durable source of truth, including Current.
        // Legacy tags/metadata remain useful compatibility evidence, but a
        // contradictory legacy claim must fail closed instead of silently
        // demoting a typed Current row (or promoting a typed non-current row).
        var typedState = ReadTypedAuthorityState(item);

        var typedSupersedesId = ReadTypedGuid(item, TypedSupersedesIdProperty);
        if (typedSupersedesId.HasValue)
        {
            supersedesIds.Add(typedSupersedesId.Value);
        }

        var typedSupersededById = ReadTypedGuid(item, TypedSupersededByIdProperty);
        if (typedSupersededById.HasValue)
        {
            supersededByIds.Add(typedSupersededById.Value);
        }

        validFrom ??= ReadTypedDate(item, TypedValidFromProperty);
        validUntil ??= ReadTypedDate(item, TypedValidUntilProperty);

        if (validFrom.HasValue && validUntil.HasValue && validFrom > validUntil)
        {
            malformedWindow = true;
        }

        var hasExplicitCurrent = typedState == RetrievalAuthorityState.Current ||
                                 legacyStates.Contains(RetrievalAuthorityState.Current);
        var hasExplicitHistorical = typedState == RetrievalAuthorityState.Historical ||
                                    legacyStates.Contains(RetrievalAuthorityState.Historical);
        var hasExplicitSuperseded = typedState == RetrievalAuthorityState.Superseded ||
                                    legacyStates.Contains(RetrievalAuthorityState.Superseded);
        var hasExplicitPending = typedState == RetrievalAuthorityState.Pending ||
                                 legacyStates.Contains(RetrievalAuthorityState.Pending);
        var legacyStateCount = legacyStates.Count;
        var hasContradictoryLegacyState = legacyStates.Contains(RetrievalAuthorityState.Conflicted) ||
                                          legacyStateCount > 1;
        var typedStateConflictsWithLegacy = typedState.HasValue &&
                                            legacyStates.Any(state => state != typedState.Value);
        var contradictoryState = typedState == RetrievalAuthorityState.Conflicted ||
                                 hasContradictoryLegacyState ||
                                 typedStateConflictsWithLegacy ||
                                 (hasExplicitCurrent && (hasExplicitHistorical || hasExplicitSuperseded || hasExplicitPending)) ||
                                 (hasExplicitHistorical && hasExplicitSuperseded);

        var explicitState = typedState ?? ResolveExplicitState(legacyStates);

        return new AuthorityEvidence(
            explicitState,
            hasExplicitCurrent,
            hasExplicitHistorical,
            hasExplicitSuperseded,
            supersedesIds,
            supersededByIds,
            typedSupersededById,
            validFrom,
            validUntil,
            authorityKey,
            contradictoryState || malformedJson,
            malformedWindow,
            malformedRelationship);
    }

    private static RetrievalAuthorityState? ResolveExplicitState(HashSet<RetrievalAuthorityState> states)
    {
        if (states.Count == 0)
        {
            return null;
        }

        if (states.Contains(RetrievalAuthorityState.Conflicted))
        {
            return RetrievalAuthorityState.Conflicted;
        }

        return states.First();
    }

    private static void AddTagState(HashSet<RetrievalAuthorityState> states, string? rawTag)
    {
        var tag = NormalizeToken(rawTag);
        switch (tag)
        {
            case "authoritycurrent":
            case "currentauthority":
            case "authoritative":
            case "sourceoftruth":
            case "current":
                states.Add(RetrievalAuthorityState.Current);
                break;
            case "authoritypending":
            case "pendingauthority":
            case "pending":
            case "proposed":
            case "draft":
                states.Add(RetrievalAuthorityState.Pending);
                break;
            case "authoritysuperseded":
            case "supersededauthority":
            case "superseded":
            case "replaced":
            case "obsolete":
            case "deprecated":
                states.Add(RetrievalAuthorityState.Superseded);
                break;
            case "authorityhistorical":
            case "historicalauthority":
            case "historical":
            case "history":
            case "legacy":
            case "stale":
                states.Add(RetrievalAuthorityState.Historical);
                break;
            case "authorityconflict":
            case "conflicted":
            case "ambiguousauthority":
                states.Add(RetrievalAuthorityState.Conflicted);
                break;
        }
    }

    private static void AddMetadataStates(HashSet<RetrievalAuthorityState> states, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var normalized = NormalizeToken(value.GetString());
            switch (normalized)
            {
                case "current":
                case "authoritative":
                case "effective":
                case "active":
                case "sourceoftruth":
                    states.Add(RetrievalAuthorityState.Current);
                    break;
                case "pending":
                case "proposed":
                case "draft":
                    states.Add(RetrievalAuthorityState.Pending);
                    break;
                case "superseded":
                case "replaced":
                case "obsolete":
                case "deprecated":
                    states.Add(RetrievalAuthorityState.Superseded);
                    break;
                case "historical":
                case "history":
                case "archived":
                case "expired":
                case "stale":
                    states.Add(RetrievalAuthorityState.Historical);
                    break;
                case "conflicted":
                case "ambiguous":
                    states.Add(RetrievalAuthorityState.Conflicted);
                    break;
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var propertyName in new[] { "state", "status", "authorityState", "authorityStatus" })
        {
            if (value.TryGetProperty(propertyName, out var nested))
            {
                AddMetadataStates(states, nested);
            }
        }
    }

    private static void ReadGuids(JsonElement value, HashSet<Guid> target, ref bool malformed)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return;
            case JsonValueKind.String:
                if (Guid.TryParse(value.GetString(), out var id))
                {
                    target.Add(id);
                }
                else
                {
                    malformed = true;
                }

                break;
            case JsonValueKind.Array:
                foreach (var element in value.EnumerateArray())
                {
                    ReadGuids(element, target, ref malformed);
                }

                break;
            case JsonValueKind.Object:
                var foundProperty = false;
                foreach (var propertyName in new[] { "id", "memoryId", "memory_id", "targetMemoryId", "successorMemoryId", "predecessorMemoryId" })
                {
                    if (value.TryGetProperty(propertyName, out var nested))
                    {
                        foundProperty = true;
                        ReadGuids(nested, target, ref malformed);
                    }
                }

                if (!foundProperty)
                {
                    malformed = true;
                }

                break;
            default:
                malformed = true;
                break;
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement value, ref bool malformed)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            malformed = true;
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : MarkMalformedDate(ref malformed);
    }

    private static DateTimeOffset? MarkMalformedDate(ref bool malformed)
    {
        malformed = true;
        return null;
    }

    private static RetrievalAuthorityState? ReadTypedAuthorityState(MemoryItem item)
    {
        var value = TypedAuthorityStateProperty?.GetValue(item)?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeToken(value) switch
        {
            "current" => RetrievalAuthorityState.Current,
            "superseded" => RetrievalAuthorityState.Superseded,
            "historical" => RetrievalAuthorityState.Historical,
            "pending" => RetrievalAuthorityState.Pending,
            _ => RetrievalAuthorityState.Conflicted
        };
    }

    private static Guid? ReadTypedGuid(MemoryItem item, PropertyInfo? property)
    {
        var value = property?.GetValue(item);
        return value is Guid id ? id : null;
    }

    private static DateTimeOffset? ReadTypedDate(MemoryItem item, PropertyInfo? property)
    {
        var value = property?.GetValue(item);
        return value is DateTimeOffset date ? date : null;
    }

    private static string ReadText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static string BuildGroupKey(MemoryItem item, AuthorityEvidence evidence)
    {
        var identity = string.IsNullOrWhiteSpace(evidence.AuthorityKey)
            ? NormalizeText(item.ExternalKey)
            : NormalizeText(evidence.AuthorityKey);
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = NormalizeText(item.Title);
        }

        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = item.Id.ToString("D");
        }

        return string.Join(
            '|',
            NormalizeGuid(item.TenantId),
            NormalizeGuid(item.OwnerUserId),
            NormalizeText(item.ProjectId),
            item.Scope,
            item.MemoryType,
            identity);
    }

    private static bool IsSameAuthorityScope(MemoryItem left, MemoryItem right)
        => left.TenantId == right.TenantId &&
           left.OwnerUserId == right.OwnerUserId &&
           left.Scope == right.Scope &&
           string.Equals(NormalizeText(left.ProjectId), NormalizeText(right.ProjectId), StringComparison.Ordinal);

    private static string NormalizeGuid(Guid? value)
        => value?.ToString("D") ?? string.Empty;

    private static string NormalizeText(string? value)
        => string.Join(' ', (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '-', '_', ':', '/', '.', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeToken(string? value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

    private enum RetrievalAuthorityState
    {
        Unknown,
        Current,
        Pending,
        Superseded,
        Historical,
        Conflicted
    }

    internal sealed record RetrievalCandidate(MemoryItem Item, decimal Score, string Excerpt);

    internal sealed record RankedRetrievalCandidate(
        MemoryItem Item,
        decimal Score,
        string Excerpt,
        string GroupKey,
        string AuthorityState,
        bool IsAuthorityConflict);

    private sealed record AuthorityEvidence(
        RetrievalAuthorityState? ExplicitState,
        bool HasExplicitCurrent,
        bool HasExplicitHistorical,
        bool HasExplicitSuperseded,
        IReadOnlySet<Guid> SupersedesIds,
        IReadOnlySet<Guid> SupersededByIds,
        Guid? TypedSupersededById,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidUntil,
        string AuthorityKey,
        bool HasContradictoryState,
        bool HasMalformedWindow,
        bool HasMalformedRelationship);

    private sealed record AuthorityObservation(
        RetrievalCandidate Candidate,
        AuthorityEvidence Evidence,
        RetrievalAuthorityState InitialState,
        string GroupKey);

    private sealed class AuthorityEvaluation
    {
        public AuthorityEvaluation(
            AuthorityObservation observation,
            RetrievalAuthorityState state,
            int authorityRank,
            bool isExplicitCurrent,
            bool isAuthorityConflict,
            bool hasSuccessorEvidence,
            DateTimeOffset evaluatedAt)
        {
            Observation = observation;
            State = state;
            AuthorityRank = authorityRank;
            IsExplicitCurrent = isExplicitCurrent;
            IsAuthorityConflict = isAuthorityConflict;
            HasSuccessorEvidence = hasSuccessorEvidence;
            EvaluatedAt = evaluatedAt;
        }

        public AuthorityObservation Observation { get; }
        public RetrievalAuthorityState State { get; }
        public int AuthorityRank { get; set; }
        public bool IsExplicitCurrent { get; }
        public bool IsAuthorityConflict { get; set; }
        public bool HasSuccessorEvidence { get; }
        public DateTimeOffset EvaluatedAt { get; }
        public RetrievalCandidate Candidate => Observation.Candidate;
        public AuthorityEvidence Evidence => Observation.Evidence;
        public string GroupKey => Observation.GroupKey;

        public RankedRetrievalCandidate ToRankedCandidate()
        {
            var excerpt = Candidate.Excerpt;
            if (IsAuthorityConflict && !excerpt.StartsWith(ConflictMarker, StringComparison.Ordinal))
            {
                excerpt = string.IsNullOrWhiteSpace(excerpt)
                    ? ConflictMarker
                    : $"{ConflictMarker} {excerpt}";
            }

            return new RankedRetrievalCandidate(
                Candidate.Item,
                Candidate.Score,
                excerpt,
                GroupKey,
                State.ToString(),
                IsAuthorityConflict);
        }
    }
}
