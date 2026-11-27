using System.Data;
using System.Data.Common;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class PlatformFoundationConsistentReadTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Effective_rights_reads_one_coherent_authority_snapshot_without_owning_the_caller_transaction(bool joined)
    {
        var services = environment.GetFactory().Services;
        using var writerScope = services.CreateScope();
        var writer = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await LoadOwner(writer);
        var parent = $"rights-snapshot-{Guid.NewGuid():N}";
        var target = $"{parent}-child";
        const string principal = "snapshot-principal";
        const string right = "download";
        var actorAccessor = SetActor(writerScope.ServiceProvider, owner, parent, target);
        var writes = writerScope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
        var now = DateTimeOffset.UtcNow;
        var peer = new TenantUser
        {
            TenantId = owner.TenantId,
            Username = $"snapshot-peer-{Guid.NewGuid():N}",
            DisplayName = "Snapshot peer",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        writer.TenantUsers.Add(peer);
        await writer.SaveChangesAsync();
        writer.ProjectExplicitGrants.Add(new ProjectExplicitGrant
        {
            TenantId = owner.TenantId,
            OwnerUserId = peer.Id,
            ProjectId = parent,
            PrincipalId = principal,
            Right = right,
            Effect = AuthorizationEffect.Deny,
            EvidenceRef = "peer-only",
            CreatedAt = now,
            UpdatedAt = now
        });
        writer.ProjectSecurityRevisions.Add(new ProjectSecurityRevision
        {
            TenantId = owner.TenantId,
            OwnerUserId = peer.Id,
            ProjectId = parent,
            GrantRevision = 1,
            UpdatedAt = now
        });
        await writer.SaveChangesAsync();

        await writes.UpsertTopologyEdgeAsync(new(parent.ToUpperInvariant(), target, "repository", true, 0), CancellationToken.None);
        await writes.UpsertPolicyAsync(new(null, parent, "*", right, AuthorizationEffect.Allow, "policy-before", 0), CancellationToken.None);
        var policyId = await writer.ProjectAuthorizationPolicies.AsNoTracking()
            .Where(policy => policy.ProjectId == parent && policy.OwnerUserId == owner.Id)
            .Select(policy => policy.Id)
            .SingleAsync();

        var interceptor = new AuthorityReadInterceptor(async cancellationToken =>
        {
            await writes.UpsertPolicyAsync(
                new(policyId, parent, "*", right, AuthorizationEffect.Deny, "policy-after", 1), cancellationToken);
        });
        var readerOptions = new DbContextOptionsBuilder<MemoryDbContext>(
                writerScope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var reader = new MemoryDbContext(readerOptions);
        var store = new PlatformFoundationStore(reader, actorAccessor, writerScope.ServiceProvider.GetRequiredService<IClock>(),
            writerScope.ServiceProvider.GetRequiredService<IEffectiveRightsEvaluator>());
        await using var transaction = joined ? await reader.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted) : null;

        var before = await store.EvaluateAsync(target, principal, [right], null, null, CancellationToken.None);
        interceptor.TransactionFlags.Should().Equal(joined);
        interceptor.CommittedChange.Should().BeTrue();
        AssertDecision(before, expectedPolicyRevision: 1, allowed: true, evidence: "policy-before");
        reader.Database.CurrentTransaction.Should().BeSameAs(transaction);

        var after = await store.EvaluateAsync(target, principal, [right], null, null, CancellationToken.None);
        interceptor.TransactionFlags.Should().Equal(joined, joined);
        AssertDecision(after, expectedPolicyRevision: 2, allowed: false, evidence: "policy-after");
        reader.Database.CurrentTransaction.Should().BeSameAs(transaction);
        if (transaction is not null)
        {
            await transaction.RollbackAsync();
        }

        static void AssertDecision(EffectiveRightsResult result, long expectedPolicyRevision, bool allowed, string evidence)
        {
            result.Revisions.Should().Be(new SecurityRevisionVector(1, expectedPolicyRevision, 0, 0));
            var decision = result.Decisions.Should().ContainSingle().Which;
            decision.Allowed.Should().Be(allowed);
            decision.Tier.Should().Be("Inherited");
            decision.Evidence.Should().Equal(evidence);
        }
    }

    [DockerRequiredFact]
    public async Task Effective_rights_preserves_the_fail_closed_check_for_a_reachable_legacy_cycle()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await LoadOwner(db);
        var parent = $"snapshot-legacy-cycle-{Guid.NewGuid():N}";
        var child = $"{parent}-child";
        SetActor(scope.ServiceProvider, owner, parent, child);
        var now = DateTimeOffset.UtcNow;
        db.ProjectHierarchies.AddRange(
            LegacyEdge(parent, child, "organization"), LegacyEdge(child, parent, "repository"));
        await db.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var read = () => store.EvaluateAsync(child, "snapshot-principal", ["download"], null, null, cancellation.Token);
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*topology contains a cycle*failed closed*");

        ProjectHierarchy LegacyEdge(string edgeParent, string edgeChild, string dimension) => new()
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ParentProjectId = edgeParent,
            ChildProjectId = edgeChild,
            Dimension = dimension,
            AuthorizationInheritable = true,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Task<TenantUser> LoadOwner(MemoryDbContext db)
        => db.TenantUsers.AsNoTracking().SingleAsync(user => user.Username == "contract-test-admin");

    private static IRequestActorAccessor SetActor(IServiceProvider services, TenantUser owner, params string[] projects)
    {
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        accessor.Current = new ContextHubRequestActor(
            owner.TenantId, owner.Id, owner.Username, owner.Role, [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], projects,
            IsAuthenticated: true);
        return accessor;
    }

    private sealed class AuthorityReadInterceptor(Func<CancellationToken, Task> commitChange) : DbCommandInterceptor
    {
        public List<bool> TransactionFlags { get; } = [];
        public bool CommittedChange { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("project_hierarchies", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("project_authorization_policies", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("project_explicit_grants", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("project_security_revisions", StringComparison.OrdinalIgnoreCase))
            {
                TransactionFlags.Add(command.Transaction is not null);
                if (!CommittedChange)
                {
                    await commitChange(cancellationToken);
                    CommittedChange = true;
                }
            }

            return result;
        }
    }
}
