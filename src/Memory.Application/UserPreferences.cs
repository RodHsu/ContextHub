using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

internal sealed record UserPreferenceMetadata(UserPreferenceKind Kind, string Rationale);

internal static class UserPreferenceMetadataSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(UserPreferenceKind kind, string rationale)
        => JsonSerializer.Serialize(new UserPreferenceMetadata(kind, rationale), SerializerOptions);

    public static UserPreferenceMetadata Deserialize(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new UserPreferenceMetadata(UserPreferenceKind.EngineeringPrinciple, string.Empty);
        }

        try
        {
            return JsonSerializer.Deserialize<UserPreferenceMetadata>(metadataJson, SerializerOptions)
                ?? new UserPreferenceMetadata(UserPreferenceKind.EngineeringPrinciple, string.Empty);
        }
        catch (JsonException)
        {
            return new UserPreferenceMetadata(UserPreferenceKind.EngineeringPrinciple, string.Empty);
        }
    }
}
