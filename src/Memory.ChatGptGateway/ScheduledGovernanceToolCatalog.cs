using System.Security.Cryptography;
using System.Text;
using Memory.Application;

namespace Memory.ChatGptGateway;

public static class ScheduledGovernanceToolCatalog
{
    public static IReadOnlySet<string> PublishedToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ScheduledGovernanceContract.ContractToolName,
        ScheduledGovernanceContract.ReviewToolName,
        ScheduledGovernanceContract.ExecuteToolName,
        ScheduledGovernanceContract.ReceiptToolName
    };

    public static string PublishedCatalogHash { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', PublishedToolNames.Order(StringComparer.Ordinal))))).ToLowerInvariant();

    public static string PublicationIdentity =>
        $"{ScheduledGovernanceContract.PublishedCatalogVersion}+{PublishedCatalogHash[..12]}";
}
