using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daleel.Web.Services;

/// <summary>
/// Shared JSON options for persisting and re-reading saved agent results. Centralized so the
/// "save" and "view" paths can never drift apart (a mismatch would silently fail to deserialize).
/// </summary>
public static class ResultSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The agent records use camelCase-friendly defaults; enums (QueryType, etc.) read better as names.
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
