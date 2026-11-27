using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Memory.Application;

public sealed record MemoryEditPatch(
    string? Title = null,
    string? Content = null,
    string? Summary = null,
    IReadOnlyList<string>? Tags = null,
    decimal? Importance = null,
    decimal? Confidence = null,
    string? MetadataJson = null);

public sealed record MemoryEditCommand(
    string ProjectId, Guid MemoryId, int ExpectedVersion, string RequestId, MemoryEditPatch Patch);

public sealed record MemoryEditRecoveryRequest(
    string ProjectId, Guid MemoryId, string RequestId, string ExpectedRequestHash,
    string HashVersion = MemoryEditContract.IntentHashVersion);

public sealed record MemoryEditReceipt(
    Guid ReceiptId, string ProjectId, Guid MemoryId, string RequestId, string RequestHash, string HashVersion,
    int PreviousVersion, int Version, Guid RevisionId, DateTimeOffset CommittedAt, bool Replayed = false);

public interface IMemoryEditingService
{
    Task<MemoryEditReceipt> ApplyAsync(MemoryEditCommand command, CancellationToken cancellationToken);
    Task<MemoryEditReceipt?> ReadReceiptAsync(MemoryEditRecoveryRequest request, CancellationToken cancellationToken);
}

public sealed record MemoryEditScope(Guid TenantId, Guid OwnerUserId, string ProjectId);

public interface IMemoryEditStore
{
    Task<MemoryEditReceipt> ApplyAsync(MemoryEditScope scope, MemoryEditCommand command, CancellationToken cancellationToken);
    Task<MemoryEditReceipt?> ReadReceiptAsync(MemoryEditScope scope, MemoryEditRecoveryRequest request, CancellationToken cancellationToken);
}

public static class MemoryEditContract
{
    public const string IntentHashVersion = "MemoryEditIntent/v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static MemoryEditCommand Normalize(MemoryEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedVersion <= 0)
            throw new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion);
        ArgumentNullException.ThrowIfNull(command.Patch);
        RequireMemoryId(command.MemoryId);
        var projectId = NormalizeProject(command.ProjectId);
        var requestId = NormalizeRequestId(command.RequestId);
        var patch = command.Patch;
        MemoryScoreContract.Validate(patch.Importance, patch.Confidence);
        RequireValidText(patch.Title);
        RequireValidText(patch.Content);
        RequireValidText(patch.Summary);
        RequireValidText(patch.MetadataJson);
        var tags = patch.Tags?.ToArray();
        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                if (tag is null) throw new ArgumentException("Memory edit tags must contain text.");
                RequireValidText(tag);
            }
        }
        if (patch.Title is null && patch.Content is null && patch.Summary is null && tags is null &&
            patch.Importance is null && patch.Confidence is null && patch.MetadataJson is null)
            throw new ArgumentException("At least one editable memory field is required.");

        return command with
        {
            ProjectId = projectId,
            RequestId = requestId,
            Patch = patch with { Tags = tags is null ? null : Array.AsReadOnly(tags) }
        };
    }

    public static string Hash(MemoryEditCommand command)
    {
        var normalized = Normalize(command);
        var patch = normalized.Patch;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = IntentHashVersion,
            projectKey = ProjectContext.IdentityKey(normalized.ProjectId),
            normalized.MemoryId,
            normalized.ExpectedVersion,
            patch = new
            {
                patch.Title,
                patch.Content,
                patch.Summary,
                patch.Tags,
                importance = patch.Importance?.ToString("G29", CultureInfo.InvariantCulture),
                confidence = patch.Confidence?.ToString("G29", CultureInfo.InvariantCulture),
                patch.MetadataJson
            }
        }, Json);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static MemoryEditRecoveryRequest Normalize(MemoryEditRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireMemoryId(request.MemoryId);
        if (request.HashVersion != IntentHashVersion || request.ExpectedRequestHash is null ||
            request.ExpectedRequestHash.Length != 64 ||
            !request.ExpectedRequestHash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("The original memory edit hash and hash version are required.");
        }
        return request with { ProjectId = NormalizeProject(request.ProjectId), RequestId = NormalizeRequestId(request.RequestId) };
    }

    private static string NormalizeProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("An explicit ProjectId is required.");
        projectId = projectId.Trim();
        RequireValidText(projectId);
        if (ProjectContext.IsShared(projectId) || ProjectContext.IsUser(projectId) || projectId == ProjectContext.AllProjectIdsSentinel)
            throw new UnauthorizedAccessException("Memory edit is unavailable.");
        return projectId;
    }

    private static string NormalizeRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("The original memory edit RequestId is required.");
        requestId = requestId.Trim();
        if (requestId.Length > 200) throw new ArgumentException("Memory edit RequestId must not exceed 200 characters.");
        RequireValidText(requestId);
        return requestId;
    }

    private static void RequireMemoryId(Guid memoryId)
    {
        if (memoryId == Guid.Empty) throw new ArgumentException("A memory identity is required.");
    }

    private static void RequireValidText(string? value)
    {
        if (value is null) return;
        if (value.Contains('\0')) throw new ArgumentException("Memory edit text is invalid.");
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Memory edit text is invalid."); }
    }
}

public sealed class MemoryEditingService(
    IMemoryEditStore store, IRequestActorAccessor actorAccessor, IMaintenanceCoordinator maintenance) : IMemoryEditingService
{
    public async Task<MemoryEditReceipt> ApplyAsync(MemoryEditCommand command, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var normalized = MemoryEditContract.Normalize(command);
        MemoryEditProposalContract.RequireDirectRequestId(normalized.RequestId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalized.ProjectId, write: true);
        if (!actor.IsServiceActor) await maintenance.EnsureWriteAllowedAsync("memory_edit", cancellationToken);
        return await store.ApplyAsync(new(actor.TenantId!.Value, actor.UserId!.Value, normalized.ProjectId), normalized, cancellationToken);
    }

    public Task<MemoryEditReceipt?> ReadReceiptAsync(MemoryEditRecoveryRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var normalized = MemoryEditContract.Normalize(request);
        MemoryEditProposalContract.RequireDirectRequestId(normalized.RequestId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalized.ProjectId, write: false);
        return store.ReadReceiptAsync(new(actor.TenantId!.Value, actor.UserId!.Value, normalized.ProjectId), normalized, cancellationToken);
    }
}
