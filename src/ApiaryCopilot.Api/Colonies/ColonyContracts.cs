using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiaryCopilot.Api.Colonies;

/// <summary>A colony as the caller records it: registered, captured or split off. Every field is checked (FR-4).</summary>
[Description("A colony as the caller records it when it is registered, captured as a swarm or split from another.")]
public sealed record ColonyRequest
{
    /// <summary>The tag id.</summary>
    [Description("The id on the QR/NFC tag on the box. Required; at most 64 characters; unique among the account's colonies.")]
    public string? TagId { get; init; }

    /// <summary>The hive's brand or ID number.</summary>
    [Description("The hive's brand or ID number. Required; at most 64 characters.")]
    public string? HiveNumber { get; init; }

    /// <summary>The yard.</summary>
    [Description("The id of the account's yard the colony is in. Required.")]
    public Guid? YardId { get; init; }

    /// <summary>The hive type.</summary>
    [Description("The kind of hive, e.g. Langstroth 10-frame. Required; at most 100 characters.")]
    public string? HiveType { get; init; }

    /// <summary>The box count.</summary>
    [Description("How many boxes the hive has. Required; 1 to 30.")]
    public int? BoxCount { get; init; }

    /// <summary>The day it happened.</summary>
    [Description("The day the colony was registered, captured or split, as yyyy-MM-dd. Required.")]
    public DateOnly? OccurredOn { get; init; }

    /// <summary>The queen, when known.</summary>
    [Description("The colony's queen, when known. Optional.")]
    public QueenRequest? Queen { get; init; }
}

/// <summary>A queen as the caller records her (FR-5).</summary>
[Description("A queen as the caller records her when she is introduced.")]
public sealed record QueenRequest
{
    /// <summary>The source.</summary>
    [Description("Where she came from: a breeder, raised in the yard, a swarm. Required; at most 200 characters.")]
    public string? Source { get; init; }

    /// <summary>The mark colour.</summary>
    [Description("The colour of her mark: white, yellow, red, green or blue. Optional; omit when she is unmarked.")]
    public string? MarkColour { get; init; }

    /// <summary>The mark year.</summary>
    [Description("The year her mark stands for. Optional; 2000 to 2100.")]
    public int? MarkYear { get; init; }

    /// <summary>The introduction day.</summary>
    [Description("The day she was introduced to the colony, as yyyy-MM-dd. Required.")]
    public DateOnly? IntroducedOn { get; init; }
}

/// <summary>A merge as the caller records it.</summary>
[Description("A merge of this colony into another of the account's active colonies.")]
public sealed record MergeRequest
{
    /// <summary>The colony merged into.</summary>
    [Description("The id of the colony this one was merged into. Required; another active colony of the account.")]
    public Guid? IntoColonyId { get; init; }

    /// <summary>The day it happened.</summary>
    [Description("The day of the merge, as yyyy-MM-dd. Required.")]
    public DateOnly? OccurredOn { get; init; }
}

/// <summary>A move as the caller records it.</summary>
[Description("A move of the colony to another of the account's yards.")]
public sealed record MoveRequest
{
    /// <summary>The destination yard.</summary>
    [Description("The id of the yard the colony moved to. Required; another of the account's yards.")]
    public Guid? YardId { get; init; }

    /// <summary>The day it happened.</summary>
    [Description("The day of the move, as yyyy-MM-dd. Required.")]
    public DateOnly? OccurredOn { get; init; }
}

/// <summary>A colony as the API returns it: its current state, projected from its events.</summary>
[Description("A colony's current state, projected from its lineage events.")]
public sealed record ColonyResult(
    [property: Description("The colony's id.")] Guid Id,
    [property: Description("The id on the QR/NFC tag on the box.")] string TagId,
    [property: Description("The hive's brand or ID number.")] string HiveNumber,
    [property: Description("The id of the yard the colony is in now.")] Guid YardId,
    [property: Description("The kind of hive.")] string HiveType,
    [property: Description("How many boxes the hive had when the colony was recorded.")] int BoxCount,
    [property: Description("active, dead-out, merged or sold.")] string Status,
    [property: Description("How it was recorded: registered, swarm-capture or split.")] string Origin,
    [property: Description("The colony it was split from, or null.")] Guid? ParentColonyId,
    [property: Description("The colony it was merged into, or null.")] Guid? MergedIntoColonyId,
    [property: Description("The current queen, or null when none is recorded.")] QueenResult? Queen)
{
    /// <summary>What the change just recorded may have got wrong, keyed by request field; null when nothing.</summary>
    [Description("Only on the response to a change, and only when there is something to say: what the change may have got wrong, keyed by the request field it concerns like a validation problem's errors, each message starting \"Warning:\". The change was recorded anyway, exactly as given. Absent otherwise, and never on a read.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Warnings { get; init; }
}

/// <summary>A queen as the API returns her.</summary>
[Description("A queen that heads, or headed, a colony.")]
public sealed record QueenResult(
    [property: Description("The queen's id.")] Guid Id,
    [property: Description("Where she came from.")] string Source,
    [property: Description("white, yellow, red, green or blue; null when unmarked.")] string? MarkColour,
    [property: Description("The year her mark stands for, or null.")] int? MarkYear,
    [property: Description("The day she was introduced, yyyy-MM-dd.")] DateOnly IntroducedOn,
    [property: Description("The day she was replaced, or null while she is the current queen.")] DateOnly? ReplacedOn,
    [property: Description("The queen who replaced her, or null.")] Guid? ReplacedByQueenId);

/// <summary>A lineage event as the API returns it.</summary>
[Description("One recorded lineage event. Recorded events are never changed or removed.")]
public sealed record ColonyEventResult(
    [property: Description("The event's id.")] Guid Id,
    [property: Description("registered, swarm-capture, split, merge, requeen or move.")] string Type,
    [property: Description("The colony it happened to; for a split, the parent.")] Guid ColonyId,
    [property: Description("The other colony: the new colony of a split, the colony merged into; otherwise null.")] Guid? RelatedColonyId,
    [property: Description("The day it happened, yyyy-MM-dd.")] DateOnly OccurredOn,
    [property: Description("When the server recorded it.")] DateTimeOffset RecordedAt,
    [property: Description("The yard a new colony was placed in, or a move's destination; otherwise null.")] Guid? YardId,
    [property: Description("The yard a move left; otherwise null.")] Guid? FromYardId,
    [property: Description("The queen the event introduced, or null.")] Guid? QueenId,
    [property: Description("The queen a requeen replaced, or null.")] Guid? ReplacedQueenId);

/// <summary>One page of colonies.</summary>
[Description("One page of colonies, ordered by tag id.")]
public sealed record ColonyPage(
    [property: Description("The colonies on this page.")] IReadOnlyList<ColonyResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>One page of a colony's queens.</summary>
[Description("One page of a colony's queens, newest first: its replacement history.")]
public sealed record QueenPage(
    [property: Description("The queens on this page.")] IReadOnlyList<QueenResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>One page of a colony's lineage events.</summary>
[Description("One page of a colony's lineage events, oldest first.")]
public sealed record ColonyEventPage(
    [property: Description("The events on this page.")] IReadOnlyList<ColonyEventResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>Source-generated JSON for the colony contracts (camelCase, web defaults).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ColonyRequest))]
[JsonSerializable(typeof(QueenRequest))]
[JsonSerializable(typeof(MergeRequest))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(ColonyResult))]
[JsonSerializable(typeof(ColonyPage))]
[JsonSerializable(typeof(QueenPage))]
[JsonSerializable(typeof(ColonyEventPage))]
public sealed partial class ColonyJsonContext : JsonSerializerContext;
