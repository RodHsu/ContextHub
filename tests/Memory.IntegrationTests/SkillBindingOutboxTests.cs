using System.Data.Common;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class SkillBindingOutboxTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public Task Failed_new_binding_retry_should_rebuild_only_automatic_events_and_preserve_manual_event()
        => AssertSameContextRetryAsync(existingBinding: false);

    [DockerRequiredFact]
    public Task Failed_binding_move_retry_should_not_publish_the_aborted_destination_or_revision()
        => AssertSameContextRetryAsync(existingBinding: true);

    private async Task AssertSameContextRetryAsync(bool existingBinding)
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var skill = NewSkill(null);
        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        var prefix = "retry-" + Guid.NewGuid().ToString("N");
        var original = prefix + "-a";
        var aborted = existingBinding ? prefix + "-b" : original;
        var committed = existingBinding ? prefix + "-c" : prefix + "-b";
        var binding = NewBinding(skill.Id, original);
        db.SkillBindings.Add(binding);
        if (existingBinding)
        {
            await db.SaveChangesAsync();
            binding.ScopeValue = aborted;
            binding.Revision = 2;
        }
        var manual = new AuthorityOutboxEvent
        {
            ProjectId = prefix + "-manual",
            Category = "ManualFixture",
            AggregateType = "Synthetic",
            AggregateId = Guid.NewGuid().ToString("D"),
            EventType = "Added",
            PayloadJson = "{\"manual\":true}",
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.AuthorityOutboxEvents.Add(manual);
        var aggregateId = binding.Id.ToString("D");
        var sql = $"CREATE FUNCTION public.skill_binding_retry_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.aggregate_id = '{aggregateId}' THEN RAISE EXCEPTION 'SKILL_RETRY_TEST_FAILURE'; END IF; RETURN NEW; END $$; CREATE TRIGGER skill_binding_retry_fail BEFORE INSERT ON audit.authority_outbox_events FOR EACH ROW EXECUTE FUNCTION public.skill_binding_retry_fail();";
        await db.Database.ExecuteSqlRawAsync(sql);
        try
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("SKILL_RETRY_TEST_FAILURE", Assert.IsType<PostgresException>(exception.InnerException).MessageText);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER skill_binding_retry_fail ON audit.authority_outbox_events; DROP FUNCTION public.skill_binding_retry_fail();");
        }
        // Deliberately keep this context and all tracked entries after the failed SaveChanges.
        binding.ScopeValue = committed;
        binding.Revision++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var persisted = await db.SkillBindings.SingleAsync(x => x.Id == binding.Id);
        persisted.ScopeValue.Should().Be(committed);
        persisted.Revision.Should().Be(existingBinding ? 3 : 2);
        var events = await db.AuthorityOutboxEvents.Where(x => x.AggregateId == aggregateId).ToArrayAsync();
        if (existingBinding)
        {
            events.Should().HaveCount(3);
            events.Should().ContainSingle(x => x.EventType == "Added" && x.ProjectId == original && x.AuthorityRevision == 1);
            events.Where(x => x.EventType == "Modified").Should().OnlyContain(x => x.AuthorityRevision == 3);
            events.Where(x => x.EventType == "Modified").Select(x => x.ProjectId).Should().BeEquivalentTo([original, committed]);
        }
        else
        {
            events.Should().ContainSingle().Which.ProjectId.Should().Be(committed);
            events.Single().AuthorityRevision.Should().Be(2);
        }
        var preserved = await db.AuthorityOutboxEvents.SingleAsync(x => x.Id == manual.Id);
        preserved.ProjectId.Should().Be(manual.ProjectId);
        preserved.PayloadJson.Should().Contain("\"manual\": true");
    }

    [DockerRequiredFact]
    public async Task Service_project_binding_should_reach_the_requested_case_preserving_projection_scope()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new(
            user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.SkillsBind], [], true);
        var skill = NewSkill(user.TenantId);
        skill.OwnerUserId = user.Id;
        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        var project = "MixedCaseBinding-" + Guid.NewGuid().ToString("N");
        var binding = await scope.ServiceProvider.GetRequiredService<ISkillService>().UpsertBindingAsync(new(
            skill.Id, SkillBindingScope.Project, project, SkillBindingMode.Recommended, "*", null,
            Guid.NewGuid().ToString("N")), CancellationToken.None);
        var run = await scope.ServiceProvider.GetRequiredService<IPlatformProjectionService>().RunAsync(new(
            user.TenantId, project, PlatformBackgroundMode.Full, "mixed-case-binding-test"), CancellationToken.None);
        run.Expected.Should().Be(1, "the ProjectId requested by the caller preserves case throughout Operations");
        var eventId = await db.AuthorityOutboxEvents.Where(x => x.AggregateId == binding.Id.ToString()).Select(x => x.Id).SingleAsync();
        (await db.MonitoringActivityProjections.SingleAsync(x => x.OutboxEventId == eventId)).ProjectId.Should().Be(project);

        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var updated = await service.UpsertBindingAsync(new(skill.Id, SkillBindingScope.Project, project.ToLowerInvariant(),
            SkillBindingMode.Required, "*", binding.Revision, Guid.NewGuid().ToString("N")), CancellationToken.None);
        updated.Id.Should().Be(binding.Id);
        updated.ScopeValue.Should().Be(project);
        updated.Revision.Should().Be(binding.Revision + 1);
        var stale = () => service.UpsertBindingAsync(new(skill.Id, SkillBindingScope.Project, project.ToUpperInvariant(),
            SkillBindingMode.Required, "*", binding.Revision, Guid.NewGuid().ToString("N")), CancellationToken.None);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*failed closed*");
        (await db.SkillBindings.CountAsync(x => x.SkillId == skill.Id)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Service_upsert_should_capture_tenant_and_roll_back_binding_and_root_when_outbox_fails()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new(
            user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.SkillsBind], [], true);
        var skill = NewSkill(user.TenantId);
        skill.OwnerUserId = user.Id;
        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        var project = "binding-" + Guid.NewGuid().ToString("N");
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var binding = await service.UpsertBindingAsync(new(skill.Id, SkillBindingScope.Project, project,
            SkillBindingMode.Recommended, "*", null, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var added = await db.AuthorityOutboxEvents.SingleAsync(x => x.AggregateId == binding.Id.ToString());
        added.TenantId.Should().Be(user.TenantId);
        added.ProjectId.Should().Be(project);
        added.Category.Should().Be("Skills");
        added.SecurityCritical.Should().BeTrue();
        added.PayloadJson.Should().NotContain(skill.Name);
        var metadataBefore = skill.MetadataVersion;

        // Failure is bounded to this unique project, so other fixture activity is unaffected.
        var sql = $"CREATE FUNCTION public.skill_binding_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.project_id = '{project}' THEN RAISE EXCEPTION 'SKILL_OUTBOX_TEST_FAILURE'; END IF; RETURN NEW; END $$; CREATE TRIGGER skill_binding_test_fail BEFORE INSERT ON audit.authority_outbox_events FOR EACH ROW EXECUTE FUNCTION public.skill_binding_test_fail();";
        await db.Database.ExecuteSqlRawAsync(sql);
        try
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.UpsertBindingAsync(new(skill.Id,
                SkillBindingScope.Project, project, SkillBindingMode.Disabled, "*", binding.Revision, Guid.NewGuid().ToString("N")), CancellationToken.None));
            Assert.Equal("SKILL_OUTBOX_TEST_FAILURE", Assert.IsType<PostgresException>(exception.InnerException).MessageText);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER skill_binding_test_fail ON audit.authority_outbox_events; DROP FUNCTION public.skill_binding_test_fail();");
            db.ChangeTracker.Clear();
        }
        (await db.Skills.SingleAsync(x => x.Id == skill.Id)).MetadataVersion.Should().Be(metadataBefore);
        var unchanged = await db.SkillBindings.SingleAsync(x => x.Id == binding.Id);
        unchanged.Revision.Should().Be(binding.Revision);
        unchanged.Mode.Should().Be(SkillBindingMode.Recommended);
        (await db.AuthorityOutboxEvents.CountAsync(x => x.AggregateId == binding.Id.ToString())).Should().Be(1);
        await service.UpsertBindingAsync(new(skill.Id, SkillBindingScope.Project, project,
            SkillBindingMode.Disabled, "*", binding.Revision, Guid.NewGuid().ToString("N")), CancellationToken.None);
        var modified = await db.AuthorityOutboxEvents.SingleAsync(x => x.AggregateId == binding.Id.ToString() && x.EventType == "Modified");
        modified.AuthorityRevision.Should().Be(binding.Revision + 1);
    }

    [DockerRequiredFact]
    public async Task Batch_root_lookup_should_be_async_and_preserve_two_tenants_with_same_project()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var tenants = new[] { NewTenant(), NewTenant() };
        db.Tenants.AddRange(tenants);
        await db.SaveChangesAsync();
        var skills = tenants.Select(x => NewSkill(x.Id)).ToArray();
        db.Skills.AddRange(skills);
        await db.SaveChangesAsync();
        var counter = new RootLookupCounter();
        await using var isolated = CreateContext(scope.ServiceProvider, counter);
        var project = "binding-" + Guid.NewGuid().ToString("N");
        var bindings = skills.Select(x => NewBinding(x.Id, project)).ToArray();
        isolated.SkillBindings.AddRange(bindings);
        await isolated.SaveChangesAsync();
        counter.AsyncLookups.Should().Be(1);
        counter.SyncLookups.Should().Be(0);
        var events = await db.AuthorityOutboxEvents.Where(x => x.ProjectId == project).ToArrayAsync();
        events.Should().HaveCount(2);
        events.Select(x => x.TenantId).Should().BeEquivalentTo(tenants.Select(x => (Guid?)x.Id));
        await isolated.SaveChangesAsync();
        counter.AsyncLookups.Should().Be(1, "no changed Project binding requires no root lookup");
        var projector = scope.ServiceProvider.GetRequiredService<IPlatformProjectionService>();
        await projector.RunAsync(new(tenants[0].Id, project, PlatformBackgroundMode.Full, "binding-test"), CancellationToken.None);
        var projections = await db.MonitoringActivityProjections.Where(x => x.ProjectId == project).ToArrayAsync();
        projections.Should().ContainSingle().Which.TenantId.Should().Be(tenants[0].Id);
    }

    [DockerRequiredFact]
    public async Task Direct_move_and_delete_should_capture_original_and_current_project_and_support_sync_save()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var counter = new RootLookupCounter();
        await using var db = CreateContext(scope.ServiceProvider, counter);
        var tenantId = await db.Tenants.Select(x => x.Id).FirstAsync();
        var skill = NewSkill(tenantId);
        var oldProject = "binding-old-" + Guid.NewGuid().ToString("N");
        var newProject = "binding-new-" + Guid.NewGuid().ToString("N");
        var binding = NewBinding(skill.Id, oldProject);
        db.Skills.Add(skill);
        db.SkillBindings.Add(binding);
        db.SaveChanges();
        counter.SyncLookups.Should().Be(0, "same-unit new root is already tracked");
        db.ChangeTracker.Clear();
        binding = await db.SkillBindings.SingleAsync(x => x.Id == binding.Id);
        binding.ScopeValue = newProject;
        binding.Revision++;
        db.SaveChanges();
        counter.SyncLookups.Should().Be(1);
        var moves = await db.AuthorityOutboxEvents.Where(x => x.AggregateId == binding.Id.ToString() && x.EventType == "Modified").ToArrayAsync();
        moves.Select(x => x.ProjectId).Should().BeEquivalentTo([oldProject, newProject]);
        moves.Should().OnlyContain(x => x.TenantId == tenantId);
        db.SkillBindings.Remove(binding);
        await db.SaveChangesAsync();
        var deleted = await db.AuthorityOutboxEvents.SingleAsync(x => x.AggregateId == binding.Id.ToString() && x.EventType == "Deleted");
        deleted.ProjectId.Should().Be(newProject);
        deleted.TenantId.Should().Be(tenantId);
    }

    [DockerRequiredFact]
    public async Task Project_scope_transitions_should_not_invent_global_events()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var skill = NewSkill(null);
        db.Skills.Add(skill);
        var project = "binding-" + Guid.NewGuid().ToString("N");
        var binding = NewBinding(skill.Id, project);
        binding.Scope = SkillBindingScope.Repository;
        db.SkillBindings.Add(binding);
        await db.SaveChangesAsync();
        (await db.AuthorityOutboxEvents.AnyAsync(x => x.AggregateId == binding.Id.ToString())).Should().BeFalse();
        binding.Scope = SkillBindingScope.Project;
        binding.Revision++;
        await db.SaveChangesAsync();
        binding.Scope = SkillBindingScope.Tenant;
        binding.ScopeValue = string.Empty;
        binding.Revision++;
        await db.SaveChangesAsync();
        var events = await db.AuthorityOutboxEvents.Where(x => x.AggregateId == binding.Id.ToString()).ToArrayAsync();
        events.Should().HaveCount(2).And.OnlyContain(x => x.ProjectId == project && x.SecurityCritical);
        foreach (var nonProject in Enum.GetValues<SkillBindingScope>().Where(x => x != SkillBindingScope.Project))
            db.SkillBindings.Add(new() { SkillId = skill.Id, Scope = nonProject, ScopeValue = "non-project", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var bindingIds = await db.SkillBindings.Where(x => x.SkillId == skill.Id).Select(x => x.Id.ToString()).ToArrayAsync();
        (await db.AuthorityOutboxEvents.CountAsync(x => bindingIds.Contains(x.AggregateId))).Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task Missing_root_or_blank_project_should_fail_closed_before_business_write()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.SkillBindings.Add(NewBinding(Guid.NewGuid(), "missing-root"));
        var missing = () => db.SaveChangesAsync();
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owning root*");
        db.ChangeTracker.Clear();
        var binding = NewBinding(Guid.NewGuid(), " ");
        db.SkillBindings.Add(binding);
        var blank = () => db.SaveChanges();
        blank.Should().Throw<InvalidOperationException>().WithMessage("*explicit project*");
        (await db.SkillBindings.AsNoTracking().AnyAsync(x => x.Id == binding.Id)).Should().BeFalse();
    }

    private static Skill NewSkill(Guid? tenantId) => new()
    {
        TenantId = tenantId,
        StableKey = "binding-" + Guid.NewGuid().ToString("N"),
        Name = "private metadata sentinel",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Tenant NewTenant() => new()
    {
        Slug = "binding-" + Guid.NewGuid().ToString("N"),
        DisplayName = "Synthetic tenant",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static SkillBinding NewBinding(Guid skillId, string project) => new()
    {
        SkillId = skillId,
        Scope = SkillBindingScope.Project,
        ScopeValue = project,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static MemoryDbContext CreateContext(IServiceProvider services, RootLookupCounter counter)
        => new(new DbContextOptionsBuilder<MemoryDbContext>(services.GetRequiredService<DbContextOptions<MemoryDbContext>>()).AddInterceptors(counter).Options);

    private sealed class RootLookupCounter : DbCommandInterceptor
    {
        public int SyncLookups { get; private set; }
        public int AsyncLookups { get; private set; }
        private static bool IsRootLookup(DbCommand command)
            => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("FROM skills AS", StringComparison.Ordinal);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (IsRootLookup(command)) SyncLookups++;
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsRootLookup(command)) AsyncLookups++;
            return ValueTask.FromResult(result);
        }
    }
}
