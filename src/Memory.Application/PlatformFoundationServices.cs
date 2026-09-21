using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed record TopologyEdgeUpsertRequest(
    string ParentProjectId,
    string ChildProjectId,
    string Dimension,
    bool AuthorizationInheritable,
    long ExpectedRevision);

public sealed record AuthorizationRuleUpsertRequest(
    Guid? Id,
    string ProjectId,
    string PrincipalId,
    string Right,
    AuthorizationEffect Effect,
    string EvidenceRef,
    long ExpectedRevision,
    string? ResourceType = null,
    string? ResourceId = null);

public sealed record CanonicalTagSuggestionRequest(string ProjectId, string SuggestedName, string Rationale);

public interface IPlatformFoundationStore
{
    Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken cancellationToken);
    Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken);
    Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken);
    Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights, string? resourceType, string? resourceId, CancellationToken cancellationToken);
    Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken cancellationToken);
}

public sealed class PlatformFoundationStore(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IEffectiveRightsEvaluator evaluator) : IPlatformFoundationStore
{
    public async Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken cancellationToken)
    {
        var parent = ProjectContext.Normalize(request.ParentProjectId);
        var child = ProjectContext.Normalize(request.ChildProjectId);
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A project cannot be its own parent.");
        if (ProjectContext.IsShared(parent) || ProjectContext.IsUser(parent) || ProjectContext.IsShared(child) || ProjectContext.IsUser(child)) throw new InvalidOperationException("Topology edges require regular projects.");
        var dimension = RequireText(request.Dimension, nameof(request.Dimension), 100);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, child, write: true);
        ActorAuthorization.EnsureProjectAllowed(actor, parent, write: false);
        var scoped = Scope(dbContext.ProjectHierarchies, actor);
        var current = await scoped.AsNoTracking().Where(x => x.Dimension == dimension).Select(MapEdgeExpression).ToListAsync(cancellationToken);
        var entity = await scoped.SingleOrDefaultAsync(x => x.Dimension == dimension && x.ParentProjectId == parent && x.ChildProjectId == child, cancellationToken);
        var currentRevision = entity?.Revision ?? 0;
        if (currentRevision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Topology revision conflict; reload before retrying.");
        var replacement = new AuthorizationTopologyEdge(parent, child, dimension, request.AuthorizationInheritable, currentRevision + 1);
        AuthorizationTopologyValidator.ValidateMutation(current, replacement, request.ExpectedRevision);
        var now = clock.UtcNow;
        if (entity is null)
        {
            entity = new ProjectHierarchy
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ParentProjectId = parent,
                ChildProjectId = child,
                Dimension = dimension,
                AuthorizationInheritable = request.AuthorizationInheritable,
                Revision = replacement.Revision,
                CreatedAt = now,
                UpdatedAt = now
            };
            await dbContext.ProjectHierarchies.AddAsync(entity, cancellationToken);
        }
        else
        {
            entity.AuthorizationInheritable = request.AuthorizationInheritable;
            entity.Revision = replacement.Revision;
            entity.UpdatedAt = now;
        }

        await BumpRevisionAsync(child, static revision => revision.TopologyRevision++, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        evaluator.InvalidateProjects(FindAuthorizationDescendants(child, current.Append(replacement)));
        return replacement;
    }

    public Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken)
        => UpsertRuleAsync(request, dbContext.ProjectAuthorizationPolicies, AuthorizationRuleSource.PolicyDerived, static revision => revision.PolicyRevision++, cancellationToken);

    public Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken)
        => UpsertRuleAsync(request, dbContext.ProjectExplicitGrants, AuthorizationRuleSource.ExplicitGrant, static revision => revision.GrantRevision++, cancellationToken);

    public async Task<EffectiveRightsResult> EvaluateAsync(
        string projectId,
        string principalId,
        IReadOnlyList<string> rights,
        string? resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        var project = ProjectContext.Normalize(projectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        var edges = await Scope(dbContext.ProjectHierarchies.AsNoTracking(), actor)
            .Where(x => x.AuthorizationInheritable)
            .Select(MapEdgeExpression)
            .ToArrayAsync(cancellationToken);
        var relevantProjects = FindAuthorizationAncestors(project, edges);
        var policies = await Scope(dbContext.ProjectAuthorizationPolicies.AsNoTracking(), actor)
            .Where(x => relevantProjects.Contains(x.ProjectId) && (x.PrincipalId == principalId || x.PrincipalId == "*") && rights.Contains(x.Right))
            .ToArrayAsync(cancellationToken);
        var grants = await Scope(dbContext.ProjectExplicitGrants.AsNoTracking(), actor)
            .Where(x => relevantProjects.Contains(x.ProjectId) && (x.PrincipalId == principalId || x.PrincipalId == "*") && rights.Contains(x.Right))
            .ToArrayAsync(cancellationToken);
        var revisionRows = await Scope(dbContext.ProjectSecurityRevisions.AsNoTracking(), actor)
            .Where(x => relevantProjects.Contains(x.ProjectId))
            .ToArrayAsync(cancellationToken);
        var revisions = new SecurityRevisionVector(
            CheckedSum(revisionRows.Select(x => x.TopologyRevision)),
            CheckedSum(revisionRows.Select(x => x.PolicyRevision)),
            CheckedSum(revisionRows.Select(x => x.GrantRevision)),
            CheckedSum(revisionRows.Select(x => x.TagRevision)));
        var rules = policies.Select(x => MapRule(x, AuthorizationRuleSource.PolicyDerived))
            .Concat(grants.Select(x => MapRule(x, AuthorizationRuleSource.ExplicitGrant)))
            .ToArray();
        return evaluator.Evaluate(new EffectiveRightsRequest(project, principalId, rights, edges, rules, revisions, resourceType, resourceId));
    }

    public async Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken cancellationToken)
    {
        var project = ProjectContext.Normalize(request.ProjectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: true);
        var name = RequireText(request.SuggestedName, nameof(request.SuggestedName), 200);
        var rationale = RequireText(request.Rationale, nameof(request.Rationale), 2000);
        var now = clock.UtcNow;
        var suggestion = new CanonicalTagSuggestion
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            SuggestedName = name,
            NormalizedName = CanonicalTagResolver.NormalizeTag(name),
            Rationale = rationale,
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.CanonicalTagSuggestions.AddAsync(suggestion, cancellationToken);
        await BumpRevisionAsync(project, static revision => revision.TagRevision++, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return suggestion;
    }

    private async Task<AuthorizationRule> UpsertRuleAsync<TEntity>(
        AuthorizationRuleUpsertRequest request,
        DbSet<TEntity> set,
        AuthorizationRuleSource source,
        Action<ProjectSecurityRevision> bump,
        CancellationToken cancellationToken)
        where TEntity : class, new()
    {
        var project = ProjectContext.Normalize(request.ProjectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: true);
        ValidateRuleRequest(request);
        var id = request.Id ?? Guid.NewGuid();
        var query = Scope(set, actor);
        var entity = request.Id.HasValue ? await query.SingleOrDefaultAsync(x => EF.Property<Guid>(x, "Id") == id, cancellationToken) : null;
        var currentRevision = entity is null ? 0 : Get<TEntity, long>(entity, "Revision");
        if (currentRevision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("Authorization rule revision conflict; reload before retrying.");
        var now = clock.UtcNow;
        entity ??= new TEntity();
        Set(entity, "Id", id);
        Set(entity, "TenantId", actor.TenantId);
        Set(entity, "OwnerUserId", actor.UserId);
        Set(entity, "ProjectId", project);
        Set(entity, "PrincipalId", request.PrincipalId.Trim());
        Set(entity, "Right", request.Right.Trim());
        Set(entity, "Effect", request.Effect);
        Set(entity, "ResourceType", request.ResourceType?.Trim());
        Set(entity, "ResourceId", request.ResourceId?.Trim());
        Set(entity, "EvidenceRef", request.EvidenceRef.Trim());
        Set(entity, "Revision", currentRevision + 1);
        Set(entity, "UpdatedAt", now);
        if (currentRevision == 0)
        {
            Set(entity, "CreatedAt", now);
            await set.AddAsync(entity, cancellationToken);
        }
        var authorizationEdges = await Scope(dbContext.ProjectHierarchies.AsNoTracking(), actor)
            .Where(x => x.AuthorizationInheritable)
            .Select(MapEdgeExpression)
            .ToArrayAsync(cancellationToken);
        await BumpRevisionAsync(project, bump, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        evaluator.InvalidateProjects(FindAuthorizationDescendants(project, authorizationEdges));
        return new AuthorizationRule(project, request.PrincipalId.Trim(), request.Right.Trim(), request.Effect, source, request.EvidenceRef.Trim(), request.ResourceType?.Trim(), request.ResourceId?.Trim());
    }

    private async Task BumpRevisionAsync(string projectId, Action<ProjectSecurityRevision> bump, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var row = await Scope(dbContext.ProjectSecurityRevisions, actor).SingleOrDefaultAsync(x => x.ProjectId == projectId, cancellationToken);
        if (row is null)
        {
            row = new ProjectSecurityRevision { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = projectId };
            await dbContext.ProjectSecurityRevisions.AddAsync(row, cancellationToken);
        }
        bump(row);
        row.UpdatedAt = clock.UtcNow;
    }

    private static IQueryable<TEntity> Scope<TEntity>(IQueryable<TEntity> query, ContextHubRequestActor actor) where TEntity : class
        => !actor.HasUser ? query : actor.IsServiceActor
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);

    private static readonly System.Linq.Expressions.Expression<Func<ProjectHierarchy, AuthorizationTopologyEdge>> MapEdgeExpression =
        x => new AuthorizationTopologyEdge(x.ParentProjectId, x.ChildProjectId, x.Dimension, x.AuthorizationInheritable, x.Revision);

    private static AuthorizationRule MapRule(ProjectAuthorizationPolicy rule, AuthorizationRuleSource source)
        => new(rule.ProjectId, rule.PrincipalId, rule.Right, rule.Effect, source, rule.EvidenceRef, rule.ResourceType, rule.ResourceId);

    private static AuthorizationRule MapRule(ProjectExplicitGrant rule, AuthorizationRuleSource source)
        => new(rule.ProjectId, rule.PrincipalId, rule.Right, rule.Effect, source, rule.EvidenceRef, rule.ResourceType, rule.ResourceId);

    private static HashSet<string> FindAuthorizationAncestors(string projectId, IReadOnlyList<AuthorizationTopologyEdge> edges)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId };
        var queue = new Queue<string>();
        queue.Enqueue(projectId);
        while (queue.TryDequeue(out var child))
        {
            foreach (var parent in edges.Where(x => x.AuthorizationInheritable && string.Equals(x.ChildProjectId, child, StringComparison.OrdinalIgnoreCase)).Select(x => x.ParentProjectId))
            {
                if (result.Add(parent)) queue.Enqueue(parent);
            }
        }
        return result;
    }

    private static HashSet<string> FindAuthorizationDescendants(string projectId, IEnumerable<AuthorizationTopologyEdge> edges)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId };
        var queue = new Queue<string>();
        queue.Enqueue(projectId);
        var edgeArray = edges.Where(x => x.AuthorizationInheritable).ToArray();
        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in edgeArray.Where(x => string.Equals(x.ParentProjectId, parent, StringComparison.OrdinalIgnoreCase)).Select(x => x.ChildProjectId))
            {
                if (result.Add(child)) queue.Enqueue(child);
            }
        }
        return result;
    }

    private static long CheckedSum(IEnumerable<long> values)
    {
        long result = 0;
        foreach (var value in values) result = checked(result + value);
        return result;
    }

    private static void ValidateRuleRequest(AuthorizationRuleUpsertRequest request)
    {
        _ = RequireText(request.PrincipalId, nameof(request.PrincipalId), 300);
        _ = RequireText(request.Right, nameof(request.Right), 200);
        _ = RequireText(request.EvidenceRef, nameof(request.EvidenceRef), 2000);
        if ((request.ResourceType is null) != (request.ResourceId is null)) throw new ArgumentException("ResourceType and ResourceId must be supplied together.");
        if (request.ExpectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(request.ExpectedRevision));
    }

    private static string RequireText(string value, string name, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > maxLength) throw new ArgumentException($"{name} is required and must not exceed {maxLength} characters.", name);
        return trimmed;
    }

    private static void Set<TEntity, TValue>(TEntity entity, string property, TValue value) where TEntity : class
        => typeof(TEntity).GetProperty(property)!.SetValue(entity, value);

    private static TValue Get<TEntity, TValue>(TEntity entity, string property) where TEntity : class
        => (TValue)typeof(TEntity).GetProperty(property)!.GetValue(entity)!;
}

