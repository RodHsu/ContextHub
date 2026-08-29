using System.Security.Cryptography;
using System.Text;
using Memory.Application;

namespace Memory.ChatGptGateway;

/// <summary>
/// Auditable source of truth for the tools intentionally published by the restricted ChatGPT gateway.
/// Contract tests compare this policy with both gateway tools/list and the backend MCP tool type so a
/// newly added tool cannot be silently omitted from, or unintentionally exposed through, /mcp-chat.
/// </summary>
public static class ChatGptGatewayToolCatalog
{
    public static IReadOnlySet<string> PublishedToolNames => McpPublishedToolCatalog.RestrictedToolNames;

    public static IReadOnlySet<string> BackendOnlyToolNames => McpPublishedToolCatalog.BackendOnlyToolNames;

    public static IReadOnlySet<string> GatewayOnlyToolNames => McpPublishedToolCatalog.GatewayOnlyToolNames;

    public static string PublishedCatalogVersion => McpPublishedToolCatalog.AppFacingCatalogVersion;

    public static string PublishedCatalogHash { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', PublishedToolNames.Order(StringComparer.Ordinal))))).ToLowerInvariant();

    public static string PublicationIdentity =>
        $"{Memory.Application.BuildMetadata.Current.Version}+catalog.{PublishedCatalogVersion}.{PublishedCatalogHash[..12]}";
}
