namespace Memory.Application;

public sealed class ManagedObjectWriteUnavailableException()
    : IOException("Managed object storage is unavailable for writing.");
