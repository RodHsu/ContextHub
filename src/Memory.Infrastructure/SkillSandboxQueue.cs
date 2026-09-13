using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class SkillSandboxOptions
{
    public const string SectionName = "ContextHub:Skills:Sandbox";
    public bool Enabled { get; set; }
    public bool NetworkIsolationAttested { get; set; }
    public string QueueRoot { get; set; } = Path.Combine(Path.GetTempPath(), "ContextHub", "skill-sandbox-queue");
    public string ReceiptKey { get; set; } = string.Empty;
    public int MaximumWaitSeconds { get; set; } = 45;
    public int PollIntervalMilliseconds { get; set; } = 100;
}

internal sealed record SkillSandboxQueueRequest(
    string JobId,
    string ContentHash,
    PortableSkillBundle Bundle,
    SkillSandboxSelfTestDefinition Definition,
    DateTimeOffset CreatedAt,
    string ContractVersion = SkillSandboxContract.Version,
    string Signature = "");

internal sealed record SkillSandboxQueueReceipt(
    string JobId,
    string ContentHash,
    bool Executed,
    bool Passed,
    string FailureCode,
    string Summary,
    long DurationMilliseconds,
    DateTimeOffset CompletedAt,
    string ContractVersion = SkillSandboxContract.Version,
    string Signature = "");

