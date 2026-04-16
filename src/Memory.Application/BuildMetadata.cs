using System.Reflection;

namespace Memory.Application;

public static class BuildMetadata
{
    public static BuildMetadataResult Current => Create();

    private static BuildMetadataResult Create()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var assemblyVersion = assembly.GetName().Version?.ToString();
        var version = !string.IsNullOrWhiteSpace(informationalVersion)
            ? informationalVersion
            : assemblyVersion ?? "unknown";

        var timestampUtc = ResolveBuildTimestampUtc(assembly);
        return new BuildMetadataResult(version, timestampUtc);
    }

    private static DateTimeOffset ResolveBuildTimestampUtc(Assembly assembly)
    {
        var location = assembly.Location;
        if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
        {
            return File.GetLastWriteTimeUtc(location);
        }

        return DateTimeOffset.UtcNow;
    }
}

public sealed record BuildMetadataResult(
    string Version,
    DateTimeOffset TimestampUtc);
