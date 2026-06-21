using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ContextHubBootstrapServiceTests
{
    [Fact]
    public void Describe_Without_ProjectId_Should_Not_Default_To_Default_Project()
    {
        var service = new ContextHubBootstrapService();

        var result = service.Describe(new ContextHubBootstrapRequest());

        result.Service.Name.Should().Be("ContextHub");
        result.Project.ProjectIdProvided.Should().BeFalse();
        result.Project.ProjectId.Should().BeNull();
        result.Project.ProjectIdRequiredForWork.Should().BeTrue();
        result.Project.Guidance.Should().Contain("projectId");
        result.UserPreferences.BootstrapDisclosure.Should().Be("summary-and-policy");
        result.UserPreferences.AvailableKinds.Should().Contain(nameof(UserPreferenceKind.ToolingPreference));
        result.Warnings.Should().Contain(x => x.Contains("ProjectContext.DefaultProjectId", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_With_ProjectId_Should_Return_Normalized_Project_Hint()
    {
        var service = new ContextHubBootstrapService();

        var result = service.Describe(new ContextHubBootstrapRequest(" ContextHub "));

        result.Project.ProjectIdProvided.Should().BeTrue();
        result.Project.ProjectId.Should().Be("ContextHub");
        result.Project.RecommendedWorkingContextCall.Should().Be("build_working_context(projectId=\"ContextHub\", query=\"...\")");
    }
}
