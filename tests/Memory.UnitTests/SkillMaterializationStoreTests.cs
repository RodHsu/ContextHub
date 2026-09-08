using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class SkillMaterializationStoreTests
{
    [Fact]
    public async Task Concurrent_materialization_should_share_cache_but_isolate_execution_directories_and_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContextHub-SkillTests", Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = new PortableSkillBundle([
                new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes("---\nname: Test\ndescription: Test materialization\n---\n"))),
                new PortableSkillFile("references/check.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes("evidence")))
            ]);
            var hash = PortableSkillBundleValidator.Validate(new(
                "test-materialize", "Test", "Test materialization", "Use in tests", "1.0.0", bundle,
                Memory.Domain.SkillSourceKind.LocalUpload, "tests", "1", "MIT")).ContentHash;
            var store = new FileSystemSkillMaterializationStore(Options.Create(new SkillRuntimeOptions { MaterializationRoot = root }));
            var firstExecution = Guid.NewGuid();
            var secondExecution = Guid.NewGuid();

            var paths = await Task.WhenAll(
                store.MaterializeAsync(firstExecution, hash, bundle, CancellationToken.None),
                store.MaterializeAsync(secondExecution, hash, bundle, CancellationToken.None));

            paths[0].Should().NotBe(paths[1]);
            File.Exists(Path.Combine(root, paths[0], "SKILL.md")).Should().BeTrue();
            File.Exists(Path.Combine(root, paths[1], "SKILL.md")).Should().BeTrue();
            Directory.GetDirectories(Path.Combine(root, "cache")).Should().ContainSingle();

            await store.CleanupExecutionAsync(firstExecution, CancellationToken.None);
            Directory.Exists(Path.Combine(root, paths[0])).Should().BeFalse();
            Directory.Exists(Path.Combine(root, paths[1])).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "cache", hash)).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }
}
