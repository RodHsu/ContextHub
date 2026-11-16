using DotNet.Testcontainers.Builders;
using Memory.Tests.Shared;

namespace Memory.IntegrationTests;

public sealed class RequestArrivalNativeClockTests
{
    [DockerRequiredFact]
    public async Task Real_linux_clock_reads_have_ordered_raw_brackets_and_stable_native_domain()
    {
        await using var container = new ContainerBuilder("mcr.microsoft.com/dotnet/runtime:10.0")
            .WithCommand("/bin/sh", "-c", "sleep 120")
            .WithResourceMapping(Path.Combine(AppContext.BaseDirectory, "Memory.ArrivalNativeClockProbe.dll"), "/probe/")
            .WithResourceMapping(Path.Combine(AppContext.BaseDirectory, "Memory.ArrivalNativeClockProbe.runtimeconfig.json"), "/probe/")
            .WithResourceMapping(Path.Combine(AppContext.BaseDirectory, "Memory.ArrivalNativeClockProbe.deps.json"), "/probe/")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig.NetworkMode = "none";
                parameters.HostConfig.Memory = 128 * 1024 * 1024;
                parameters.HostConfig.CapDrop = ["ALL"];
            })
            .Build();
        await container.StartAsync();
        var result = await container.ExecAsync(["dotnet", "/probe/Memory.ArrivalNativeClockProbe.dll"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ARRIVAL_NATIVE_CLOCK_READ_PASS samples=64 domainStable=true settingsChanged=false", result.Stdout.Trim());
        Assert.Empty(result.Stderr);
    }
}
