using Microsoft.AspNetCore.Http;

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
    private DashboardBrowserTestProfile? _cached;

    public DashboardBrowserTestProfileAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public DashboardBrowserTestProfile Current
    {
        get
        {
            _cached ??= ResolveProfile();
            return _cached.Value;
        }
    }

    public DashboardBrowserTestProfile GetProfile() => Current;

    private DashboardBrowserTestProfile ResolveProfile()
    {
        var raw = _httpContextAccessor.HttpContext?.Request.Query["uiProfile"].ToString();
        return Enum.TryParse<DashboardBrowserTestProfile>(raw, ignoreCase: true, out var profile)
            ? profile
            : DashboardBrowserTestProfile.Normal;
    }
}
