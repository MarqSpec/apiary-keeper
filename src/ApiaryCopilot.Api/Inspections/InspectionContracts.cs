using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiaryCopilot.Api.Inspections;

/// <summary>An inspection as the caller records it. Every field is checked (FR-7).</summary>
[Description("An inspection of a colony as the caller records it.")]
public sealed record InspectionRequest
{
    /// <summary>The day inspected.</summary>
    [Description("The day the colony was inspected, as yyyy-MM-dd; not later than today (UTC) and not before 2000-01-01. Required.")]
    public DateOnly? InspectedOn { get; init; }

    /// <summary>What was seen.</summary>
    [Description("What was seen, as an object of the inspection fields the record operation lists; leave out a field not looked at. Optional, but an inspection needs at least one finding or a note.")]
    public Dictionary<string, JsonElement>? Findings { get; init; }

    /// <summary>The notes.</summary>
    [Description("Free-text notes. Optional; at most 4000 characters.")]
    public string? Notes { get; init; }
}

/// <summary>An inspection as the API returns it.</summary>
[Description("A recorded inspection of a colony.")]
public sealed record InspectionResult(
    [property: Description("The inspection's id.")] Guid Id,
    [property: Description("The colony inspected.")] Guid ColonyId,
    [property: Description("The day inspected, yyyy-MM-dd.")] DateOnly InspectedOn,
    [property: Description("When the server recorded it.")] DateTimeOffset RecordedAt,
    [property: Description("What was seen: the inspection fields filled in, each as recorded, in the catalogue's order.")] IReadOnlyDictionary<string, JsonElement> Findings,
    [property: Description("Free-text notes, or null.")] string? Notes);

/// <summary>One page of a colony's inspections.</summary>
[Description("One page of a colony's inspections, newest first.")]
public sealed record InspectionPage(
    [property: Description("The inspections on this page.")] IReadOnlyList<InspectionResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>Source-generated JSON for the inspection contracts (camelCase, web defaults).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InspectionRequest))]
[JsonSerializable(typeof(InspectionResult))]
[JsonSerializable(typeof(InspectionPage))]
public sealed partial class InspectionJsonContext : JsonSerializerContext;
