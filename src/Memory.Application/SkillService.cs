using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class SkillService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IEmbeddingProvider embeddingProvider,
    ISkillMaterializationStore materializationStore,
    TimeProvider timeProvider) : ISkillService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SkillLifecycleStatus[] SearchableStatuses = [SkillLifecycleStatus.Published, SkillLifecycleStatus.Deprecated];
    private static readonly SkillRejectionReason[] RuntimeReasons =
    [
        SkillRejectionReason.MissingCapability,
        SkillRejectionReason.ToolUnavailable,
        SkillRejectionReason.NetworkUnavailable,
        SkillRejectionReason.SecretUnavailable,
        SkillRejectionReason.PermissionDenied,
        SkillRejectionReason.UnsupportedRuntime
    ];

    public Task<SkillImportPreviewResult> PreviewImportAsync(SkillImportPreviewRequest request, CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsManage);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PortableSkillBundleValidator.Validate(request));
    }

    public async Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsManage);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var preview = PortableSkillBundleValidator.Validate(request.Skill);
        if (!preview.CanImport)
        {
            throw new InvalidOperationException("Skill bundle validation failed: " + string.Join("; ", preview.Validation.Issues.Select(issue => issue.Code)));
        }

        var actor = actorAccessor.Current;
        var stableKey = preview.StableKey;
        var existing = await ScopedSkills(includeArchived: true)
            .Include(skill => skill.Versions)
            .Include(skill => skill.Bindings)
            .FirstOrDefaultAsync(skill => skill.StableKey == stableKey, cancellationToken);
        if (existing is not null)
        {
            var replay = existing.Versions.FirstOrDefault(version => version.ContentHash == preview.ContentHash);
            if (replay is not null)
            {
                return new(ToSummary(existing), ToVersionSummary(replay), false, true, preview.Validation);
            }

            if (existing.Versions.Any(version => version.Version == request.Skill.Version.Trim()))
            {
                throw new InvalidOperationException("The semantic version already exists with different content; Published versions are immutable.");
            }
        }

        await EnsureNameAndAliasesAreUnambiguousAsync(existing?.Id, request.Skill.Name, request.Skill.Aliases ?? [], cancellationToken);
        var now = timeProvider.GetUtcNow();
        var skill = existing ?? new Skill
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            StableKey = stableKey,
            CreatedAt = now
        };
        skill.Name = request.Skill.Name.Trim();
        skill.Description = request.Skill.Description.Trim();
        skill.WhenToUse = request.Skill.WhenToUse.Trim();
        skill.Tags = NormalizeValues(request.Skill.Tags);
        skill.Aliases = NormalizeValues(request.Skill.Aliases);
        skill.License = request.Skill.License.Trim();
        skill.MaintainersJson = Serialize(NormalizeValues(request.Skill.Maintainers));
        skill.RiskLevel = request.Skill.RiskLevel;
        skill.UpdatedAt = now;
        if (existing is not null)
        {
            skill.MetadataVersion++;
        }
        else
        {
            dbContext.Skills.Add(skill);
        }

        var version = new SkillVersion
        {
            SkillId = skill.Id,
            Version = request.Skill.Version.Trim(),
            Status = SkillLifecycleStatus.Draft,
            ContentHash = preview.ContentHash,
            BundleJson = Serialize(request.Skill.Bundle),
            SearchText = BuildSearchText(skill, preview.SkillMarkdown),
            CompatibilityJson = "{}",
            RequiredCapabilities = NormalizeValues(request.Skill.RequiredCapabilities),
            RequiredTools = NormalizeValues(request.Skill.RequiredTools),
            AllowedActions = NormalizeValues(request.Skill.AllowedActions),
            RequiresNetwork = request.Skill.RequiresNetwork,
            RequiresSecrets = request.Skill.RequiresSecrets,
            SourceKind = request.Skill.SourceKind,
            SourceRef = request.Skill.SourceRef.Trim(),
            SourceRevision = request.Skill.SourceRevision.Trim(),
            TrustLevel = request.Skill.TrustLevel,
            SignatureAlgorithm = request.Skill.SignatureAlgorithm?.Trim() ?? string.Empty,
            SignatureValue = request.Skill.SignatureValue?.Trim() ?? string.Empty,
            SignatureVerified = preview.Validation.SignatureVerified,
            PublishEvidenceJson = Serialize(preview.Validation),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SkillVersions.Add(version);

        foreach (var dependency in request.Skill.Dependencies ?? [])
        {
            if (dependency.TargetSkillId == skill.Id)
            {
                throw new InvalidOperationException("A SkillVersion cannot depend on or conflict with its own Skill.");
            }

            dbContext.SkillVersionDependencies.Add(new SkillVersionDependency
            {
                SkillVersionId = version.Id,
                TargetSkillId = dependency.TargetSkillId,
                Kind = dependency.Kind,
                VersionConstraint = NormalizeConstraint(dependency.VersionConstraint)
            });
        }

        await ValidateDependencyTargetsAsync(version, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ToSummary(skill), ToVersionSummary(version), true, false, preview.Validation);
    }

    public async Task<SkillVersionSummaryResult> PublishAsync(SkillPublishRequest request, CancellationToken cancellationToken)
    {
        EnsurePublishAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        var version = await ScopedVersions(includeArchivedSkills: false)
            .Include(item => item.Skill)
            .Include(item => item.Dependencies)
            .SingleOrDefaultAsync(item => item.Id == request.SkillVersionId, cancellationToken)
            ?? throw new KeyNotFoundException("SkillVersion was not found.");
        if (version.Status == SkillLifecycleStatus.Published)
        {
            EnsureHash(version.ContentHash, request.ExpectedContentHash);
            return ToVersionSummary(version);
        }

        if (version.Status != SkillLifecycleStatus.Draft)
        {
            throw new InvalidOperationException("Only a Draft SkillVersion can be published.");
        }

        EnsureHash(version.ContentHash, request.ExpectedContentHash);
        var bundle = Deserialize<PortableSkillBundle>(version.BundleJson);
        var validation = PortableSkillBundleValidator.Validate(BuildPreviewRequest(version.Skill!, version, bundle));
        if (!validation.CanImport)
        {
            throw new InvalidOperationException("Publish validation failed: " + string.Join("; ", validation.Validation.Issues.Select(issue => issue.Code)));
        }

        if (validation.RequiresPublishApproval && !request.ApprovalGranted)
        {
            throw new InvalidOperationException("High-risk, executable, network, or secret-dependent skills require explicit publish approval.");
        }

        await ValidateDependencyGraphAsync(version, cancellationToken);
        var now = timeProvider.GetUtcNow();
        version.Status = SkillLifecycleStatus.Published;
        version.PublishedAt = now;
        version.UpdatedAt = now;
        version.PublishEvidenceJson = Serialize(validation.Validation);
        if (version.Skill!.DefaultVersionId is null)
        {
            version.Skill.DefaultVersionId = version.Id;
            version.Skill.MetadataVersion++;
            version.Skill.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await IndexPublishedVersionAsync(version, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToVersionSummary(version);
    }

    public async Task<SkillVersionSummaryResult> ChangeLifecycleAsync(SkillLifecycleRequest request, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var version = await ScopedVersions(includeArchivedSkills: true)
            .Include(item => item.Skill)
            .SingleOrDefaultAsync(item => item.Id == request.SkillVersionId, cancellationToken)
            ?? throw new KeyNotFoundException("SkillVersion was not found.");
        if (request.TargetStatus == SkillLifecycleStatus.Revoked)
        {
            EnsureSecurityAccess();
        }
        else
        {
            EnsurePublishAccess();
        }

        if (version.Status == request.TargetStatus)
        {
            return ToVersionSummary(version);
        }

        if (!IsLifecycleTransitionAllowed(version.Status, request.TargetStatus))
        {
            throw new InvalidOperationException($"SkillVersion lifecycle transition {version.Status} -> {request.TargetStatus} is not allowed.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("A bounded lifecycle reason is required.");
        }

        var now = timeProvider.GetUtcNow();
        version.Status = request.TargetStatus;
        version.UpdatedAt = now;
        if (request.TargetStatus == SkillLifecycleStatus.Deprecated) version.DeprecatedAt = now;
        if (request.TargetStatus == SkillLifecycleStatus.Revoked) version.RevokedAt = now;
        if (request.TargetStatus == SkillLifecycleStatus.Archived) version.ArchivedAt = now;
        if (version.Skill!.DefaultVersionId == version.Id && request.TargetStatus is SkillLifecycleStatus.Revoked or SkillLifecycleStatus.Archived)
        {
            version.Skill.DefaultVersionId = await ResolveNewestPublishedVersionIdAsync(version.SkillId, version.Id, cancellationToken);
            version.Skill.MetadataVersion++;
            version.Skill.UpdatedAt = now;
        }

        if (request.TargetStatus == SkillLifecycleStatus.Revoked)
        {
            var materializations = await dbContext.SkillMaterializations
                .Where(item => item.SkillVersionId == version.Id && item.Status == SkillMaterializationStatus.Active)
                .ToListAsync(cancellationToken);
            foreach (var materialization in materializations)
            {
                materialization.Status = SkillMaterializationStatus.Revoked;
                materialization.UpdatedAt = now;
                materialization.FailureReason = PortableSkillBundleValidator.BoundAndRedact(request.Reason, 500);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToVersionSummary(version);
    }

    public async Task<SkillSummaryResult> SetDefaultVersionAsync(SkillDefaultVersionRequest request, CancellationToken cancellationToken)
    {
        EnsurePublishAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        var skill = await ScopedSkills(includeArchived: false)
            .Include(item => item.Versions)
            .Include(item => item.Bindings)
            .SingleOrDefaultAsync(item => item.Id == request.SkillId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill was not found.");
        if (skill.MetadataVersion != request.ExpectedMetadataVersion)
        {
            throw new InvalidOperationException("Skill metadata changed since it was read; default-version update failed closed.");
        }

        var version = skill.Versions.SingleOrDefault(item => item.Id == request.SkillVersionId)
            ?? throw new InvalidOperationException("The target version does not belong to the Skill.");
        if (version.Status != SkillLifecycleStatus.Published)
        {
            throw new InvalidOperationException("Only a Published SkillVersion can become the default.");
        }

        skill.DefaultVersionId = version.Id;
        skill.MetadataVersion++;
        skill.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(skill);
    }

    public async Task<SkillBindingResult> UpsertBindingAsync(SkillBindingUpsertRequest request, CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsBind);
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.Scope != SkillBindingScope.Tenant && string.IsNullOrWhiteSpace(request.ScopeValue))
        {
            throw new InvalidOperationException("ScopeValue is required for non-tenant Skill bindings.");
        }

        var skill = await ScopedSkills(includeArchived: false).SingleOrDefaultAsync(item => item.Id == request.SkillId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill was not found.");
        var scopeValue = NormalizeScopeValue(request.ScopeValue);
        var binding = await dbContext.SkillBindings.SingleOrDefaultAsync(
            item => item.SkillId == skill.Id && item.Scope == request.Scope && item.ScopeValue == scopeValue,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (binding is null)
        {
            if (request.ExpectedRevision.HasValue)
            {
                throw new InvalidOperationException("Binding does not exist; expected revision cannot be satisfied.");
            }

            binding = new SkillBinding
            {
                SkillId = skill.Id,
                Scope = request.Scope,
                ScopeValue = scopeValue,
                Revision = 1,
                CreatedAt = now
            };
            dbContext.SkillBindings.Add(binding);
        }
        else
        {
            if (!request.ExpectedRevision.HasValue || request.ExpectedRevision.Value != binding.Revision)
            {
                throw new InvalidOperationException("Binding changed since it was read; update failed closed.");
            }
            binding.Revision++;
        }

        binding.Mode = request.Mode;
        binding.VersionConstraint = NormalizeConstraint(request.VersionConstraint);
        binding.UpdatedAt = now;
        skill.MetadataVersion++;
        skill.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToBindingResult(binding);
    }

    public async Task<IReadOnlyList<SkillSummaryResult>> ListAsync(string? projectId, bool includeArchived, CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, ProjectContext.Normalize(projectId), false);
        }

        return (await ScopedSkills(includeArchived)
            .Include(skill => skill.Versions)
            .Include(skill => skill.Bindings)
            .OrderBy(skill => skill.Name)
            .ToListAsync(cancellationToken))
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<SkillSummaryResult?> GetAsync(Guid skillId, CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        var skill = await ScopedSkills(includeArchived: true)
            .Include(item => item.Versions)
            .Include(item => item.Bindings)
            .SingleOrDefaultAsync(item => item.Id == skillId, cancellationToken);
        return skill is null ? null : ToSummary(skill);
    }

    public async Task<PortableSkillBundle> ExportAsync(Guid skillVersionId, CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        var version = await ScopedVersions(includeArchivedSkills: true)
            .SingleOrDefaultAsync(item => item.Id == skillVersionId, cancellationToken)
            ?? throw new KeyNotFoundException("SkillVersion was not found.");
        return Deserialize<PortableSkillBundle>(version.BundleJson);
    }

    public async Task<SkillSourceObservationResult> RecordSourceObservationAsync(
        SkillSourceObservationRequest request,
        CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsManage);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var actor = actorAccessor.Current;
        var replay = await dbContext.SkillSourceObservations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (replay is not null)
        {
            return ToSourceObservation(replay, true);
        }

        var skill = await ScopedSkills(includeArchived: true).Include(item => item.Versions)
            .SingleOrDefaultAsync(item => item.Id == request.SkillId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill was not found.");
        var sourceRef = request.SourceRef.Trim();
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            throw new InvalidOperationException("SourceRef is required for source drift observation.");
        }

        var authorityVersion = skill.Versions
            .Where(item => string.Equals(item.SourceRef, sourceRef, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Observed SourceRef does not match this Skill's recorded provenance.");
        var observedHash = request.ObservedContentHash.Trim().ToLowerInvariant();
        if (request.SourceAvailable && (observedHash.Length != 64 || observedHash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new InvalidOperationException("An available source observation requires a SHA-256 content hash.");
        }

        var status = request.CompromiseReported
            ? SkillSourceDriftStatus.Compromised
            : !request.SourceAvailable
                ? SkillSourceDriftStatus.Deleted
                : authorityVersion.TrustLevel is >= SkillTrustLevel.Signed && !request.SignatureVerified
                    ? SkillSourceDriftStatus.TrustChanged
                    : string.Equals(authorityVersion.SourceRevision, request.ObservedRevision.Trim(), StringComparison.Ordinal) &&
                      string.Equals(authorityVersion.ContentHash, observedHash, StringComparison.OrdinalIgnoreCase)
                        ? SkillSourceDriftStatus.InSync
                        : SkillSourceDriftStatus.Changed;
        var now = timeProvider.GetUtcNow();
        var observation = new SkillSourceObservation
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            SkillId = skill.Id,
            SourceRef = sourceRef,
            ObservedRevision = request.ObservedRevision.Trim(),
            ObservedContentHash = observedHash,
            Status = status,
            SourceAvailable = request.SourceAvailable,
            SignatureVerified = request.SignatureVerified,
            EvidenceJson = Serialize(new
            {
                evidenceRef = PortableSkillBundleValidator.BoundAndRedact(request.EvidenceRef, 500),
                authoritySkillVersionId = authorityVersion.Id,
                authorityVersion = authorityVersion.Version,
                authorityRevision = authorityVersion.SourceRevision,
                authorityContentHash = authorityVersion.ContentHash
            }),
            IdempotencyKey = request.IdempotencyKey,
            ObservedAt = now,
            CreatedAt = now
        };
        dbContext.SkillSourceObservations.Add(observation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSourceObservation(observation, false);
    }

    public async Task<IReadOnlyList<SkillSourceObservationResult>> ListSourceObservationsAsync(
        Guid skillId,
        CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        _ = await ScopedSkills(includeArchived: true).AsNoTracking().SingleOrDefaultAsync(item => item.Id == skillId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill was not found.");
        var actor = actorAccessor.Current;
        var query = dbContext.SkillSourceObservations.AsNoTracking().Where(item => item.SkillId == skillId);
        if (actor.HasUser)
        {
            query = query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        }

        return (await query.OrderByDescending(item => item.ObservedAt).Take(200).ToListAsync(cancellationToken))
            .Select(item => ToSourceObservation(item, false))
            .ToArray();
    }

    public async Task<SkillSearchForExecutionResult> SearchForExecutionAsync(SkillSearchForExecutionRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateSearchRequest(request);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, projectId, false);
        var actor = actorAccessor.Current;
        var replay = await dbContext.SkillResolutions
            .Include(item => item.Candidates)
            .SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return await BuildSearchResultAsync(replay, true, cancellationToken);
        }

        var round = request.Round ?? await ResolveNextRoundAsync(request.ExecutionId, cancellationToken);
        if (round > request.Policy.MaxSearchRounds)
        {
            throw new InvalidOperationException("The bounded Skill discovery search-round limit has been reached.");
        }

        var generation = await EnsureActiveGenerationAsync(cancellationToken);
        var threshold = Math.Clamp(request.Policy.Threshold ?? generation.Threshold, 0m, 1m);
        var queryText = BuildQueryText(request);
        var queryTerms = SkillText.Tokenize(queryText);
        var queryHash = Sha256(queryText);
        float[] queryEmbedding = [];
        var degraded = false;
        try
        {
            queryEmbedding = (await embeddingProvider.EmbedAsync(queryText, EmbeddingPurpose.Query, cancellationToken)).Values;
        }
        catch when (request.Policy.AllowKeywordOnlyFallback)
        {
            degraded = true;
        }

        var excluded = (request.ExcludedSkillVersionIds ?? []).ToHashSet();
        var documents = await dbContext.SkillSearchDocuments
            .Where(document => document.GenerationId == generation.Id && !excluded.Contains(document.SkillVersionId))
            .ToListAsync(cancellationToken);
        var versionIds = documents.Select(document => document.SkillVersionId).ToArray();
        var versions = await ScopedVersions(includeArchivedSkills: false)
            .Include(version => version.Skill!)
                .ThenInclude(skill => skill.Bindings)
            .Where(version => versionIds.Contains(version.Id))
            .ToListAsync(cancellationToken);
        var documentByVersion = documents.ToDictionary(document => document.SkillVersionId);
        var ranked = new List<RankedCandidate>();
        foreach (var version in versions)
        {
            var explicitCandidate = request.ExplicitSkillVersionId == version.Id || request.ExplicitSkillId == version.SkillId;
            if (!PassesHardFilters(version, request, projectId, explicitCandidate, out var bindingMode, out var filterReason))
            {
                continue;
            }

            var document = documentByVersion[version.Id];
            var terms = Deserialize<string[]>(document.TermsJson);
            var keyword = SkillText.KeywordScore(queryTerms, terms);
            var semantic = degraded ? 0m : SkillText.CosineScore(queryEmbedding, Deserialize<float[]>(document.EmbeddingJson));
            var requestedTags = NormalizeValues(request.Tags);
            var tag = requestedTags.Length == 0 ? 0m : SkillText.KeywordScore(requestedTags, version.Skill!.Tags.Select(SkillText.NormalizeToken).ToArray());
            var when = SkillText.KeywordScore(queryTerms, SkillText.Tokenize(version.Skill!.WhenToUse));
            var score = keyword * 0.45m + semantic * 0.35m + tag * 0.10m + when * 0.10m;
            if (bindingMode == SkillBindingMode.Required) score += 0.10m;
            if (bindingMode == SkillBindingMode.Auto) score += 0.05m;
            if (version.Status == SkillLifecycleStatus.Deprecated) score -= 0.15m;
            if (explicitCandidate) score = Math.Max(score, threshold);
            score = Math.Clamp(score, 0m, 1m);
            if (score < threshold)
            {
                continue;
            }

            var reasons = new List<string>();
            if (keyword > 0) reasons.Add("keyword");
            if (semantic > 0) reasons.Add("semantic");
            if (tag > 0) reasons.Add("tags");
            if (when > 0) reasons.Add("when-to-use");
            if (bindingMode is SkillBindingMode.Required or SkillBindingMode.Auto) reasons.Add($"binding:{bindingMode}");
            if (explicitCandidate) reasons.Add("explicit-request");
            ranked.Add(new(version, score, reasons, bindingMode, explicitCandidate, filterReason));
        }

        var selected = ranked
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Version.Skill!.StableKey, StringComparer.Ordinal)
            .ThenByDescending(item => ParseVersion(item.Version.Version))
            .Take(request.Policy.TopN)
            .ToArray();
        var requiredMissing = await HasRequiredBindingWithoutCandidateAsync(request, projectId, selected, cancellationToken);
        var status = requiredMissing
            ? SkillResolutionStatus.RequiresHumanDecision
            : selected.Length == 0 ? SkillResolutionStatus.NoApplicableSkill : SkillResolutionStatus.Searching;
        var now = timeProvider.GetUtcNow();
        var resolution = new SkillResolution
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ExecutionId = request.ExecutionId,
            WorkItemId = request.WorkItemId,
            ProjectId = projectId,
            RepositoryId = NormalizeScopeValue(request.RepositoryId),
            AgentType = NormalizeScopeValue(request.AgentType),
            Round = round,
            MaxSearchRounds = request.Policy.MaxSearchRounds,
            MaxSelectedSkills = request.Policy.MaxSelectedSkills,
            QueryHash = queryHash,
            QueryTermsJson = Serialize(queryTerms),
            SearchGenerationId = generation.Id,
            Status = status,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SkillResolutions.Add(resolution);
        for (var index = 0; index < selected.Length; index++)
        {
            var item = selected[index];
            var candidate = new SkillResolutionCandidate
            {
                ResolutionId = resolution.Id,
                SkillVersionId = item.Version.Id,
                Rank = index + 1,
                Score = item.Score,
                Threshold = threshold,
                MatchReasonsJson = Serialize(item.MatchReasons),
                CreatedAt = now
            };
            resolution.Candidates.Add(candidate);
            dbContext.SkillTelemetryEvents.Add(CreateTelemetryEvent(
                item.Version,
                resolution,
                SkillTelemetryEventType.SearchImpression,
                $"search:{request.IdempotencyKey}:{item.Version.Id:D}",
                candidate));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await BuildSearchResultAsync(resolution, false, cancellationToken, degraded, generation);
    }

    public async Task<IReadOnlyList<SkillResolutionDetailResult>> ListResolutionsAsync(
        string? projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        if (limit is < 1 or > 200)
        {
            throw new InvalidOperationException("Resolution audit limit must be between 1 and 200.");
        }

        var actor = actorAccessor.Current;
        var normalizedProjectId = string.IsNullOrWhiteSpace(projectId) ? null : ProjectContext.Normalize(projectId);
        if (normalizedProjectId is not null)
        {
            ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, false);
        }

        var query = dbContext.SkillResolutions.AsNoTracking()
            .Include(item => item.Candidates)
            .Include(item => item.Pins)
            .AsQueryable();
        if (actor.HasUser)
        {
            query = query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        }

        if (actor.AllowedProjectIds.Count > 0)
        {
            query = query.Where(item => actor.AllowedProjectIds.Contains(item.ProjectId));
        }

        if (normalizedProjectId is not null)
        {
            query = query.Where(item => item.ProjectId == normalizedProjectId);
        }

        var resolutions = await query.OrderByDescending(item => item.UpdatedAt).Take(limit).ToListAsync(cancellationToken);
        var resolutionIds = resolutions.Select(item => item.Id).ToArray();
        var versionIds = resolutions.SelectMany(item => item.Candidates.Select(candidate => candidate.SkillVersionId)
            .Concat(item.Pins.Select(pin => pin.SkillVersionId))).Distinct().ToArray();
        var versions = await ScopedVersions(includeArchivedSkills: true).AsNoTracking().Include(item => item.Skill)
            .Where(item => versionIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var events = await ScopedTelemetry().AsNoTracking()
            .Where(item => item.ResolutionId.HasValue && resolutionIds.Contains(item.ResolutionId.Value))
            .OrderBy(item => item.OccurredAt).ToListAsync(cancellationToken);
        var eventsByResolution = events.GroupBy(item => item.ResolutionId!.Value).ToDictionary(group => group.Key, group => group.ToArray());

        return resolutions.Select(resolution =>
        {
            var pins = resolution.Pins.ToDictionary(item => item.SkillVersionId);
            var candidates = resolution.Candidates.OrderBy(item => item.Rank).Where(item => versions.ContainsKey(item.SkillVersionId)).Select(item =>
            {
                var version = versions[item.SkillVersionId];
                pins.TryGetValue(item.SkillVersionId, out var pin);
                return new SkillResolutionCandidateAuditResult(item.SkillVersionId, version.Version, version.Skill!.Name, item.Rank, item.Score, item.Threshold,
                    Deserialize<string[]>(item.MatchReasonsJson), pin is not null, pin?.ReleasedAt is not null, pin?.ContentHash ?? version.ContentHash);
            }).ToArray();
            var auditEvents = eventsByResolution.GetValueOrDefault(resolution.Id, []).Select(item => new SkillResolutionEventAuditResult(
                item.Id, item.SkillVersionId, item.EventType, item.RejectionStage, item.ReasonClass, item.ReasonText, item.EvidenceJson, item.OccurredAt)).ToArray();
            return new SkillResolutionDetailResult(resolution.Id, resolution.ExecutionId, resolution.WorkItemId, resolution.ProjectId, resolution.RepositoryId,
                resolution.AgentType, resolution.Round, resolution.MaxSearchRounds, resolution.Status, resolution.QueryHash, resolution.SearchGenerationId,
                candidates, auditEvents, resolution.CreatedAt, resolution.UpdatedAt);
        }).ToArray();
    }

    public async Task<SkillResolutionFeedbackResult> RecordFeedbackAsync(SkillResolutionFeedbackRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.ReasonClass == SkillRejectionReason.Other && string.IsNullOrWhiteSpace(request.ReasonText))
        {
            throw new InvalidOperationException("ReasonText is required when ReasonClass is Other.");
        }

        var actor = actorAccessor.Current;
        var replay = await dbContext.SkillTelemetryEvents.SingleOrDefaultAsync(
            item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (replay is not null)
        {
            var replayResolution = await OwnedResolution(request.ResolutionId).SingleAsync(cancellationToken);
            return new(replay.Id, request.ResolutionId, request.SkillVersionId, request.Stage, request.ReasonClass, true, replayResolution.Round < replayResolution.MaxSearchRounds, replayResolution.Round + 1, replayResolution.Status);
        }

        var resolution = await OwnedResolution(request.ResolutionId)
            .Include(item => item.Candidates)
            .Include(item => item.Pins)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Skill resolution was not found.");
        ValidateFeedbackStage(resolution, request);
        var version = await ScopedVersions(includeArchivedSkills: true).Include(item => item.Skill).SingleAsync(item => item.Id == request.SkillVersionId, cancellationToken);
        var candidate = resolution.Candidates.SingleOrDefault(item => item.SkillVersionId == version.Id);
        var pin = resolution.Pins.SingleOrDefault(item => item.SkillVersionId == version.Id && item.ReleasedAt == null);
        var eventType = pin is not null ? SkillTelemetryEventType.SelectedThenReleased : SkillTelemetryEventType.Rejected;
        if (request.Stage == SkillRejectionStage.RevokedOrPolicyCancelled)
        {
            eventType = SkillTelemetryEventType.Cancellation;
        }

        if (pin is not null)
        {
            pin.ReleasedAt = timeProvider.GetUtcNow();
        }

        var telemetry = CreateTelemetryEvent(version, resolution, eventType, request.IdempotencyKey, candidate);
        telemetry.RejectionStage = request.Stage;
        telemetry.ReasonClass = request.ReasonClass;
        telemetry.ReasonText = PortableSkillBundleValidator.BoundAndRedact(request.ReasonText);
        telemetry.EvidenceJson = Serialize(new
        {
            evidenceRefs = NormalizeAndRedact(request.EvidenceRefs),
            missingCapabilities = NormalizeValues(request.MissingCapabilities),
            requestedAlternativeTags = NormalizeValues(request.RequestedAlternativeTags),
            requestedAlternativeKeywords = NormalizeValues(request.RequestedAlternativeKeywords)
        });
        dbContext.SkillTelemetryEvents.Add(telemetry);
        resolution.Status = request.Stage == SkillRejectionStage.RevokedOrPolicyCancelled &&
                            request.ReasonClass is SkillRejectionReason.PermissionDenied or SkillRejectionReason.PolicyConflict or SkillRejectionReason.Revoked
            ? SkillResolutionStatus.RequiresHumanDecision
            : SkillResolutionStatus.Searching;
        resolution.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(telemetry.Id, resolution.Id, version.Id, request.Stage, request.ReasonClass, false, resolution.Round < resolution.MaxSearchRounds, resolution.Round + 1, resolution.Status);
    }

    public async Task<SkillSelectForExecutionResult> SelectAsync(SkillSelectForExecutionRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.SkillVersionIds.Count == 0)
        {
            throw new InvalidOperationException("At least one candidate SkillVersion must be selected.");
        }

        var resolution = await OwnedResolution(request.ResolutionId)
            .Include(item => item.Candidates)
            .Include(item => item.Pins)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Skill resolution was not found.");
        if (resolution.Pins.Count > 0)
        {
            return await BuildSelectionResultAsync(resolution, true, cancellationToken);
        }

        var candidateIds = resolution.Candidates.Select(item => item.SkillVersionId).ToHashSet();
        if (request.SkillVersionIds.Any(id => !candidateIds.Contains(id)))
        {
            throw new InvalidOperationException("Only SkillVersions returned by this exact resolution may be selected.");
        }

        var resolved = await ResolveDependenciesAsync(request.SkillVersionIds, resolution.MaxSelectedSkills, cancellationToken);
        ValidateConflicts(resolved);
        var now = timeProvider.GetUtcNow();
        foreach (var selected in resolved)
        {
            if (selected.Version.Status is not (SkillLifecycleStatus.Published or SkillLifecycleStatus.Deprecated))
            {
                throw new InvalidOperationException("Only Published or policy-eligible Deprecated exact SkillVersions may be pinned for a new execution.");
            }

            var pin = new SkillResolutionPin
            {
                ResolutionId = resolution.Id,
                SkillVersionId = selected.Version.Id,
                ContentHash = selected.Version.ContentHash,
                IsDependency = selected.IsDependency,
                CreatedAt = now
            };
            dbContext.SkillResolutionPins.Add(pin);
            var telemetry = CreateTelemetryEvent(selected.Version, resolution, SkillTelemetryEventType.Selected, $"select:{request.IdempotencyKey}:{selected.Version.Id:D}");
            telemetry.ReasonText = PortableSkillBundleValidator.BoundAndRedact(request.SelectionReason, 500);
            dbContext.SkillTelemetryEvents.Add(telemetry);
        }

        resolution.Status = SkillResolutionStatus.Selected;
        resolution.UpdatedAt = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                $"Skill selection lost an optimistic concurrency race for: {string.Join(", ", ex.Entries.Select(entry => entry.Metadata.ClrType.Name).Distinct())}.", ex);
        }
        return await BuildSelectionResultAsync(resolution, false, cancellationToken);
    }

    public async Task<SkillVersionBundleResult> GetPinnedVersionAsync(SkillVersionGetRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        var resolution = await OwnedResolution(request.ResolutionId)
            .Include(item => item.Pins)
            .SingleOrDefaultAsync(item => item.ExecutionId == request.ExecutionId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill resolution was not found for this execution.");
        var pin = resolution.Pins.SingleOrDefault(item => item.SkillVersionId == request.SkillVersionId && item.ReleasedAt == null)
            ?? throw new UnauthorizedAccessException("The SkillVersion is not pinned to this execution.");
        EnsureHash(pin.ContentHash, request.ExpectedContentHash);
        var version = await ScopedVersions(includeArchivedSkills: true).SingleAsync(item => item.Id == request.SkillVersionId, cancellationToken);
        EnsureHash(version.ContentHash, pin.ContentHash);
        var revoked = version.Status == SkillLifecycleStatus.Revoked;
        if (revoked)
        {
            throw new UnauthorizedAccessException("The pinned SkillVersion was revoked during execution and cannot be retrieved or newly materialized.");
        }

        return new(version.SkillId, version.Id, version.Version, version.ContentHash, Deserialize<PortableSkillBundle>(version.BundleJson), version.Status, false);
    }

    public async Task<SkillMaterializationResult> MaterializeAsync(SkillMaterializeRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        var actor = actorAccessor.Current;
        var existing = await dbContext.SkillMaterializations.SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.ExecutionId == request.ExecutionId &&
            item.SkillVersionId == request.SkillVersionId && item.ContentHash == request.ExpectedContentHash,
            cancellationToken);
        if (existing is not null)
        {
            return new(existing.Id, existing.SkillVersionId, existing.ContentHash, existing.RelativePath, existing.Status, true);
        }

        var bundle = await GetPinnedVersionAsync(new(request.ExecutionId, request.ResolutionId, request.SkillVersionId, request.ExpectedContentHash), cancellationToken);
        var relativePath = await materializationStore.MaterializeAsync(request.ExecutionId, bundle.ContentHash, bundle.Bundle, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var materialization = new SkillMaterialization
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ExecutionId = request.ExecutionId,
            ResolutionId = request.ResolutionId,
            SkillVersionId = request.SkillVersionId,
            ContentHash = bundle.ContentHash,
            RelativePath = relativePath,
            Status = SkillMaterializationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SkillMaterializations.Add(materialization);
        var resolution = await OwnedResolution(request.ResolutionId).SingleAsync(cancellationToken);
        var version = await ScopedVersions(includeArchivedSkills: true).Include(item => item.Skill).SingleAsync(item => item.Id == request.SkillVersionId, cancellationToken);
        dbContext.SkillTelemetryEvents.Add(CreateTelemetryEvent(version, resolution, SkillTelemetryEventType.Materialized, $"materialize:{request.IdempotencyKey}"));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(materialization.Id, materialization.SkillVersionId, materialization.ContentHash, materialization.RelativePath, materialization.Status, false);
    }

    public async Task<SkillMaterializationCleanupResult> CleanupMaterializationsAsync(SkillMaterializationCleanupRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        var actor = actorAccessor.Current;
        var cleanupEventKey = $"cleanup:{request.IdempotencyKey}";
        var replay = await dbContext.SkillTelemetryEvents.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == cleanupEventKey,
            cancellationToken);
        if (replay is not null)
        {
            using var evidence = JsonDocument.Parse(replay.EvidenceJson);
            var ids = evidence.RootElement.TryGetProperty("materializationIds", out var values)
                ? values.EnumerateArray().Select(value => value.GetGuid()).ToArray()
                : [];
            return new(request.ExecutionId, ids.Length, ids, true);
        }
        var materializations = await dbContext.SkillMaterializations
            .Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.ExecutionId == request.ExecutionId && item.Status != SkillMaterializationStatus.Cleaned)
            .ToListAsync(cancellationToken);
        await materializationStore.CleanupExecutionAsync(request.ExecutionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var item in materializations)
        {
            item.Status = SkillMaterializationStatus.Cleaned;
            item.CleanedAt = now;
            item.UpdatedAt = now;
            item.FailureReason = string.Empty;
        }
        if (materializations.Count > 0)
        {
            var first = materializations[0];
            var resolution = await OwnedResolution(first.ResolutionId).SingleAsync(cancellationToken);
            var version = await ScopedVersions(includeArchivedSkills: true).Include(item => item.Skill)
                .SingleAsync(item => item.Id == first.SkillVersionId, cancellationToken);
            var telemetry = CreateTelemetryEvent(version, resolution, SkillTelemetryEventType.Cancellation, cleanupEventKey);
            telemetry.RejectionStage = SkillRejectionStage.InvocationAborted;
            telemetry.ReasonClass = SkillRejectionReason.ExecutionContextChanged;
            telemetry.ReasonText = PortableSkillBundleValidator.BoundAndRedact(request.Reason, 500);
            telemetry.EvidenceJson = Serialize(new { materializationIds = materializations.Select(item => item.Id).ToArray() });
            dbContext.SkillTelemetryEvents.Add(telemetry);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(request.ExecutionId, materializations.Count, materializations.Select(item => item.Id).ToArray(), false);
    }

    public async Task<SkillTelemetryRecordResult> RecordInvocationAsync(SkillInvocationRecordRequest request, CancellationToken cancellationToken)
    {
        EnsureExecutionAccess();
        ValidateIdempotencyKey(request.IdempotencyKey);
        var actor = actorAccessor.Current;
        var replay = await dbContext.SkillTelemetryEvents.SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return new(replay.Id, replay.EventType, true);
        }

        var resolution = await OwnedResolution(request.ResolutionId).Include(item => item.Pins)
            .SingleOrDefaultAsync(item => item.ExecutionId == request.ExecutionId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill resolution was not found for this execution.");
        var pin = resolution.Pins.SingleOrDefault(item => item.SkillVersionId == request.SkillVersionId && item.ReleasedAt == null)
            ?? throw new UnauthorizedAccessException("Invocation evidence can only be recorded for a pinned SkillVersion.");
        EnsureHash(pin.ContentHash, request.ContentHash);
        var version = await ScopedVersions(includeArchivedSkills: true).Include(item => item.Skill).SingleAsync(item => item.Id == request.SkillVersionId, cancellationToken);
        if (version.Status == SkillLifecycleStatus.Revoked && request.Started)
        {
            throw new UnauthorizedAccessException("The SkillVersion was revoked; invocation fails closed.");
        }

        var eventType = request.Started
            ? SkillTelemetryEventType.InvocationStarted
            : request.Succeeded ? SkillTelemetryEventType.InvocationSucceeded : SkillTelemetryEventType.InvocationFailed;
        var telemetry = CreateTelemetryEvent(version, resolution, eventType, request.IdempotencyKey);
        telemetry.WorkItemId = request.WorkItemId;
        telemetry.ReasonText = PortableSkillBundleValidator.BoundAndRedact(request.FailureClass, 500);
        telemetry.EvidenceJson = Serialize(new
        {
            latencyMilliseconds = Math.Max(0, request.LatencyMilliseconds),
            toolUsage = NormalizeValues(request.ToolUsage),
            evidenceRefs = NormalizeAndRedact(request.EvidenceRefs)
        });
        dbContext.SkillTelemetryEvents.Add(telemetry);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(telemetry.Id, telemetry.EventType, false);
    }

    public async Task<SkillReindexResult> ReindexAsync(SkillReindexRequest request, CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsReindex);
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.Threshold is < 0 or > 1)
        {
            throw new InvalidOperationException("Skill search threshold must be between zero and one.");
        }

        var actor = actorAccessor.Current;
        var existing = (await dbContext.SkillSearchGenerations.Where(item => item.TenantId == actor.TenantId).ToListAsync(cancellationToken))
            .SingleOrDefault(item => item.BenchmarkJson.Contains(request.IdempotencyKey, StringComparison.Ordinal));
        if (existing is not null)
        {
            var count = await dbContext.SkillSearchDocuments.CountAsync(item => item.GenerationId == existing.Id, cancellationToken);
            return new(existing.Id, existing.Status, count, existing.BenchmarkJson, existing.Status == SkillSearchGenerationStatus.Active, true);
        }

        return await BuildGenerationAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<SkillSearchGenerationResult>> ListSearchGenerationsAsync(CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        var actor = actorAccessor.Current;
        var query = dbContext.SkillSearchGenerations.AsNoTracking().AsQueryable();
        if (actor.HasUser)
        {
            query = query.Where(item => item.TenantId == actor.TenantId);
        }

        var generations = await query.OrderByDescending(item => item.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var generationIds = generations.Select(item => item.Id).ToArray();
        var counts = await dbContext.SkillSearchDocuments.AsNoTracking()
            .Where(item => generationIds.Contains(item.GenerationId))
            .GroupBy(item => item.GenerationId)
            .Select(group => new { GenerationId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GenerationId, item => item.Count, cancellationToken);
        return generations.Select(item => new SkillSearchGenerationResult(
            item.Id,
            item.SearchProfileVersion,
            item.EmbeddingModelId,
            item.EmbeddingModelVersion,
            item.Threshold,
            item.Status,
            counts.GetValueOrDefault(item.Id),
            item.BenchmarkJson,
            item.CreatedAt,
            item.UpdatedAt,
            item.ActivatedAt)).ToArray();
    }

    public async Task<SkillReindexResult> ActivateSearchGenerationAsync(
        SkillSearchGenerationActivateRequest request,
        CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsReindex);
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("A bounded search-generation activation reason is required.");
        }

        var actor = actorAccessor.Current;
        var query = dbContext.SkillSearchGenerations.AsQueryable();
        if (actor.HasUser)
        {
            query = query.Where(item => item.TenantId == actor.TenantId);
        }

        var target = await query.SingleOrDefaultAsync(item => item.Id == request.GenerationId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill search generation was not found.");
        var indexed = await dbContext.SkillSearchDocuments.CountAsync(item => item.GenerationId == target.Id, cancellationToken);
        var eligibleCount = await ScopedVersions(includeArchivedSkills: false)
            .CountAsync(item => SearchableStatuses.Contains(item.Status), cancellationToken);
        if (indexed != eligibleCount)
        {
            throw new InvalidOperationException("Search generation cannot be activated because its indexed version count is stale.");
        }

        if (target.Status == SkillSearchGenerationStatus.Active)
        {
            return new(target.Id, target.Status, indexed, target.BenchmarkJson, true, true);
        }

        if (target.Status is SkillSearchGenerationStatus.Building or SkillSearchGenerationStatus.Validating or SkillSearchGenerationStatus.Failed)
        {
            throw new InvalidOperationException($"Search generation in status {target.Status} cannot be activated.");
        }

        await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            var active = await dbContext.SkillSearchGenerations
                .Where(item => item.TenantId == target.TenantId && item.Status == SkillSearchGenerationStatus.Active)
                .ToListAsync(transactionCancellationToken);
            foreach (var current in active)
            {
                current.Status = SkillSearchGenerationStatus.RolledBack;
                current.UpdatedAt = timeProvider.GetUtcNow();
            }

            await dbContext.SaveChangesAsync(transactionCancellationToken);
            target.Status = SkillSearchGenerationStatus.Active;
            target.ActivatedAt = timeProvider.GetUtcNow();
            target.UpdatedAt = target.ActivatedAt.Value;
            target.BenchmarkJson = MergeActivationEvidence(target.BenchmarkJson, request.Reason, request.IdempotencyKey);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return true;
        }, cancellationToken);
        return new(target.Id, target.Status, indexed, target.BenchmarkJson, true, false);
    }

    public async Task<IReadOnlyList<SkillTelemetryAggregateResult>> GetAnalyticsAsync(SkillAnalyticsRequest request, CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        if (request.WindowDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("Analytics WindowDays must be between 1 and 3650.");
        }

        var actor = actorAccessor.Current;
        var from = timeProvider.GetUtcNow().AddDays(-request.WindowDays);
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime);
        var eventQuery = dbContext.SkillTelemetryEvents.AsNoTracking().Where(item =>
            item.OccurredAt >= from &&
            !dbContext.SkillTelemetryAggregationLedgers.Any(ledger => ledger.EventId == item.Id));
        var aggregateQuery = dbContext.SkillTelemetryDailyAggregates.AsNoTracking().Where(item => item.AggregateDate >= fromDate);
        if (actor.HasUser)
        {
            eventQuery = eventQuery.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
            aggregateQuery = aggregateQuery.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
            if (actor.AllowedProjectIds.Count > 0)
            {
                eventQuery = eventQuery.Where(item => actor.AllowedProjectIds.Contains(item.ProjectId));
                aggregateQuery = aggregateQuery.Where(item => actor.AllowedProjectIds.Contains(item.ProjectId));
            }
        }
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            ActorAuthorization.EnsureProjectAllowed(actor, projectId, false);
            eventQuery = eventQuery.Where(item => item.ProjectId == projectId);
            aggregateQuery = aggregateQuery.Where(item => item.ProjectId == projectId);
        }
        if (!string.IsNullOrWhiteSpace(request.RepositoryId))
        {
            var repositoryId = NormalizeScopeValue(request.RepositoryId);
            eventQuery = eventQuery.Where(item => item.RepositoryId == repositoryId);
            aggregateQuery = aggregateQuery.Where(item => item.RepositoryId == repositoryId);
        }
        if (!string.IsNullOrWhiteSpace(request.AgentType))
        {
            var agentType = NormalizeScopeValue(request.AgentType);
            eventQuery = eventQuery.Where(item => item.AgentType == agentType);
            aggregateQuery = aggregateQuery.Where(item => item.AgentType == agentType);
        }
        if (request.SkillId.HasValue)
        {
            eventQuery = eventQuery.Where(item => item.SkillId == request.SkillId.Value);
            aggregateQuery = aggregateQuery.Where(item => item.SkillId == request.SkillId.Value);
        }
        if (request.SkillVersionId.HasValue)
        {
            eventQuery = eventQuery.Where(item => item.SkillVersionId == request.SkillVersionId.Value);
            aggregateQuery = aggregateQuery.Where(item => item.SkillVersionId == request.SkillVersionId.Value);
        }

        var aggregateRows = (await aggregateQuery.ToListAsync(cancellationToken)).Select(item => new TelemetryMetricRow(
            item.SkillId, item.SkillVersionId, item.ProjectId, item.RepositoryId, item.AgentType,
            item.EventType, item.RejectionStage, item.ReasonClass, item.EventCount, item.LastOccurredAt));
        var eventRows = (await eventQuery.ToListAsync(cancellationToken)).Select(item => new TelemetryMetricRow(
            item.SkillId, item.SkillVersionId, item.ProjectId, item.RepositoryId, item.AgentType,
            item.EventType, item.RejectionStage, item.ReasonClass, 1, item.OccurredAt));
        return aggregateRows.Concat(eventRows)
            .GroupBy(item => AnalyticsKey(item, request.Dimension))
            .Select(group => BuildAggregate(group.Key, group, request.WindowDays))
            .OrderByDescending(item => item.SearchImpressionCount)
            .ToArray();
    }

    public Task<SkillTelemetryReconciliationResult> ReconcileTelemetryAsync(
        SkillTelemetryReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsManage);
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (request.RawEventRetentionDays is < 90 or > 3650 ||
            request.AggregateRetentionDays < request.RawEventRetentionDays ||
            request.AggregateRetentionDays > 3650)
        {
            throw new InvalidOperationException("Telemetry retention must keep raw events for 90-3650 days so the 7/30/90-day governance windows remain reproducible, and aggregates for at least as long.");
        }

        var actor = actorAccessor.Current;
        return dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await dbContext.AcquireTransactionLockAsync(
                $"skill-telemetry-reconcile:{actor.TenantId:D}:{actor.UserId:D}",
                transactionCancellationToken);
            var replay = await dbContext.SkillTelemetryReconciliationRuns.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId && item.IdempotencyKey == request.IdempotencyKey,
                transactionCancellationToken);
            if (replay is not null)
            {
                return ToReconciliationResult(replay, true);
            }

            var eventQuery = ScopedTelemetry().Where(item =>
                !dbContext.SkillTelemetryAggregationLedgers.Any(ledger => ledger.EventId == item.Id));
            var events = await eventQuery.OrderBy(item => item.CreatedAt).ToListAsync(transactionCancellationToken);
            var grouped = events.GroupBy(AggregateKey).ToArray();
            var dates = grouped.Select(group => group.Key.AggregateDate).Distinct().ToArray();
            var existingQuery = dbContext.SkillTelemetryDailyAggregates.AsQueryable();
            if (actor.HasUser)
            {
                existingQuery = existingQuery.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
            }
            if (dates.Length > 0)
            {
                existingQuery = existingQuery.Where(item => dates.Contains(item.AggregateDate));
            }
            else
            {
                existingQuery = existingQuery.Where(_ => false);
            }
            var existing = (await existingQuery.ToListAsync(transactionCancellationToken))
                .ToDictionary(AggregateKey, StringComparer.Ordinal);
            var now = timeProvider.GetUtcNow();
            foreach (var group in grouped)
            {
                var key = AggregateKey(group.Key);
                if (!existing.TryGetValue(key, out var aggregate))
                {
                    aggregate = new SkillTelemetryDailyAggregate
                    {
                        TenantId = group.Key.TenantId,
                        OwnerUserId = group.Key.OwnerUserId,
                        AggregateDate = group.Key.AggregateDate,
                        SkillId = group.Key.SkillId,
                        SkillVersionId = group.Key.SkillVersionId,
                        ProjectId = group.Key.ProjectId,
                        RepositoryId = group.Key.RepositoryId,
                        AgentType = group.Key.AgentType,
                        EventType = group.Key.EventType,
                        RejectionStage = group.Key.RejectionStage,
                        ReasonClass = group.Key.ReasonClass,
                        EventCount = 0
                    };
                    dbContext.SkillTelemetryDailyAggregates.Add(aggregate);
                    existing[key] = aggregate;
                }

                aggregate.EventCount += group.Count();
                aggregate.LastOccurredAt = group.Max(item => item.OccurredAt);
                aggregate.UpdatedAt = now;
            }

            foreach (var item in events)
            {
                dbContext.SkillTelemetryAggregationLedgers.Add(new SkillTelemetryAggregationLedger
                {
                    EventId = item.Id,
                    AggregatedAt = now
                });
            }
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            var rawCutoff = now.AddDays(-request.RawEventRetentionDays);
            var aggregateCutoff = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-request.AggregateRetentionDays));
            var protectedQuery = ScopedTelemetry().Where(item => item.OccurredAt < rawCutoff &&
                (item.RejectionStage == SkillRejectionStage.RevokedOrPolicyCancelled ||
                 item.ReasonClass == SkillRejectionReason.Revoked ||
                 item.ReasonClass == SkillRejectionReason.PolicyConflict ||
                 item.ReasonClass == SkillRejectionReason.PermissionDenied));
            var protectedCount = await protectedQuery.CountAsync(transactionCancellationToken);
            var deletedRaw = await ScopedTelemetry().Where(item => item.OccurredAt < rawCutoff &&
                item.RejectionStage != SkillRejectionStage.RevokedOrPolicyCancelled &&
                item.ReasonClass != SkillRejectionReason.Revoked &&
                item.ReasonClass != SkillRejectionReason.PolicyConflict &&
                item.ReasonClass != SkillRejectionReason.PermissionDenied)
                .ExecuteDeleteAsync(transactionCancellationToken);
            var aggregateRetentionQuery = dbContext.SkillTelemetryDailyAggregates.Where(item => item.AggregateDate < aggregateCutoff);
            if (actor.HasUser)
            {
                aggregateRetentionQuery = aggregateRetentionQuery.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
            }
            var deletedAggregates = await aggregateRetentionQuery.ExecuteDeleteAsync(transactionCancellationToken);
            var run = new SkillTelemetryReconciliationRun
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                IdempotencyKey = request.IdempotencyKey,
                AggregatedEventCount = events.Count,
                AggregateRowCount = grouped.Length,
                DeletedRawEventCount = deletedRaw,
                DeletedAggregateRowCount = deletedAggregates,
                ProtectedRawEventCount = protectedCount,
                RawRetentionDays = request.RawEventRetentionDays,
                AggregateRetentionDays = request.AggregateRetentionDays,
                CreatedAt = now
            };
            dbContext.SkillTelemetryReconciliationRuns.Add(run);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return ToReconciliationResult(run, false);
        }, cancellationToken);
    }

    public async Task<SkillMetadataGovernanceReviewResult> ReviewMetadataGovernanceAsync(SkillMetadataGovernancePolicy policy, CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        if (policy.MinimumSampleSize < 2 || policy.WindowDays is < 1 or > 90 || policy.MinimumRejectionRate is < 0 or > 1 || policy.MinimumSignalConfidence is < 0 or > 1)
        {
            throw new InvalidOperationException("Skill metadata governance thresholds are invalid; raw evidence windows are limited to 90 days.");
        }

        var skills = await ScopedSkills(includeArchived: false).AsNoTracking().ToListAsync(cancellationToken);
        var events = await ScopedTelemetry().Where(item => item.OccurredAt >= timeProvider.GetUtcNow().AddDays(-policy.WindowDays)).ToListAsync(cancellationToken);
        var actor = actorAccessor.Current;
        var pendingProposalQuery = dbContext.SkillMetadataProposals.AsNoTracking()
            .Where(item => item.Status == SkillMetadataProposalStatus.Pending);
        if (actor.HasUser)
        {
            pendingProposalQuery = pendingProposalQuery.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        }
        var pendingProposals = await pendingProposalQuery.ToListAsync(cancellationToken);
        var findings = new List<SkillMetadataGovernanceFindingResult>();
        var exceptionCount = 0;
        foreach (var skill in skills)
        {
            var skillEvents = events.Where(item => item.SkillId == skill.Id).ToArray();
            var impressions = skillEvents.Count(item => item.EventType == SkillTelemetryEventType.SearchImpression);
            var qualityRejections = skillEvents.Where(IsRelevanceQualityRejection).ToArray();
            if (impressions < policy.MinimumSampleSize || qualityRejections.Length == 0)
            {
                continue;
            }

            var rate = decimal.Divide(qualityRejections.Length, impressions);
            if (rate < policy.MinimumRejectionRate)
            {
                continue;
            }

            var classification = ClassifyGovernanceSignal(qualityRejections);
            var confidence = Math.Clamp(rate * Math.Min(1m, decimal.Divide(impressions, policy.MinimumSampleSize * 2)), 0m, 1m);
            if (confidence < policy.MinimumSignalConfidence)
            {
                continue;
            }

            var counts = qualityRejections
                .GroupBy(item => $"{item.RejectionStage}:{item.ReasonClass}")
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var metadataHash = MetadataHash(skill);
            if (pendingProposals.Any(item => item.SkillId == skill.Id &&
                                             item.ExpectedMetadataVersion == skill.MetadataVersion &&
                                             string.Equals(item.ExpectedMetadataHash, metadataHash, StringComparison.OrdinalIgnoreCase)))
            {
                exceptionCount++;
                continue;
            }
            findings.Add(new(
                $"skill-metadata:{skill.Id:D}:{classification.SignalType}:{policy.WindowDays}",
                skill.Id,
                null,
                classification.SignalType,
                impressions,
                rate,
                confidence,
                metadataHash,
                counts,
                classification.RecommendedAction,
                classification.RequiresNewVersion,
                qualityRejections.Select(item => $"skill-telemetry:{item.Id:D}").Take(25).ToArray()));
        }

        return new(skills.Count, skills.Count, findings.Count + exceptionCount, findings.Count, exceptionCount, true, false, findings);
    }

    public async Task<IReadOnlyList<SkillMetadataProposalResult>> ListMetadataProposalsAsync(
        SkillMetadataProposalStatus? status,
        CancellationToken cancellationToken)
    {
        EnsureReadAccess();
        var actor = actorAccessor.Current;
        var query = dbContext.SkillMetadataProposals.AsNoTracking().AsQueryable();
        if (actor.HasUser) query = query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        if (status.HasValue) query = query.Where(item => item.Status == status.Value);
        return await query.OrderByDescending(item => item.UpdatedAt).Take(500).Select(item => new SkillMetadataProposalResult(
            item.Id, item.SkillId, item.ExpectedMetadataVersion, item.ExpectedMetadataHash,
            item.ProposedPatchJson, item.EvidenceJson, item.Confidence, item.Status,
            item.GovernanceRunId, item.CreatedAt, item.UpdatedAt)).ToArrayAsync(cancellationToken);
    }

    public async Task<SkillMetadataProposalResult> DecideMetadataProposalAsync(
        SkillMetadataProposalDecisionRequest request,
        CancellationToken cancellationToken)
    {
        EnsureManagementAccess(SecurityScopes.SkillsManage);
        var actor = actorAccessor.Current;
        var query = dbContext.SkillMetadataProposals.AsQueryable();
        if (actor.HasUser) query = query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        var proposal = await query.SingleOrDefaultAsync(item => item.Id == request.ProposalId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill metadata proposal was not found.");
        if (proposal.Status != SkillMetadataProposalStatus.Pending) return ToMetadataProposal(proposal);
        var skill = await ScopedSkills(includeArchived: false).SingleAsync(item => item.Id == proposal.SkillId, cancellationToken);
        var currentHash = MetadataHash(skill);
        if (request.ExpectedMetadataVersion != skill.MetadataVersion ||
            proposal.ExpectedMetadataVersion != skill.MetadataVersion ||
            !string.Equals(request.ExpectedMetadataHash, currentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(proposal.ExpectedMetadataHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            proposal.Status = SkillMetadataProposalStatus.Stale;
            proposal.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToMetadataProposal(proposal);
        }

        if (!request.Approve)
        {
            proposal.Status = SkillMetadataProposalStatus.Rejected;
            proposal.EvidenceJson = MergeDecisionEvidence(proposal.EvidenceJson, request.Note);
            proposal.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToMetadataProposal(proposal);
        }

        var name = request.Name?.Trim() ?? skill.Name;
        var aliases = request.Aliases is null ? skill.Aliases : NormalizeValues(request.Aliases);
        await EnsureNameAndAliasesAreUnambiguousAsync(skill.Id, name, aliases, cancellationToken);
        skill.Name = name;
        skill.Description = request.Description?.Trim() ?? skill.Description;
        skill.WhenToUse = request.WhenToUse?.Trim() ?? skill.WhenToUse;
        skill.Tags = request.Tags is null ? skill.Tags : NormalizeValues(request.Tags);
        skill.Aliases = aliases;
        skill.RiskLevel = request.RiskLevel ?? skill.RiskLevel;
        skill.MetadataVersion++;
        skill.UpdatedAt = timeProvider.GetUtcNow();
        proposal.Status = SkillMetadataProposalStatus.Applied;
        proposal.ProposedPatchJson = Serialize(new { skill.Name, skill.Description, skill.WhenToUse, skill.Tags, skill.Aliases, skill.RiskLevel });
        proposal.EvidenceJson = MergeDecisionEvidence(proposal.EvidenceJson, request.Note);
        proposal.UpdatedAt = skill.UpdatedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToMetadataProposal(proposal);
    }

    private IQueryable<Skill> ScopedSkills(bool includeArchived)
    {
        var actor = actorAccessor.Current;
        var query = dbContext.Skills.AsQueryable();
        if (actor.HasUser) query = query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        return includeArchived ? query : query.Where(item => item.ArchivedAt == null);
    }

    private IQueryable<SkillVersion> ScopedVersions(bool includeArchivedSkills)
    {
        var actor = actorAccessor.Current;
        var query = dbContext.SkillVersions.AsQueryable();
        if (actor.HasUser)
        {
            query = query.Where(item => item.Skill != null && item.Skill.TenantId == actor.TenantId && item.Skill.OwnerUserId == actor.UserId);
        }
        return includeArchivedSkills ? query : query.Where(item => item.Skill != null && item.Skill.ArchivedAt == null);
    }

    private IQueryable<SkillResolution> OwnedResolution(Guid resolutionId)
    {
        var actor = actorAccessor.Current;
        var query = dbContext.SkillResolutions.Where(item => item.Id == resolutionId);
        return actor.HasUser ? query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId) : query;
    }

    private IQueryable<SkillTelemetryEvent> ScopedTelemetry()
    {
        var actor = actorAccessor.Current;
        var query = dbContext.SkillTelemetryEvents.AsQueryable();
        return actor.HasUser ? query.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId) : query;
    }

    private void EnsureReadAccess()
    {
        var actor = actorAccessor.Current;
        if (actor.HasUser && !actor.IsAdmin && !actor.HasScope(SecurityScopes.SkillsRead) && !actor.HasScope(SecurityScopes.MemoryRead))
            throw new UnauthorizedAccessException($"Scope '{SecurityScopes.SkillsRead}' is required.");
    }

    private void EnsureExecutionAccess()
    {
        var actor = actorAccessor.Current;
        if (actor.HasUser && !actor.IsAdmin && !actor.HasScope(SecurityScopes.SkillsExecute))
            throw new UnauthorizedAccessException($"Scope '{SecurityScopes.SkillsExecute}' is required.");
    }

    private void EnsureManagementAccess(string scope)
    {
        var actor = actorAccessor.Current;
        if (actor.HasUser && !actor.IsAdmin && !actor.HasScope(scope))
            throw new UnauthorizedAccessException($"Admin role or scope '{scope}' is required.");
    }

    private void EnsurePublishAccess() => EnsureManagementAccess(SecurityScopes.SkillsPublish);

    private void EnsureSecurityAccess()
    {
        var actor = actorAccessor.Current;
        if (actor.HasUser && actor.Role != TenantUserRole.Owner && !actor.HasScope(SecurityScopes.SkillsSecurity) && !actor.HasScope(SecurityScopes.SecurityManage))
            throw new UnauthorizedAccessException("Owner role or skills:security scope is required to revoke a SkillVersion.");
    }

    private async Task EnsureNameAndAliasesAreUnambiguousAsync(Guid? currentSkillId, string name, IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        var candidates = NormalizeValues(aliases.Append(name).ToArray()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await ScopedSkills(includeArchived: true).AsNoTracking().Where(item => !currentSkillId.HasValue || item.Id != currentSkillId.Value).ToListAsync(cancellationToken);
        if (existing.Any(skill => candidates.Contains(skill.Name) || skill.Aliases.Any(candidates.Contains)))
        {
            throw new InvalidOperationException("Skill name and aliases must be unambiguous within the tenant/owner scope.");
        }
    }

    private async Task ValidateDependencyTargetsAsync(SkillVersion version, CancellationToken cancellationToken)
    {
        var targetIds = dbContext.SkillVersionDependencies.Local.Where(item => item.SkillVersionId == version.Id).Select(item => item.TargetSkillId).Distinct().ToArray();
        if (targetIds.Length == 0) return;
        var count = await ScopedSkills(includeArchived: false).CountAsync(item => targetIds.Contains(item.Id), cancellationToken);
        if (count != targetIds.Length) throw new InvalidOperationException("One or more Skill dependencies are outside the authorized scope or do not exist.");
    }

    private async Task ValidateDependencyGraphAsync(SkillVersion root, CancellationToken cancellationToken)
    {
        const int maximumDepth = 8;
        const int maximumCount = 32;
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        async Task Visit(Guid versionId, int depth)
        {
            if (depth > maximumDepth || visited.Count > maximumCount) throw new InvalidOperationException("Skill dependency graph exceeds the bounded depth/count policy.");
            if (!visiting.Add(versionId)) throw new InvalidOperationException("Skill dependency cycle detected.");
            var dependencies = await dbContext.SkillVersionDependencies.AsNoTracking().Where(item => item.SkillVersionId == versionId && item.Kind == SkillDependencyKind.Requires).ToListAsync(cancellationToken);
            foreach (var dependency in dependencies)
            {
                var target = await ResolveDependencyVersionAsync(dependency, cancellationToken)
                    ?? throw new InvalidOperationException($"Required dependency {dependency.TargetSkillId:D} has no compatible Published version.");
                await Visit(target.Id, depth + 1);
            }
            visiting.Remove(versionId);
            visited.Add(versionId);
        }
        await Visit(root.Id, 0);
    }

    private async Task<SkillVersion?> ResolveDependencyVersionAsync(SkillVersionDependency dependency, CancellationToken cancellationToken)
    {
        var versions = await ScopedVersions(includeArchivedSkills: false).AsNoTracking()
            .Where(item => item.SkillId == dependency.TargetSkillId && item.Status == SkillLifecycleStatus.Published)
            .ToListAsync(cancellationToken);
        return versions.Where(item => SemanticVersionConstraint.IsSatisfied(item.Version, dependency.VersionConstraint))
            .OrderByDescending(item => ParseVersion(item.Version)).FirstOrDefault();
    }

    private async Task<Guid?> ResolveNewestPublishedVersionIdAsync(Guid skillId, Guid excludedId, CancellationToken cancellationToken)
    {
        var versions = await ScopedVersions(includeArchivedSkills: false).AsNoTracking()
            .Where(item => item.SkillId == skillId && item.Id != excludedId && item.Status == SkillLifecycleStatus.Published)
            .ToListAsync(cancellationToken);
        return versions.OrderByDescending(item => ParseVersion(item.Version)).Select(item => (Guid?)item.Id).FirstOrDefault();
    }

    private async Task IndexPublishedVersionAsync(SkillVersion version, CancellationToken cancellationToken)
    {
        var generation = await EnsureActiveGenerationAsync(cancellationToken);
        var existing = await dbContext.SkillSearchDocuments.SingleOrDefaultAsync(item => item.GenerationId == generation.Id && item.SkillVersionId == version.Id, cancellationToken);
        if (existing is not null) return;
        var vector = await embeddingProvider.EmbedAsync(version.SearchText, EmbeddingPurpose.Document, cancellationToken);
        dbContext.SkillSearchDocuments.Add(new SkillSearchDocument
        {
            GenerationId = generation.Id,
            SkillVersionId = version.Id,
            SearchText = version.SearchText,
            TermsJson = Serialize(SkillText.Tokenize(version.SearchText)),
            EmbeddingJson = Serialize(vector.Values),
            CreatedAt = timeProvider.GetUtcNow()
        });
    }

    private async Task<SkillSearchGeneration> EnsureActiveGenerationAsync(CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var generation = await dbContext.SkillSearchGenerations.SingleOrDefaultAsync(item => item.TenantId == actor.TenantId && item.Status == SkillSearchGenerationStatus.Active, cancellationToken);
        if (generation is not null) return generation;
        generation = new SkillSearchGeneration
        {
            TenantId = actor.TenantId,
            SearchProfileVersion = "skills-v1",
            EmbeddingModelId = embeddingProvider.ModelKey,
            EmbeddingModelVersion = "runtime",
            Threshold = 0.35m,
            Status = SkillSearchGenerationStatus.Active,
            BenchmarkJson = Serialize(new { bootstrap = true }),
            CreatedAt = timeProvider.GetUtcNow(),
            UpdatedAt = timeProvider.GetUtcNow(),
            ActivatedAt = timeProvider.GetUtcNow()
        };
        dbContext.SkillSearchGenerations.Add(generation);
        // The search document references the generation by id without a tracked
        // navigation, so persist the bootstrap generation before adding documents.
        await dbContext.SaveChangesAsync(cancellationToken);
        return generation;
    }

    private async Task<SkillReindexResult> BuildGenerationAsync(SkillReindexRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var stopwatch = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow();
        var generation = new SkillSearchGeneration
        {
            TenantId = actor.TenantId,
            SearchProfileVersion = request.SearchProfileVersion.Trim(),
            EmbeddingModelId = request.EmbeddingModelId.Trim(),
            EmbeddingModelVersion = request.EmbeddingModelVersion.Trim(),
            Threshold = request.Threshold,
            Status = SkillSearchGenerationStatus.Building,
            BenchmarkJson = Serialize(new { request.IdempotencyKey, startedAt = now }),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SkillSearchGenerations.Add(generation);
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            var versions = await ScopedVersions(includeArchivedSkills: false).Include(item => item.Skill)
                .Where(item => SearchableStatuses.Contains(item.Status)).ToListAsync(cancellationToken);
            foreach (var version in versions)
            {
                var vector = await embeddingProvider.EmbedAsync(version.SearchText, EmbeddingPurpose.Document, cancellationToken);
                dbContext.SkillSearchDocuments.Add(new SkillSearchDocument
                {
                    GenerationId = generation.Id,
                    SkillVersionId = version.Id,
                    SearchText = version.SearchText,
                    TermsJson = Serialize(SkillText.Tokenize(version.SearchText)),
                    EmbeddingJson = Serialize(vector.Values),
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }

            generation.Status = SkillSearchGenerationStatus.Validating;
            await dbContext.SaveChangesAsync(cancellationToken);
            var indexed = await dbContext.SkillSearchDocuments.CountAsync(item => item.GenerationId == generation.Id, cancellationToken);
            if (indexed != versions.Count) throw new InvalidOperationException("Shadow Skill index validation count does not match the eligible Published/Deprecated version count.");
            stopwatch.Stop();
            generation.BenchmarkJson = Serialize(new { request.IdempotencyKey, indexedVersionCount = indexed, elapsedMilliseconds = stopwatch.ElapsedMilliseconds, validation = "count-and-hard-filter-pass" });
            if (request.Activate)
            {
                await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
                {
                    var active = await dbContext.SkillSearchGenerations
                        .Where(item => item.TenantId == actor.TenantId && item.Status == SkillSearchGenerationStatus.Active)
                        .ToListAsync(transactionCancellationToken);
                    foreach (var old in active)
                    {
                        old.Status = SkillSearchGenerationStatus.Retired;
                        old.UpdatedAt = timeProvider.GetUtcNow();
                    }

                    // PostgreSQL enforces one Active generation per tenant. Persist the
                    // retirement first, but keep both writes inside one transaction so
                    // readers observe either the old generation or the new generation.
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    generation.Status = SkillSearchGenerationStatus.Active;
                    generation.ActivatedAt = timeProvider.GetUtcNow();
                    generation.UpdatedAt = timeProvider.GetUtcNow();
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return true;
                }, cancellationToken);
            }
            else
            {
                generation.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return new(generation.Id, generation.Status, indexed, generation.BenchmarkJson, request.Activate, false);
        }
        catch
        {
            generation.Status = SkillSearchGenerationStatus.Failed;
            generation.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private bool PassesHardFilters(SkillVersion version, SkillSearchForExecutionRequest request, string projectId, bool explicitCandidate, out SkillBindingMode bindingMode, out string filterReason)
    {
        bindingMode = SkillBindingMode.Recommended;
        filterReason = string.Empty;
        if (version.Status == SkillLifecycleStatus.Revoked || version.Status == SkillLifecycleStatus.Archived || version.Status == SkillLifecycleStatus.Draft) return false;
        if (version.Status == SkillLifecycleStatus.Deprecated && !request.Policy.AllowDeprecated) return false;
        if (version.Skill!.RiskLevel > request.Policy.MaximumRisk) return false;
        var capabilities = NormalizeValues(request.AvailableCapabilities).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tools = NormalizeValues(request.AvailableTools).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedActions = NormalizeValues(request.AllowedActions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (version.RequiredCapabilities.Any(value => !capabilities.Contains(value)) ||
            version.RequiredTools.Any(value => !tools.Contains(value)) ||
            version.AllowedActions.Any(value => !allowedActions.Contains(value))) return false;
        var matches = version.Skill.Bindings.Where(binding => BindingMatches(binding, request, projectId)).OrderByDescending(binding => BindingPrecedence(binding.Scope)).ToArray();
        if (matches.Any(binding => binding.Mode == SkillBindingMode.Disabled)) return false;
        var strongest = matches.FirstOrDefault();
        if (strongest is not null)
        {
            bindingMode = strongest.Mode;
            if (!SemanticVersionConstraint.IsSatisfied(version.Version, strongest.VersionConstraint)) return false;
        }
        if (request.ExplicitSkillVersionId.HasValue && version.Id != request.ExplicitSkillVersionId.Value) return false;
        if (!request.ExplicitSkillVersionId.HasValue && request.ExplicitSkillId.HasValue && version.SkillId != request.ExplicitSkillId.Value) return false;
        if (explicitCandidate) filterReason = "explicit-request-still-passed-hard-filters";
        return true;
    }

    private static bool BindingMatches(SkillBinding binding, SkillSearchForExecutionRequest request, string projectId)
        => binding.Scope switch
        {
            SkillBindingScope.Tenant => true,
            SkillBindingScope.Project => string.Equals(binding.ScopeValue, projectId, StringComparison.OrdinalIgnoreCase),
            SkillBindingScope.Repository => string.Equals(binding.ScopeValue, NormalizeScopeValue(request.RepositoryId), StringComparison.OrdinalIgnoreCase),
            SkillBindingScope.AgentType => string.Equals(binding.ScopeValue, NormalizeScopeValue(request.AgentType), StringComparison.OrdinalIgnoreCase),
            SkillBindingScope.Execution => string.Equals(binding.ScopeValue, request.ExecutionId.ToString("D"), StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private async Task<bool> HasRequiredBindingWithoutCandidateAsync(SkillSearchForExecutionRequest request, string projectId, IReadOnlyList<RankedCandidate> selected, CancellationToken cancellationToken)
    {
        var selectedSkillIds = selected.Select(item => item.Version.SkillId).ToHashSet();
        var required = await ScopedSkills(includeArchived: false).Include(item => item.Bindings).ToListAsync(cancellationToken);
        return required.Any(skill => skill.Bindings.Any(binding => binding.Mode == SkillBindingMode.Required && BindingMatches(binding, request, projectId)) && !selectedSkillIds.Contains(skill.Id));
    }

    private async Task<IReadOnlyList<ResolvedVersion>> ResolveDependenciesAsync(IReadOnlyList<Guid> selectedVersionIds, int maximumCount, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<Guid, ResolvedVersion>();
        var visiting = new HashSet<Guid>();
        async Task Visit(Guid versionId, bool dependency, int depth)
        {
            if (depth > 8 || resolved.Count >= Math.Min(maximumCount, 32)) throw new InvalidOperationException("Selected Skill dependency closure exceeds the bounded depth/count policy.");
            if (resolved.TryGetValue(versionId, out var current)) { if (!dependency) resolved[versionId] = current with { IsDependency = false }; return; }
            if (!visiting.Add(versionId)) throw new InvalidOperationException("Skill dependency cycle detected.");
            var version = await ScopedVersions(includeArchivedSkills: false).Include(item => item.Skill).Include(item => item.Dependencies).SingleAsync(item => item.Id == versionId, cancellationToken);
            resolved[versionId] = new(version, dependency);
            foreach (var requirement in version.Dependencies.Where(item => item.Kind == SkillDependencyKind.Requires))
            {
                var target = await ResolveDependencyVersionAsync(requirement, cancellationToken)
                    ?? throw new InvalidOperationException($"Required dependency {requirement.TargetSkillId:D} cannot resolve to a compatible Published version.");
                await Visit(target.Id, true, depth + 1);
            }
            visiting.Remove(versionId);
        }
        foreach (var id in selectedVersionIds.Distinct()) await Visit(id, false, 0);
        if (resolved.Count > maximumCount) throw new InvalidOperationException("Selected Skill set exceeds MaxSelectedSkills after dependency resolution.");
        return resolved.Values.ToArray();
    }

    private static void ValidateConflicts(IReadOnlyList<ResolvedVersion> resolved)
    {
        var skillIds = resolved.Select(item => item.Version.SkillId).ToHashSet();
        foreach (var item in resolved)
        {
            var conflict = item.Version.Dependencies.FirstOrDefault(dependency => dependency.Kind == SkillDependencyKind.ConflictsWith && skillIds.Contains(dependency.TargetSkillId));
            if (conflict is not null) throw new InvalidOperationException($"SkillSetConflict: {item.Version.SkillId:D} conflicts with {conflict.TargetSkillId:D}.");
        }
    }

    private async Task<SkillSearchForExecutionResult> BuildSearchResultAsync(SkillResolution resolution, bool replayed, CancellationToken cancellationToken, bool degraded = false, SkillSearchGeneration? knownGeneration = null)
    {
        var generation = knownGeneration ?? await dbContext.SkillSearchGenerations.AsNoTracking().SingleAsync(item => item.Id == resolution.SearchGenerationId, cancellationToken);
        var candidates = resolution.Candidates.Count > 0 ? resolution.Candidates.ToArray() : await dbContext.SkillResolutionCandidates.AsNoTracking().Where(item => item.ResolutionId == resolution.Id).OrderBy(item => item.Rank).ToArrayAsync(cancellationToken);
        var ids = candidates.Select(item => item.SkillVersionId).ToArray();
        var versions = await ScopedVersions(includeArchivedSkills: true).AsNoTracking().Include(item => item.Skill).Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        var byId = versions.ToDictionary(item => item.Id);
        var results = candidates.OrderBy(item => item.Rank).Where(item => byId.ContainsKey(item.SkillVersionId)).Select(item =>
        {
            var version = byId[item.SkillVersionId];
            return new SkillSearchCandidateResult(version.SkillId, version.Id, version.Version, version.Skill!.Name, version.Skill.Description, version.Skill.WhenToUse, item.Score, item.Rank, item.Threshold, Deserialize<string[]>(item.MatchReasonsJson), version.Skill.RiskLevel, version.RequiredCapabilities, version.RequiredTools, version.ContentHash, SkillBindingMode.Recommended, item.MatchReasonsJson.Contains("explicit-request", StringComparison.Ordinal));
        }).ToArray();
        var reason = resolution.Status switch
        {
            SkillResolutionStatus.NoApplicableSkill => "NoApplicableSkill",
            SkillResolutionStatus.RequiresHumanDecision => "RequiredSkillConflict",
            _ => string.Empty
        };
        return new(resolution.Id, resolution.ExecutionId, resolution.Round, resolution.MaxSearchRounds, resolution.Status, results, resolution.QueryHash, generation.Id, generation.SearchProfileVersion, generation.EmbeddingModelId, generation.EmbeddingModelVersion, candidates.FirstOrDefault()?.Threshold ?? generation.Threshold, degraded, replayed, reason);
    }

    private async Task<SkillSelectForExecutionResult> BuildSelectionResultAsync(SkillResolution resolution, bool replayed, CancellationToken cancellationToken)
    {
        var pins = resolution.Pins.Count > 0 ? resolution.Pins.ToArray() : await dbContext.SkillResolutionPins.AsNoTracking().Where(item => item.ResolutionId == resolution.Id && item.ReleasedAt == null).ToArrayAsync(cancellationToken);
        var ids = pins.Select(item => item.SkillVersionId).ToArray();
        var versions = await ScopedVersions(includeArchivedSkills: true).AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        var byId = versions.ToDictionary(item => item.Id);
        return new(resolution.Id, resolution.ExecutionId, pins.Where(item => byId.ContainsKey(item.SkillVersionId)).Select(item =>
        {
            var version = byId[item.SkillVersionId];
            return new SkillPinnedVersionResult(version.SkillId, version.Id, version.Version, item.ContentHash, item.IsDependency);
        }).ToArray(), resolution.Status, replayed);
    }

    private SkillTelemetryEvent CreateTelemetryEvent(SkillVersion version, SkillResolution resolution, SkillTelemetryEventType type, string idempotencyKey, SkillResolutionCandidate? candidate = null)
    {
        var actor = actorAccessor.Current;
        var now = timeProvider.GetUtcNow();
        return new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            SkillId = version.SkillId,
            SkillVersionId = version.Id,
            ExecutionId = resolution.ExecutionId,
            WorkItemId = resolution.WorkItemId,
            ResolutionId = resolution.Id,
            ResolutionRound = resolution.Round,
            EventType = type,
            QueryHash = resolution.QueryHash,
            CandidateRank = candidate?.Rank,
            CandidateScore = candidate?.Score,
            Threshold = candidate?.Threshold,
            ProjectId = resolution.ProjectId,
            RepositoryId = resolution.RepositoryId,
            AgentType = resolution.AgentType,
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            CreatedAt = now
        };
    }

    private static AnalyticsGroupKey AnalyticsKey(TelemetryMetricRow item, SkillAnalyticsDimension dimension) => new(
        item.SkillId,
        dimension == SkillAnalyticsDimension.SkillVersion ? item.SkillVersionId : null,
        dimension is SkillAnalyticsDimension.Project or SkillAnalyticsDimension.Repository or SkillAnalyticsDimension.AgentType ? item.ProjectId : string.Empty,
        dimension is SkillAnalyticsDimension.Repository or SkillAnalyticsDimension.AgentType ? item.RepositoryId : string.Empty,
        dimension == SkillAnalyticsDimension.AgentType ? item.AgentType : string.Empty);

    private static SkillTelemetryAggregateResult BuildAggregate(AnalyticsGroupKey key, IEnumerable<TelemetryMetricRow> events, int windowDays)
    {
        var array = events.ToArray();
        var impressions = array.Where(item => item.EventType == SkillTelemetryEventType.SearchImpression).Sum(item => item.Count);
        var selected = array.Where(item => item.EventType == SkillTelemetryEventType.Selected).Sum(item => item.Count);
        var rejected = array.Where(item => item.EventType is SkillTelemetryEventType.Rejected or SkillTelemetryEventType.SelectedThenReleased).Sum(item => item.Count);
        var invocations = array.Where(item => item.EventType == SkillTelemetryEventType.InvocationStarted).Sum(item => item.Count);
        var successes = array.Where(item => item.EventType == SkillTelemetryEventType.InvocationSucceeded).Sum(item => item.Count);
        var failures = array.Where(item => item.EventType == SkillTelemetryEventType.InvocationFailed).Sum(item => item.Count);
        var matrix = array.Where(item => item.RejectionStage.HasValue || item.ReasonClass.HasValue)
            .GroupBy(item => $"{item.RejectionStage}:{item.ReasonClass}")
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.Ordinal);
        return new(key.SkillId, key.SkillVersionId, impressions, selected, rejected, invocations, successes, failures,
            Rate(selected, impressions), Rate(rejected, impressions), Rate(invocations, selected), Rate(successes, invocations), matrix,
            Latest(array, SkillTelemetryEventType.SearchImpression),
            Latest(array, SkillTelemetryEventType.Selected),
            Latest(array, SkillTelemetryEventType.Rejected, SkillTelemetryEventType.SelectedThenReleased),
            Latest(array, SkillTelemetryEventType.InvocationStarted),
            key.ProjectId,
            key.RepositoryId,
            key.AgentType,
            windowDays);
    }

    private static bool IsRelevanceQualityRejection(SkillTelemetryEvent item)
        => item.EventType is SkillTelemetryEventType.Rejected or SkillTelemetryEventType.SelectedThenReleased &&
           item.RejectionStage != SkillRejectionStage.RevokedOrPolicyCancelled &&
           item.ReasonClass.HasValue && !RuntimeReasons.Contains(item.ReasonClass.Value) &&
           item.ReasonClass is not SkillRejectionReason.PolicyConflict and not SkillRejectionReason.Revoked;

    private static (string SignalType, string RecommendedAction, bool RequiresNewVersion) ClassifyGovernanceSignal(IReadOnlyList<SkillTelemetryEvent> events)
    {
        if (events.Any(item => item.RejectionStage == SkillRejectionStage.PostInvocationRejected && item.ReasonClass is SkillRejectionReason.ResultInvalid or SkillRejectionReason.ResultNotUseful))
            return ("ImplementationQualitySignal", "CreateNewSkillVersionProposal", true);
        if (events.Any(item => item.RejectionStage == SkillRejectionStage.MaterializedRejectedBeforeInvoke && item.ReasonClass == SkillRejectionReason.InstructionMismatch))
            return ("InstructionMismatchSignal", "CreateNewSkillVersionProposal", true);
        if (events.Any(item => item.ReasonClass is SkillRejectionReason.DuplicateCoverage or SkillRejectionReason.SupersededByBetterSkill))
            return ("DuplicateSkillSignal", "CreateDeprecationProposal", false);
        if (events.Any(item => item.ReasonClass == SkillRejectionReason.IncorrectCompatibilityMetadata))
            return ("CompatibilityMismatchSignal", "ProposeCompatibilitySidecarUpdate", false);
        if (events.Any(item => item.ReasonClass is SkillRejectionReason.IncorrectTagOrTrigger or SkillRejectionReason.RepositoryMismatch))
            return ("TagMismatchSignal", "ProposeTagOrWhenToUseSidecarUpdate", false);
        return ("DescriptionOverbreadthSignal", "ProposeDescriptionOrSearchProfileSidecarUpdate", false);
    }

    private static void ValidateSearchRequest(SkillSearchForExecutionRequest request)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        if (!request.Policy.Enabled) throw new InvalidOperationException("Skill discovery is disabled by the execution policy.");
        if (request.Policy.TopN is < 1 or > 50 || request.Policy.MaxSearchRounds is < 1 or > 10 || request.Policy.MaxSelectedSkills is < 1 or > 32)
            throw new InvalidOperationException("Skill discovery bounds are outside the supported policy limits.");
        if (request.Policy.Threshold is < 0 or > 1) throw new InvalidOperationException("Skill search threshold must be between zero and one.");
        if (string.IsNullOrWhiteSpace(request.Objective)) throw new InvalidOperationException("Execution objective is required for Skill discovery.");
    }

    private static void ValidateFeedbackStage(SkillResolution resolution, SkillResolutionFeedbackRequest request)
    {
        var candidate = resolution.Candidates.Any(item => item.SkillVersionId == request.SkillVersionId);
        var pinned = resolution.Pins.Any(item => item.SkillVersionId == request.SkillVersionId && item.ReleasedAt == null);
        if (request.Stage is SkillRejectionStage.SearchCandidateRejected or SkillRejectionStage.SelectionCancelledBeforePin && (!candidate || pinned))
            throw new InvalidOperationException("The rejection stage is invalid because the SkillVersion is not an unpinned candidate.");
        if (request.Stage is SkillRejectionStage.PinnedReleasedBeforeMaterialize or SkillRejectionStage.MaterializedRejectedBeforeInvoke or SkillRejectionStage.InvocationAborted or SkillRejectionStage.PostInvocationRejected or SkillRejectionStage.RevokedOrPolicyCancelled && !pinned)
            throw new InvalidOperationException("The rejection/cancellation stage requires an active pinned SkillVersion.");
    }

    private async Task<int> ResolveNextRoundAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var rounds = dbContext.SkillResolutions.Where(item => item.ExecutionId == executionId);
        if (actor.HasUser) rounds = rounds.Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == actor.UserId);
        return (await rounds.Select(item => (int?)item.Round).MaxAsync(cancellationToken) ?? 0) + 1;
    }

    private static bool IsLifecycleTransitionAllowed(SkillLifecycleStatus current, SkillLifecycleStatus target)
        => (current, target) switch
        {
            (SkillLifecycleStatus.Draft, SkillLifecycleStatus.Archived) => true,
            (SkillLifecycleStatus.Published, SkillLifecycleStatus.Deprecated) => true,
            (SkillLifecycleStatus.Published, SkillLifecycleStatus.Revoked) => true,
            (SkillLifecycleStatus.Deprecated, SkillLifecycleStatus.Published) => true,
            (SkillLifecycleStatus.Deprecated, SkillLifecycleStatus.Revoked) => true,
            (SkillLifecycleStatus.Deprecated, SkillLifecycleStatus.Archived) => true,
            (SkillLifecycleStatus.Revoked, SkillLifecycleStatus.Archived) => true,
            _ => false
        };

    private static SkillImportPreviewRequest BuildPreviewRequest(Skill skill, SkillVersion version, PortableSkillBundle bundle)
        => new(skill.StableKey, skill.Name, skill.Description, skill.WhenToUse, version.Version, bundle, version.SourceKind, version.SourceRef, version.SourceRevision, skill.License, skill.Tags, skill.Aliases, Deserialize<string[]>(skill.MaintainersJson), version.RequiredCapabilities, version.RequiredTools, version.AllowedActions, version.Dependencies.Select(item => new SkillDependencyInput(item.TargetSkillId, item.Kind, item.VersionConstraint)).ToArray(), skill.RiskLevel, version.TrustLevel, version.RequiresNetwork, version.RequiresSecrets, version.SignatureAlgorithm, version.SignatureValue);

    private static SkillSummaryResult ToSummary(Skill skill)
        => new(skill.Id, skill.StableKey, skill.Name, skill.Description, skill.WhenToUse, skill.Tags, skill.Aliases, skill.License, Deserialize<string[]>(skill.MaintainersJson), skill.RiskLevel, skill.MetadataVersion, MetadataHash(skill), skill.DefaultVersionId, skill.Bindings.OrderByDescending(item => BindingPrecedence(item.Scope)).Select(ToBindingResult).ToArray(), skill.Versions.OrderByDescending(item => ParseVersion(item.Version)).Select(ToVersionSummary).ToArray(), skill.CreatedAt, skill.UpdatedAt, skill.ArchivedAt);

    private static SkillVersionSummaryResult ToVersionSummary(SkillVersion version)
        => new(version.Id, version.SkillId, version.Version, version.Status, version.ContentHash, version.RequiredCapabilities, version.RequiredTools, version.AllowedActions, version.SourceKind, version.SourceRef, version.SourceRevision, version.TrustLevel, version.SignatureVerified, version.CreatedAt, version.PublishedAt, version.DeprecatedAt, version.RevokedAt, version.ArchivedAt);

    private static SkillBindingResult ToBindingResult(SkillBinding binding)
        => new(binding.Id, binding.SkillId, binding.Scope, binding.ScopeValue, binding.Mode, binding.VersionConstraint, binding.Revision, binding.UpdatedAt);

    private static string MetadataHash(Skill skill)
        => PortableSkillBundleValidator.ComputeMetadataHash(skill.StableKey, skill.Name, skill.Description, skill.WhenToUse, skill.Tags, skill.Aliases, skill.RiskLevel, skill.MetadataVersion);

    private static string BuildSearchText(Skill skill, string markdown)
        => string.Join('\n', skill.Name, skill.Description, skill.WhenToUse, string.Join(' ', skill.Tags), string.Join(' ', skill.Aliases), markdown.Length <= 4000 ? markdown : markdown[..4000]);

    private static string BuildQueryText(SkillSearchForExecutionRequest request)
        => string.Join('\n', request.Objective.Trim(), string.Join(' ', NormalizeValues(request.Keywords)), string.Join(' ', NormalizeValues(request.Tags)), string.Join(' ', NormalizeValues(request.AvailableCapabilities)), request.RepositoryId, request.AgentType);

    private static string[] NormalizeValues(IReadOnlyList<string>? values)
        => (values ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();

    private static string[] NormalizeAndRedact(IReadOnlyList<string>? values)
        => NormalizeValues(values).Select(value => PortableSkillBundleValidator.BoundAndRedact(value, 500)).ToArray();

    private static string NormalizeConstraint(string? value) => string.IsNullOrWhiteSpace(value) ? "*" : value.Trim();
    private static string NormalizeScopeValue(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static int BindingPrecedence(SkillBindingScope scope) => scope switch { SkillBindingScope.Execution => 50, SkillBindingScope.Repository => 40, SkillBindingScope.Project => 30, SkillBindingScope.AgentType => 20, _ => 10 };
    private static SemanticVersion ParseVersion(string version) => SemanticVersion.TryParse(version, out var parsed) ? parsed : default;
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    private static decimal Rate(int numerator, int denominator) => denominator == 0 ? 0m : decimal.Divide(numerator, denominator);
    private static DateTimeOffset? Latest(IEnumerable<TelemetryMetricRow> events, params SkillTelemetryEventType[] types)
        => events.Where(item => types.Contains(item.EventType)).Select(item => (DateTimeOffset?)item.LastOccurredAt).Max();
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new InvalidOperationException($"Stored {typeof(T).Name} JSON is invalid.");

    private static SkillMetadataProposalResult ToMetadataProposal(SkillMetadataProposal item) => new(
        item.Id, item.SkillId, item.ExpectedMetadataVersion, item.ExpectedMetadataHash,
        item.ProposedPatchJson, item.EvidenceJson, item.Confidence, item.Status,
        item.GovernanceRunId, item.CreatedAt, item.UpdatedAt);

    private static SkillSourceObservationResult ToSourceObservation(SkillSourceObservation item, bool replayed) => new(
        item.Id,
        item.SkillId,
        item.Status,
        item.SourceRef,
        item.ObservedRevision,
        item.ObservedContentHash,
        item.SourceAvailable,
        item.SignatureVerified,
        item.Status == SkillSourceDriftStatus.Changed,
        item.Status is SkillSourceDriftStatus.Compromised or SkillSourceDriftStatus.TrustChanged,
        item.EvidenceJson,
        item.ObservedAt,
        replayed);

    private static SkillTelemetryReconciliationResult ToReconciliationResult(SkillTelemetryReconciliationRun item, bool replayed) => new(
        item.Id,
        item.AggregatedEventCount,
        item.AggregateRowCount,
        item.DeletedRawEventCount,
        item.DeletedAggregateRowCount,
        item.ProtectedRawEventCount,
        item.RawRetentionDays,
        item.AggregateRetentionDays,
        item.CreatedAt,
        replayed);

    private static TelemetryAggregateKey AggregateKey(SkillTelemetryEvent item) => new(
        item.TenantId,
        item.OwnerUserId,
        DateOnly.FromDateTime(item.OccurredAt.UtcDateTime),
        item.SkillId,
        item.SkillVersionId,
        item.ProjectId,
        item.RepositoryId,
        item.AgentType,
        item.EventType,
        item.RejectionStage,
        item.ReasonClass);

    private static string AggregateKey(SkillTelemetryDailyAggregate item) => AggregateKey(new TelemetryAggregateKey(
        item.TenantId,
        item.OwnerUserId,
        item.AggregateDate,
        item.SkillId,
        item.SkillVersionId,
        item.ProjectId,
        item.RepositoryId,
        item.AgentType,
        item.EventType,
        item.RejectionStage,
        item.ReasonClass));

    private static string AggregateKey(TelemetryAggregateKey item) => string.Join("|",
        item.TenantId,
        item.OwnerUserId,
        item.AggregateDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        item.SkillId,
        item.SkillVersionId,
        item.ProjectId,
        item.RepositoryId,
        item.AgentType,
        item.EventType,
        item.RejectionStage,
        item.ReasonClass);

    private static string MergeDecisionEvidence(string evidenceJson, string? note) => Serialize(new
    {
        sourceEvidence = evidenceJson,
        decisionNote = PortableSkillBundleValidator.BoundAndRedact(note, 500)
    });

    private static string MergeActivationEvidence(string benchmarkJson, string reason, string idempotencyKey) => Serialize(new
    {
        previousBenchmark = benchmarkJson,
        activationReason = PortableSkillBundleValidator.BoundAndRedact(reason, 500),
        activationIdempotencyKey = idempotencyKey
    });

    private static void EnsureHash(string actual, string expected)
    {
        if (!string.Equals(actual, expected?.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SkillVersion contentHash mismatch; operation failed closed.");
    }

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length is < 8 or > 200) throw new InvalidOperationException("A stable idempotency key between 8 and 200 characters is required.");
    }

    private sealed record RankedCandidate(SkillVersion Version, decimal Score, IReadOnlyList<string> MatchReasons, SkillBindingMode BindingMode, bool Explicit, string FilterReason);
    private sealed record ResolvedVersion(SkillVersion Version, bool IsDependency);
    private sealed record TelemetryAggregateKey(
        Guid? TenantId,
        Guid? OwnerUserId,
        DateOnly AggregateDate,
        Guid SkillId,
        Guid SkillVersionId,
        string ProjectId,
        string RepositoryId,
        string AgentType,
        SkillTelemetryEventType EventType,
        SkillRejectionStage? RejectionStage,
        SkillRejectionReason? ReasonClass);
    private sealed record TelemetryMetricRow(
        Guid SkillId,
        Guid SkillVersionId,
        string ProjectId,
        string RepositoryId,
        string AgentType,
        SkillTelemetryEventType EventType,
        SkillRejectionStage? RejectionStage,
        SkillRejectionReason? ReasonClass,
        int Count,
        DateTimeOffset LastOccurredAt);
    private sealed record AnalyticsGroupKey(
        Guid SkillId,
        Guid? SkillVersionId,
        string ProjectId,
        string RepositoryId,
        string AgentType);
}
