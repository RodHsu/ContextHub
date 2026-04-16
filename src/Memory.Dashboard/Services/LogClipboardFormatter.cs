using System.Text.Json;
using Memory.Application;

namespace Memory.Dashboard.Services;

public static class LogClipboardFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Format(LogEntryResult log)
    {
        var payload = NormalizeJsonOrText(log.PayloadJson);

        var dto = new LogClipboardDto(
            log.Id,
            log.ServiceName,
            log.Category,
            log.Level,
            log.TraceId,
            log.RequestId,
            log.CreatedAt,
            log.Message,
            string.IsNullOrWhiteSpace(log.Exception) ? null : log.Exception,
            payload);

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

    private sealed record LogClipboardDto(
        long Id,
        string ServiceName,
        string Category,
        string Level,
        string TraceId,
        string RequestId,
        DateTimeOffset CreatedAt,
        string Message,
        string? Exception,
        object? Payload);
}
