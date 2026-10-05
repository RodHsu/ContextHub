namespace Memory.Application;

/// <summary>Stops repeated disposable-cache attempts after a transport failure within one operation.</summary>
public sealed class RedisCacheOperationScope : IDisposable
{
    private static readonly AsyncLocal<OperationState?> Current = new();
    private readonly OperationState? _previous;
    private int _disposed;

    private RedisCacheOperationScope(OperationState? previous) => _previous = previous;

    public static RedisCacheOperationScope BeginOrJoin()
    {
        var previous = Current.Value;
        Current.Value ??= new OperationState();
        return new(previous);
    }

    public static bool IsBypassed => Current.Value is { } state && Volatile.Read(ref state.Bypassed) != 0;

    public static void MarkTransportFailure()
    {
        if (Current.Value is { } state) Interlocked.Exchange(ref state.Bypassed, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Current.Value = _previous;
    }

    private sealed class OperationState
    {
        public int Bypassed;
    }
}
