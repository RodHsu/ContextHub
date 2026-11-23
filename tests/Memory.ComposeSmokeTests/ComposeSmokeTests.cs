using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace Memory.ComposeSmokeTests;

internal sealed class ComposeSmokeFactAttribute : FactAttribute
{
    public ComposeSmokeFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_COMPOSE_SMOKE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set RUN_COMPOSE_SMOKE_TESTS=1 to run the Compose smoke test.";
        }
    }
}

public sealed class ComposeSmokeTests
{
    private const string Override = """
        services:
          mcp-docker-proxy:
            image: !reset null
          dashboard-docker-proxy:
            image: !reset null
          embedding-service:
            image: !reset null
            ports: ["127.0.0.1::8081"]
          postgres:
            image: !reset null
            ports: !reset []
          redis:
            ports: !reset []
          mcp-server:
            image: !reset null
          skill-sandbox:
            image: !reset null
          chatgpt-gateway:
            image: !reset null
          automation-gateway:
            image: !reset null
          worker:
            image: !reset null
          dashboard:
            image: !reset null
        """;

    [ComposeSmokeFact]
    public async Task Docker_Compose_Should_Be_Healthy()
    {
        var root = FindRepositoryRoot();
        var project = "contexthub-smoke-" + Guid.NewGuid().ToString("N");
        var composeFile = Path.Combine(root, "docker-compose.yml");
        using var scratch = new TemporaryDirectory(project);
        var overrideFile = Path.Combine(scratch.Path, "smoke.yml");
        await File.WriteAllTextAsync(overrideFile, Override);

        var composeVariables = Regex.Matches(await File.ReadAllTextAsync(composeFile), @"\$\{([A-Z][A-Z0-9_]*)")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        const string username = "smoke-admin";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var environment = new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            ["CONTEXTHUB_SECRET_KEY"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["CONTEXTHUB_SECURITY_BOOTSTRAP_TOKEN"] = token,
            ["DASHBOARD_API_TOKEN"] = token,
            ["DASHBOARD_ADMIN_USERNAME"] = username,
            ["DASHBOARD_ADMIN_PASSWORD_HASH"] = new PasswordHasher<object>().HashPassword(new object(), password),
            ["DASHBOARD_COMPOSE_PROJECT"] = project
        };
        foreach (var service in new[] { "MCP_SERVER", "DASHBOARD", "CHATGPT_GATEWAY", "AUTOMATION_GATEWAY" })
        {
            environment[service + "_HOST_BIND"] = "127.0.0.1";
            environment[service + "_HOST_PORT"] = "0";
        }

        Task<string> ComposeAsync(TimeSpan timeout, params string[] arguments) =>
            RunProcessAsync(root, timeout, composeVariables, environment,
                ["compose", "--env-file", Path.Combine(root, ".env.example"), "-f", composeFile, "-f", overrideFile,
                    "-p", project, .. arguments]);

        async Task<Uri> GetEndpointAsync(string service, string containerPort)
        {
            var address = await ComposeAsync(TimeSpan.FromSeconds(15), "port", service, containerPort);
            var endpoint = IPEndPoint.Parse(address.Trim());
            endpoint.Address.Should().Be(IPAddress.Loopback);
            endpoint.Port.Should().BePositive();
            return new UriBuilder("http", endpoint.Address.ToString(), endpoint.Port).Uri;
        }

        try
        {
            await ComposeAsync(TimeSpan.FromMinutes(15), "up", "-d", "--build", "--wait", "--wait-timeout", "360");
            var mcp = await GetEndpointAsync("mcp-server", "8080");
            var embedding = await GetEndpointAsync("embedding-service", "8081");
            var dashboard = await GetEndpointAsync("dashboard", "8088");
            var cookieContainer = new CookieContainer();
            using var dashboardClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                UseCookies = true,
                CookieContainer = cookieContainer,
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            var lastFailure = "no probe executed";
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri(mcp, "/health/ready"));
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(mcp, "/api/status"));
                        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var statusResponse = await client.SendAsync(statusRequest);
                        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        var status = await statusResponse.Content.ReadFromJsonAsync<SystemStatusResult>();
                        status.Should().NotBeNull();
                        status!.EmbeddingProfile.Should().Be("compact");
                        status.Dimensions.Should().Be(384);
                        status.ExecutionProvider.Should().Be("CPUExecutionProvider");
                        status.BatchSize.Should().Be(8);
                        status.BatchingEnabled.Should().BeTrue();

