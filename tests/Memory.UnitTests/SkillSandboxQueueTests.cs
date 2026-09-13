using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class SkillSandboxQueueTests
{
    [Fact]
    public async Task Queue_runner_should_accept_only_a_hash_bound_signed_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContextHubTests", Guid.NewGuid().ToString("N"));
        var key = new string('k', 40);
        try
        {
            var options = Options.Create(new SkillSandboxOptions
            {
                Enabled = true,
                QueueRoot = root,
                ReceiptKey = key,
                MaximumWaitSeconds = 5,
                PollIntervalMilliseconds = 25
            });
            var bundle = Bundle();
            var hash = PortableSkillBundleValidator.ComputeContentHash(bundle);
            var runner = new FileQueueSkillSandboxSelfTestRunner(options);
            var run = runner.RunAsync(new(hash, bundle, new("scripts/check.sh", [], 1)), CancellationToken.None);

            var requestPath = await WaitForSingleFileAsync(Path.Combine(root, "requests"));
            var request = JsonSerializer.Deserialize<SkillSandboxQueueRequest>(await File.ReadAllTextAsync(requestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            SkillSandboxQueueProtocol.Verify(request, key).Should().BeTrue();
            var receipt = new SkillSandboxQueueReceipt(request.JobId, hash, true, true, string.Empty, "PASS", 12, DateTimeOffset.UtcNow);
            receipt = receipt with { Signature = SkillSandboxQueueProtocol.Sign(receipt, key) };
            await SkillSandboxQueueProtocol.WriteAtomicAsync(Path.Combine(root, "results", $"{request.JobId}.json"), receipt, CancellationToken.None);

            var result = await run;
            result.Executed.Should().BeTrue();
            result.Passed.Should().BeTrue();
            result.ReceiptId.Should().Be(request.JobId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Queue_runner_should_fail_closed_for_tampered_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContextHubTests", Guid.NewGuid().ToString("N"));
        var key = new string('r', 40);
        try
        {
            var runner = new FileQueueSkillSandboxSelfTestRunner(Options.Create(new SkillSandboxOptions
            {
                Enabled = true,
                QueueRoot = root,
                ReceiptKey = key,
                MaximumWaitSeconds = 5,
                PollIntervalMilliseconds = 25
            }));
            var bundle = Bundle();
            var hash = PortableSkillBundleValidator.ComputeContentHash(bundle);
            var run = runner.RunAsync(new(hash, bundle, new("scripts/check.sh", [], 1)), CancellationToken.None);
            var requestPath = await WaitForSingleFileAsync(Path.Combine(root, "requests"));
            var jobId = Path.GetFileNameWithoutExtension(requestPath);
            var receipt = new SkillSandboxQueueReceipt(jobId, hash, true, true, string.Empty, "forged", 1, DateTimeOffset.UtcNow, Signature: new string('0', 64));
            await SkillSandboxQueueProtocol.WriteAtomicAsync(Path.Combine(root, "results", $"{jobId}.json"), receipt, CancellationToken.None);

            var result = await run;
            result.Executed.Should().BeFalse();
            result.FailureCode.Should().Be("ReceiptInvalid");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WaitForSingleFileAsync(string directory)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").FirstOrDefault() is { } path) return path;
            await Task.Delay(25);
        }
        throw new TimeoutException("Sandbox request was not materialized.");
    }

    private static PortableSkillBundle Bundle() => new([
        new("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes("---\nname: Queue\ndescription: Queue test\n---\n"))),
        new("scripts/check.sh", Convert.ToBase64String(Encoding.UTF8.GetBytes("exit 0")), true)
    ]);
}
