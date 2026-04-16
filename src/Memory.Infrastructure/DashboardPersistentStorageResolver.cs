namespace Memory.Infrastructure;

internal static class DashboardPersistentStorageResolver
{
    public static PersistentStorageSnapshot? Resolve(
        DockerRuntimeSnapshot dockerSnapshot,
        DockerContainerRuntimeSnapshot? container,
        string destination)
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

        return new PersistentStorageSnapshot(
            displayName,
            matchingVolume?.SizeBytes ?? 0);
    }

    private static string NormalizeDockerPath(string value)
        => value.Replace('\\', '/').TrimEnd('/');
}

internal sealed record PersistentStorageSnapshot(string DisplayName, long SizeBytes);