                        var embeddingInfo = await client.GetFromJsonAsync<EmbeddingServiceInfoResult>(new Uri(embedding, "/info"));
                        embeddingInfo.Should().NotBeNull();
                        embeddingInfo!.ExecutionProvider.Should().Be("CPUExecutionProvider");
                        embeddingInfo.BatchSize.Should().Be(8);
                        embeddingInfo.BatchingEnabled.Should().BeTrue();

                        using var batchEmbedResponse = await client.PostAsJsonAsync(
                            new Uri(embedding, "/embed/batch"),
                            new BatchEmbeddingServiceEmbedRequest(
                            [
                                new EmbeddingServiceEmbedRequest("compose smoke batch item 1", EmbeddingPurpose.Document),
                                new EmbeddingServiceEmbedRequest("compose smoke batch item 2", EmbeddingPurpose.Document)
                            ]));
                        batchEmbedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        var batchEmbed = await batchEmbedResponse.Content.ReadFromJsonAsync<BatchEmbeddingServiceEmbedResponse>();
                        batchEmbed.Should().NotBeNull();
                        batchEmbed!.Results.Should().HaveCount(2);
                        batchEmbed.Dimensions.Should().Be(384);

                        using var performanceRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(mcp, "/api/performance/measure"))
                        {
                            Content = JsonContent.Create(new PerformanceMeasureRequest(
                                Query: "compose smoke verification",
                                Document: "Measure the current runtime profile and verify the compose deployment is healthy.",
                                SearchLimit: 3,
                                WarmupIterations: 0,
                                MeasurementIterations: 1))
                        };
                        performanceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var performanceResponse = await client.SendAsync(performanceRequest);

                        performanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        using var dashboardReadyRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(dashboard, "/health/ready"));
                        dashboardReadyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        using var dashboardReady = await client.SendAsync(dashboardReadyRequest);
                        dashboardReady.StatusCode.Should().Be(HttpStatusCode.OK);

                        using var loginPage = await dashboardClient.GetAsync(new Uri(dashboard, "/login"));
                        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
                        var loginHtml = await loginPage.Content.ReadAsStringAsync();
                        loginHtml.Should().Contain("ContextHub");
                        var antiforgeryToken = ExtractAntiforgeryToken(loginHtml);
                        EnsureAntiforgeryCookie(cookieContainer, dashboard);

                        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(dashboard, "/account/login"))
                        {
                            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["__RequestVerificationToken"] = antiforgeryToken,
                                ["Username"] = username,
                                ["Password"] = password,
                                ["ReturnUrl"] = "/"
                            })
                        };

                        using var loginResponse = await dashboardClient.SendAsync(loginRequest);
                        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
                        loginResponse.Headers.Location.Should().NotBeNull();
                        loginResponse.Headers.Location!.OriginalString.Should().Be("/");

                        using var overviewResponse = await dashboardClient.GetAsync(dashboard);
                        overviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        var overviewHtml = await overviewResponse.Content.ReadAsStringAsync();
                        overviewHtml.Should().Contain("工作總覽").And.Contain("/account/logout");
                        return;
                    }

                    lastFailure = $"status {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
                    lastFailure = ex.Message;
                }

                await Task.Delay(1000);
            }

            false.Should().BeTrue($"docker compose services never became healthy; last probe result: {lastFailure}");
        }
        finally
        {
            await ComposeAsync(TimeSpan.FromMinutes(3), "down", "-v", "--rmi", "local");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextHub.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the ContextHub repository for the Compose smoke test.");
    }

    private static async Task<string> RunProcessAsync(string workingDirectory, TimeSpan timeout, string[] composeVariables,
        IReadOnlyDictionary<string, string> environment, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var name in composeVariables)
        {
            process.StartInfo.Environment.Remove(name);
        }
        foreach (var (name, value) in environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Docker {string.Join(' ', arguments)} timed out.{Environment.NewLine}{await outputTask}{Environment.NewLine}{await errorTask}");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker {string.Join(' ', arguments)} failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
        return output;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success)
        {
            throw new InvalidOperationException("Dashboard login page did not render an antiforgery token.");
        }

        return match.Groups[1].Value;
    }

    private static void EnsureAntiforgeryCookie(CookieContainer cookieContainer, Uri dashboard)
    {
        var cookies = cookieContainer.GetCookies(dashboard);
        var cookie = cookies.Cast<Cookie>()
            .FirstOrDefault(x => x.Name.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase));
        if (cookie is null || string.IsNullOrWhiteSpace(cookie.Value))
        {
            throw new InvalidOperationException("Dashboard antiforgery cookie was not found.");
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory(prefix).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