public sealed record AuthorizationTopologyEdge(
    string ParentProjectId,
    string ChildProjectId,
    string Dimension,
    bool AuthorizationInheritable,
    long Revision);

public sealed record AuthorizationRule(
    string ProjectId,
    string PrincipalId,
    string Right,
    AuthorizationEffect Effect,
    AuthorizationRuleSource Source,
    string EvidenceRef,
    string? ResourceType = null,
    string? ResourceId = null);

public sealed record SecurityRevisionVector(long Topology, long Policy, long Grant, long Tag);

public sealed record EffectiveRightsRequest(
    string ProjectId,
    string PrincipalId,
    IReadOnlyList<string> Rights,
    IReadOnlyList<AuthorizationTopologyEdge> Edges,
    IReadOnlyList<AuthorizationRule> Rules,
    SecurityRevisionVector Revisions,
    string? ResourceType = null,
    string? ResourceId = null);

public sealed record EffectiveRightDecision(
    string Right,
    bool Allowed,
    string Tier,
    IReadOnlyList<string> Evidence,
    string Explanation);

public sealed record EffectiveRightsResult(
    string ProjectId,
    string PrincipalId,
    SecurityRevisionVector Revisions,
    IReadOnlyList<EffectiveRightDecision> Decisions);

public interface IEffectiveRightsEvaluator
{
    EffectiveRightsResult Evaluate(EffectiveRightsRequest request);
    void InvalidateProject(string projectId);
    void InvalidateProjects(IEnumerable<string> projectIds);
}

