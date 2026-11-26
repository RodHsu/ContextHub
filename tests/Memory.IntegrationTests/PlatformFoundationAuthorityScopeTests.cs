using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class PlatformFoundationAuthorityScopeTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Effective_rights_cache_must_preserve_authority_visibility_for_equal_revisions()
    {
        var services = environment.GetFactory().Services;
        var projectId = $"authority-scope-{Guid.NewGuid():N}";
        var resourceId = Guid.NewGuid().ToString();
        const string principalId = "fixture-principal";
        const string resourceType = "File";
        const string right = "download";
        var now = DateTimeOffset.UtcNow;
        var otherTenant = new Tenant
        {
            Slug = projectId,
            DisplayName = "Other authority tenant",
            CreatedAt = now,
            UpdatedAt = now
        };

        TenantUser firstOwner;
        TenantUser secondOwner;
        TenantUser otherTenantOwner;
        using (var seed = services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
            firstOwner = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
            secondOwner = NewOwner(firstOwner.TenantId);
            otherTenantOwner = NewOwner(otherTenant.Id);
            db.Tenants.Add(otherTenant);
            db.TenantUsers.AddRange(secondOwner, otherTenantOwner);
            await db.SaveChangesAsync();

            AddRule(firstOwner, AuthorizationEffect.Allow, "owner-a");
            AddRule(secondOwner, AuthorizationEffect.Deny, "owner-b");
            AddRule(otherTenantOwner, AuthorizationEffect.Allow, "other-tenant");
            await db.SaveChangesAsync();

            void AddRule(TenantUser owner, AuthorizationEffect effect, string evidenceRef)
            {
                db.ProjectAuthorizationPolicies.Add(new ProjectAuthorizationPolicy
                {
                    TenantId = owner.TenantId,
                    OwnerUserId = owner.Id,
                    ProjectId = projectId,
                    PrincipalId = principalId,
                    Right = right,
                    Effect = effect,
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    EvidenceRef = evidenceRef,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                // Zero is a valid revision and keeps the tenant-wide sum equal to each owner's revision.
                db.ProjectSecurityRevisions.Add(new ProjectSecurityRevision
                {
                    TenantId = owner.TenantId,
                    OwnerUserId = owner.Id,
                    ProjectId = projectId,
                    UpdatedAt = now
                });
            }
        }

        var firstResult = await Evaluate(firstOwner);
        AssertDecision(firstResult, true, "owner-a");
        AssertDecision(await Evaluate(secondOwner), false, "owner-b");
        var serviceResult = await Evaluate(firstOwner, serviceActor: true);
        AssertDecision(serviceResult, false, "owner-a", "owner-b");
        AssertDecision(await Evaluate(otherTenantOwner), true, "other-tenant");
        AssertDecision(await Evaluate(otherTenantOwner, serviceActor: true), true, "other-tenant");
        (await Evaluate(firstOwner)).Should().BeSameAs(firstResult);
        (await Evaluate(secondOwner, serviceActor: true)).Should().BeSameAs(serviceResult);

        TenantUser NewOwner(Guid tenantId) => new()
        {
            TenantId = tenantId,
            Username = $"authority-owner-{Guid.NewGuid():N}",
            DisplayName = "Authority owner",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };

        async Task<EffectiveRightsResult> Evaluate(TenantUser owner, bool serviceActor = false)
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
                owner.TenantId, owner.Id, owner.Username, owner.Role, [SecurityScopes.MemoryRead], [projectId],
                IsAuthenticated: true, IsServiceActor: serviceActor);
            var store = scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
            return await store.EvaluateAsync(projectId, principalId, [right], resourceType, resourceId, CancellationToken.None);
        }

        static void AssertDecision(EffectiveRightsResult result, bool allowed, params string[] evidence)
        {
            result.Revisions.Should().Be(new SecurityRevisionVector(0, 0, 0, 0));
            var decision = result.Decisions.Should().ContainSingle().Which;
            decision.Tier.Should().Be("Resource");
            decision.Allowed.Should().Be(allowed);
            decision.Evidence.Should().Equal(evidence);
        }
    }

    [DockerRequiredFact]
    public async Task Topology_rejects_cross_dimension_cycles_and_keeps_discussion_non_inheritable()
    {
        var owner = await LoadFixtureOwner();
        var prefix = $"combined-topology-{Guid.NewGuid():N}";
        var root = $"{prefix}-root";
        var branch = $"{prefix}-branch";
        var leaf = $"{prefix}-leaf";
        using var scope = CreateTopologyScope(owner, root, branch, leaf);
        var store = scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();

        await store.UpsertTopologyEdgeAsync(new(root, branch, "organization", true, 0), CancellationToken.None);
        await store.UpsertTopologyEdgeAsync(new(branch, leaf, "repository", true, 0), CancellationToken.None);

        var closeCycle = () => store.UpsertTopologyEdgeAsync(new(leaf, root, "portfolio", true, 0), CancellationToken.None);
        await closeCycle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*topology contains a cycle*");

        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectHierarchies.AsNoTracking().AnyAsync(x => x.ChildProjectId == root)).Should().BeFalse();
        (await db.ProjectSecurityRevisions.AsNoTracking().AnyAsync(x => x.ProjectId == root)).Should().BeFalse();

        var discussion = await store.UpsertTopologyEdgeAsync(new(leaf, root, "discussion", false, 0), CancellationToken.None);
        discussion.Revision.Should().Be(1);
        var stalePromotion = () => store.UpsertTopologyEdgeAsync(new(leaf, root, "discussion", true, 0), CancellationToken.None);
        await stalePromotion.Should().ThrowAsync<DbUpdateConcurrencyException>().WithMessage("*revision conflict*");
        var promoteDiscussion = () => store.UpsertTopologyEdgeAsync(new(leaf, root, "discussion", true, 1), CancellationToken.None);
        await promoteDiscussion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*topology contains a cycle*");

        var persisted = await db.ProjectHierarchies.AsNoTracking().SingleAsync(x => x.ChildProjectId == root);
        persisted.Dimension.Should().Be("discussion");
        persisted.AuthorizationInheritable.Should().BeFalse();
        persisted.Revision.Should().Be(1);
        (await db.ProjectSecurityRevisions.AsNoTracking().SingleAsync(x => x.ProjectId == root)).TopologyRevision.Should().Be(1);

        await store.UpsertPolicyAsync(new(null, leaf, "topology-fixture", "download", AuthorizationEffect.Allow, "leaf-policy", 0),
            CancellationToken.None);
        var leafRights = await store.EvaluateAsync(leaf, "topology-fixture", ["download"], null, null, CancellationToken.None);
        leafRights.Decisions.Should().ContainSingle().Which.Allowed.Should().BeTrue();
        var rootRights = await store.EvaluateAsync(root, "topology-fixture", ["download"], null, null, CancellationToken.None);
        rootRights.Decisions.Should().ContainSingle().Which.Tier.Should().Be("DefaultDeny");
    }

    [DockerRequiredFact]
    public async Task Topology_serializes_competing_writes_to_different_children_in_the_same_authority_scope()
    {
        var owner = await LoadFixtureOwner();
        var prefix = $"competing-topology-{Guid.NewGuid():N}";
        var first = $"{prefix}-first";
        var second = $"{prefix}-second";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = Write(new(first, second, "organization", true, 0));
        var secondWrite = Write(new(second, first, "repository", true, 0));
        start.SetResult();
        var errors = await Task.WhenAll(firstWrite, secondWrite);
        errors.Count(x => x is null).Should().Be(1);
        errors.OfType<InvalidOperationException>().Should().ContainSingle().Which.Message.Should().Contain("topology contains a cycle");

        using var scope = CreateTopologyScope(owner, first, second);
        var store = scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var committed = (await db.ProjectHierarchies.AsNoTracking()
            .Where(x => x.ChildProjectId == first || x.ChildProjectId == second).ToArrayAsync(timeout.Token))
            .Should().ContainSingle().Which;
        var revision = (await db.ProjectSecurityRevisions.AsNoTracking()
            .Where(x => x.ProjectId == first || x.ProjectId == second).ToArrayAsync(timeout.Token))
            .Should().ContainSingle().Which;
        revision.ProjectId.Should().Be(committed.ChildProjectId);
        revision.TopologyRevision.Should().Be(1);

        await store.UpsertPolicyAsync(
            new(null, committed.ParentProjectId, "topology-fixture", "download", AuthorizationEffect.Allow, "parent-policy", 0), timeout.Token);
        var parentRights = await store.EvaluateAsync(committed.ParentProjectId, "topology-fixture", ["download"], null, null, timeout.Token);
        parentRights.Decisions.Should().ContainSingle().Which.Tier.Should().Be("Project");
        var childRights = await store.EvaluateAsync(committed.ChildProjectId, "topology-fixture", ["download"], null, null, timeout.Token);
        var decision = childRights.Decisions.Should().ContainSingle().Which;
        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be("Inherited");
        decision.Evidence.Should().Equal("parent-policy");

        async Task<Exception?> Write(TopologyEdgeUpsertRequest request)
        {
            using var writeScope = CreateTopologyScope(owner, first, second);
            var writer = writeScope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
            await start.Task.WaitAsync(timeout.Token);
            return await Record.ExceptionAsync(() => writer.UpsertTopologyEdgeAsync(request, timeout.Token));
        }
    }

    private async Task<TenantUser> LoadFixtureOwner()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
    }

    private IServiceScope CreateTopologyScope(TenantUser owner, params string[] projects)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            owner.TenantId, owner.Id, owner.Username, owner.Role, [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], projects,
            IsAuthenticated: true);
        return scope;
    }
}
