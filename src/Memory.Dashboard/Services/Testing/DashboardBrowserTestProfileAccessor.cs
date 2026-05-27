using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.WebUtilities;

namespace Memory.Dashboard.Services.Testing;

internal enum DashboardBrowserTestProfile
{
    Normal,
    Empty,
    Dense,
    GraphDemo
}

internal sealed class DashboardBrowserTestProfileAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly NavigationManager _navigationManager;
    private readonly DashboardBrowserTestProfile _defaultProfile;
    private DashboardBrowserTestProfile? _cachedExplicitProfile;

    public DashboardBrowserTestProfileAccessor(
        IHttpContextAccessor httpContextAccessor,
        NavigationManager navigationManager,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _navigationManager = navigationManager;
        _defaultProfile = TryReadExplicitProfile(configuration["Dashboard:BrowserTestDefaultProfile"], out var profile)
            ? profile
            : DashboardBrowserTestProfile.Normal;
    }

    public DashboardBrowserTestProfile Current => ResolveProfile();

    public DashboardBrowserTestProfile GetProfile() => Current;

    private DashboardBrowserTestProfile ResolveProfile()
    {
        if (TryReadExplicitProfile(_httpContextAccessor.HttpContext?.Request.Query["uiProfile"].ToString(), out var httpProfile))
        {
            _cachedExplicitProfile = httpProfile;
            return httpProfile;
        }

        if (TryResolveSelectedGraphDemoProfile(_httpContextAccessor.HttpContext?.Request.Query["selected"].ToString(), out var selectedProfile))
        {
            _cachedExplicitProfile = selectedProfile;
            return selectedProfile;
        }

        if (TryResolveNavigationProfile(out var navigationProfile))
        {
            _cachedExplicitProfile = navigationProfile;
            return navigationProfile;
        }

        return _cachedExplicitProfile ?? _defaultProfile;
    }

    private bool TryResolveNavigationProfile(out DashboardBrowserTestProfile profile)
    {
        profile = default;

        try
        {
            if (!Uri.TryCreate(_navigationManager.Uri, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var query = QueryHelpers.ParseQuery(uri.Query);
            if (TryReadExplicitProfile(
                query.TryGetValue("uiProfile", out var profileValues)
                    ? profileValues.ToString()
                    : null,
                out profile))
            {
                return true;
            }

            return TryResolveSelectedGraphDemoProfile(
                query.TryGetValue("selected", out var selectedValues)
                    ? selectedValues.ToString()
                    : null,
                out profile);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryResolveSelectedGraphDemoProfile(string? raw, out DashboardBrowserTestProfile profile)
    {
        profile = default;
        if (!Guid.TryParse(raw, out var selectedId))
        {
            return false;
        }

        for (var index = 1; index <= 12; index++)
        {
            if (selectedId == Guid.Parse($"10000000-0000-0000-0000-{index:000000000000}"))
            {
                profile = DashboardBrowserTestProfile.GraphDemo;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadExplicitProfile(string? raw, out DashboardBrowserTestProfile profile)
        => Enum.TryParse(raw, ignoreCase: true, out profile);
}
