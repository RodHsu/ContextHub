using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class SecretSecurityTests
{
    [Fact]
    public void Aal2_and_aal3_operations_must_not_downgrade_to_password_aal1()
    {
        StepUpRiskPolicy.Describe(StepUpOperationClass.SecretCreate).Should().BeEquivalentTo(
            new StepUpAuthorizationResult(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal1, "FreshPasswordRequired"));
        StepUpRiskPolicy.Describe(StepUpOperationClass.CredentialRotate).Should().BeEquivalentTo(
            new StepUpAuthorizationResult(StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable"));
        StepUpRiskPolicy.Describe(StepUpOperationClass.SecretExport).Should().BeEquivalentTo(
            new StepUpAuthorizationResult(StepUpRequirementOutcome.Disabled, AssuranceLevel.Aal3, "Aal3Unavailable"));
    }

    [Fact]
    public void Ssh_signer_must_sign_only_the_canonical_bound_authentication_payload()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var challenge = RandomNumberGenerator.GetBytes(32);
        var payload = SecretTargetBinding.BuildSshAuthenticationPayload("session-1", "host.example.test", 22, "deploy", challenge);
        try
        {
            var signature = new RsaSshBoundSigner().SignAuthenticationPayload(privateKey, payload);
            rsa.VerifyData(payload, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1).Should().BeTrue();
            var foreignPayload = SecretTargetBinding.BuildSshAuthenticationPayload("session-1", "other.example.test", 22, "deploy", challenge);
            rsa.VerifyData(foreignPayload, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1).Should().BeFalse();
            CryptographicOperations.ZeroMemory(foreignPayload);
            CryptographicOperations.ZeroMemory(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(challenge);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Fact]
    public void Secret_external_contracts_must_not_contain_plaintext_or_key_material_fields()
    {
        var externalTypes = new[]
        {
            typeof(SecretSummary), typeof(SecretVersionResult), typeof(SecretLeaseResult),
            typeof(SecretUseResult), typeof(SshCertificateLeaseResult), typeof(StepUpAssertionResult)
        };
        var names = externalTypes.SelectMany(x => x.GetProperties()).Select(x => x.Name.ToLowerInvariant()).ToArray();
        names.Should().NotContain(x => x.Contains("plaintext", StringComparison.Ordinal) || x.Contains("password", StringComparison.Ordinal) ||
            x.Contains("wrappeddek", StringComparison.Ordinal) || x.Contains("privatekey", StringComparison.Ordinal) || x.Contains("kek", StringComparison.Ordinal));
    }

    [Fact]
    public void Secret_key_authority_must_fail_closed_without_a_secure_host_key_file()
    {
        var authority = new SecureFileSecretEnvelopeKeyAuthority(Options.Create(new SecretKeyAuthorityOptions
        {
            CurrentKeyId = "missing",
            KeyFiles = { ["missing"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        }));
        var action = () => authority.Wrap(Guid.NewGuid(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        action.Should().Throw<CryptographicException>().WithMessage("*unavailable*");
    }

    [Fact]
    public void Secret_key_authority_must_reject_relative_key_paths_before_file_access()
    {
        var authority = new SecureFileSecretEnvelopeKeyAuthority(Options.Create(new SecretKeyAuthorityOptions
        {
            CurrentKeyId = "v1",
            KeyFiles = { ["v1"] = "relative-secret-kek" }
        }));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var action = () => authority.Wrap(Guid.NewGuid(), Guid.NewGuid(), key);
            action.Should().Throw<CryptographicException>().WithMessage("*absolute*");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Theory]
    [InlineData("ssh://host.example.test:22/deploy", "host.example.test", 22, "deploy")]
    [InlineData("ssh://[2001:db8::1]:2222/root", "[2001:db8::1]", 2222, "root")]
    public void Ssh_target_binding_is_deterministic(string expected, string host, int port, string user)
        => SecretTargetBinding.Ssh(host, port, user).Should().Be(expected);

    [Fact]
    public void Ssh_target_binding_rejects_delimiter_injection()
    {
        var action = () => SecretTargetBinding.BuildSshAuthenticationPayload("session|forged", "host.example.test", 22, "deploy", new byte[32]);
        action.Should().Throw<ArgumentException>();
    }
}