public sealed class EffectiveRightsEvaluator : IEffectiveRightsEvaluator
{
    private readonly ConcurrentDictionary<string, EffectiveRightsResult> cache = new(StringComparer.Ordinal);

    public EffectiveRightsResult Evaluate(EffectiveRightsRequest request)
    {
        Validate(request);
        var key = BuildCacheKey(request);
        return cache.GetOrAdd(key, _ => EvaluateCore(request));
    }

    public void InvalidateProject(string projectId)
    {
        var prefix = $"{Normalize(projectId)}\n";
        foreach (var key in cache.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            cache.TryRemove(key, out _);
        }
    }

    public void InvalidateProjects(IEnumerable<string> projectIds)
    {
        foreach (var projectId in projectIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            InvalidateProject(projectId);
        }
    }

    private static EffectiveRightsResult EvaluateCore(EffectiveRightsRequest request)
    {
        var projectId = Normalize(request.ProjectId);
        var principal = request.PrincipalId.Trim();
        var rules = request.Rules
            .Where(x => PrincipalMatches(x.PrincipalId, principal))
            .ToArray();
        var paths = BuildAncestorPaths(projectId, request.Edges);
        var decisions = request.Rights
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(right => EvaluateRight(projectId, right, request.ResourceType, request.ResourceId, paths, rules))
            .ToArray();
        return new EffectiveRightsResult(projectId, principal, request.Revisions, decisions);
    }

