using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class ProjectDiscussionService(
    IApplicationDbContext dbContext,
    IClock clock,
    IRequestActorAccessor actorAccessor) : IProjectDiscussionService
{
    public async Task<ProjectHierarchyResult> SetChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken)
    {
        var parent = ProjectContext.Normalize(request.ParentProjectId);
        var children = NormalizeProjects(request.ChildProjectIds).Where(x => !string.Equals(x, parent, StringComparison.OrdinalIgnoreCase)).ToArray();
        EnsureRegularProjects([parent, .. children], minimumCount: 1);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, parent, write: true);
        ActorAuthorization.EnsureProjectsAllowed(actor, children, write: false);
        var existing = ApplyActorScope(dbContext.ProjectHierarchies.Where(x => x.ParentProjectId == parent), actor);
        dbContext.ProjectHierarchies.RemoveRange(existing);
        var now = clock.UtcNow;
        foreach (var child in children)
        {
            await dbContext.ProjectHierarchies.AddAsync(new ProjectHierarchy { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ParentProjectId = parent, ChildProjectId = child, CreatedAt = now, UpdatedAt = now }, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ProjectHierarchyResult(parent, children, now);
    }

    public async Task<ProjectHierarchyResult> GetChildrenAsync(string parentProjectId, CancellationToken cancellationToken)
    {
        var parent = ProjectContext.Normalize(parentProjectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureProjectAllowed(actor, parent, write: false);
        var children = await ApplyActorScope(dbContext.ProjectHierarchies.AsNoTracking().Where(x => x.ParentProjectId == parent), actor).OrderBy(x => x.ChildProjectId).ToListAsync(cancellationToken);
        return new ProjectHierarchyResult(parent, children.Select(x => x.ChildProjectId).ToArray(), children.Select(x => x.UpdatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max());
    }

    public async Task<DiscussionThreadDetailResult> CreateThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken)
    {
        var host = ProjectContext.Normalize(request.HostProjectId);
        var sender = ProjectContext.Normalize(request.SenderProjectId);
        var participants = NormalizeProjects([.. request.ParticipantProjectIds, host, sender]);
        EnsureRegularProjects(participants);
        ValidateText(request.Title, 200, "Discussion title");
        ValidateText(request.InitialMessage, 12000, "Initial message");
        EnsureActorCanReadParticipants(participants);
        ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, sender, write: true);
        var now = clock.UtcNow;
        var actor = actorAccessor.Current;
        var thread = new DiscussionThread { TenantId = actor.TenantId, OwnerUserId = actor.UserId, HostProjectId = host, Title = request.Title.Trim(), CreatedAt = now, UpdatedAt = now };
        thread.Participants.AddRange(participants.Select(x => new DiscussionParticipant { ThreadId = thread.Id, ProjectId = x, LastReadAt = string.Equals(x, sender, StringComparison.OrdinalIgnoreCase) ? now : DateTimeOffset.MinValue }));
        thread.Messages.Add(new DiscussionMessage { ThreadId = thread.Id, SenderProjectId = sender, Content = request.InitialMessage.Trim(), CreatedAt = now });
        await dbContext.DiscussionThreads.AddAsync(thread, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapDetail(thread, sender);
    }

    public async Task<IReadOnlyList<DiscussionThreadResult>> ListThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var project = string.IsNullOrWhiteSpace(request.ProjectId) ? null : ProjectContext.Normalize(request.ProjectId);
        if (project is not null) ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        IQueryable<DiscussionThread> query = dbContext.DiscussionThreads.AsNoTracking()
            .Include(x => x.Participants)
            .Include(x => x.Messages)
            ;
        query = ApplyActorScope(query, actor);
        if (!request.IncludeArchived) query = query.Where(x => x.ArchivedAt == null);
        if (project is not null) query = query.Where(x => x.Participants.Any(p => p.ProjectId == project));
        else if (actor.AllowedProjectIds.Count > 0) query = query.Where(x => x.Participants.Any(p => actor.AllowedProjectIds.Contains(p.ProjectId)));
        if (!string.IsNullOrWhiteSpace(request.HostProjectId)) { var host = ProjectContext.Normalize(request.HostProjectId); ActorAuthorization.EnsureProjectAllowed(actor, host, false); query = query.Where(x => x.HostProjectId == host); }
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(x => x.Status == request.Status.Trim());
        var threads = await query.OrderByDescending(x => x.UpdatedAt).Skip(Math.Max(0, request.Offset)).Take(Math.Clamp(request.Limit, 1, 100)).ToListAsync(cancellationToken);
        return threads.Select(x => MapSummary(x, project)).ToArray();
    }

    public async Task<DiscussionThreadDetailResult?> GetThreadAsync(Guid threadId, string? readerProjectId, CancellationToken cancellationToken)
    {
        var thread = await LoadThreadAsync(threadId, cancellationToken);
        if (thread is null) return null;
        var reader = ResolveReaderProjectId(thread, readerProjectId);
        return MapDetail(thread, reader);
    }

    public async Task<DiscussionThreadResult?> CloseThreadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var thread = await LoadThreadAsync(threadId, cancellationToken);
        if (thread is null) return null;

        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, thread.HostProjectId, write: true);
        EnsureNotArchived(thread);

        if (string.Equals(thread.Status, "Open", StringComparison.OrdinalIgnoreCase))
        {
            thread.Status = "Closed";
            thread.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return MapSummary(thread, thread.HostProjectId);
    }

    public async Task<DiscussionThreadResult?> SetThreadArchivedAsync(Guid threadId, bool archived, CancellationToken cancellationToken)
    {
        var thread = await LoadThreadAsync(threadId, cancellationToken);
        if (thread is null) return null;

        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, thread.HostProjectId, write: true);

        var shouldChange = archived ? thread.ArchivedAt is null : thread.ArchivedAt is not null;
        if (shouldChange)
        {
            var now = clock.UtcNow;
            thread.ArchivedAt = archived ? now : null;
            thread.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return MapSummary(thread, thread.HostProjectId);
    }

    public async Task<DiscussionThreadResult?> AdvanceThreadReadCursorAsync(Guid threadId, string? readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken)
    {
        var thread = await LoadThreadAsync(threadId, cancellationToken);
        if (thread is null) return null;
        EnsureNotArchived(thread);
        var reader = ResolveReaderProjectId(thread, readerProjectId);
        var lastReadMessage = thread.Messages.SingleOrDefault(message => message.Id == lastReadMessageId)
            ?? throw new ArgumentException("The read cursor message does not belong to this discussion thread.", nameof(lastReadMessageId));
        var participant = thread.Participants.Single(x => x.ProjectId == reader);
        if (lastReadMessage.CreatedAt > participant.LastReadAt)
        {
            participant.LastReadAt = lastReadMessage.CreatedAt;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return MapSummary(thread, reader);
    }

    public async Task<DiscussionMessageResult> AddMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken)
    {
        ValidateText(request.Content, 12000, "Discussion message");
        var thread = await LoadThreadAsync(request.ThreadId, cancellationToken) ?? throw new InvalidOperationException("Discussion thread was not found.");
        EnsureNotArchived(thread);
        if (!string.Equals(thread.Status, "Open", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Discussion thread is closed.");
        var sender = ProjectContext.Normalize(request.SenderProjectId);
        _ = ResolveReaderProjectId(thread, sender);
        ActorAuthorization.EnsureScopeAllowed(actorAccessor.Current, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, sender, write: true);
        var now = clock.UtcNow;
        var message = new DiscussionMessage { ThreadId = thread.Id, SenderProjectId = sender, Content = request.Content.Trim(), CreatedAt = now };
        await dbContext.DiscussionMessages.AddAsync(message, cancellationToken);
        thread.Participants.Single(x => x.ProjectId == sender).LastReadAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        var updated = await dbContext.DiscussionThreads
            .Where(x => x.Id == thread.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("Discussion thread was not found.");
        return new DiscussionMessageResult(message.Id, sender, message.Content, now);
    }

    private async Task<DiscussionThread?> LoadThreadAsync(Guid id, CancellationToken cancellationToken)
        => await ApplyActorScope(dbContext.DiscussionThreads.Include(x => x.Participants).Include(x => x.Messages).Where(x => x.Id == id), actorAccessor.Current).SingleOrDefaultAsync(cancellationToken);
    private string ResolveReaderProjectId(DiscussionThread thread, string? readerProjectId)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        if (!string.IsNullOrWhiteSpace(readerProjectId))
        {
            var requested = ProjectContext.Normalize(readerProjectId);
            ActorAuthorization.EnsureProjectAllowed(actor, requested, false);
            if (!thread.Participants.Any(x => string.Equals(x.ProjectId, requested, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException($"Project '{requested}' is not a discussion participant.");
            return requested;
        }

        var readable = thread.Participants.Select(x => x.ProjectId).FirstOrDefault(project => actor.AllowedProjectIds.Count == 0 || actor.AllowedProjectIds.Contains(project, StringComparer.OrdinalIgnoreCase));
        return readable ?? throw new UnauthorizedAccessException("No readable discussion participant is available for the current token.");
    }
    private void EnsureActorCanReadParticipants(IReadOnlyList<string> projects) { var actor = actorAccessor.Current; ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead); ActorAuthorization.EnsureProjectsAllowed(actor, projects, false); }
    private static IQueryable<DiscussionThread> ApplyActorScope(IQueryable<DiscussionThread> query, ContextHubRequestActor actor)
        => !actor.HasUser
            ? query
            : actor.IsServiceActor
                ? query.Where(x => x.TenantId == actor.TenantId)
                : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);

    private static IQueryable<ProjectHierarchy> ApplyActorScope(IQueryable<ProjectHierarchy> query, ContextHubRequestActor actor)
        => !actor.HasUser
            ? query
            : actor.IsServiceActor
                ? query.Where(x => x.TenantId == actor.TenantId)
                : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
    private static string[] NormalizeProjects(IReadOnlyList<string> projects) => projects.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ProjectContext.Normalize(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    private static void EnsureRegularProjects(IReadOnlyList<string> projects, int minimumCount = 2) { if (projects.Count < minimumCount || projects.Any(x => ProjectContext.IsShared(x) || ProjectContext.IsUser(x))) throw new InvalidOperationException("Discussions require at least two regular ProjectIds; project hierarchy requires regular ProjectIds."); }
    private static void ValidateText(string value, int maxLength, string field) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength) throw new InvalidOperationException($"{field} is required and must not exceed {maxLength} characters."); }
    private static DiscussionThreadResult MapSummary(DiscussionThread thread, string? reader)
    {
        var participant = reader is null
            ? null
            : thread.Participants.SingleOrDefault(p => string.Equals(p.ProjectId, reader, StringComparison.OrdinalIgnoreCase));
        var unreadCount = participant is null
            ? 0
            : thread.Messages.Count(x => x.CreatedAt > participant.LastReadAt && !string.Equals(x.SenderProjectId, reader, StringComparison.OrdinalIgnoreCase));
        return new(thread.Id, thread.HostProjectId, thread.Title, thread.Status, thread.Participants.Select(x => x.ProjectId).OrderBy(x => x).ToArray(), unreadCount, thread.CreatedAt, thread.UpdatedAt, thread.ArchivedAt);
    }
    private static DiscussionThreadDetailResult MapDetail(DiscussionThread thread, string reader)
    {
        var participant = thread.Participants.Single(x => string.Equals(x.ProjectId, reader, StringComparison.OrdinalIgnoreCase));
        var messages = thread.Messages.OrderBy(x => x.CreatedAt).ToArray();
        var unreadMessageIds = messages
            .Where(x => x.CreatedAt > participant.LastReadAt && !string.Equals(x.SenderProjectId, reader, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToArray();
        return new(
            thread.Id,
            thread.HostProjectId,
            thread.Title,
            thread.Status,
            thread.Participants.Select(x => x.ProjectId).OrderBy(x => x).ToArray(),
            messages.Select(x => new DiscussionMessageResult(x.Id, x.SenderProjectId, x.Content, x.CreatedAt)).ToArray(),
            thread.CreatedAt,
            thread.UpdatedAt,
            reader,
            unreadMessageIds,
            thread.ArchivedAt);
    }

    private static void EnsureNotArchived(DiscussionThread thread)
    {
        if (thread.ArchivedAt.HasValue) throw new InvalidOperationException("Discussion thread is archived. Restore it before making changes.");
    }
}
