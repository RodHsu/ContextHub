using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ManagedFileSecurityTests
{
    private readonly ManagedFileContentScanner scanner = new();

    [Fact]
    public void Benign_content_is_normal_and_allows_search_projection()
    {
        var assessment = Scan("quarterly public report");

        FileSecurityPolicy.Classify(assessment.Findings).Should().Be(FileClassification.Normal);
        FileSecurityPolicy.Authorize(FileClassification.Normal, FileOperation.SearchIndex, "index", false).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("EICAR-STANDARD-ANTIVIRUS-TEST-FILE", SecurityFindingCategory.Malware)]
    [InlineData("ARCHIVE_DEPTH=10 ARCHIVE_BOMB", SecurityFindingCategory.ArchiveBomb)]
    [InlineData("EMBEDDED_ACTIVE_PAYLOAD", SecurityFindingCategory.EmbeddedPayload)]
    public void Critical_active_payload_fixtures_are_quarantined(string content, SecurityFindingCategory category)
    {
        var assessment = Scan(content);

        assessment.Findings.Should().Contain(x => x.Category == category);
        FileSecurityPolicy.Classify(assessment.Findings).Should().Be(FileClassification.Quarantined);
        FileSecurityPolicy.Authorize(FileClassification.Quarantined, FileOperation.Read, "review", false).Allowed.Should().BeFalse();
        FileSecurityPolicy.Authorize(FileClassification.Quarantined, FileOperation.SecurityRescan, "review", true).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----\nredacted", SecurityFindingCategory.PrivateKey)]
    [InlineData("password=not-a-real-secret", SecurityFindingCategory.CredentialSecret)]
    [InlineData("token=not-a-real-token", SecurityFindingCategory.Token)]
    public void Credential_fixtures_are_restricted_without_retaining_raw_evidence(string content, SecurityFindingCategory category)
    {
        var assessment = Scan(content);

        assessment.Findings.Should().Contain(x => x.Category == category);
        assessment.Findings.Should().OnlyContain(x => !x.RedactedEvidence.Contains("not-a-real", StringComparison.Ordinal));
        FileSecurityPolicy.Classify(assessment.Findings).Should().Be(FileClassification.Restricted);
        FileSecurityPolicy.Authorize(FileClassification.Restricted, FileOperation.Embed, "index", true).Allowed.Should().BeFalse();
        FileSecurityPolicy.Authorize(FileClassification.Restricted, FileOperation.Download, string.Empty, true).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Low_scope_pii_is_sensitive_and_high_volume_pii_is_restricted()
    {
        FileSecurityPolicy.Classify(Scan("person@example.test").Findings).Should().Be(FileClassification.Sensitive);
        FileSecurityPolicy.Classify(Scan("a@e.test b@e.test c@e.test d@e.test e@e.test").Findings).Should().Be(FileClassification.Restricted);
    }

    [Fact]
    public void Macro_reputation_changes_severity_without_quarantining_every_macro()
    {
        var signed = Scan("SIGNED_MACRO");
        var unsigned = Scan("UNSIGNED_MACRO");

        signed.Findings.Single(x => x.Category == SecurityFindingCategory.Macro).Severity.Should().Be(SecurityFindingSeverity.Medium);
        unsigned.Findings.Single(x => x.Category == SecurityFindingCategory.Macro).Severity.Should().Be(SecurityFindingSeverity.High);
        FileSecurityPolicy.Classify(signed.Findings).Should().Be(FileClassification.Sensitive);
        FileSecurityPolicy.Classify(unsigned.Findings).Should().Be(FileClassification.Sensitive);
    }

    [Fact]
    public void Content_type_spoof_and_scanner_failures_fail_closed()
    {
        var spoof = scanner.Scan([(byte)'M', (byte)'Z', 0, 0], "text/plain", "scanner-1", "policy-1");

        FileSecurityPolicy.Classify(spoof.Findings).Should().Be(FileClassification.Restricted);
        FileSecurityPolicy.Classify([], scannerAvailable: false, parserSupported: true).Should().Be(FileClassification.Restricted);
        FileSecurityPolicy.Classify([], scannerAvailable: true, parserSupported: false).Should().Be(FileClassification.Restricted);
    }

    [Fact]
    public void Sensitive_auto_share_is_disabled_and_restricted_processing_requires_controlled_authority()
    {
        FileSecurityPolicy.Authorize(FileClassification.Sensitive, FileOperation.AutoShare, "policy", false).Allowed.Should().BeFalse();
        FileSecurityPolicy.Authorize(FileClassification.Restricted, FileOperation.Ocr, "controlled", false).Allowed.Should().BeFalse();
        FileSecurityPolicy.Authorize(FileClassification.Restricted, FileOperation.Ocr, "controlled", true).Allowed.Should().BeTrue();
    }

    private FileSecurityAssessment Scan(string content)
        => scanner.Scan(System.Text.Encoding.UTF8.GetBytes(content), "text/plain", "scanner-1", "policy-1");
}
