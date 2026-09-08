using System.Diagnostics;
using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class SkillSearchBenchmarkTests
{
    [Fact]
    public void Hybrid_keyword_when_to_use_ranking_should_retrieve_relevant_skills_from_2500_candidates()
    {
        const int corpusSize = 2_500;
        const int relevantCount = 25;
        const int topN = 10;
        var query = SkillText.Tokenize("postgres migration rollback production schema");
        var corpus = Enumerable.Range(0, corpusSize).Select(index => new
        {
            Id = index,
            Relevant = index < relevantCount,
            Terms = SkillText.Tokenize(index < relevantCount
                ? $"postgres migration rollback production schema database runbook {index}"
                : $"frontend typography color layout illustration asset {index}"),
            When = SkillText.Tokenize(index < relevantCount
                ? "Use for production PostgreSQL schema migration rollback"
                : "Use for visual design review")
        }).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var result = corpus.Select(item => new
        {
            item.Id,
            item.Relevant,
            Score = SkillText.KeywordScore(query, item.Terms) * 0.80m + SkillText.KeywordScore(query, item.When) * 0.20m
        })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Id)
            .Take(topN)
            .ToArray();
        stopwatch.Stop();

        var precisionAtN = decimal.Divide(result.Count(x => x.Relevant), topN);
        var recallAtN = decimal.Divide(result.Count(x => x.Relevant), relevantCount);
        precisionAtN.Should().Be(1m);
        recallAtN.Should().Be(0.4m);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
