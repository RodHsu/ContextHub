using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.McpServer;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.ApiContractTests;

public sealed class MemoryEditingEndpointTests
{
    private static readonly Guid MemoryId = Guid.Parse("936a7f4d-b578-429d-a087-3ea8c8dfb78c");
    private const string ProjectId = "ContextHub";
    private const string RequestId = "synthetic edit identity";
    private const string PrivateText = "synthetic private edit body";
    private static string EditPath => $"/api/memories/{MemoryId:D}/edits";
    private static MemoryEditCommand Command => new(ProjectId, MemoryId, 8, RequestId,
        new(Title: "Synthetic title", Content: PrivateText, Tags: ["one", "two"], MetadataJson: """{"source":"fixture"}"""));
    private static MemoryEditRecoveryRequest Recovery => new(ProjectId, MemoryId, RequestId, MemoryEditContract.Hash(Command));

    [Fact]
    public async Task Apply_binds_exact_intent_and_returns_only_no_store_receipt_metadata()
    {
        var fixture = new EditingFixture();
        await using var app = await HostAsync(fixture);
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync(EditPath, Command);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.ApplyCommand.Should().BeEquivalentTo(Command);
        fixture.ApplyCalls.Should().Be(1);
        fixture.RecoveryCalls.Should().Be(0);

        var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        responseBody.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "receiptId", "projectId", "memoryId", "requestId", "requestHash", "hashVersion",
            "previousVersion", "version", "revisionId", "committedAt", "replayed");
        responseBody.GetRawText().Should().NotContain(PrivateText);
        responseBody.Deserialize<MemoryEditReceipt>(JsonSerializerOptions.Web).Should().Be(fixture.Receipt);
    }

    [Fact]
    public async Task Recovery_posts_the_original_fingerprint_and_uses_no_edit_operation()
    {
        var fixture = new EditingFixture();
        await using var app = await HostAsync(fixture, write: false);
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync(EditPath + "/receipt", new
        {
            projectId = ProjectId,
            memoryId = MemoryId,
            requestId = RequestId,
            expectedRequestHash = Recovery.ExpectedRequestHash
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.RequestMessage!.RequestUri!.Query.Should().BeEmpty();
        fixture.RecoveryRequest.Should().Be(Recovery);
        fixture.RecoveryCalls.Should().Be(1);
        fixture.ApplyCalls.Should().Be(0);
        (await response.Content.ReadFromJsonAsync<MemoryEditReceipt>()).Should().Be(fixture.Receipt);

        fixture.ReceiptMissing = true;
        using var missing = await client.PostAsJsonAsync(EditPath + "/receipt", Recovery);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await missing.Content.ReadAsStringAsync()).Should().BeEmpty();
        fixture.ApplyCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route_and_body_must_identify_the_same_nonempty_memory(bool receipt)
    {
        var fixture = new EditingFixture();
        await using var app = await HostAsync(fixture);
        using var client = Client(app);
        var otherMemoryId = Guid.NewGuid();
        using var mismatch = receipt
            ? await client.PostAsJsonAsync(EditPath + "/receipt", Recovery with { MemoryId = otherMemoryId })
            : await client.PostAsJsonAsync(EditPath, Command with { MemoryId = otherMemoryId });

        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        mismatch.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("TargetMismatch");
        fixture.ApplyCalls.Should().Be(0);
        fixture.RecoveryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, true, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(false, false, true, true, HttpStatusCode.Forbidden)]
    [InlineData(false, true, false, true, HttpStatusCode.Forbidden)]
    [InlineData(true, false, true, true, HttpStatusCode.Forbidden)]
    public async Task Authentication_and_route_scopes_run_before_the_use_case(
        bool receipt, bool read, bool write, bool authenticated, HttpStatusCode expected)
    {
        var fixture = new EditingFixture();
        await using var app = await HostAsync(fixture, read, write);
        using var client = Client(app, authenticated);
        using var response = receipt
            ? await client.PostAsJsonAsync(EditPath + "/receipt", Recovery)
            : await client.PostAsJsonAsync(EditPath, Command);

        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.ApplyCalls.Should().Be(0);
        fixture.RecoveryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, SafeEditConflictCodes.MissingReadVersion, null, null)]
    [InlineData(false, SafeEditConflictCodes.StaleReadVersion, 11L, 11L)]
    [InlineData(false, SafeEditConflictCodes.RequestIdentityMismatch, 11L, null)]
    [InlineData(true, SafeEditConflictCodes.RequestIdentityMismatch, 11L, null)]
    public async Task Known_conflicts_return_typed_409_without_unnecessary_version_information(
        bool receipt, string code, long? storeVersion, long? visibleVersion)
    {
        var fixture = new EditingFixture { Failure = new SafeEditConflictException(code, storeVersion) };
        await using var app = await HostAsync(fixture);
        using var client = Client(app);
        using var response = receipt
            ? await client.PostAsJsonAsync(EditPath + "/receipt", Recovery)
            : await client.PostAsJsonAsync(EditPath, Command);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("code", "currentVersion");
        body.GetProperty("code").GetString().Should().Be(code);
        var version = body.GetProperty("currentVersion");
        if (visibleVersion.HasValue) version.GetInt64().Should().Be(visibleVersion.Value);
        else version.ValueKind.Should().Be(JsonValueKind.Null);
        fixture.ApplyCalls.Should().Be(receipt ? 0 : 1);
        fixture.RecoveryCalls.Should().Be(receipt ? 1 : 0);
    }

    [Theory]
    [InlineData(false, "forbidden", HttpStatusCode.NotFound)]
    [InlineData(true, "forbidden", HttpStatusCode.NotFound)]
    [InlineData(false, "missing", HttpStatusCode.NotFound)]
    [InlineData(true, "missing", HttpStatusCode.NotFound)]
    [InlineData(false, "invalid", HttpStatusCode.BadRequest)]
    [InlineData(true, "invalid", HttpStatusCode.BadRequest)]
    [InlineData(false, "score", HttpStatusCode.BadRequest)]
    [InlineData(false, "runtime", HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, "runtime", HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, "unrecognized-conflict", HttpStatusCode.ServiceUnavailable)]
    public async Task Private_failures_have_safe_metadata_and_never_replay(
        bool receipt, string cause, HttpStatusCode expected)
    {
        var fixture = new EditingFixture { Failure = Failure(cause) };
        await using var app = await HostAsync(fixture);
        using var client = Client(app);
        using var response = receipt
            ? await client.PostAsJsonAsync(EditPath + "/receipt", Recovery)
            : await client.PostAsJsonAsync(EditPath, Command);

        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain(PrivateText).And.NotContain(RequestId).And.NotContain(Recovery.ExpectedRequestHash)
            .And.NotContain(MemoryId.ToString("D")).And.NotContain("Exception");
        if (expected == HttpStatusCode.NotFound) payload.Should().BeEmpty();
        if (expected == HttpStatusCode.BadRequest) payload.Should().Contain("InvalidEditIntent");
        if (expected == HttpStatusCode.ServiceUnavailable)
        {
            using var document = JsonDocument.Parse(payload);
            document.RootElement.GetProperty("code").GetString().Should().Be("RequestOutcomeUnknown");
            document.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
            response.Headers.RetryAfter.Should().BeNull();
        }
        fixture.ApplyCalls.Should().Be(receipt ? 0 : 1);
        fixture.RecoveryCalls.Should().Be(receipt ? 1 : 0);
    }

    private static Exception Failure(string cause) => cause switch
    {
        "forbidden" => new UnauthorizedAccessException(PrivateText),
        "missing" => new KeyNotFoundException(PrivateText),
        "invalid" => new ArgumentException(PrivateText),
        "score" => ScoreFailure(),
        "unrecognized-conflict" => new SafeEditConflictException(PrivateText, 99),
        _ => new InvalidOperationException(PrivateText)
    };

    private static MemoryScoreValidationException ScoreFailure()
    {
        try { MemoryScoreContract.Validate(1000m, null); }
        catch (MemoryScoreValidationException failure) { return failure; }
        throw new InvalidOperationException("Expected a fixture score failure.");
    }

    private static async Task<WebApplication> HostAsync(EditingFixture fixture, bool read = true, bool write = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMemoryEditingService>(fixture);
        builder.Services.AddSingleton<IRequestActorAccessor>(new FixtureActor(read, write));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("synthetic", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.Use(CloudflareCacheHeaders.ApplyNoStorePolicyAsync);
        app.UseAuthentication();
        app.UseAuthorization();
        var memories = app.MapGroup("/api/memories");
        memories.RequireAuthIfEnabled(true);
        memories.RequireScopeIfEnabled(true, SecurityScopes.MemoryRead);
        memories.MapMemoryEditingEndpoints(true);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, bool authenticated = true)
    {
        var client = app.GetTestClient();
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-Authentication", "synthetic");
        return client;
    }

    private sealed class FixtureActor(bool read, bool write) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            (read ? new[] { SecurityScopes.MemoryRead } : []).Concat(write ? [SecurityScopes.MemoryWrite] : []).ToArray(), [ProjectId], true);
    }

    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.ContainsKey("X-Test-Authentication")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class EditingFixture : IMemoryEditingService
    {
        public MemoryEditReceipt Receipt { get; } = new(Guid.NewGuid(), ProjectId, MemoryId, RequestId, Recovery.ExpectedRequestHash,
            MemoryEditContract.IntentHashVersion, 8, 9, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        public Exception? Failure { get; init; }
        public bool ReceiptMissing { get; set; }
        public int ApplyCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public MemoryEditCommand? ApplyCommand { get; private set; }
        public MemoryEditRecoveryRequest? RecoveryRequest { get; private set; }

        public Task<MemoryEditReceipt> ApplyAsync(MemoryEditCommand command, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            ApplyCommand = command;
            return Failure is not null ? Task.FromException<MemoryEditReceipt>(Failure) : Task.FromResult(Receipt);
        }

        public Task<MemoryEditReceipt?> ReadReceiptAsync(MemoryEditRecoveryRequest request, CancellationToken cancellationToken)
        {
            RecoveryCalls++;
            RecoveryRequest = request;
            return Failure is not null
                ? Task.FromException<MemoryEditReceipt?>(Failure)
                : Task.FromResult(ReceiptMissing ? null : Receipt);
        }
    }
}