    private static EffectiveRightDecision EvaluateRight(
        string projectId,
        string right,
        string? resourceType,
        string? resourceId,
        IReadOnlyList<IReadOnlyList<string>> paths,
        IReadOnlyList<AuthorizationRule> rules)
    {
        var inherited = new List<AuthorizationRule>();
        foreach (var path in paths)
        {
            var nearest = path
                .Select(ancestor => rules.Where(rule => IsProjectRule(rule, ancestor, right)).ToArray())
                .FirstOrDefault(candidate => candidate.Length > 0);
            if (nearest is not null)
            {
                inherited.AddRange(nearest);
            }
        }

        var targetProject = rules.Where(rule => IsProjectRule(rule, projectId, right)).ToArray();
        var targetResource = resourceType is null || resourceId is null
            ? []
            : rules.Where(rule =>
                string.Equals(Normalize(rule.ProjectId), projectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.Right, right, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase)).ToArray();

        var (tier, selected) = targetResource.Length > 0
            ? ("Resource", targetResource)
            : targetProject.Length > 0
                ? ("Project", targetProject)
                : inherited.Count > 0
                    ? ("Inherited", inherited.ToArray())
                    : ("DefaultDeny", Array.Empty<AuthorizationRule>());
        var allowed = selected.Length > 0 && selected.All(x => x.Effect != AuthorizationEffect.Deny) && selected.Any(x => x.Effect == AuthorizationEffect.Allow);
        var evidence = selected.Select(x => x.EvidenceRef).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sourceSummary = selected.Length == 0
            ? "No applicable rule was found."
            : $"{selected.Count(x => x.Source == AuthorizationRuleSource.PolicyDerived)} policy-derived and {selected.Count(x => x.Source == AuthorizationRuleSource.ExplicitGrant)} explicit rule(s) were evaluated.";
        var explanation = allowed
            ? $"Allowed at {tier} tier. {sourceSummary}"
            : $"Denied at {tier} tier; deny wins within the selected tier. {sourceSummary}";
        return new EffectiveRightDecision(right, allowed, tier, evidence, explanation);
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildAncestorPaths(string projectId, IReadOnlyList<AuthorizationTopologyEdge> edges)
    {
        var parents = edges
            .Where(x => x.AuthorizationInheritable)
            .GroupBy(x => Normalize(x.ChildProjectId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => Normalize(x.ParentProjectId)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var results = new List<IReadOnlyList<string>>();
        Walk(projectId, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectId });
        return results;

        void Walk(string current, List<string> path, HashSet<string> visiting)
        {
            if (!parents.TryGetValue(current, out var currentParents) || currentParents.Length == 0)
            {
                results.Add(path.ToArray());
                return;
            }

            foreach (var parent in currentParents)
            {
                if (!visiting.Add(parent))
                {
                    throw new InvalidOperationException("Authorization topology contains a cycle; evaluation failed closed.");
                }

                path.Add(parent);
                Walk(parent, path, visiting);
                path.RemoveAt(path.Count - 1);
                visiting.Remove(parent);
            }
        }
    }

    private static bool IsProjectRule(AuthorizationRule rule, string projectId, string right)
        => string.Equals(Normalize(rule.ProjectId), projectId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rule.Right, right, StringComparison.OrdinalIgnoreCase)
            && rule.ResourceType is null
            && rule.ResourceId is null;

    private static bool PrincipalMatches(string candidate, string requested)
        => candidate == "*" || string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase);

    private static void Validate(EffectiveRightsRequest request)
    {
        _ = Normalize(request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.PrincipalId)) throw new ArgumentException("PrincipalId is required.");
        if (request.Rights.Count == 0) throw new ArgumentException("At least one right is required.");
        if ((request.ResourceType is null) != (request.ResourceId is null)) throw new ArgumentException("ResourceType and ResourceId must be supplied together.");
        if (request.Revisions is { Topology: < 0 } or { Policy: < 0 } or { Grant: < 0 } or { Tag: < 0 }) throw new ArgumentException("Security revisions cannot be negative.");
    }

