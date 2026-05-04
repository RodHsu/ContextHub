using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Memory.Dashboard.Services.Testing;

internal enum DashboardBrowserTestProfile
{
    Normal,
    Empty,
    Dense
}

internal sealed class DashboardBrowserTestProfileAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly NavigationManager _navigationManager;
    private DashboardBrowserTestProfile? _cachedExplicitProfile;

    public DashboardBrowserTestProfileAccessor(IHttpContextAccessor httpContextAccessor, NavigationManager navigationManager)
    {
        _httpContextAccessor = httpContextAccessor;
        _navigationManager = navigationManager;
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

        if (TryResolveNavigationProfile(out var navigationProfile))
        {
            _cachedExplicitProfile = navigationProfile;
            return navigationProfile;
        }

        return _cachedExplicitProfile ?? DashboardBrowserTestProfile.Normal;
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

            return TryReadExplicitProfile(
                QueryHelpers.ParseQuery(uri.Query).TryGetValue("uiProfile", out var profileValues)
                    ? profileValues.ToString()
                    : null,
                out profile);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadExplicitProfile(string? raw, out DashboardBrowserTestProfile profile)
        => Enum.TryParse(raw, ignoreCase: true, out profile);
}
