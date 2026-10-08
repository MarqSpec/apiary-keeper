using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiaryCopilot.Api.Yards;

/// <summary>A yard as the caller writes it, to create or replace one. Every field is checked (FR-1, FR-2).</summary>
[Description("A yard and its host landowner, as the caller writes it to create or replace one.")]
public sealed record YardRequest
{
    /// <summary>The yard's name.</summary>
    [Description("The name the family uses for the yard. Required; at most 200 characters.")]
    public string? Name { get; init; }

    /// <summary>The yard's GPS position.</summary>
    [Description("The yard's GPS position. Required.")]
    public LocationRequest? Location { get; init; }

    /// <summary>The state's two-letter code.</summary>
    [Description("The state's two-letter code. Required; only TX (Texas) is accepted for now.")]
    public string? State { get; init; }

    /// <summary>The county.</summary>
    [Description("The county the yard is in. Required; at most 100 characters.")]
    public string? County { get; init; }

    /// <summary>Access notes.</summary>
    [Description("How to get in: gates, codes, tracks, when to avoid. Optional; at most 2000 characters.")]
    public string? AccessNotes { get; init; }

    /// <summary>Forage notes.</summary>
    [Description("What the bees forage on around the yard, and when. Optional; at most 2000 characters.")]
    public string? ForageNotes { get; init; }

    /// <summary>The host landowner.</summary>
    [Description("The host farmer or landowner and the arrangement with them. Required.")]
    public LandownerRequest? Landowner { get; init; }
}

/// <summary>A GPS position as the caller writes it.</summary>
[Description("A GPS position in decimal degrees (WGS 84).")]
public sealed record LocationRequest
{
    /// <summary>Latitude.</summary>
    [Description("Latitude in decimal degrees, from -90 to 90. Required.")]
    public double? Latitude { get; init; }

    /// <summary>Longitude.</summary>
    [Description("Longitude in decimal degrees, from -180 to 180. Required.")]
    public double? Longitude { get; init; }
}

/// <summary>A yard's host landowner as the caller writes it (FR-2).</summary>
[Description("The host farmer or landowner of a yard.")]
public sealed record LandownerRequest
{
    /// <summary>Contact.</summary>
    [Description("How to reach the landowner: name, phone, email. Required; at most 500 characters.")]
    public string? Contact { get; init; }

    /// <summary>Terms.</summary>
    [Description("What the landowner gets for hosting the yard: honey, fee or free. Required.")]
    public string? Terms { get; init; }

    /// <summary>Nearby crops.</summary>
    [Description("Crops grown near the yard. Optional; at most 1000 characters.")]
    public string? NearbyCrops { get; init; }

    /// <summary>Spray contact.</summary>
    [Description("Who to call about pesticide spraying near the yard. Optional; at most 500 characters.")]
    public string? SprayContact { get; init; }
}

/// <summary>A yard as the API returns it.</summary>
[Description("A yard and its host landowner.")]
public sealed record YardResult(
    [property: Description("The yard's id; use it to read or update the yard.")] Guid Id,
    [property: Description("The name the family uses for the yard.")] string Name,
    [property: Description("The yard's GPS position.")] LocationResult Location,
    [property: Description("The state's two-letter code: TX.")] string State,
    [property: Description("The county the yard is in.")] string County,
    [property: Description("How to get in, or null.")] string? AccessNotes,
    [property: Description("What the bees forage on, or null.")] string? ForageNotes,
    [property: Description("The host landowner.")] LandownerResult Landowner);

/// <summary>A GPS position as the API returns it.</summary>
[Description("A GPS position in decimal degrees (WGS 84).")]
public sealed record LocationResult(
    [property: Description("Latitude, from -90 to 90.")] double Latitude,
    [property: Description("Longitude, from -180 to 180.")] double Longitude);

/// <summary>A landowner as the API returns it.</summary>
[Description("The host farmer or landowner of a yard.")]
public sealed record LandownerResult(
    [property: Description("How to reach the landowner.")] string Contact,
    [property: Description("What the landowner gets: honey, fee or free.")] string Terms,
    [property: Description("Crops grown near the yard, or null.")] string? NearbyCrops,
    [property: Description("Who to call about spraying, or null.")] string? SprayContact);

/// <summary>One page of yards.</summary>
[Description("One page of yards, ordered by name.")]
public sealed record YardPage(
    [property: Description("The yards on this page.")] IReadOnlyList<YardResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>Source-generated JSON for the yard contracts (camelCase, web defaults).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(YardRequest))]
[JsonSerializable(typeof(YardResult))]
[JsonSerializable(typeof(YardPage))]
public sealed partial class YardJsonContext : JsonSerializerContext;
