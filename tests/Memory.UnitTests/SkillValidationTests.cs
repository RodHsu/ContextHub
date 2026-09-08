using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class SkillValidationTests
{
    [Fact]
    public void Portable_bundle_should_be_canonical_safe_and_self_testable()
    {
        var request = ValidRequest([
            File("SKILL.md", "---\nname: Incident helper\ndescription: Reviews bounded evidence\n---\n# Instructions"),
            File("references/policy.md", "Use least privilege."),
            File(".contexthub/self-test.json", "{\"requiredFiles\":[\"SKILL.md\",\"references/policy.md\"]}")
        ]);

        var result = PortableSkillBundleValidator.Validate(request);

        result.CanImport.Should().BeTrue();
        result.Validation.SelfTestExecuted.Should().BeTrue();
        result.Validation.SelfTestPassed.Should().BeTrue();
        result.ContentHash.Should().MatchRegex("^[a-f0-9]{64}$");
        PortableSkillBundleValidator.Validate(request).ContentHash.Should().Be(result.ContentHash);
    }

    [Theory]
    [InlineData("../secret.txt", "UnsafePath")]
    [InlineData("identity.pem", "ForbiddenSecretFile")]
    public void Portable_bundle_should_fail_closed_for_unsafe_supply_chain_content(string path, string expectedCode)
    {
        var result = PortableSkillBundleValidator.Validate(ValidRequest([
            File("SKILL.md", "---\nname: Safe\ndescription: Safe description\n---\n"),
            File(path, "-----BEGIN PRIVATE KEY-----")
        ]));

        result.CanImport.Should().BeFalse();
        result.Validation.Issues.Select(x => x.Code).Should().Contain(expectedCode);
    }

    [Fact]
    public void Semantic_version_constraints_should_support_pinned_ranges()
    {
        SemanticVersion.TryParse("2.4.1", out var version).Should().BeTrue();
        SemanticVersionConstraint.IsSatisfied("2.4.1", ">=2.0.0 <3.0.0").Should().BeTrue();
        SemanticVersionConstraint.IsSatisfied("2.4.1", "^2.3.0").Should().BeTrue();
        SemanticVersionConstraint.IsSatisfied("2.4.1", "~2.3.0").Should().BeFalse();
        SemanticVersionConstraint.IsSatisfied("2.4.1", "=2.4.0").Should().BeFalse();
    }

    [Fact]
    public void Telemetry_reason_text_should_be_bounded_and_redacted()
    {
        var input = "token=abcdefghijklmnopqrstuvwxyz0123456789 " + new string('x', 2_000);
        var result = PortableSkillBundleValidator.BoundAndRedact(input);

        result.Should().NotContain("abcdefghijklmnopqrstuvwxyz0123456789");
        result.Length.Should().BeLessThanOrEqualTo(PortableSkillBundleValidator.MaximumReasonTextLength);
    }

    private static SkillImportPreviewRequest ValidRequest(IReadOnlyList<PortableSkillFile> files) => new(
        "incident-helper", "Incident helper", "Reviews bounded evidence", "Use for incident review", "1.2.3",
        new PortableSkillBundle(files), SkillSourceKind.LocalUpload, "https://example.test/skills/incident-helper", "abc123", "MIT",
        ["incident", "review"], ["triage-helper"], ["security-team"], ["filesystem:read"], ["rg"], ["read"],
        RiskLevel: SkillRiskLevel.Low, TrustLevel: SkillTrustLevel.SourceVerified);

    private static PortableSkillFile File(string path, string content, bool executable = false)
        => new(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), executable);
}
