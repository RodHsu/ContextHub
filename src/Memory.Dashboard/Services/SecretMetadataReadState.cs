using System.Net;
using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services;

public sealed class SecretMetadataReadState(IContextHubApiClient api) : IDisposable
{
    private CancellationTokenSource? _inventoryRequest;
    private CancellationTokenSource? _selectionRequest;
    private bool _disposed;

    public string ProjectId { get; private set; } = string.Empty;
    public IReadOnlyList<SecretSummary> Secrets { get; private set; } = [];
    public SecretSummary? Selected { get; private set; }
    public EffectiveRightsResult? Rights { get; private set; }
    public StepUpAuthorizationResult? RevealRequirement { get; private set; }
    public StepUpAuthorizationResult? RevokeRequirement { get; private set; }
    public bool Loading { get; private set; }
    public bool PermissionDenied { get; private set; }
    public bool SelectionLoading { get; private set; }
    public string? Error { get; private set; }
    public string? SelectionError { get; private set; }
    public DateTimeOffset? LastUpdatedAt { get; private set; }

    public async Task LoadAsync(string projectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var project = projectId.Trim();
        var sameProject = ProjectContext.Matches(project, ProjectId);
        var preferredId = sameProject ? Selected?.Id : null;
        var previousRows = sameProject ? Secrets : [];
        var previousUpdatedAt = sameProject ? LastUpdatedAt : null;
        Cancel(ref _inventoryRequest);
        Cancel(ref _selectionRequest);
        ProjectId = project;
        Secrets = [];
        ClearSelection();
        PermissionDenied = false;
        Error = null;
        LastUpdatedAt = null;
        Loading = false;
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        var request = _inventoryRequest = new();
        var token = request.Token;
        Loading = true;
        try
        {
            var rows = await api.GetSecretsAsync(project, token);
            if (!Current(request, _inventoryRequest)) return;
            if (rows.Any(row => !ProjectContext.Matches(row.ProjectId, project)))
                throw new InvalidOperationException("Secret metadata scope does not match its request.");
            Secrets = rows;
            LastUpdatedAt = DateTimeOffset.UtcNow;
            var selected = Secrets.FirstOrDefault(row => row.Id == preferredId) ?? Secrets.FirstOrDefault();
            if (selected is not null) await SelectAsync(selected.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ContextHubApiClient.GetInventoryHttpFailure(exception)?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            if (Current(request, _inventoryRequest)) PermissionDenied = true;
        }
        catch (Exception exception)
        {
            if (!Current(request, _inventoryRequest)) return;
            var httpFailure = ContextHubApiClient.GetInventoryHttpFailure(exception);
            if (previousUpdatedAt.HasValue && httpFailure?.StatusCode is { } status && (int)status is >= 500 and <= 599)
            {
                Secrets = previousRows;
                Selected = Secrets.FirstOrDefault(secret => secret.Id == preferredId);
                LastUpdatedAt = previousUpdatedAt;
            }
            Error = httpFailure is not null
                ? DashboardUiErrorFormatter.BuildLoadError("credential metadata", httpFailure, Secrets.Count > 0)
                : "無法載入 credential metadata，請確認存取權限或稍後重試。";
        }
        finally { if (Current(request, _inventoryRequest)) Loading = false; }
    }

    public async Task SelectAsync(Guid secretId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PermissionDenied || Error is not null) return;
        var selected = Secrets.FirstOrDefault(row => row.Id == secretId && ProjectContext.Matches(row.ProjectId, ProjectId));
        if (selected is null) return;
        Cancel(ref _selectionRequest);
        ClearSelection();
        var project = ProjectId;
        var resource = selected.Id.ToString("D");
        Selected = selected;
        var request = _selectionRequest = new();
        var token = request.Token;
        SelectionLoading = true;
        try
        {
            var rights = await api.GetEffectiveRightsAsync(project, ["metadata", "use", "manage", "rotate", "revoke", "reveal", "export"], "Secret", resource, token);
            if (!CurrentSelection(request, project, secretId)) return;
            if (!ProjectContext.Matches(rights.ProjectId, project)) throw new InvalidOperationException("Secret rights scope does not match its request.");
            var reveal = await api.GetStepUpRequirementAsync(StepUpOperationClass.SecretReveal, "secret:reveal", "Secret", resource, token);
            if (!CurrentSelection(request, project, secretId)) return;
            var revoke = await api.GetStepUpRequirementAsync(StepUpOperationClass.CredentialRevoke, "secret:revoke", "Secret", resource, token);
            if (!CurrentSelection(request, project, secretId)) return;
            // Publish only one complete result bound to this project and logical secret.
            Rights = rights;
            RevealRequirement = reveal;
            RevokeRequirement = revoke;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (CurrentSelection(request, project, secretId)) SelectionError = "無法載入所選 credential 的權限與 assurance，請重新選取或稍後重試。";
        }
        finally { if (CurrentSelection(request, project, secretId)) SelectionLoading = false; }
    }

    private bool Current(CancellationTokenSource request, CancellationTokenSource? active)
        => !_disposed && ReferenceEquals(request, active);
    private bool CurrentSelection(CancellationTokenSource request, string project, Guid secret)
        => Current(request, _selectionRequest) && ProjectContext.Matches(project, ProjectId) && Selected?.Id == secret;
    private void ClearSelection()
    {
        Selected = null;
        Rights = null;
        RevealRequirement = RevokeRequirement = null;
        SelectionError = null;
        SelectionLoading = false;
    }
    private static void Cancel(ref CancellationTokenSource? request)
    {
        request?.Cancel();
        request?.Dispose();
        request = null;
    }
    public void Dispose()
    {
        _disposed = true;
        Cancel(ref _inventoryRequest);
        Cancel(ref _selectionRequest);
        Secrets = [];
        ClearSelection();
        Loading = false;
        PermissionDenied = false;
        Error = null;
        LastUpdatedAt = null;
    }
}
