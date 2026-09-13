using Memory.Infrastructure;
using Memory.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<SkillSandboxOptions>(builder.Configuration.GetSection(SkillSandboxOptions.SectionName));
builder.Services.AddHostedService<SkillSandboxQueueHostedService>();
if (args.Contains("--self-check", StringComparer.Ordinal))
{
    builder.Services.AddSingleton<ISkillSandboxSelfTestRunner, FileQueueSkillSandboxSelfTestRunner>();
    using var host = builder.Build();
    await host.StartAsync();
    var runner = host.Services.GetRequiredService<ISkillSandboxSelfTestRunner>();
    var passing = await RunCheckAsync("printf 'sandbox-pass'; test \"$(id -u)\" -eq 65534; test \"$(ls /sys/class/net | wc -l)\" -eq 1; test -e /sys/class/net/lo", 5);
    var mutation = await RunCheckAsync("printf 'mutation' >> SKILL.md", 5);
    var timeout = await RunCheckAsync("sleep 10", 1);
    var results = new { Passing = passing, Mutation = mutation, Timeout = timeout };
    Console.WriteLine(JsonSerializer.Serialize(results));
    await host.StopAsync();
    Environment.ExitCode = passing is { Executed: true, Passed: true } &&
                           mutation is { Executed: true, Passed: false, FailureCode: "BundleMutationDetected" } &&
                           timeout is { Executed: true, Passed: false, FailureCode: "TimedOut" } ? 0 : 1;

    async Task<SkillSandboxSelfTestResult> RunCheckAsync(string script, int timeoutSeconds)
    {
        var bundle = new PortableSkillBundle([
            new("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes("---\nname: Sandbox self-check\ndescription: Isolated runtime check\n---\n"))),
            new("scripts/check.sh", Convert.ToBase64String(Encoding.UTF8.GetBytes($"#!/bin/sh\n{script}")), true)
        ]);
        return await runner.RunAsync(
            new(PortableSkillBundleValidator.ComputeContentHash(bundle), bundle, new("scripts/check.sh", [], timeoutSeconds)), CancellationToken.None);
    }
}
else
{
    await builder.Build().RunAsync();
}
