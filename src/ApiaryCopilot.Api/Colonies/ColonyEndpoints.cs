using System.ComponentModel;
using System.Globalization;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Paging;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ApiaryCopilot.Api.Colonies;

/// <summary>
/// The account's colonies, their queens and their lineage (UC-3; FR-4, FR-5, FR-6). Every change appends an
/// event to the colony's history; no endpoint changes or deletes a recorded event (ARCHITECTURE.md D-2).
/// </summary>
public static class ColonyEndpoints
{
    /// <summary>The route the colony endpoints live under.</summary>
    public const string Path = "/api/colonies";

    /// <summary>Registers the colony contracts' source-generated JSON.</summary>
    public static IServiceCollection AddColonies(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ColonyJsonContext.Default));

    /// <summary>
    /// Maps the colony endpoints: reads, and appends through named lineage operations — nothing that updates or
    /// deletes. Each is for a signed-in owner or helper acting in their own account and fails closed (<see cref="AccountMemberAuthorization"/>).
    /// </summary>
    public static RouteGroupBuilder MapColonies(this IEndpointRouteBuilder endpoints)
    {
        var colonies = endpoints.MapGroup(Path)
            .WithTags("Colonies")
            .RequireAccountMember();

        colonies.MapPost("/", RegisterAsync)
            .WithName("RegisterColony")
            .WithSummary("Register a colony")
            .WithDescription("Records an existing colony in one of the account's yards, with its queen when known, as the first event in its history. Returns the colony, with the id to read it or record its lineage by.")
            .ProducesValidationProblem();

        colonies.MapPost("/swarm-captures", CaptureSwarmAsync)
            .WithName("CaptureSwarm")
            .WithSummary("Record a captured swarm")
            .WithDescription("Records a captured swarm as a new colony in one of the account's yards, as the first event in its history. Returns the new colony.")
            .ProducesValidationProblem();

        colonies.MapGet("/", ListAsync)
            .WithName("ListColonies")
            .WithSummary("List colonies")
            .WithDescription("Lists the account's colonies by tag id with their current queens, one page at a time. Pass the returned nextCursor to get the next page.")
            .ProducesValidationProblem();

        colonies.MapGet("/{id:guid}", GetAsync)
            .WithName("GetColony")
            .WithSummary("Read a colony")
            .WithDescription("Returns one colony's current state, projected from its lineage events, with its current queen. A colony the account does not have is not found.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapGet("/{id:guid}/events", ListEventsAsync)
            .WithName("ListColonyEvents")
            .WithSummary("List a colony's lineage")
            .WithDescription("Lists a colony's lineage events oldest first, one page at a time: every event about it, and the split that created it or a merge into it. Recorded events are never changed or removed.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapGet("/{id:guid}/queens", ListQueensAsync)
            .WithName("ListQueens")
            .WithSummary("List a colony's queens")
            .WithDescription("Lists a colony's queens by introduction day, newest first, one page at a time: its replacement history. The one with no replacedOn is the current queen; after a backdated requeen she sorts by the day it gave, so she is not always first.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapPost("/{id:guid}/splits", SplitAsync)
            .WithName("SplitColony")
            .WithSummary("Split a colony")
            .WithDescription("Records a split of an active colony: a new colony, with its own tag and yard and its queen when one was introduced, whose parent is this one. The split appears in both colonies' histories. Returns the new colony.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapPost("/{id:guid}/merges", MergeAsync)
            .WithName("MergeColony")
            .WithSummary("Merge a colony into another")
            .WithDescription("Records that an active colony was merged into another active colony of the account; it is then merged and its lineage ends. The merge appears in both colonies' histories. Returns the merged colony.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapPost("/{id:guid}/requeens", RequeenAsync)
            .WithName("RequeenColony")
            .WithSummary("Requeen a colony")
            .WithDescription("Records a new queen introduced to an active colony on her introduction day; the current queen, if any, is marked replaced that day. A day later than today (UTC) is refused. Returns the colony with its new queen.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        colonies.MapPost("/{id:guid}/moves", MoveAsync)
            .WithName("MoveColony")
            .WithSummary("Move a colony between yards")
            .WithDescription("Records a move of an active colony to another of the account's yards. Returns the colony where it is now.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return colonies;
    }

    private static async Task<Results<Created<ColonyResult>, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        ColonyRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseColony(request);
        return parsed.Value is not { } colony
            ? ProblemResults.Validation(Errors(parsed.Errors))
            : ToCreated(await store.RegisterAsync(colony.Profile, colony.Queen, colony.OccurredOn, cancellationToken));
    }

    private static async Task<Results<Created<ColonyResult>, ValidationProblem, ProblemHttpResult>> CaptureSwarmAsync(
        ColonyRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseColony(request);
        return parsed.Value is not { } colony
            ? ProblemResults.Validation(Errors(parsed.Errors))
            : ToCreated(await store.CaptureSwarmAsync(colony.Profile, colony.Queen, colony.OccurredOn, cancellationToken));
    }

    private static async Task<Results<Ok<ColonyPage>, ValidationProblem>> ListAsync(
        IColonyStore store,
        CancellationToken cancellationToken,
        [Description("How many colonies to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var page, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var colonies = await store.ListAsync(page.Skip, page.Fetch, cancellationToken);
        return TypedResults.Ok(new ColonyPage([.. colonies.Take(page.Take).Select(ToResult)], page.NextCursor(colonies.Count)));
    }

    private static async Task<Results<Ok<ColonyResult>, ProblemHttpResult>> GetAsync(
        [Description("The colony's id.")] Guid id, IColonyStore store, CancellationToken cancellationToken)
    {
        var colony = id == Guid.Empty ? null : await store.FindAsync(new ColonyId(id), cancellationToken);
        return colony is null ? NoSuchColony() : TypedResults.Ok(ToResult(colony));
    }

    private static async Task<Results<Ok<ColonyEventPage>, ValidationProblem, ProblemHttpResult>> ListEventsAsync(
        [Description("The colony's id.")] Guid id,
        IColonyStore store,
        CancellationToken cancellationToken,
        [Description("How many events to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var page, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var events = id == Guid.Empty ? null : await store.ListEventsAsync(new ColonyId(id), page.Skip, page.Fetch, cancellationToken);
        return events is null
            ? NoSuchColony()
            : TypedResults.Ok(new ColonyEventPage([.. events.Take(page.Take).Select(ToResult)], page.NextCursor(events.Count)));
    }

    private static async Task<Results<Ok<QueenPage>, ValidationProblem, ProblemHttpResult>> ListQueensAsync(
        [Description("The colony's id.")] Guid id,
        IColonyStore store,
        CancellationToken cancellationToken,
        [Description("How many queens to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var page, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var queens = id == Guid.Empty ? null : await store.ListQueensAsync(new ColonyId(id), page.Skip, page.Fetch, cancellationToken);
        return queens is null
            ? NoSuchColony()
            : TypedResults.Ok(new QueenPage([.. queens.Take(page.Take).Select(ToResult)], page.NextCursor(queens.Count)));
    }

    private static async Task<Results<Created<ColonyResult>, ValidationProblem, ProblemHttpResult>> SplitAsync(
        [Description("The id of the colony split.")] Guid id, ColonyRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseColony(request);
        if (parsed.Value is not { } colony)
        {
            return ProblemResults.Validation(Errors(parsed.Errors));
        }

        return id == Guid.Empty
            ? NoSuchColony()
            : ToCreated(await store.SplitAsync(new ColonyId(id), colony.Profile, colony.Queen, colony.OccurredOn, cancellationToken));
    }

    private static async Task<Results<Ok<ColonyResult>, ValidationProblem, ProblemHttpResult>> MergeAsync(
        [Description("The id of the colony merged.")] Guid id, MergeRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseMerge(request);
        if (parsed.Value is not { } merge)
        {
            return ProblemResults.Validation(Errors(parsed.Errors));
        }

        return id == Guid.Empty
            ? NoSuchColony()
            : ToOk(await store.MergeAsync(new ColonyId(id), merge.IntoColonyId, merge.OccurredOn, cancellationToken));
    }

    private static async Task<Results<Ok<ColonyResult>, ValidationProblem, ProblemHttpResult>> RequeenAsync(
        [Description("The id of the colony requeened.")] Guid id, QueenRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseQueen(request);
        if (parsed.Value is not { } queen)
        {
            return ProblemResults.Validation(Errors(parsed.Errors));
        }

        return id == Guid.Empty
            ? NoSuchColony()
            : ToOk(await store.RequeenAsync(new ColonyId(id), queen, cancellationToken));
    }

    private static async Task<Results<Ok<ColonyResult>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        [Description("The id of the colony moved.")] Guid id, MoveRequest request, IColonyStore store, CancellationToken cancellationToken)
    {
        var parsed = ColonyRequestParser.ParseMove(request);
        if (parsed.Value is not { } move)
        {
            return ProblemResults.Validation(Errors(parsed.Errors));
        }

        return id == Guid.Empty
            ? NoSuchColony()
            : ToOk(await store.MoveAsync(new ColonyId(id), move.ToYardId, move.OccurredOn, cancellationToken));
    }

    // ToCreated and ToOk are the one place a store outcome becomes a §4.3 category.
    private static Results<Created<ColonyResult>, ValidationProblem, ProblemHttpResult> ToCreated(ColonyChangeResult result) => result.Outcome switch
    {
        ColonyChangeOutcome.Recorded => TypedResults.Created($"{Path}/{result.Colony!.Colony.ColonyId}", ToRecorded(result)),
        ColonyChangeOutcome.Refused => ProblemResults.Validation(Refusal(result.Refusal!.Value)),
        _ => NotRecorded(result.Outcome),
    };

    private static Results<Ok<ColonyResult>, ValidationProblem, ProblemHttpResult> ToOk(ColonyChangeResult result) => result.Outcome switch
    {
        ColonyChangeOutcome.Recorded => TypedResults.Ok(ToRecorded(result)),
        ColonyChangeOutcome.Refused => ProblemResults.Validation(Refusal(result.Refusal!.Value)),
        _ => NotRecorded(result.Outcome),
    };

    private static ProblemHttpResult NotRecorded(ColonyChangeOutcome outcome) => outcome switch
    {
        ColonyChangeOutcome.NotFound => NoSuchColony(),
        ColonyChangeOutcome.Conflict => ProblemResults.Unavailable("a colony this change touches changed while it was being recorded; read it again and retry."),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static Dictionary<string, string[]> Refusal(ColonyRefusal refusal)
    {
        var (field, message) = refusal switch
        {
            ColonyRefusal.TagIdTaken => ("tagId", "tagId is already on another of the account's colonies."),
            ColonyRefusal.NoSuchYard => ("yardId", "yardId must be one of the account's yards."),
            ColonyRefusal.AlreadyInThatYard => ("yardId", "the colony is already in that yard."),
            ColonyRefusal.NotActive => ("id", "the colony is no longer active (merged, dead-out or sold), so its lineage has ended."),
            ColonyRefusal.NoSuchTargetColony => ("intoColonyId", "intoColonyId must be one of the account's colonies."),
            ColonyRefusal.TargetNotActive => ("intoColonyId", "the colony to merge into is no longer active."),
            ColonyRefusal.MergeIntoItself => ("intoColonyId", "a colony cannot be merged into itself."),
            ColonyRefusal.IntroducedInFuture => ("introducedOn", "introducedOn cannot be in the future; record a requeen on or after the day she was introduced."),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
        };
        return new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [$"Validation: {message}"] };
    }

    // A warning is keyed and prefixed like a §4.3 field error, but rides on the success it accompanies.
    private static ColonyResult ToRecorded(ColonyChangeResult result) => ToResult(result.Colony!) with
    {
        Warnings = result.Warning switch
        {
            null => null,
            RequeenBeforeCurrentQueen w => new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["introducedOn"] = [string.Create(
                    CultureInfo.InvariantCulture,
                    $"Warning: introducedOn {w.IntroducedOn:yyyy-MM-dd} is before {w.CurrentQueenIntroducedOn:yyyy-MM-dd}, the day the queen she replaces was introduced. It was recorded as given; recorded events are never changed.")],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Warning.GetType().Name, null),
        },
    };

    private static ProblemHttpResult NoSuchColony() => ProblemResults.NotFound("no colony with that id.");

    private static Dictionary<string, string[]> Errors(IReadOnlyDictionary<string, string[]> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    private static ColonyResult ToResult(ColonyState state)
    {
        var colony = state.Colony;
        return new ColonyResult(
            colony.ColonyId.Value,
            colony.TagId,
            colony.HiveNumber,
            colony.YardId.Value,
            colony.HiveType,
            colony.BoxCount,
            ColonyRequestParser.StatusCode(colony.Status),
            ColonyRequestParser.OriginCode(colony.Origin),
            colony.ParentColonyId?.Value,
            colony.MergedIntoColonyId?.Value,
            state.Queen is null ? null : ToResult(state.Queen));
    }

    private static QueenResult ToResult(Queen queen) => new(
        queen.QueenId.Value,
        queen.Details.Source,
        queen.Details.MarkColour is { } colour ? ColonyRequestParser.MarkColourCode(colour) : null,
        queen.Details.MarkYear,
        queen.Details.IntroducedOn,
        queen.ReplacedOn,
        queen.ReplacedByQueenId?.Value);

    private static ColonyEventResult ToResult(ColonyEvent colonyEvent)
    {
        (string Type, YardId? Yard, YardId? FromYard, QueenId? Queen, QueenId? Replaced) named = colonyEvent.Data switch
        {
            ColonyRegistered r => ("registered", r.Colony.YardId, null, r.Queen?.QueenId, null),
            SwarmCaptured s => ("swarm-capture", s.Colony.YardId, null, s.Queen?.QueenId, null),
            ColonySplit s => ("split", s.NewColony.YardId, null, s.Queen?.QueenId, null),
            ColonyMerged => ("merge", null, null, null, null),
            ColonyRequeened r => ("requeen", null, null, r.Queen.QueenId, r.ReplacedQueenId),
            ColonyMoved m => ("move", m.ToYardId, m.FromYardId, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(colonyEvent), colonyEvent.Data.GetType().Name, null),
        };
        var (type, yard, fromYard, queen, replaced) = named;
        return new ColonyEventResult(
            colonyEvent.EventId.Value,
            type,
            colonyEvent.ColonyId.Value,
            colonyEvent.RelatedColonyId?.Value,
            colonyEvent.OccurredOn,
            colonyEvent.RecordedAt,
            yard?.Value,
            fromYard?.Value,
            queen?.Value,
            replaced?.Value);
    }
}
