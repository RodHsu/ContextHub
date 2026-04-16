using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.ComposeSmokeTests;

public sealed class ComposeSmokeTests
{
    [Fact]
    public async Task Docker_Compose_Should_Be_Healthy_When_Enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_COMPOSE_SMOKE_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        const string projectName = "contexthub-smoke";
        await RunProcessAsync("docker", $"compose -p {projectName} up -d --build", root, TimeSpan.FromMinutes(10));

        try
        {
            var cookieContainer = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                UseCookies = true,
                CookieContainer = cookieContainer,
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            var lastFailure = "no probe executed";
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var status = await client.GetFromJsonAsync<SystemStatusResult>("http://127.0.0.1:8080/api/status");
                        status.Should().NotBeNull();
                        status!.EmbeddingProfile.Should().Be("compact");
                        status.Dimensions.Should().Be(384);
                        status.ExecutionProvider.Should().Be("CPUExecutionProvider");
                        status.BatchSize.Should().Be(8);
                        status.BatchingEnabled.Should().BeTrue();

                        var embeddingInfo = await client.GetFromJsonAsync<EmbeddingServiceInfoResult>("http://127.0.0.1:8081/info");
                        embeddingInfo.Should().NotBeNull();
                        embeddingInfo!.ExecutionProvider.Should().Be("CPUExecutionProvider");
                        embeddingInfo.BatchSize.Should().Be(8);
                        embeddingInfo.BatchingEnabled.Should().BeTrue();

                        using var batchEmbedResponse = await client.PostAsJsonAsync(
                            "http://127.0.0.1:8081/embed/batch",
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

                        using var performanceResponse = await client.PostAsJsonAsync(
                            "http://127.0.0.1:8080/api/performance/measure",
                            new PerformanceMeasureRequest(
                                Query: "compose smoke verification",
                                Document: "Measure the current runtime profile and verify the compose deployment is healthy.",
                                SearchLimit: 3,
                                WarmupIterations: 0,
                                MeasurementIterations: 1));

                        performanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        using var dashboardReady = await client.GetAsync("http://127.0.0.1:8088/health/ready");
                        dashboardReady.StatusCode.Should().Be(HttpStatusCode.OK);

                        using var loginPage = await client.GetAsync("http://127.0.0.1:8088/login");
                        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
                        var loginHtml = await loginPage.Content.ReadAsStringAsync();
                        loginHtml.Should().Contain("ContextHub");
                        var token = ExtractAntiforgeryToken(loginHtml);
                        EnsureAntiforgeryCookie(cookieContainer);

                        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8088/account/login")
                        {
                            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["__RequestVerificationToken"] = token,
                                ["Username"] = "admin",
                                ["Password"] = "ContextHub!123",
                                ["ReturnUrl"] = "/"
                            })
                        };

                        using var loginResponse = await client.SendAsync(loginRequest);
                        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

                        using var overviewResponse = await client.GetAsync("http://127.0.0.1:8088/");
                        overviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                        var overviewHtml = await overviewResponse.Content.ReadAsStringAsync();
                        overviewHtml.Should().Contain("ContextHub 管理主控台");
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
            await RunProcessAsync("docker", $"compose -p {projectName} down -v", root, TimeSpan.FromMinutes(3));
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

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
            throw new TimeoutException($"Command '{fileName} {arguments}' timed out.");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed: {fileName} {arguments}{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
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

    private static void EnsureAntiforgeryCookie(CookieContainer cookieContainer)
    {
        var cookies = cookieContainer.GetCookies(new Uri("http://127.0.0.1:8088"));
        var cookie = cookies.Cast<Cookie>()
            .FirstOrDefault(x => x.Name.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase));
        if (cookie is null || string.IsNullOrWhiteSpace(cookie.Value))
        {
            throw new InvalidOperationException("Dashboard antiforgery cookie was not found.");
        }
    }
}
