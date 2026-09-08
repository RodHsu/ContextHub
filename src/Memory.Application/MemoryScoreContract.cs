using System.Globalization;
using System.Text.Json;

namespace Memory.Application;

/// <summary>
/// Canonical contract for durable-memory Importance and Confidence scores.
/// Scores are already expressed as decimals in the inclusive [0, 1] range;
/// callers must not send percentage values and this contract never normalizes
/// them.
/// </summary>
public static class MemoryScoreContract
{
    public const decimal Minimum = 0m;
    public const decimal Maximum = 1m;

    public const string InvalidRangeCode = "memory-score-out-of-range";
    public const string InvalidTypeCode = "memory-score-invalid-type";
    public const string InvalidRangeReasonClass = "InvalidNumericRange";
    public const string InvalidTypeReasonClass = "InvalidNumericType";

    public static void Validate(MemoryUpsertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Importance, request.Confidence);
    }

    public static void Validate(MemoryUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Importance, request.Confidence);
    }

    public static void Validate(UserPreferenceUpsertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Importance, request.Confidence);
    }

    public static void Validate(decimal importance, decimal confidence)
    {
        Validate("importance", importance);
        Validate("confidence", confidence);
    }

    public static void Validate(decimal? importance, decimal? confidence)
    {
        if (importance.HasValue)
        {
            Validate("importance", importance.Value);
        }

        if (confidence.HasValue)
        {
            Validate("confidence", confidence.Value);
        }
    }

    /// <summary>
    /// Validates score properties in a serialized proposal payload without
    /// changing the payload or accepting a percentage-scale value.
    /// Missing and null optional update properties are left untouched.
    /// </summary>
    public static void ValidateJsonPayload(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        ValidateJsonProperty(document.RootElement, "importance");
        ValidateJsonProperty(document.RootElement, "confidence");
    }

    public static bool IsValid(decimal value)
        => value >= Minimum && value <= Maximum;

    private static void Validate(string field, decimal value)
    {
        if (!IsValid(value))
        {
            throw MemoryScoreValidationException.OutOfRange(field, value);
        }
    }

    private static void ValidateJsonProperty(JsonElement payload, string field)
    {
        foreach (var property in payload.EnumerateObject()
                     .Where(property => string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase)))
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var value))
            {
                throw MemoryScoreValidationException.InvalidType(field, property.Value.ValueKind);
            }

            Validate(field, value);
        }
    }
}

/// <summary>
/// Stable, bounded validation failure for the Importance/Confidence contract.
/// The exception deliberately carries only field and score metadata, never a
/// full request or proposal payload.
/// </summary>
public sealed class MemoryScoreValidationException : InvalidOperationException
{
    private MemoryScoreValidationException(
        string code,
        string reasonClass,
        string field,
        decimal? numericValue,
        string receivedValue)
        : base(BuildMessage(code, reasonClass, field, receivedValue))
    {
        Code = code;
        ReasonClass = reasonClass;
        Field = field;
        NumericValue = numericValue;
        ReceivedValue = receivedValue;
    }

    public string Code { get; }

    public string ValidationCode => Code;

    public string ReasonClass { get; }

    public string Field { get; }

    public decimal? NumericValue { get; }

    public decimal? Value => NumericValue;

    public string ReceivedValue { get; }

    public decimal Minimum => MemoryScoreContract.Minimum;

    public decimal Maximum => MemoryScoreContract.Maximum;

    internal static MemoryScoreValidationException OutOfRange(string field, decimal value)
        => new(
            MemoryScoreContract.InvalidRangeCode,
            MemoryScoreContract.InvalidRangeReasonClass,
            field,
            value,
            value.ToString(CultureInfo.InvariantCulture));

    internal static MemoryScoreValidationException InvalidType(string field, JsonValueKind valueKind)
        => new(
            MemoryScoreContract.InvalidTypeCode,
            MemoryScoreContract.InvalidTypeReasonClass,
            field,
            null,
            valueKind.ToString());

    private static string BuildMessage(string code, string reasonClass, string field, string receivedValue)
        => $"Memory score contract violation (code={code}; reasonClass={reasonClass}; field={field}; expectedRange=[0,1]; received={receivedValue}; normalization=forbidden).";
}
