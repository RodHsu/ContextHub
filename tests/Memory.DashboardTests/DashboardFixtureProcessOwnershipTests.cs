using System.Diagnostics;
using FluentAssertions;

namespace Memory.DashboardTests;

public sealed class DashboardFixtureProcessOwnershipTests
{
    [Fact]
    public async Task Dispose_should_close_the_original_safe_handle_of_a_naturally_exited_owned_process()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        var started = false;
        var exitedNaturally = false;
        try
        {
            started = process.Start();
            started.Should().BeTrue();
            var originalHandle = process.SafeHandle;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            exitedNaturally = true;
            process.HasExited.Should().BeTrue();
            process.ExitCode.Should().Be(0);
            originalHandle.IsClosed.Should().BeFalse();

            var fixture = new DashboardBrowserFixture(process);
            await fixture.DisposeAsync();

            originalHandle.IsClosed.Should().BeTrue();
        }
        finally
        {
            try
            {
                if (started && !exitedNaturally && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
