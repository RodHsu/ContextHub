namespace Memory.Infrastructure;

internal static class DashboardPersistentStorageResolver
{
    public static PersistentStorageSnapshot? Resolve(
        DockerRuntimeSnapshot dockerSnapshot,
        DockerContainerRuntimeSnapshot? container,
        string destination,
        long fallbackSizeBytes = 0,
        string? fallbackUsageLabel = null)
    {
        if (container is null)
        {
            return null;
        }

        var mount = container.Mounts.FirstOrDefault(x =>
            string.Equals(NormalizeDockerPath(x.Destination), NormalizeDockerPath(destination), StringComparison.OrdinalIgnoreCase));

        if (mount is null)
        {
            return null;
        }

        var matchingVolume = dockerSnapshot.Volumes.FirstOrDefault(x =>
            (!string.IsNullOrWhiteSpace(mount.Name) && string.Equals(x.Name, mount.Name, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(mount.Source) && string.Equals(NormalizeDockerPath(x.Mountpoint), NormalizeDockerPath(mount.Source), StringComparison.OrdinalIgnoreCase)));

        var displayName = !string.IsNullOrWhiteSpace(mount.Name)
            ? mount.Name
            : !string.IsNullOrWhiteSpace(mount.Source)
                ? mount.Source
                : destination;

        var volumeSizeBytes = matchingVolume?.SizeBytes ?? 0;
        if (volumeSizeBytes > 0 || fallbackSizeBytes <= 0)
        {
            return new PersistentStorageSnapshot(displayName, volumeSizeBytes, false);
        }

        return new PersistentStorageSnapshot(
            string.IsNullOrWhiteSpace(fallbackUsageLabel)
                ? displayName
                : $"{displayName} ({fallbackUsageLabel})",
            fallbackSizeBytes,
            true);
    }

    private static string NormalizeDockerPath(string value)
        => value.Replace('\\', '/').TrimEnd('/');
}

internal sealed record PersistentStorageSnapshot(string DisplayName, long SizeBytes, bool IsLogicalFallback);
