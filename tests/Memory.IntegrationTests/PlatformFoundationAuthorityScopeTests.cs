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
}
