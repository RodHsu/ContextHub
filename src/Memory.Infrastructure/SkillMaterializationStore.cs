using System.Collections.Concurrent;
using Memory.Application;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class SkillRuntimeOptions
{
    public const string SectionName = "ContextHub:Skills";
    public string MaterializationRoot { get; set; } = Path.Combine(Path.GetTempPath(), "ContextHub", "skills");
}

public sealed class FileSystemSkillMaterializationStore(IOptions<SkillRuntimeOptions> options) : ISkillMaterializationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ContentLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ExecutionLocks = [];
    private readonly string root = Path.GetFullPath(options.Value.MaterializationRoot);

    public async Task<string> MaterializeAsync(Guid executionId, string contentHash, PortableSkillBundle bundle, CancellationToken cancellationToken)
    {
        ValidateHash(contentHash);
        var actualContentHash = PortableSkillBundleValidator.ComputeContentHash(bundle);
        if (!string.Equals(contentHash, actualContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Materialization bundle does not match the pinned contentHash.");
        }
        var contentRelative = Path.Combine("cache", contentHash);
        var contentPath = ResolveInsideRoot(contentRelative);
        var contentLock = ContentLocks.GetOrAdd(contentHash, _ => new SemaphoreSlim(1, 1));
        await contentLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(contentPath) && !BundleMatchesDirectory(contentPath, bundle))
            {
                DeleteTree(contentPath);
            }
            if (!Directory.Exists(contentPath))
            {
                var staging = ResolveInsideRoot(Path.Combine("staging", $"{contentHash}-{Guid.NewGuid():N}"));
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var file in bundle.Files)
                    {
                        var normalized = PortableSkillBundleValidator.NormalizeRelativePath(file.Path)
                            ?? throw new InvalidOperationException("Unsafe portable bundle path reached materialization.");
                        var destination = ResolveInside(staging, normalized);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await File.WriteAllBytesAsync(destination, Convert.FromBase64String(file.ContentBase64), cancellationToken);
                        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
                    try
                    {
                        Directory.Move(staging, contentPath);
                    }
                    catch (IOException) when (Directory.Exists(contentPath))
                    {
                        DeleteTree(staging);
                    }
                }
                catch
                {
                    DeleteTree(staging);
                    throw;
                }
            }
        }
        finally
        {
            contentLock.Release();
        }

        var executionRelative = Path.Combine("executions", executionId.ToString("D"), contentHash);
        var executionPath = ResolveInsideRoot(executionRelative);
        var executionLock = ExecutionLocks.GetOrAdd(executionId, _ => new SemaphoreSlim(1, 1));
        await executionLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(executionPath) && !BundleMatchesDirectory(executionPath, bundle))
            {
                DeleteTree(executionPath);
            }
            if (!Directory.Exists(executionPath))
            {
                var staging = ResolveInsideRoot(Path.Combine("staging", $"execution-{executionId:N}-{contentHash}-{Guid.NewGuid():N}"));
                Directory.CreateDirectory(staging);
                try
                {
                    CopyTreeReadOnly(contentPath, staging);
                    if (!BundleMatchesDirectory(staging, bundle))
                    {
                        DeleteTree(contentPath);
                        throw new InvalidOperationException("Materialization cache failed contentHash integrity validation.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(executionPath)!);
                    try
                    {
                        Directory.Move(staging, executionPath);
                    }
                    catch (IOException) when (Directory.Exists(executionPath))
                    {
                        DeleteTree(staging);
                    }
                }
                catch
                {
                    DeleteTree(staging);
                    throw;
                }
            }
        }
        finally
        {
            executionLock.Release();
        }

        return executionRelative.Replace('\\', '/');
    }

    public async Task CleanupExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var executionLock = ExecutionLocks.GetOrAdd(executionId, _ => new SemaphoreSlim(1, 1));
        await executionLock.WaitAsync(cancellationToken);
        try
        {
            var executionPath = ResolveInsideRoot(Path.Combine("executions", executionId.ToString("D")));
            DeleteTree(executionPath);
        }
        finally
        {
            executionLock.Release();
        }
    }

    private string ResolveInsideRoot(string relativePath) => ResolveInside(root, relativePath);

    private static string ResolveInside(string parent, string relativePath)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(parent, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Materialization path escaped the configured Skills root.");
        }
        return fullPath;
    }

    private static void CopyTreeReadOnly(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
    }

    private static bool BundleMatchesDirectory(string directory, PortableSkillBundle bundle)
    {
        var expected = bundle.Files.ToDictionary(
            file => PortableSkillBundleValidator.NormalizeRelativePath(file.Path)!,
            file => Convert.FromBase64String(file.ContentBase64),
            StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(directory, file).Replace('\\', '/'),
                file => file,
                StringComparer.Ordinal);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys))
        {
            return false;
        }

        return expected.All(item => File.ReadAllBytes(actual[item.Key]).AsSpan().SequenceEqual(item.Value));
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateHash(string contentHash)
    {
        if (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Materialization requires a canonical SHA-256 contentHash.");
    }
}