    private static string BuildCacheKey(EffectiveRightsRequest request)
        => string.Join('\n', Normalize(request.ProjectId), request.PrincipalId.Trim().ToUpperInvariant(), request.ResourceType ?? "", request.ResourceId ?? "",
            request.Revisions.Topology, request.Revisions.Policy, request.Revisions.Grant, request.Revisions.Tag,
            string.Join('|', request.Rights.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

    private static string Normalize(string projectId)
        => ProjectContext.Normalize(projectId);
}

public sealed record CanonicalTagCandidate(Guid DefinitionId, string CanonicalName, string MatchKind, double Score);
public sealed record CanonicalTagResourceHit(Guid DefinitionId, string CanonicalName, string ResourceType, string ResourceId, double Score);
public sealed record CanonicalTagResolution(IReadOnlyList<CanonicalTagCandidate> Candidates, IReadOnlyList<CanonicalTagResourceHit> Resources);

public interface ICanonicalTagResolver
{
    CanonicalTagResolution Resolve(
        string query,
        IReadOnlyList<CanonicalTagDefinition> definitions,
        IReadOnlyList<CanonicalTagAlias> aliases,
        IReadOnlyList<CanonicalTagBinding> bindings,
        IReadOnlySet<string> authorizedResourceKeys,
        int limit = 20);
}

public sealed class CanonicalTagResolver : ICanonicalTagResolver
{
    public CanonicalTagResolution Resolve(
        string query,
        IReadOnlyList<CanonicalTagDefinition> definitions,
        IReadOnlyList<CanonicalTagAlias> aliases,
        IReadOnlyList<CanonicalTagBinding> bindings,
        IReadOnlySet<string> authorizedResourceKeys,
        int limit = 20)
    {
        var normalized = NormalizeTag(query);
        if (normalized.Length == 0) throw new ArgumentException("Tag query is required.", nameof(query));
        var aliasesByDefinition = aliases.GroupBy(x => x.DefinitionId).ToDictionary(x => x.Key, x => x.Select(a => a.NormalizedAlias).ToArray());
        var candidates = definitions.Select(definition => Score(definition, aliasesByDefinition.GetValueOrDefault(definition.Id, []), normalized))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArray();
        var allowedBindings = bindings.Where(binding => authorizedResourceKeys.Contains(ResourceKey(binding.ProjectId, binding.ResourceType, binding.ResourceId))).ToArray();
        var definitionsById = definitions.ToDictionary(x => x.Id);
        var scoreByDefinition = candidates.ToDictionary(x => x.DefinitionId, x => x.Score);
        var resources = allowedBindings
            .Where(x => scoreByDefinition.ContainsKey(x.DefinitionId) && definitionsById.ContainsKey(x.DefinitionId))
            .Select(x => new CanonicalTagResourceHit(x.DefinitionId, definitionsById[x.DefinitionId].CanonicalName, x.ResourceType, x.ResourceId, scoreByDefinition[x.DefinitionId]))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.ResourceId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArray();
        return new CanonicalTagResolution(candidates, resources);
    }

    public static string NormalizeTag(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousSeparator = false;
        foreach (var character in normalized)
        {
            var separator = char.IsWhiteSpace(character) || character is '-' or '_';
            if (separator)
            {
                if (!previousSeparator && builder.Length > 0) builder.Append('-');
                previousSeparator = true;
            }
            else if (char.GetUnicodeCategory(character) is not UnicodeCategory.Control and not UnicodeCategory.Format)
            {
                builder.Append(character);
                previousSeparator = false;
            }
        }
        return builder.ToString().Trim('-');
    }

    public static string ResourceKey(string projectId, string resourceType, string resourceId)
        => $"{ProjectContext.Normalize(projectId)}\n{resourceType.Trim().ToUpperInvariant()}\n{resourceId.Trim()}";

    private static CanonicalTagCandidate Score(CanonicalTagDefinition definition, IReadOnlyList<string> aliases, string query)
    {
        var canonical = NormalizeTag(definition.NormalizedName.Length == 0 ? definition.CanonicalName : definition.NormalizedName);
        if (canonical == query) return new(definition.Id, definition.CanonicalName, "Canonical", 1);
        if (aliases.Any(alias => NormalizeTag(alias) == query)) return new(definition.Id, definition.CanonicalName, "Alias", 0.98);
        var tokens = canonical.Split('-', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var queryTokens = query.Split('-', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var union = tokens.Union(queryTokens).Count();
        var score = union == 0 ? 0 : (double)tokens.Intersect(queryTokens).Count() / union;
        return new(definition.Id, definition.CanonicalName, score > 0 ? "SemanticCandidate" : "None", score * 0.8);
    }
}

public static class AuthorizationTopologyValidator
{
    public static void ValidateMutation(
        IReadOnlyList<AuthorizationTopologyEdge> current,
        AuthorizationTopologyEdge replacement,
        long expectedRevision)
    {
        if (replacement.Revision != expectedRevision + 1) throw new InvalidOperationException("Topology revision conflict; reload before retrying.");
        var candidate = current.Where(x => !(string.Equals(x.Dimension, replacement.Dimension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ParentProjectId, replacement.ParentProjectId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ChildProjectId, replacement.ChildProjectId, StringComparison.OrdinalIgnoreCase))).Append(replacement).ToArray();
        _ = new EffectiveRightsEvaluator().Evaluate(new EffectiveRightsRequest(
            replacement.ChildProjectId,
            "topology-validation",
            ["topology.validate"],
            candidate,
            [],
            new SecurityRevisionVector(replacement.Revision, 0, 0, 0)));
    }
}
