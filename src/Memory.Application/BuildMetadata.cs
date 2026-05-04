using System.Reflection;

namespace Memory.Application;

public static class BuildMetadata
{
    public static BuildMetadataResult Current => Create();

    private static BuildMetadataResult Create()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var assemblyVersion = assembly.GetName().Version?.ToString();
        var rawVersion = !string.IsNullOrWhiteSpace(informationalVersion)
            ? informationalVersion
            : !string.IsNullOrWhiteSpace(fileVersion)
            ? fileVersion
            : assemblyVersion ?? "unknown";
        var version = NormalizeVersion(rawVersion);

        var timestampUtc = ResolveBuildTimestampUtc(assembly);
        return new BuildMetadataResult(version, timestampUtc);
    }

    internal static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        var metadataIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? normalized[..metadataIndex] : normalized;
    }

    private static DateTimeOffset ResolveBuildTimestampUtc(Assembly assembly)
    {
        var metadataTimestamp = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "BuildTimestampUtc", StringComparison.Ordinal))
            ?.Value;
        if (DateTimeOffset.TryParse(metadataTimestamp, out var parsedMetadataTimestamp))
        {
            return parsedMetadataTimestamp;
        }

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
