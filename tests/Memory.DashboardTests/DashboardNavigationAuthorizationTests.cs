using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Dashboard.Components.Layout;
using Memory.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Memory.DashboardTests;

public sealed class DashboardNavigationAuthorizationTests
{
    [Fact]
    public void Member_navigation_must_not_advertise_routes_denied_by_the_existing_middleware()
    {
        var member = User("Member");
        var routes = DashboardNavigation.VisibleGroups(DashboardRouteAuthorization.IsAdmin(member))
            .SelectMany(group => group.Items).Select(item => item.Href).ToArray();

        routes.Should().BeEquivalentTo(new[] { "/memories", "/graph", "/preferences", "/account/tokens" });
        foreach (var route in routes)
        {
            DashboardRouteAuthorization.CanAccess(member, new PathString(route)).Should().BeTrue(route);
            DashboardRouteAuthorization.CanAccess(member, new PathString(route + "/detail")).Should().BeTrue(route);
        }
    }

    [Theory]
    [InlineData("/project-information")]
    [InlineData("/project-tree")]
    [InlineData("/discussions")]
    [InlineData("/files")]
    public void Administrative_project_routes_stay_in_the_catalog_but_not_in_member_navigation(string route)
    {
        DashboardNavigation.Groups.SelectMany(group => group.Items).Should().ContainSingle(item => item.Href == route && item.RequiresAdmin);
        DashboardNavigation.VisibleGroups(false).SelectMany(group => group.Items).Should().NotContain(item => item.Href == route);
        DashboardRouteAuthorization.CanAccess(User("Member"), new PathString(route)).Should().BeFalse();
        DashboardRouteAuthorization.CanAccess(User("Member"), new PathString(route + "/detail")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    [InlineData("DashboardAdmin")]
    public void Every_existing_management_identity_keeps_the_complete_navigation_and_direct_routes(string role)
    {
        var user = User(role);
        DashboardRouteAuthorization.IsAdmin(user).Should().BeTrue();
        var allRoutes = DashboardNavigation.Groups.SelectMany(group => group.Items).Select(item => item.Href).ToArray();
        var visibleRoutes = DashboardNavigation.VisibleGroups(DashboardRouteAuthorization.IsAdmin(user))
            .SelectMany(group => group.Items).Select(item => item.Href).ToArray();

        visibleRoutes.Should().BeEquivalentTo(allRoutes);
        foreach (var route in visibleRoutes)
            DashboardRouteAuthorization.CanAccess(user, new PathString(route)).Should().BeTrue(route);
    }

    [Fact]
    public void Non_management_identity_cannot_reach_a_management_route_by_a_similarly_named_prefix()
    {
        var member = User("Member");
        DashboardRouteAuthorization.CanAccess(member, "/memories-admin").Should().BeFalse();
        DashboardRouteAuthorization.CanAccess(member, "/graph-admin").Should().BeFalse();
        DashboardRouteAuthorization.CanAccess(member, "/preferences-admin").Should().BeFalse();
        DashboardRouteAuthorization.CanAccess(member, "/account/tokens-admin").Should().BeFalse();
    }

    [Fact]
    public void Anonymous_identity_has_no_management_identity_or_administrative_route_access()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        DashboardRouteAuthorization.IsAdmin(anonymous).Should().BeFalse();
        foreach (var route in new[] { "/", "/project-information", "/project-tree", "/discussions", "/files", "/secrets" })
            DashboardRouteAuthorization.CanAccess(anonymous, new PathString(route)).Should().BeFalse(route);
    }

    [Theory]
    [InlineData("Member", false)]
    [InlineData("Owner", true)]
    [InlineData("Admin", true)]
    [InlineData("DashboardAdmin", true)]
    public async Task Actual_navigation_and_native_search_share_the_existing_route_visibility(string role, bool management)
    {
        var html = await RenderLayoutAsync(User(role));
        html.Should().Contain("主要導覽").And.Contain("workspace-mobile-nav").And.Contain("workspace-command");
        foreach (var route in new[] { "/project-information", "/project-tree", "/discussions", "/files" })
            AssertNavigationLinks(html, route, management);
        foreach (var route in new[] { "/memories", "/graph", "/preferences", "/account/tokens" })
            AssertNavigationLinks(html, route, true);
    }

    private static void AssertNavigationLinks(string html, string route, bool visible)
    {
        var href = Regex.Escape($"href=\"{route}\"");
        Regex.Matches(html, $"<a\\b(?=[^>]*{href})(?=[^>]*class=\"nav-item(?: |\"))[^>]*>").Count.Should().Be(visible ? 2 : 0);
        Regex.Matches(html, $"<a\\b(?=[^>]*{href})(?=[^>]*class=\"command-route(?: |\"))[^>]*>").Count.Should().Be(visible ? 1 : 0);
    }

    [Fact]
    public async Task Actual_anonymous_layout_renders_neither_navigation_nor_the_command_launcher()
    {
        var html = await RenderLayoutAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        html.Should().NotContain("href=").And.NotContain("command-button").And.NotContain("command-palette");
    }

    private static async Task<string> RenderLayoutAsync(ClaimsPrincipal user)
    {
        using var services = new ServiceCollection().AddLogging().AddAuthorizationCore()
            .AddSingleton<NavigationManager, TestNavigationManager>()
            .AddSingleton<IJSRuntime, NoJavaScript>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<LayoutHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LayoutHost.User)] = user
            }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    public sealed class LayoutHost : ComponentBase
    {
        [Parameter] public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, "Value", Task.FromResult(new AuthenticationState(User)));
            builder.AddAttribute(2, "IsFixed", true);
            builder.AddAttribute(3, "ChildContent", (RenderFragment)(content =>
            {
                content.OpenComponent<MainLayout>(0);
                content.AddAttribute(1, "Body", (RenderFragment)(_ => { }));
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/memories");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private static ClaimsPrincipal User(string role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "synthetic-navigation-user"), new Claim(ClaimTypes.Role, role)], "synthetic"));
}
