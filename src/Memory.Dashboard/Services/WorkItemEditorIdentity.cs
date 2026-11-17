using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Memory.Dashboard.Services;

/// <summary>Local UI binding identity, never an authorization claim or a credential.</summary>
public static class WorkItemEditorIdentity
{
    public static string SubjectKey(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return string.Empty;
        var tenant = user.FindFirst("contexthub:tenant_id")?.Value;
        var actor = user.FindFirst("contexthub:user_id")?.Value;
        // A partially populated tenant principal must not fall back to another identity kind.
        if (tenant is not null || actor is not null)
            return Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty &&
                Guid.TryParse(actor, out var actorId) && actorId != Guid.Empty
                ? $"tenant:{tenantId:N}:actor:{actorId:N}" : string.Empty;
        // Existing legacy Dashboard cookies carry a server-authenticated name/role, not
        // tenant IDs. Keep that principal distinct without reading/exposing cookie material.
        if (string.IsNullOrWhiteSpace(user.Identity.Name)) return string.Empty;
        var binding = JsonSerializer.Serialize(new { kind = "legacy-dashboard", user.Identity.AuthenticationType, user.Identity.Name });
        return "legacy:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant();
    }
}
