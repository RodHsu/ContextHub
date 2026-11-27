using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Shared;
using Memory.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

public sealed class SkillCanonicalTagPanelTests
{
    [Fact]
    public async Task Api_reads_one_encoded_project_and_distinguishes_empty_from_missing_route()
    {
        var skill = Guid.NewGuid();
        var page = Page(skill, "team/one", 25);
        using var handler = new Handler(page);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        var api = new ContextHubApiClient(http);

        (await api.GetSkillCanonicalTagsAsync(skill, "team/one", 25, 25, default))!.Items.Should().BeEmpty();
        handler.Method.Should().Be(HttpMethod.Get);
        handler.Uri!.AbsolutePath.Should().Be($"/api/skills/{skill:D}/canonical-tags");
        handler.Uri.Query.Should().Contain("projectId=team%2Fone").And.Contain("offset=25").And.Contain("limit=25");

        handler.Status = HttpStatusCode.NotFound;
        (await api.GetSkillCanonicalTagsAsync(skill, "team/one", 25, 25, default)).Should().BeNull();
        var invalid = () => api.GetSkillCanonicalTagsAsync(skill, "*", 0, 25, default);
        await invalid.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_project_is_required_and_paging_reads_the_same_explicit_scope()
    {
        var skill = Guid.NewGuid();
        var calls = new List<(Guid Skill, string Project, int Offset)>();
        await using var fixture = new Fixture((id, project, offset, _) =>
        {
            calls.Add((id, project, offset));
            return Task.FromResult<SkillCanonicalTagBindingsResult?>(project == "other" ? null
                : offset == 0 ? Page(id, project, offset, "Alpha", hasMore: true) : Page(id, project, offset));
        });
        await fixture.Renderer.MountAsync<SkillCanonicalTagPanel>(Parameters(skill));

        calls.Should().BeEmpty();
        fixture.Renderer.Elements().Where(element => element.Tag == "option").Select(element => element.Text.Trim())
            .Should().Equal("選擇專案", "one", "other");
        await ChangeProjectAsync(fixture.Renderer, "one");
        HasText(fixture.Renderer, "Alpha").Should().BeTrue();

        await fixture.Renderer.DispatchAsync(Button(fixture.Renderer, "下頁標籤"), "onclick", new MouseEventArgs());
        HasText(fixture.Renderer, "目前沒有此 Skill").Should().BeTrue();
        HasText(fixture.Renderer, "Alpha").Should().BeFalse();

        await ChangeProjectAsync(fixture.Renderer, "other");
        HasText(fixture.Renderer, "目前無法取得此專案的標籤綁定").Should().BeTrue();
        HasText(fixture.Renderer, "目前沒有此 Skill").Should().BeFalse();
        calls.Should().Equal((skill, "one", 0), (skill, "one", 25), (skill, "other", 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_projects_discards_old_success_or_error(bool fail)
    {
        var skill = Guid.NewGuid();
        var stale = new TaskCompletionSource<SkillCanonicalTagBindingsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture((id, project, offset, token) =>
        {
            if (project == "other")
                return Task.FromResult<SkillCanonicalTagBindingsResult?>(Page(id, project, offset, "Current tag"));
            started.SetResult(token);
            return stale.Task;
        });
        await fixture.Renderer.MountAsync<SkillCanonicalTagPanel>(Parameters(skill));

        var old = ChangeProjectAsync(fixture.Renderer, "one");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ChangeProjectAsync(fixture.Renderer, "other");
        token.IsCancellationRequested.Should().BeTrue();
        if (fail) stale.SetException(new HttpRequestException("private origin"));
        else stale.SetResult(Page(skill, "one", 0, "Stale tag"));
        await old;

        HasText(fixture.Renderer, "Current tag").Should().BeTrue();
        HasText(fixture.Renderer, "Stale tag").Should().BeFalse();
        HasText(fixture.Renderer, "目前無法取得此專案").Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_skill_or_disposing_cancels_the_old_binding_read(bool dispose)
    {
        var skill = Guid.NewGuid();
        var stale = new TaskCompletionSource<SkillCanonicalTagBindingsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture((_, _, _, token) =>
        {
            started.SetResult(token);
            return stale.Task;
        });
        await fixture.Renderer.MountAsync<SkillCanonicalTagPanel>(Parameters(skill));

        var old = ChangeProjectAsync(fixture.Renderer, "one");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) await fixture.Renderer.RemoveAsync();
        else await fixture.Renderer.UpdateAsync(Parameters(Guid.NewGuid()));
        token.IsCancellationRequested.Should().BeTrue();
        stale.SetResult(Page(skill, "one", 0, "Previous Skill tag"));
        await old;
        HasText(fixture.Renderer, "Previous Skill tag").Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_supersedes_the_in_flight_page_and_rejects_an_incorrect_response_scope()
    {
        var skill = Guid.NewGuid();
        var stale = new TaskCompletionSource<SkillCanonicalTagBindingsResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = new Fixture((id, project, offset, token) =>
        {
            calls++;
            if (offset == 0) return Task.FromResult<SkillCanonicalTagBindingsResult?>(Page(id, project, offset, "First", hasMore: true));
            if (calls > 2) return Task.FromResult<SkillCanonicalTagBindingsResult?>(Page(id, "other", offset, "Unrelated scope"));
            started.SetResult(token);
            return stale.Task;
        });
        await fixture.Renderer.MountAsync<SkillCanonicalTagPanel>(Parameters(skill));
        await ChangeProjectAsync(fixture.Renderer, "one");
        var paging = fixture.Renderer.DispatchAsync(Button(fixture.Renderer, "下頁標籤"), "onclick", new MouseEventArgs());
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.UpdateAsync(Parameters(skill, refreshGeneration: 1));
        token.IsCancellationRequested.Should().BeTrue();
        stale.SetResult(Page(skill, "one", 25, "Previous page"));
        await paging;

        HasText(fixture.Renderer, "Previous page").Should().BeFalse();
        HasText(fixture.Renderer, "Unrelated scope").Should().BeFalse();
        HasText(fixture.Renderer, "目前無法取得此專案").Should().BeTrue();
    }

    private static ParameterView Parameters(Guid skill, long refreshGeneration = 0) => ParameterView.FromDictionary(
        new Dictionary<string, object?>
        {
            [nameof(SkillCanonicalTagPanel.SkillId)] = skill,
            [nameof(SkillCanonicalTagPanel.RefreshGeneration)] = refreshGeneration
        });

    private static Task ChangeProjectAsync(WorkItemEditorTestRenderer renderer, string project)
        => renderer.DispatchAsync(renderer.Elements().Single(element => element.Tag == "select"), "onchange", new ChangeEventArgs { Value = project });

    private static WorkItemEditorElement Button(WorkItemEditorTestRenderer renderer, string text)
        => renderer.Elements().Single(element => element.Tag == "button" && element.Text.Trim() == text);

    private static bool HasText(WorkItemEditorTestRenderer renderer, string text)
        => renderer.Elements().Any(element => element.Text.Contains(text, StringComparison.Ordinal));

    private static SkillCanonicalTagBindingsResult Page(Guid id, string project, int offset, string? name = null, bool hasMore = false)
        => new(id, project, offset, hasMore, name is null ? [] : [new(Guid.NewGuid(), Guid.NewGuid(), name, 1, "fixture", 1)]);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public WorkItemEditorTestRenderer Renderer { get; }

        public Fixture(Func<Guid, string, int, CancellationToken, Task<SkillCanonicalTagBindingsResult?>> read)
        {
            var api = DispatchProxy.Create<IContextHubApiClient, FileMetadataReadTests.FileReadApiProxy>();
            ((FileMetadataReadTests.FileReadApiProxy)(object)api).Handler = (method, args) => method switch
            {
                nameof(IContextHubApiClient.GetSkillCanonicalTagsAsync) => read((Guid)args[0]!, (string)args[1]!, (int)args[2]!, (CancellationToken)args[4]!),
                nameof(IContextHubApiClient.GetProjectInformationProjectsAsync) => Task.FromResult<IReadOnlyList<ProjectInformationListItem>>(
                    [Project("one"), Project("ONE"), Project("other"), Project("default"), Project("shared"), Project("user"), Project("*")]),
                _ => throw new InvalidOperationException(method)
            };
            _services = new ServiceCollection().AddLogging().AddSingleton(api).BuildServiceProvider();
            Renderer = new WorkItemEditorTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        private static ProjectInformationListItem Project(string project)
            => new(new(Guid.NewGuid(), project, project, "", DateTimeOffset.UnixEpoch), 0);

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeRendererAsync();
            _services.Dispose();
        }
    }

    private sealed class Handler(SkillCanonicalTagBindingsResult page) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(page) });
        }
    }
}