public sealed class FileQueueSkillSandboxSelfTestRunner(IOptions<SkillSandboxOptions> configuredOptions) : ISkillSandboxSelfTestRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SkillSandboxOptions options = configuredOptions.Value;

    public async Task<SkillSandboxSelfTestResult> RunAsync(SkillSandboxSelfTestRequest request, CancellationToken cancellationToken)
    {
        if (!options.Enabled || !SkillSandboxQueueProtocol.HasValidKey(options.ReceiptKey))
        {
            return new(false, false, string.Empty, "SandboxUnavailable", "The isolated Skill sandbox queue is disabled or has no valid receipt key.", 0);
        }

        if (!string.Equals(request.ContentHash, PortableSkillBundleValidator.ComputeContentHash(request.Bundle), StringComparison.OrdinalIgnoreCase))
        {
            return new(false, false, string.Empty, "ContentHashMismatch", "The sandbox request did not match the pinned contentHash.", 0);
        }

        var root = Path.GetFullPath(options.QueueRoot);
        var requests = Path.Combine(root, "requests");
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(requests);
        Directory.CreateDirectory(results);
        var jobId = Guid.NewGuid().ToString("N");
        var unsigned = new SkillSandboxQueueRequest(jobId, request.ContentHash, request.Bundle, request.Definition, DateTimeOffset.UtcNow);
        var envelope = unsigned with { Signature = SkillSandboxQueueProtocol.Sign(unsigned, options.ReceiptKey) };
        var requestPath = Path.Combine(requests, $"{jobId}.json");
        await SkillSandboxQueueProtocol.WriteAtomicAsync(requestPath, envelope, cancellationToken);

        var resultPath = Path.Combine(results, $"{jobId}.json");
        var waitSeconds = Math.Clamp(options.MaximumWaitSeconds, request.Definition.TimeoutSeconds + 5, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
        try
        {
            while (!File.Exists(resultPath))
            {
                await Task.Delay(Math.Clamp(options.PollIntervalMilliseconds, 25, 1000), timeout.Token);
            }

            var receipt = JsonSerializer.Deserialize<SkillSandboxQueueReceipt>(await File.ReadAllTextAsync(resultPath, timeout.Token), JsonOptions)
                ?? throw new InvalidOperationException("Sandbox returned an empty receipt.");
            if (!SkillSandboxQueueProtocol.Verify(receipt, options.ReceiptKey) ||
                !string.Equals(receipt.ContractVersion, SkillSandboxContract.Version, StringComparison.Ordinal) ||
                !string.Equals(receipt.JobId, jobId, StringComparison.Ordinal) ||
                !string.Equals(receipt.ContentHash, request.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, false, jobId, "ReceiptInvalid", "The sandbox receipt signature or binding was invalid.", 0);
            }

            return new(receipt.Executed, receipt.Passed, receipt.JobId, receipt.FailureCode, receipt.Summary, receipt.DurationMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, jobId, "SandboxQueueTimeout", "The isolated Skill sandbox did not return a receipt before the bounded deadline.", 0);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

public sealed class SkillSandboxQueueHostedService(
    IOptions<SkillSandboxOptions> configuredOptions,
    ILogger<SkillSandboxQueueHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SkillSandboxOptions options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.NetworkIsolationAttested || !SkillSandboxQueueProtocol.HasValidKey(options.ReceiptKey))
        {
            throw new InvalidOperationException("Skill sandbox requires Enabled=true, a network-isolation attestation, and a receipt key of at least 32 characters.");
        }
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The production Skill sandbox worker must run in its hardened Linux container.");
        }

        var root = Path.GetFullPath(options.QueueRoot);
        var requests = Path.Combine(root, "requests");
        var processing = Path.Combine(root, "processing");
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(requests);
        Directory.CreateDirectory(processing);
        Directory.CreateDirectory(results);
        var privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(root, privateDirectoryMode);
        File.SetUnixFileMode(requests, privateDirectoryMode);
        File.SetUnixFileMode(processing, privateDirectoryMode);
        File.SetUnixFileMode(results, privateDirectoryMode);
        await File.WriteAllTextAsync("/tmp/contexthub-skill-sandbox-ready", DateTimeOffset.UtcNow.ToString("O"), stoppingToken);

        foreach (var interruptedPath in Directory.EnumerateFiles(processing, "*.json").Order(StringComparer.Ordinal))
        {
            await ProcessClaimAsync(interruptedPath, results, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            CleanupStaleFiles(results, TimeSpan.FromMinutes(10));
            var handled = false;
            foreach (var requestPath in Directory.EnumerateFiles(requests, "*.json").Order(StringComparer.Ordinal))
            {
                handled = true;
                var claimedPath = Path.Combine(processing, Path.GetFileName(requestPath));
                try
                {
                    File.Move(requestPath, claimedPath);
                }
                catch (IOException)
                {
                    continue;
                }

                await ProcessClaimAsync(claimedPath, results, stoppingToken);
            }

            if (!handled)
            {
                await Task.Delay(Math.Clamp(options.PollIntervalMilliseconds, 25, 1000), stoppingToken);
            }
        }
    }

    private async Task ProcessClaimAsync(string claimedPath, string results, CancellationToken cancellationToken)
    {
        SkillSandboxQueueRequest? request = null;
        SkillSandboxQueueReceipt receipt;
        try
        {
            if (new FileInfo(claimedPath).Length > 15 * 1024 * 1024)
            {
                throw new InvalidOperationException("Sandbox request exceeded the queue envelope limit.");
            }
            request = JsonSerializer.Deserialize<SkillSandboxQueueRequest>(await File.ReadAllTextAsync(claimedPath, cancellationToken), JsonOptions)
                ?? throw new InvalidOperationException("Sandbox request was empty.");
            if (!SkillSandboxQueueProtocol.Verify(request, options.ReceiptKey))
            {
                throw new InvalidOperationException("Sandbox request signature was invalid.");
            }
            if (!string.Equals(request.ContractVersion, SkillSandboxContract.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Sandbox request contract version was unsupported.");
            }
            if (!string.Equals(request.JobId, Path.GetFileNameWithoutExtension(claimedPath), StringComparison.Ordinal) ||
                !Guid.TryParseExact(request.JobId, "N", out _) ||
                request.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-2) ||
                request.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                throw new InvalidOperationException("Sandbox request identity or freshness binding was invalid.");
            }
            if (!string.Equals(request.ContentHash, PortableSkillBundleValidator.ComputeContentHash(request.Bundle), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Sandbox request contentHash binding was invalid.");
            }
            ValidateDefinition(request);

            receipt = await ExecuteIsolatedAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Rejected Skill sandbox queue item {QueueItem}: {FailureType}", Path.GetFileName(claimedPath), ex.GetType().Name);
            var jobId = request?.JobId ?? Path.GetFileNameWithoutExtension(claimedPath);
            receipt = new(jobId, request?.ContentHash ?? string.Empty, false, false, "RequestRejected", "The sandbox request failed validation.", 0, DateTimeOffset.UtcNow);
        }

        var signed = receipt with { Signature = SkillSandboxQueueProtocol.Sign(receipt, options.ReceiptKey) };
        await SkillSandboxQueueProtocol.WriteAtomicAsync(Path.Combine(results, $"{receipt.JobId}.json"), signed, cancellationToken);
        File.Delete(claimedPath);
    }

    private static async Task<SkillSandboxQueueReceipt> ExecuteIsolatedAsync(SkillSandboxQueueRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var jobRoot = Path.Combine("/tmp", "contexthub-skill-jobs", request.JobId);
        var bundleRoot = Path.Combine(jobRoot, "bundle");
        var tempRoot = Path.Combine(jobRoot, "tmp");
        Directory.CreateDirectory(bundleRoot);
        Directory.CreateDirectory(tempRoot);
        try
        {
            foreach (var file in request.Bundle.Files)
            {
                var normalized = PortableSkillBundleValidator.NormalizeRelativePath(file.Path)
                    ?? throw new InvalidOperationException("Unsafe sandbox bundle path.");
                var target = ResolveInside(bundleRoot, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, Convert.FromBase64String(file.ContentBase64), cancellationToken);
            }

            var entrypoint = ResolveInside(bundleRoot, request.Definition.Entrypoint);
            if (!File.Exists(entrypoint))
            {
                throw new InvalidOperationException("Sandbox entrypoint was missing.");
            }

            await RunUtilityAsync("chown", ["-R", "65534:65534", jobRoot], cancellationToken);
            var start = new ProcessStartInfo("/usr/bin/setsid")
            {
                WorkingDirectory = bundleRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin";
            start.Environment["HOME"] = tempRoot;
            start.Environment["TMPDIR"] = tempRoot;
            start.Environment["LANG"] = "C.UTF-8";
            foreach (var argument in new[]
                     {
                         "/usr/bin/setpriv", "--reuid=65534", "--regid=65534", "--clear-groups", "--no-new-privs",
                         "/bin/sh", $"./{request.Definition.Entrypoint}"
                     })
            {
                start.ArgumentList.Add(argument);
            }
            foreach (var argument in request.Definition.Arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Sandbox process could not start.");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Definition.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TrySignalProcessGroupAsync(process.Id, "TERM");
                await Task.Delay(100, CancellationToken.None);
                await TrySignalProcessGroupAsync(process.Id, "KILL");
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { }
                return Receipt(false, "TimedOut", "Sandbox self-test exceeded its declared deadline.");
            }
            catch (OperationCanceledException)
            {
                await TrySignalProcessGroupAsync(process.Id, "KILL");
                throw;
            }

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (!BundleMatchesDirectory(bundleRoot, request.Bundle))
            {
                return Receipt(false, "BundleMutationDetected", "Sandbox self-test modified its immutable input bundle.");
            }

            var passed = process.ExitCode == 0;
            var summary = PortableSkillBundleValidator.BoundAndRedact(
                string.Join(' ', new[] { standardOutput, standardError }.Where(value => !string.IsNullOrWhiteSpace(value))), 1000);
            return Receipt(passed, passed ? string.Empty : "NonZeroExit", string.IsNullOrWhiteSpace(summary) ? $"Sandbox process exited with code {process.ExitCode}." : summary);
        }
        finally
        {
            try { Directory.Delete(jobRoot, recursive: true); } catch (IOException) { }
        }

        SkillSandboxQueueReceipt Receipt(bool passed, string failureCode, string summary)
            => new(request.JobId, request.ContentHash, true, passed, failureCode, summary, stopwatch.ElapsedMilliseconds, DateTimeOffset.UtcNow);
    }

    private static void ValidateDefinition(SkillSandboxQueueRequest request)
    {
        var definition = request.Definition;
        var normalized = PortableSkillBundleValidator.NormalizeRelativePath(definition.Entrypoint);
        if (!string.Equals(normalized, definition.Entrypoint, StringComparison.Ordinal) ||
            !definition.Entrypoint.StartsWith("scripts/", StringComparison.Ordinal) ||
            !definition.Entrypoint.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
            definition.TimeoutSeconds is < 1 or > 30 ||
            definition.Arguments.Count > 16 ||
            definition.Arguments.Any(argument => argument.Length > 256 || argument.Contains('\0')) ||
            !request.Bundle.Files.Any(file => file.Executable &&
                string.Equals(PortableSkillBundleValidator.NormalizeRelativePath(file.Path), definition.Entrypoint, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Sandbox execution definition was outside the bounded allowlist.");
        }
    }

    private static async Task RunUtilityAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Required sandbox utility '{executable}' could not start.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Required sandbox utility '{executable}' failed.");
    }

    private static async Task TrySignalProcessGroupAsync(int processGroupId, string signal)
    {
        try
        {
            await RunUtilityAsync("/usr/bin/kill", [$"-{signal}", "--", $"-{processGroupId}"], CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The bounded process group may already have exited between the deadline and signal delivery.
        }
    }

    private static void CleanupStaleFiles(string directory, TimeSpan maximumAge)
    {
        var cutoff = DateTime.UtcNow - maximumAge;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException)
            {
                // Another queue participant may be reading or deleting the file.
            }
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Sandbox path escaped the job root.");
        return resolved;
    }

    private static bool BundleMatchesDirectory(string root, PortableSkillBundle bundle)
    {
        var expected = bundle.Files.ToDictionary(file => PortableSkillBundleValidator.NormalizeRelativePath(file.Path)!, file => Convert.FromBase64String(file.ContentBase64), StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), StringComparer.Ordinal);
        return expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys) && expected.All(item => File.ReadAllBytes(actual[item.Key]).AsSpan().SequenceEqual(item.Value));
    }
}

internal static class SkillSandboxQueueProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool HasValidKey(string key) => Encoding.UTF8.GetByteCount(key.Trim()) >= 32;

    public static string Sign(SkillSandboxQueueRequest request, string key)
        => Compute(request with { Signature = string.Empty }, key);

    public static string Sign(SkillSandboxQueueReceipt receipt, string key)
        => Compute(receipt with { Signature = string.Empty }, key);

    public static bool Verify(SkillSandboxQueueRequest request, string key)
        => FixedEquals(request.Signature, Sign(request, key));

    public static bool Verify(SkillSandboxQueueReceipt receipt, string key)
        => FixedEquals(receipt.Signature, Sign(receipt, key));

    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
        File.Move(temporary, path, overwrite: false);
    }

    private static string Compute<T>(T value, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key.Trim()));
        return Convert.ToHexStringLower(hmac.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));
    }

    private static bool FixedEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
}
