using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Memory.UnitTests;

public sealed class SecurityOptionsTests
{
    [Fact]
    public void ContextHub_Security_Should_Require_Authentication_By_Default()
    {
        var options = new ContextHubSecurityOptions();

        options.RequireAuthentication.Should().BeTrue();
    }

    [Fact]
    public void ActorAuthorization_Should_Reject_Missing_Scope()
    {
        var actor = new ContextHubRequestActor(
            IsAuthenticated: true,
            TenantId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            Username: "member",
            Role: TenantUserRole.Member,
            Scopes: [SecurityScopes.MemoryRead],
            AllowedProjectIds: ["ContextHub"],
            IsServiceActor: false);

        var act = () => ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*memory:write*");
    }

    [Fact]
    public void ActorAuthorization_Should_Reject_Disallowed_Project()
    {
        var actor = new ContextHubRequestActor(
            IsAuthenticated: true,
            TenantId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            Username: "member",
            Role: TenantUserRole.Member,
            Scopes: [SecurityScopes.MemoryRead],
            AllowedProjectIds: ["ContextHub"],
            IsServiceActor: false);

        var act = () => ActorAuthorization.EnsureProjectAllowed(actor, "OtherProject", write: false);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*OtherProject*not readable*");
    }

    [Fact]
    public void AesSecretProtector_Should_Require_Configured_Key_In_Production()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestHostEnvironment("Production");

        var act = () => new AesSecretProtector(configuration, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SecretKey*Production*");
    }

    [Fact]
    public void AesSecretProtector_Should_Use_Configured_Key_In_Production()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContextHub:SecretKey"] = "unit-test-secret-key"
            })
            .Build();
        var environment = new TestHostEnvironment("Production");

        var protector = new AesSecretProtector(configuration, environment);
        var protectedText = protector.Protect("secret");

        protector.Unprotect(protectedText).Should().Be("secret");
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Memory.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
