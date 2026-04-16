using System.Text.Json;
using Memory.Application;

namespace Memory.Dashboard.Services;

public static class JobClipboardFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Format(JobListItemResult job)
    {
        var payload = NormalizeJsonOrText(job.PayloadJson);

        var dto = new JobClipboardDto(
            job.Id,
            job.JobType.ToString(),
            job.Status.ToString(),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            payload,
            string.IsNullOrWhiteSpace(job.Error) ? null : job.Error);

        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    private static object? NormalizeJsonOrText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private sealed record JobClipboardDto(
        Guid Id,
        string JobType,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        object? Payload,
        string? Error);
}
