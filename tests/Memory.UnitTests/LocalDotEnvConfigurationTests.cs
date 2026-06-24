using FluentAssertions;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class LocalDotEnvConfigurationTests
{
    [Fact]
    public void ReadDotEnv_Should_Parse_Quoted_And_Exported_Values()
    {
        var testDirectory = CreateRepoTestDataPath("dot-env", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(testDirectory, "test.env");
        try
        {
            File.WriteAllLines(path,
            [
                "# ignored",
                "DASHBOARD_API_TOKEN=\"dashboard-token\"",
                "export CONTEXTHUB_SECURITY_BOOTSTRAP_TOKEN='bootstrap-token'",
                "CONTEXTHUB_SECURITY_BOOTSTRAP_USERNAME=dashboard-service"
            ]);

            var values = LocalDotEnvConfiguration.ReadDotEnv(path);

            values["DASHBOARD_API_TOKEN"].Should().Be("dashboard-token");
            values["CONTEXTHUB_SECURITY_BOOTSTRAP_TOKEN"].Should().Be("bootstrap-token");
            values["CONTEXTHUB_SECURITY_BOOTSTRAP_USERNAME"].Should().Be("dashboard-service");
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string CreateRepoTestDataPath(params string[] segments)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var pathSegments = new[] { repoRoot, ".agent", "local", "test-results", "unit-tests" }
            .Concat(segments)
            .ToArray();
        var path = Path.Combine(pathSegments);
        Directory.CreateDirectory(path);
        return path;
    }
}
