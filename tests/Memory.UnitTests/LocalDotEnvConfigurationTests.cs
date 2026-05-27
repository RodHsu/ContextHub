using FluentAssertions;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class LocalDotEnvConfigurationTests
{
    [Fact]
    public void ReadDotEnv_Should_Parse_Quoted_And_Exported_Values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.env");
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
            File.Delete(path);
        }
    }
}
