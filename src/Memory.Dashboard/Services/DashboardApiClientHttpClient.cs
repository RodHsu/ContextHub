using System.Net.Http.Headers;
using Memory.Application;

namespace Memory.Dashboard.Services;

public static class DashboardApiClientHttpClient
{
    public static void Configure(HttpClient client, DashboardOptions options)
    {
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Add(RequestTrafficConstants.DashboardRequestHeader, RequestTrafficConstants.DashboardRequestHeaderValue);

        if (!string.IsNullOrWhiteSpace(options.ApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken.Trim());
        }
    }
}
