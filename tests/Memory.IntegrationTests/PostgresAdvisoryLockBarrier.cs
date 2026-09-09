using System.Diagnostics;
using System.Data;
using System.Data.Common;
using Npgsql;

namespace Memory.IntegrationTests;

/// <summary>
/// Observes a production-owned PostgreSQL advisory lock and exposes a
/// deterministic waiter barrier for a real second application connection.
/// The lock key is intentionally not reproduced here: callers must acquire it
/// through <c>IGovernanceRunReceiptService.AcquireRunLockAsync</c> first.
/// </summary>
internal sealed class PostgresAdvisoryLockBarrier : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IAsyncDisposable heldRunLock;
    private readonly DbConnection holder;
    private readonly string observerConnectionString;
    private readonly LockIdentity identity;
    private bool holderReleased;
    private bool disposed;

    private PostgresAdvisoryLockBarrier(
        IAsyncDisposable heldRunLock,
        DbConnection holder,
        string observerConnectionString,
        int holderPid,
        int writerPid,
        LockIdentity identity)
    {
        this.heldRunLock = heldRunLock;
        this.holder = holder;
        this.observerConnectionString = observerConnectionString;
        HolderPid = holderPid;
        WriterPid = writerPid;
        this.identity = identity;
    }

    public int HolderPid { get; }

    public int WriterPid { get; }

    public static async Task<PostgresAdvisoryLockBarrier> CreateAsync(
        IAsyncDisposable heldRunLock,
        DbConnection holder,
        DbConnection writer,
        string observerConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldRunLock);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(observerConnectionString);

        if (holder.State != ConnectionState.Open || writer.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Both the production lock connection and the real writer connection must be explicitly open.");
        }

        try
        {
            var holderPid = await ReadBackendPidAsync(holder, cancellationToken);
            var writerPid = await ReadBackendPidAsync(writer, cancellationToken);
            var identity = await ReadSingleGrantedLockIdentityAsync(holder, holderPid, cancellationToken);
            return new PostgresAdvisoryLockBarrier(
                heldRunLock,
                holder,
                observerConnectionString,
                holderPid,
                writerPid,
                identity);
        }
        catch
        {
            await SafeDisposeAsync(heldRunLock);
            throw;
        }
    }

    /// <summary>
    /// Waits until PostgreSQL reports the real writer connection waiting for
    /// the exact lock held by the production lock scope.
    /// </summary>
    public async Task WaitForWriterBlockedAsync(CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(DefaultTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (await IsHolderGrantedAndWriterBlockedAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL did not report writer PID {WriterPid} blocked by holder PID {HolderPid} " +
            $"on advisory lock ({identity.ClassId}/{identity.ObjectId}/{identity.ObjectSubId}).");
    }

    public async Task ReleaseHolderAsync(CancellationToken cancellationToken = default)
    {
        if (holderReleased)
        {
            return;
        }

        await heldRunLock.DisposeAsync();
        holderReleased = true;
        await WaitForHolderReleasedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Exception? cleanupFailure = null;
        try
        {
            await ReleaseHolderAsync();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
            await SafeDisposeAsync(heldRunLock);
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private async Task<bool> IsHolderGrantedAndWriterBlockedAsync(
        CancellationToken cancellationToken)
    {
        await using var observer = NewObserverConnection();
        await observer.OpenAsync(cancellationToken);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks AS holder_lock
                WHERE holder_lock.pid = @holder_pid
                  AND holder_lock.locktype = 'advisory'
                  AND holder_lock.classid::bigint = @class_id
                  AND holder_lock.objid::bigint = @obj_id
                  AND holder_lock.objsubid = @obj_sub_id
                  AND holder_lock.granted
                  AND EXISTS (
                      SELECT 1
                      FROM pg_locks AS writer_lock
                      WHERE writer_lock.pid = @writer_pid
                        AND writer_lock.locktype = 'advisory'
                        AND writer_lock.classid::bigint = @class_id
                        AND writer_lock.objid::bigint = @obj_id
                        AND writer_lock.objsubid = @obj_sub_id
                        AND NOT writer_lock.granted));
            """;
        AddIntParameter(command, "holder_pid", HolderPid);
        AddIntParameter(command, "writer_pid", WriterPid);
        AddLongParameter(command, "class_id", identity.ClassId);
        AddLongParameter(command, "obj_id", identity.ObjectId);
        AddIntParameter(command, "obj_sub_id", identity.ObjectSubId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task WaitForHolderReleasedAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(DefaultTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!await IsHolderLockGrantedAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL still reports advisory lock ({identity.ClassId}/{identity.ObjectId}/{identity.ObjectSubId}) " +
            $"granted to holder PID {HolderPid} after production lock disposal.");
    }

    private async Task<bool> IsHolderLockGrantedAsync(CancellationToken cancellationToken)
    {
        await using var observer = NewObserverConnection();
        await observer.OpenAsync(cancellationToken);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks
                WHERE pid = @holder_pid
                  AND locktype = 'advisory'
                  AND classid::bigint = @class_id
                  AND objid::bigint = @obj_id
                  AND objsubid = @obj_sub_id
                  AND granted);
            """;
        AddIntParameter(command, "holder_pid", HolderPid);
        AddLongParameter(command, "class_id", identity.ClassId);
        AddLongParameter(command, "obj_id", identity.ObjectId);
        AddIntParameter(command, "obj_sub_id", identity.ObjectSubId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<LockIdentity> ReadSingleGrantedLockIdentityAsync(
        DbConnection connection,
        int pid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT classid::bigint, objid::bigint, objsubid::int
            FROM pg_locks
            WHERE pid = @pid
              AND locktype = 'advisory'
              AND granted
            LIMIT 2;
            """;
        AddIntParameter(command, "pid", pid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"The production lock connection PID {pid} has no granted advisory lock.");
        }

        var identity = new LockIdentity(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"The production lock connection PID {pid} has multiple granted advisory locks; " +
                "the test cannot identify the run lock without reproducing production key derivation.");
        }

        return identity;
    }

    private NpgsqlConnection NewObserverConnection()
        => new(new NpgsqlConnectionStringBuilder(observerConnectionString)
        {
            Pooling = false
        }.ConnectionString);

    private static async Task<int> ReadBackendPidAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid();";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task SafeDisposeAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync();
        }
        catch
        {
        }
    }

    private static void AddLongParameter(DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int64;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddIntParameter(DbCommand command, string name, int value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int32;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private readonly record struct LockIdentity(long ClassId, long ObjectId, int ObjectSubId);
}