public sealed class MemoryEditingProgramTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Actual_program_applies_and_recovers_the_same_authorized_edit_with_one_postgres_receipt()
    {
        var factory = environment.GetFactory();
        var project = $"memory-edit-http-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        var command = new MemoryEditCommand(project, id, 8, "fixture-http-edit",
            new(Content: "new fixture body", Tags: ["http-fixture"]));
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.SingleAsync(item => item.Username == "contract-test-admin");
            var now = DateTimeOffset.UtcNow;
            db.MemoryItems.Add(new MemoryItem
            {
                Id = id,
                ExternalKey = $"fixture:{id:N}",
                TenantId = user.TenantId,
                OwnerUserId = user.Id,
                ProjectId = project,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Status = MemoryStatus.Active,
                Title = "Fixture edit",
                Content = "original fixture body",
                SourceType = "test",
                SourceRef = "memory-editing-http",
                Version = 8,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var endpoint = $"/api/memories/{id:D}/edits";
        using var applied = await client.PostAsJsonAsync(endpoint, command);
        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        applied.Headers.CacheControl!.NoStore.Should().BeTrue();
        var receipt = (await applied.Content.ReadFromJsonAsync<MemoryEditReceipt>())!;
        receipt.Version.Should().Be(9);
        receipt.Replayed.Should().BeFalse();
        receipt.RequestHash.Should().Be(MemoryEditContract.Hash(command));

        using var recovered = await client.PostAsJsonAsync(endpoint + "/receipt",
            new MemoryEditRecoveryRequest(project, id, command.RequestId, receipt.RequestHash));
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
        (await recovered.Content.ReadFromJsonAsync<MemoryEditReceipt>()).Should().Be(receipt with { Replayed = true });

        using var repeated = await client.PostAsJsonAsync(endpoint, command);
        repeated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await repeated.Content.ReadFromJsonAsync<MemoryEditReceipt>()).Should().Be(receipt with { Replayed = true });

        using var stale = await client.PostAsJsonAsync(endpoint, command with { RequestId = "fixture-stale-edit" });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        conflict.GetProperty("code").GetString().Should().Be(SafeEditConflictCodes.StaleReadVersion);
        conflict.GetProperty("currentVersion").GetInt32().Should().Be(receipt.Version);

        using var verify = factory.Services.CreateScope();
        var persisted = verify.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var memory = await persisted.MemoryItems.AsNoTracking().SingleAsync(item => item.Id == id);
        memory.Version.Should().Be(receipt.Version);
        memory.Content.Should().Be(command.Patch.Content);
        (await persisted.MemoryEditReceipts.CountAsync(item => item.MemoryItemId == id)).Should().Be(1);
    }
}
