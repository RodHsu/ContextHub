using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

internal static class SuggestedActionEquivalence
{
    public static string GetIdentity(SuggestedAction action)
    {
        var payload = Deserialize(action.PayloadJson);
        var primaryMemoryId = TryReadGuid(payload, "primaryMemoryId");

        if (action.Type == SuggestedActionType.ArchiveStaleMemory && primaryMemoryId.HasValue)
        {
            return $"{action.Type}:memory:{primaryMemoryId.Value:N}";
        }

        if (action.Type == SuggestedActionType.MergeDuplicateCandidate && primaryMemoryId.HasValue)
        {
            var secondaryMemoryId = TryReadGuid(payload, "secondaryMemoryId") ??
                                    TryReadFindingPair(payload, primaryMemoryId.Value);
            if (secondaryMemoryId.HasValue)
            {
                var ids = new[] { primaryMemoryId.Value, secondaryMemoryId.Value }
                    .OrderBy(x => x)
                    .Select(x => x.ToString("N"));
                return $"{action.Type}:pair:{string.Join(':', ids)}";
            }
        }

        var dedupKey = !string.IsNullOrWhiteSpace(action.DedupKey)
            ? action.DedupKey
            : TryReadString(payload, "dedupKey") ?? string.Empty;
        return string.IsNullOrWhiteSpace(dedupKey)
            ? string.Empty
            : $"{action.Type}:dedup:{dedupKey}";
    }

    private static Guid? TryReadFindingPair(IReadOnlyDictionary<string, JsonElement> payload, Guid primaryMemoryId)
    {
        var findingId = TryReadString(payload, "findingId");
        if (string.IsNullOrWhiteSpace(findingId))
        {
            return null;
        }

        var related = findingId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => Guid.TryParse(token, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue && id.Value != primaryMemoryId)
            .Select(id => id!.Value)
            .LastOrDefault();
        return related == Guid.Empty ? null : related;
    }

    private static Dictionary<string, JsonElement> Deserialize(string payloadJson)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson)
            ?? [];

    private static string? TryReadString(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? TryReadGuid(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => Guid.TryParse(TryReadString(payload, key), out var id) ? id : null;
}
