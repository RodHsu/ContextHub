using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ArtifactGovernanceIdentityTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Governance_keeps_distinct_file_version_pins_out_of_duplicate_and_authority_cohorts(bool repinBeforeExecution)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.PreferencesRead], [], true);
        var project = $"file-pins-{Guid.NewGuid():N}";
        var run = $"file-pins-{Guid.NewGuid():N}";
        var file = Guid.NewGuid();
        var sharedPin = Guid.NewGuid();
        var first = Artifact(file, sharedPin, 0.80m);
        var samePin = Artifact(file, sharedPin, 0.76m);
        var otherVersion = Artifact(file, Guid.NewGuid(), 0.89m);
        var otherFile = Artifact(Guid.NewGuid(), Guid.NewGuid(), 0.85m);
        var malformed = Artifact(file, Guid.Empty, 0.75m);
        malformed.SourceType = "legacy-import";
        db.MemoryItems.AddRange(first, samePin, otherVersion, otherFile, malformed);
        await db.SaveChangesAsync();

        var reviewService = services.GetRequiredService<IKnowledgeReviewService>();
        var review = await reviewService.ReviewAsync(new KnowledgeReviewRequest([project], GovernanceRunId: run), default);
        var artifacts = review.GovernancePlan.Where(x => x.Classification == "DuplicateArtifact").ToArray();
        artifacts.Should().ContainSingle().Which.AuthorityResourceId.Should().Be(first.Id);
        artifacts.Single().RelatedResourceIds.Should().ContainSingle().Which.Should().Be(first.Id);
        var findings = await db.GovernanceFindings.AsNoTracking().Where(x =>
            x.ProjectId == project && (x.Type == GovernanceFindingType.DuplicateMemoryCandidate ||
                                      x.Type == GovernanceFindingType.MergeMemoryCandidate ||
                                      x.Type == GovernanceFindingType.AuthoritativeSourceCandidate)).ToArrayAsync();
        findings.Select(x => x.Type).Should().Contain([
            GovernanceFindingType.DuplicateMemoryCandidate,
            GovernanceFindingType.MergeMemoryCandidate,
            GovernanceFindingType.AuthoritativeSourceCandidate]);
        findings.Should().OnlyContain(x =>
            x.PrimaryMemoryId == first.Id && x.SecondaryMemoryId == samePin.Id ||
            x.PrimaryMemoryId == samePin.Id && x.SecondaryMemoryId == first.Id);

        var executor = services.GetRequiredService<IGovernanceBatchExecutor>();
        var request = new GovernanceBatchExecuteRequest(run, [project], review.DurableMemoryCoverage!.SnapshotToken,
            AllowedActionTypes: [GovernanceBatchActionType.ArtifactReconcile], MaxRiskLevel: GovernanceBatchRiskLevel.Medium);
        if (repinBeforeExecution)
        {
            request = request with { AllowedActionTypes = [GovernanceBatchActionType.ArtifactReconcile, GovernanceBatchActionType.Merge] };
            var planned = await executor.ExecuteAsync(request with { DryRun = true }, default);
            planned.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, planned.StoppedReason);
            samePin.MetadataJson = Pin(file, Guid.NewGuid());
            await db.SaveChangesAsync();

            var suggestion = await db.SuggestedActions.AsNoTracking().FirstAsync(x =>
                x.ProjectId == project && x.Type == SuggestedActionType.MergeDuplicateCandidate);
            var actions = services.GetRequiredService<ISuggestedActionService>();
            await FluentActions.Invoking(() => actions.AcceptAsync(suggestion.Id, default))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not support merging*");
            (await db.MemoryLinks.AnyAsync(x => x.LinkType == "replaced_by" &&
                (x.FromId == samePin.Id || x.ToId == samePin.Id))).Should().BeFalse();
            var resultAfterRepin = await executor.ExecuteAsync(request, default);
            resultAfterRepin.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, resultAfterRepin.StoppedReason);
            resultAfterRepin.Items.Should().Contain(x => x.ResourceId == samePin.Id &&
                x.ActionType == GovernanceBatchActionType.ArtifactReconcile &&
                x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision);
            resultAfterRepin.Items.Should().Contain(x => x.ItemKind == "Finding" &&
                x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision &&
                x.Summary.Contains("not an exact or same-key mechanical duplicate", StringComparison.Ordinal));
            resultAfterRepin.Items.SelectMany(x => x.ProposalIds).Should().BeEmpty();
            (await db.MemoryItems.AsNoTracking().CountAsync(x => x.ProjectId == project && x.Status == MemoryStatus.Active))
                .Should().Be(5);

            // A previously executed suggestion can be encountered again during analysis.
            await db.SuggestedActions.Where(x => x.Id == suggestion.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, SuggestedActionStatus.Executed));
            await services.GetRequiredService<IGovernanceService>().AnalyzeAsync(project, default);
            (await db.MemoryLinks.AnyAsync(x => x.LinkType == "replaced_by" &&
                (x.FromId == samePin.Id || x.ToId == samePin.Id))).Should().BeFalse();
            return;
        }

        var result = await executor.ExecuteAsync(request, default);
        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, result.StoppedReason);
        result.Items.Should().Contain(x => x.ResourceId == samePin.Id &&
                                          x.ActionType == GovernanceBatchActionType.ArtifactReconcile &&
                                          x.Disposition == GovernanceBatchItemDisposition.Applied);
        var archived = await db.MemoryItems.AsNoTracking().Where(x => x.ProjectId == project && x.Status == MemoryStatus.Archived)
            .Select(x => x.Id).ToArrayAsync();
        archived.Should().Equal(samePin.Id);

        MemoryItem Artifact(Guid fileId, Guid versionId, decimal confidence) => new()
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = project,
            ExternalKey = Guid.NewGuid().ToString("N"),
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Status = MemoryStatus.Active,
            Title = "Managed supplier document",
            Summary = "Revision instructions for supplier onboarding",
            Content = "ContextHub managed file reference.",
            SourceType = ProjectArtifactExchangeService.SourceType,
            SourceRef = "fixture",
            Tags = ["artifact-exchange", "artifact-kind:filereference"],
            MetadataJson = Pin(fileId, versionId),
            Confidence = confidence,
            Importance = 0.76m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        static string Pin(Guid fileId, Guid versionId)
            => JsonSerializer.Serialize(new { artifactExchange = true, kind = "FileReference", fileId, fileVersionId = versionId });
    }
}
