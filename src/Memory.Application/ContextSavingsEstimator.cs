using Memory.Domain;

namespace Memory.Application;

public static class ContextSavingsEstimator
{
    public const string HighConfidence = "High";
    public const string MediumConfidence = "Medium";
    public const string LowConfidence = "Low";

    public static ContextSavingsEstimateResult Estimate(
        IReadOnlyList<MemorySearchHit> hits,
        WorkingContextResult result)
    {
        var baseline = EstimateBaselineTokens(hits, out var coveragePercent);
        var returned = EstimateReturnedTokens(result);
        var saved = Math.Max(0, baseline - returned);
        var savingPercent = baseline > 0
            ? saved / (double)baseline * 100d
            : 0d;

        return new ContextSavingsEstimateResult(
            baseline,
            returned,
            saved,
            Math.Round(savingPercent, 2),
            ResolveConfidence(coveragePercent),
            Math.Round(coveragePercent, 2));
    }

    public static int EstimateTextTokens(string? text)
        => ChunkingService.ApproximateTokenCount(text ?? string.Empty);

    private static int EstimateBaselineTokens(IReadOnlyList<MemorySearchHit> hits, out double coveragePercent)
    {
        var distinct = hits
            .GroupBy(hit => hit.MemoryId)
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length == 0)
        {
            coveragePercent = 0d;
            return 0;
        }

        var sourceEstimateCount = distinct.Count(hit => hit.SourceTokenEstimate > 0);
        coveragePercent = sourceEstimateCount / (double)distinct.Length * 100d;

        return distinct.Sum(hit => hit.SourceTokenEstimate > 0
            ? hit.SourceTokenEstimate
            : EstimateTextTokens(hit.Excerpt));
    }

    private static int EstimateReturnedTokens(WorkingContextResult result)
    {
        var total = 0;
        total += EstimateSections(result.Facts);
        total += EstimateSections(result.Decisions);
        total += EstimateSections(result.Episodes);
        total += EstimateSections(result.Artifacts);
        total += result.RecentLogs.Sum(log => EstimateTextTokens(log.Message) + EstimateTextTokens(log.Exception));
        total += result.UserPreferences.Sum(preference =>
            EstimateTextTokens(preference.Key) +
            EstimateTextTokens(preference.Content) +
            EstimateTextTokens(preference.Rationale));
        total += result.SuggestedTests.Sum(EstimateTextTokens);
        total += result.Citations.Sum(citation =>
            EstimateTextTokens(citation.SourceRef) +
            EstimateTextTokens(citation.Excerpt));

        return total;
    }

    private static int EstimateSections(IEnumerable<WorkingContextSection> sections)
        => sections.Sum(section =>
            EstimateTextTokens(section.Title) +
            EstimateTextTokens(section.Summary) +
            EstimateTextTokens(section.Excerpt));

    private static string ResolveConfidence(double sourceCoveragePercent)
        => sourceCoveragePercent switch
        {
            >= 80d => HighConfidence,
            >= 50d => MediumConfidence,
            _ => LowConfidence
        };
}
