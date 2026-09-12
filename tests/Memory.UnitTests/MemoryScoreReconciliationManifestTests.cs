using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed partial class MemoryScoreReconciliationManifestTests
{
    [Fact]
    public void Migration_manifest_is_exact_immutable_and_never_normalizes_scores()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var manifest = ManifestRowRegex().Matches(sql)
            .Select(match => new
            {
                Id = Guid.Parse(match.Groups["id"].Value),
                Key = match.Groups["key"].Value.Replace("''", "'", StringComparison.Ordinal),
                Importance = decimal.Parse(match.Groups["importance"].Value, System.Globalization.CultureInfo.InvariantCulture)
            })
            .ToArray();

        manifest.Should().HaveCount(33);
        manifest.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        manifest.Select(row => row.Key).Should().OnlyHaveUniqueItems();
        manifest.Select(row => row.Importance).Distinct().Should().BeEquivalentTo([4m, 7m, 8m, 9m]);

        sql.Should().Contain("evidence_class = 'RequiresHumanDecision'");
        sql.Should().Contain("replacement_memory_id IS NULL");
        sql.Should().Contain("BEFORE UPDATE OR DELETE ON memory_score_reconciliation_runs");
        sql.Should().Contain("BEFORE TRUNCATE ON memory_score_reconciliation_runs");
        sql.Should().Contain("BEFORE UPDATE OR DELETE ON memory_score_reconciliation_quarantine");
        sql.Should().Contain("BEFORE TRUNCATE ON memory_score_reconciliation_quarantine");
        sql.Should().Contain("immutable copy/read-back gate failed");
        sql.Should().Contain("malformed memory score set differs from the exact 33-row reconciliation manifest");
        sql.Should().Contain("LOCK TABLE memory_items IN ACCESS EXCLUSIVE MODE");
        sql.Should().Contain("LOCK TABLE conversation_insights IN ACCESS EXCLUSIVE MODE");
        sql.Should().Contain("3bbcc307-50b9-4f18-ad01-439f166feed3");
        sql.Should().Contain("edf09285-cc6b-482b-8bac-f4568b62728e");
        sql.Should().Contain("e311bfc7-08f8-42e9-84ec-1a97f85258dc");
        sql.Should().Contain("e1eac3fb-b506-418c-9408-61ae68b522d8");
        sql.Should().Contain("789bd766-5623-4137-a566-e8531b6b08af");

        var normalized = Regex.Replace(sql, @"\s+", " ").ToUpperInvariant();
        normalized.Should().NotContain("/ 100");
        normalized.Should().NotContain("/100");
        normalized.Should().NotContain("SET IMPORTANCE =");
        normalized.Should().NotContain("SET CONFIDENCE =");
    }

    [Fact]
    public void Reconciliation_runs_after_authority_epoch_and_before_score_constraints()
    {
        var migrations = typeof(MemoryDbContext).Assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Sql.Migrations.", StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var authority = Array.FindIndex(migrations, name => name.EndsWith(".041_scheduled_governance_authority_epochs.sql", StringComparison.Ordinal));
        var reconciliation = Array.FindIndex(migrations, name => name.EndsWith(".041a_memory_score_reconciliation.sql", StringComparison.Ordinal));
        var constraints = Array.FindIndex(migrations, name => name.EndsWith(".042_memory_score_contract.sql", StringComparison.Ordinal));

        authority.Should().BeGreaterThanOrEqualTo(0);
        reconciliation.Should().BeGreaterThan(authority);
        constraints.Should().BeGreaterThan(reconciliation);
    }

    private static string ReadMigration(string suffix)
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\('(?<id>[0-9a-f-]{36})',\s*'(?<key>(?:''|[^'])+)',\s*(?<importance>[0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestRowRegex();
}
