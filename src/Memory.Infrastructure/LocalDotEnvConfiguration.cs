using Microsoft.Extensions.Configuration;

namespace Memory.Infrastructure;

public static class LocalDotEnvConfiguration
{
    public static void AddFallbacks(
        ConfigurationManager configuration,
        string contentRootPath,
        IReadOnlyDictionary<string, string> keyMappings)
    {
        var dotEnvPath = FindDotEnvPath(contentRootPath);
        if (dotEnvPath is null)
        {
            return;
        }

        var dotEnv = ReadDotEnv(dotEnvPath);
        var fallbacks = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceKey, configurationKey) in keyMappings)
        {
            if (!string.IsNullOrWhiteSpace(configuration[configurationKey]))
            {
                continue;
            }

            if (dotEnv.TryGetValue(sourceKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                fallbacks[configurationKey] = value;
            }
        }

        if (fallbacks.Count > 0)
        {
            configuration.AddInMemoryCollection(fallbacks);
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadDotEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = Unquote(line[(separatorIndex + 1)..].Trim());
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string? FindDotEnvPath(string contentRootPath)
    {
        for (var directory = new DirectoryInfo(contentRootPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
