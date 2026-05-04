using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class EvaluationService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IEmbeddingProvider embeddingProvider,
    IClock clock) : IEvaluationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EvaluationSuiteResult>> ListSuitesAsync(string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        var suites = await dbContext.EvaluationSuites
            .AsNoTracking()
            .Include(x => x.Cases)
            .Where(x => x.ProjectId == normalizedProjectId)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);
        return suites.Select(MapSuite).ToArray();
    }

    public async Task<EvaluationSuiteResult> CreateSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken)
    {
        var suiteName = NormalizeRequiredValue(request.Name, "Evaluation suite name");
        var suiteDescription = request.Description.Trim();

        if (request.Cases.Count == 0)
        {
            throw new InvalidOperationException("Evaluation suite must include at least one case.");
        }

        var normalizedCases = request.Cases
            .Select((draft, index) => NormalizeCaseDraft(draft, index))
            .ToArray();

        var suite = new EvaluationSuite
        {
            ProjectId = ProjectContext.Normalize(request.ProjectId),
            Name = suiteName,
            Description = suiteDescription,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        await dbContext.EvaluationSuites.AddAsync(suite, cancellationToken);

        foreach (var draft in normalizedCases)
        {
            await dbContext.EvaluationCases.AddAsync(new EvaluationCase
            {
                SuiteId = suite.Id,
                ProjectId = suite.ProjectId,
                ScenarioLabel = draft.ScenarioLabel,
                Query = draft.Query,
                ExpectedMemoryIds = draft.ExpectedMemoryIds.Select(id => id.ToString("D")).ToArray(),
                ExpectedExternalKeys = draft.ExpectedExternalKeys.ToArray(),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            }, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var created = await dbContext.EvaluationSuites
            .AsNoTracking()
            .Include(x => x.Cases)
            .FirstAsync(x => x.Id == suite.Id, cancellationToken);
        return MapSuite(created);
    }

    public async Task<EvaluationRunResult> RunAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        var suite = await dbContext.EvaluationSuites
            .Include(x => x.Cases)
            .FirstOrDefaultAsync(x => x.Id == request.SuiteId, cancellationToken)
            ?? throw new InvalidOperationException($"Evaluation suite '{request.SuiteId}' was not found.");
        if (suite.Cases.Count == 0)
        {
            throw new InvalidOperationException("Evaluation suite does not contain any cases.");
        }

        foreach (var evaluationCase in suite.Cases)
        {
            ValidateStoredCase(evaluationCase);
        }

        var run = new EvaluationRun
        {
            SuiteId = suite.Id,
            ProjectId = suite.ProjectId,
            Status = EvaluationRunStatus.Running,
            EmbeddingProfile = string.IsNullOrWhiteSpace(request.EmbeddingProfile) ? embeddingProvider.EmbeddingProfile : request.EmbeddingProfile.Trim(),
            QueryMode = request.QueryMode.ToString(),
            UseSummaryLayer = request.UseSummaryLayer,
            TopK = Math.Clamp(request.TopK, 1, 20),
            CreatedAt = clock.UtcNow,
            StartedAt = clock.UtcNow
        };
        await dbContext.EvaluationRuns.AddAsync(run, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var items = new List<EvaluationRunItem>(suite.Cases.Count);
            decimal hitSum = 0m;
            decimal recallSum = 0m;
            decimal reciprocalRankSum = 0m;
            double latencySum = 0d;

            foreach (var evaluationCase in suite.Cases.OrderBy(x => x.CreatedAt))
            {
                var expectedMemoryIds = evaluationCase.ExpectedMemoryIds
                    .Select(ParseGuidOrEmpty)
                    .Where(id => id != Guid.Empty)
                    .ToHashSet();
                if (expectedMemoryIds.Count == 0 && evaluationCase.ExpectedExternalKeys.Length > 0)
                {
                    var resolvedIds = await dbContext.MemoryItems
                        .AsNoTracking()
                        .Where(x => x.ProjectId == suite.ProjectId)
                        .Where(x => evaluationCase.ExpectedExternalKeys.Contains(x.ExternalKey))
                        .Select(x => x.Id)
                        .ToListAsync(cancellationToken);
                    foreach (var id in resolvedIds)
                    {
                        expectedMemoryIds.Add(id);
                    }
                }

                var stopwatch = Stopwatch.StartNew();
                var hits = await memoryService.SearchAsync(
                    new MemorySearchRequest(
                        evaluationCase.Query,
                        run.TopK,
                        false,
                        suite.ProjectId,
                        null,
                        request.QueryMode,
                        request.UseSummaryLayer,
                        new RetrievalTelemetryContext("evaluation.search", "evaluation", "evaluation run")),
                    cancellationToken);
                stopwatch.Stop();

                var reciprocalRank = 0m;
                var hitAtK = false;
                for (var index = 0; index < hits.Count; index++)
                {
                    if (!expectedMemoryIds.Contains(hits[index].MemoryId))
                    {
                        continue;
                    }

                    hitAtK = true;
                    reciprocalRank = 1m / (index + 1);
                    break;
                }

                var matchedCount = hits.Count(hit => expectedMemoryIds.Contains(hit.MemoryId));
                var recall = expectedMemoryIds.Count == 0 ? 0m : matchedCount / (decimal)expectedMemoryIds.Count;
                hitSum += hitAtK ? 1m : 0m;
                recallSum += recall;
                reciprocalRankSum += reciprocalRank;
                latencySum += stopwatch.Elapsed.TotalMilliseconds;

                var hitIds = hits.Select(x => x.MemoryId).ToArray();
                var hitExternalKeys = await dbContext.MemoryItems
                    .AsNoTracking()
                    .Where(x => hitIds.Contains(x.Id))
                    .OrderByDescending(x => x.UpdatedAt)
                    .Select(x => x.ExternalKey)
                    .ToListAsync(cancellationToken);

                items.Add(new EvaluationRunItem
                {
                    RunId = run.Id,
                    CaseId = evaluationCase.Id,
                    Query = evaluationCase.Query,
                    ScenarioLabel = evaluationCase.ScenarioLabel,
                    ExpectedMemoryIds = expectedMemoryIds.Select(id => id.ToString("D")).ToArray(),
                    ExpectedExternalKeys = evaluationCase.ExpectedExternalKeys,
                    HitMemoryIds = hitIds.Select(id => id.ToString("D")).ToArray(),
                    HitExternalKeys = hitExternalKeys.ToArray(),
                    HitAtK = hitAtK,
                    RecallAtK = recall,
                    ReciprocalRank = reciprocalRank,
                    LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                    CreatedAt = clock.UtcNow
                });
            }

            await dbContext.EvaluationRunItems.AddRangeAsync(items, cancellationToken);
            run.Status = EvaluationRunStatus.Completed;
            run.HitRate = items.Count == 0 ? 0m : hitSum / items.Count;
            run.RecallAtK = items.Count == 0 ? 0m : recallSum / items.Count;
            run.MeanReciprocalRank = items.Count == 0 ? 0m : reciprocalRankSum / items.Count;
            run.AverageLatencyMs = items.Count == 0 ? 0d : latencySum / items.Count;
            run.CompletedAt = clock.UtcNow;
            suite.UpdatedAt = clock.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken);
            await MaybeCreateRegressionActionAsync(suite, run, cancellationToken);
            return await GetRunRequiredAsync(run.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            run.Status = EvaluationRunStatus.Failed;
            run.Error = ex.Message;
            run.CompletedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<EvaluationRunResult?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await dbContext.EvaluationRuns
            .AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return run is null ? null : MapRun(run);
    }

    public async Task<EvaluationRunResult?> GetLatestRunAsync(string projectId, CancellationToken cancellationToken)
    {
        var run = await dbContext.EvaluationRuns
            .AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.ProjectId == ProjectContext.Normalize(projectId))
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : MapRun(run);
    }

    private async Task MaybeCreateRegressionActionAsync(EvaluationSuite suite, EvaluationRun currentRun, CancellationToken cancellationToken)
    {
        var previous = await dbContext.EvaluationRuns
            .AsNoTracking()
            .Where(x => x.SuiteId == suite.Id && x.Id != currentRun.Id && x.Status == EvaluationRunStatus.Completed)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (previous is null)
        {
            return;
        }

        var degraded =
            previous.HitRate - currentRun.HitRate >= 0.2m ||
            previous.RecallAtK - currentRun.RecallAtK >= 0.15m ||
            previous.MeanReciprocalRank - currentRun.MeanReciprocalRank >= 0.15m;
        if (!degraded)
        {
            return;
        }

        var dedupKey = $"evaluation-regression:{suite.ProjectId}:{suite.Id}:{currentRun.Id}";
        var exists = await dbContext.SuggestedActions.AnyAsync(
            x => x.ProjectId == suite.ProjectId &&
                 x.Status == SuggestedActionStatus.Pending &&
                 x.PayloadJson.Contains(dedupKey),
            cancellationToken);
        if (exists)
        {
            return;
        }

        await dbContext.SuggestedActions.AddAsync(new SuggestedAction
        {
            ProjectId = suite.ProjectId,
            Type = SuggestedActionType.ReindexProject,
            Status = SuggestedActionStatus.Pending,
            Title = $"評測品質回退：{suite.Name}",
            Summary = $"最新評測相較前一次 baseline 明顯退步，建議先重新索引專案 '{suite.ProjectId}'。",
            PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dedupKey"] = dedupKey,
                ["projectId"] = suite.ProjectId,
                ["suiteId"] = suite.Id,
                ["runId"] = currentRun.Id
            }, JsonOptions),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        }, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<EvaluationRunResult> GetRunRequiredAsync(Guid id, CancellationToken cancellationToken)
        => await GetRunAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Evaluation run '{id}' was not found.");

    private static Guid ParseGuidOrEmpty(string value)
        => Guid.TryParse(value, out var id) ? id : Guid.Empty;

    private static NormalizedEvaluationCaseDraft NormalizeCaseDraft(EvaluateCaseUpsertRequest draft, int index)
    {
        var caseNumber = index + 1;
        return new NormalizedEvaluationCaseDraft(
            NormalizeRequiredValue(draft.ScenarioLabel, $"Evaluation case #{caseNumber} scenario label"),
            NormalizeRequiredValue(draft.Query, $"Evaluation case #{caseNumber} query"),
            (draft.ExpectedMemoryIds ?? [])
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray(),
            (draft.ExpectedExternalKeys ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static void ValidateStoredCase(EvaluationCase evaluationCase)
    {
        _ = NormalizeRequiredValue(
            evaluationCase.ScenarioLabel,
            $"Evaluation case '{evaluationCase.Id}' scenario label");
        _ = NormalizeRequiredValue(
            evaluationCase.Query,
            $"Evaluation case '{evaluationCase.Id}' query");
    }

    private static string NormalizeRequiredValue(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return normalized;
    }

    private static EvaluationSuiteResult MapSuite(EvaluationSuite suite)
        => new(
            suite.Id,
            suite.ProjectId,
            suite.Name,
            suite.Description,
            suite.CreatedAt,
            suite.UpdatedAt,
            suite.Cases
                .OrderBy(x => x.CreatedAt)
                .Select(caseItem => new EvaluationCaseResult(
                    caseItem.Id,
                    caseItem.SuiteId,
                    caseItem.ProjectId,
                    caseItem.ScenarioLabel,
                    caseItem.Query,
                    caseItem.ExpectedMemoryIds.Select(ParseGuidOrEmpty).Where(id => id != Guid.Empty).ToArray(),
                    caseItem.ExpectedExternalKeys,
                    caseItem.CreatedAt,
                    caseItem.UpdatedAt))
                .ToArray());

    private static EvaluationRunResult MapRun(EvaluationRun run)
        => new(
            run.Id,
            run.SuiteId,
            run.ProjectId,
            run.Status,
            run.EmbeddingProfile,
            Enum.TryParse<MemoryQueryMode>(run.QueryMode, true, out var queryMode) ? queryMode : MemoryQueryMode.CurrentOnly,
            run.UseSummaryLayer,
            run.TopK,
            run.HitRate,
            run.RecallAtK,
            run.MeanReciprocalRank,
            run.AverageLatencyMs,
            run.Error,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.Items
                .OrderBy(x => x.CreatedAt)
                .Select(item => new EvaluationRunItemResult(
                    item.Id,
                    item.RunId,
                    item.CaseId,
                    item.Query,
                    item.ScenarioLabel,
                    item.ExpectedMemoryIds.Select(ParseGuidOrEmpty).Where(id => id != Guid.Empty).ToArray(),
                    item.ExpectedExternalKeys,
                    item.HitMemoryIds.Select(ParseGuidOrEmpty).Where(id => id != Guid.Empty).ToArray(),
                    item.HitExternalKeys,
                    item.HitAtK,
                    item.RecallAtK,
                    item.ReciprocalRank,
                    item.LatencyMs,
                    item.CreatedAt))
                .ToArray());

    private sealed record NormalizedEvaluationCaseDraft(
        string ScenarioLabel,
        string Query,
        IReadOnlyList<Guid> ExpectedMemoryIds,
        IReadOnlyList<string> ExpectedExternalKeys);
}
