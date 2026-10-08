using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Api.Colonies;

/// <summary>The outcome of parsing a request: the value, or the field errors that stopped it.</summary>
public sealed record ParseResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors)
    where T : class;

/// <summary>A new colony as parsed: what is recorded about it, its queen when known, and the day.</summary>
public sealed record NewColony(ColonyProfile Profile, QueenDetails? Queen, DateOnly OccurredOn);

/// <summary>A merge as parsed.</summary>
public sealed record MergeColony(ColonyId IntoColonyId, DateOnly OccurredOn);

/// <summary>A move as parsed.</summary>
public sealed record MoveColony(YardId ToYardId, DateOnly OccurredOn);

/// <summary>
/// Parses the colony requests into the domain (FR-4, FR-5, FR-6); never throws. Text is trimmed, and blank optional
/// text is null. Every invalid field is reported, keyed by its JSON path, with a message that starts <c>Validation:</c>.
/// </summary>
public static class ColonyRequestParser
{
    /// <summary>The wire value of a colony's status.</summary>
    public static string StatusCode(ColonyStatus status) => status switch
    {
        ColonyStatus.Active => "active",
        ColonyStatus.DeadOut => "dead-out",
        ColonyStatus.Merged => "merged",
        ColonyStatus.Sold => "sold",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>The wire value of a colony's origin.</summary>
    public static string OriginCode(ColonyOrigin origin) => origin switch
    {
        ColonyOrigin.Registered => "registered",
        ColonyOrigin.SwarmCapture => "swarm-capture",
        ColonyOrigin.Split => "split",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };

    /// <summary>The wire value of a queen's mark colour.</summary>
    public static string MarkColourCode(QueenMarkColour colour) => colour switch
    {
        QueenMarkColour.White => "white",
        QueenMarkColour.Yellow => "yellow",
        QueenMarkColour.Red => "red",
        QueenMarkColour.Green => "green",
        QueenMarkColour.Blue => "blue",
        _ => throw new ArgumentOutOfRangeException(nameof(colour), colour, null),
    };

    /// <summary>Parses a colony being registered, captured or split off.</summary>
    public static ParseResult<NewColony> ParseColony(ColonyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var tagId = Required(request.TagId, "tagId", ColonyProfile.IdMaxLength, errors);
        var hiveNumber = Required(request.HiveNumber, "hiveNumber", ColonyProfile.IdMaxLength, errors);
        var yardId = Id(request.YardId, "yardId", errors);
        var hiveType = Required(request.HiveType, "hiveType", ColonyProfile.HiveTypeMaxLength, errors);
        var boxCount = BoxCount(request.BoxCount, errors);
        var occurredOn = Day(request.OccurredOn, "occurredOn", errors);
        var queen = request.Queen is null ? null : Queen(request.Queen, "queen.", errors);

        if (errors.Count > 0)
        {
            return new ParseResult<NewColony>(null, errors);
        }

        var profile = new ColonyProfile
        {
            TagId = tagId!,
            HiveNumber = hiveNumber!,
            YardId = new YardId(yardId!.Value),
            HiveType = hiveType!,
            BoxCount = boxCount!.Value,
        };
        return new ParseResult<NewColony>(new NewColony(profile, queen, occurredOn!.Value), errors);
    }

    /// <summary>Parses the queen a requeen introduces; her introduction day is the event's day.</summary>
    public static ParseResult<QueenDetails> ParseQueen(QueenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var queen = Queen(request, string.Empty, errors);
        return new ParseResult<QueenDetails>(errors.Count > 0 ? null : queen, errors);
    }

    /// <summary>Parses a merge.</summary>
    public static ParseResult<MergeColony> ParseMerge(MergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var into = Id(request.IntoColonyId, "intoColonyId", errors);
        var occurredOn = Day(request.OccurredOn, "occurredOn", errors);
        return new ParseResult<MergeColony>(
            errors.Count > 0 ? null : new MergeColony(new ColonyId(into!.Value), occurredOn!.Value), errors);
    }

    /// <summary>Parses a move.</summary>
    public static ParseResult<MoveColony> ParseMove(MoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var yard = Id(request.YardId, "yardId", errors);
        var occurredOn = Day(request.OccurredOn, "occurredOn", errors);
        return new ParseResult<MoveColony>(
            errors.Count > 0 ? null : new MoveColony(new YardId(yard!.Value), occurredOn!.Value), errors);
    }

    private static QueenDetails? Queen(QueenRequest queen, string prefix, Dictionary<string, string[]> errors)
    {
        var source = Required(queen.Source, $"{prefix}source", QueenDetails.SourceMaxLength, errors);
        var markColour = MarkColour(queen.MarkColour, $"{prefix}markColour", errors);
        var markYear = MarkYear(queen.MarkYear, $"{prefix}markYear", errors);
        var introducedOn = Day(queen.IntroducedOn, $"{prefix}introducedOn", errors);
        return source is null || introducedOn is null || markColour.Failed || markYear.Failed
            ? null
            : new QueenDetails { Source = source, MarkColour = markColour.Value, MarkYear = markYear.Value, IntroducedOn = introducedOn.Value };
    }

    private static (QueenMarkColour? Value, bool Failed) MarkColour(string? colour, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = colour?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (null, false);
        }

        foreach (var candidate in Enum.GetValues<QueenMarkColour>())
        {
            if (string.Equals(trimmed, MarkColourCode(candidate), StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, false);
            }
        }

        errors[field] = [$"Validation: {field} must be white, yellow, red, green or blue, or left out when she is unmarked."];
        return (null, true);
    }

    private static (int? Value, bool Failed) MarkYear(int? year, string field, Dictionary<string, string[]> errors)
    {
        if (year is null or (>= QueenDetails.MarkYearMin and <= QueenDetails.MarkYearMax))
        {
            return (year, false);
        }

        errors[field] = [$"Validation: {field} must be between {QueenDetails.MarkYearMin} and {QueenDetails.MarkYearMax}."];
        return (null, true);
    }

    private static int? BoxCount(int? boxCount, Dictionary<string, string[]> errors)
    {
        if (boxCount is >= 1 and <= ColonyProfile.BoxCountMax)
        {
            return boxCount;
        }

        errors["boxCount"] = [$"Validation: boxCount is required, from 1 to {ColonyProfile.BoxCountMax}."];
        return null;
    }

    private static Guid? Id(Guid? id, string field, Dictionary<string, string[]> errors)
    {
        if (id is { } value && value != Guid.Empty)
        {
            return value;
        }

        errors[field] = [$"Validation: {field} is required."];
        return null;
    }

    private static DateOnly? Day(DateOnly? day, string field, Dictionary<string, string[]> errors)
    {
        if (day is null)
        {
            errors[field] = [$"Validation: {field} is required, as yyyy-MM-dd."];
        }

        return day;
    }

    private static string? Required(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = [$"Validation: {field} is required."];
            return null;
        }

        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        errors[field] = [$"Validation: {field} must be at most {maxLength} characters."];
        return null;
    }
}
