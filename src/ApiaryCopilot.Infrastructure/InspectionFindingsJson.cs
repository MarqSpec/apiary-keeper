using System.Text.Json;
using System.Text.Json.Serialization;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The stored form of an inspection's findings (the <c>Inspections.Findings</c> jsonb column): a plain object of
/// field values. It is read for ever, so it holds whatever the catalogue held when the inspection was recorded; a
/// field the catalogue later drops stays readable, after the catalogue's fields.
/// </summary>
internal static class InspectionFindingsJson
{
    public static string Serialize(InspectionFindings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return JsonSerializer.Serialize(findings.Values, InspectionFindingsJsonContext.Default.IReadOnlyDictionaryStringJsonElement);
    }

    // jsonb does not keep key order, so the catalogue's order is applied on every read.
    public static InspectionFindings Deserialize(string json) => InspectionFields.InCatalogueOrder(
        JsonSerializer.Deserialize(json, InspectionFindingsJsonContext.Default.DictionaryStringJsonElement)
            ?? throw new JsonException("Stored inspection findings are not an object."));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class InspectionFindingsJsonContext : JsonSerializerContext;
