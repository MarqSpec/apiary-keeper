using System.ComponentModel;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Paging;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ApiaryCopilot.Api.Inspections;

/// <summary>
/// A colony's inspections (UC-1; FR-7): record one, list a colony's newest first, read one. An inspection is
/// recorded once; no endpoint changes or deletes it.
/// </summary>
public static class InspectionEndpoints
{
    /// <summary>The route a colony's inspections live under, after the colony's id.</summary>
    public const string ColonyPath = "/api/colonies/{id:guid}/inspections";

    /// <summary>The route one inspection is read at.</summary>
    public const string Path = "/api/inspections";

    /// <summary>Registers the inspection contracts' source-generated JSON.</summary>
    public static IServiceCollection AddInspections(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, InspectionJsonContext.Default));

    /// <summary>
    /// Maps the inspection endpoints: two reads and one append, nothing that updates or deletes. Each is for a
    /// signed-in owner or helper acting in their own account and fails closed (<see cref="AccountMemberAuthorization"/>).
    /// </summary>
    public static RouteGroupBuilder MapInspections(this IEndpointRouteBuilder endpoints)
    {
        var inspections = endpoints.MapGroup(string.Empty)
            .WithTags("Inspections")
            .RequireAccountMember();

        inspections.MapPost(ColonyPath, RecordAsync)
            .WithName("RecordInspection")
            .WithSummary("Record an inspection")
            .WithDescription(
                "Records an inspection of one of the account's colonies: the day, the findings and notes. The day may be neither later than today (UTC) nor before 2000-01-01. Returns the inspection, with the id to read it by. " +
                "An inspection needs at least one finding or a note. The findings are an object of these fields; leave out any not looked at. " +
                InspectionFields.Describe())
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        inspections.MapGet(ColonyPath, ListAsync)
            .WithName("ListInspections")
            .WithSummary("List a colony's inspections")
            .WithDescription("Lists a colony's inspections newest first, by the day inspected and then by when recorded, one page at a time. Pass the returned nextCursor to get the next page.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        inspections.MapGet($"{Path}/{{id:guid}}", GetAsync)
            .WithName("GetInspection")
            .WithSummary("Read an inspection")
            .WithDescription("Returns one inspection. An inspection the account does not have is not found.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return inspections;
    }

    private static async Task<Results<Created<InspectionResult>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        [Description("The id of the colony inspected.")] Guid id, InspectionRequest request, IInspectionStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var parsed = InspectionRequestParser.Parse(request, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        if (parsed.Value is not { } details)
        {
            return ProblemResults.Validation(parsed.Errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
        }

        var inspection = id == Guid.Empty ? null : await store.AddAsync(new ColonyId(id), details, cancellationToken);
        return inspection is null
            ? NoSuchColony()
            : TypedResults.Created($"{Path}/{inspection.InspectionId}", ToResult(inspection));
    }

    private static async Task<Results<Ok<InspectionPage>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [Description("The colony's id.")] Guid id,
        IInspectionStore store,
        CancellationToken cancellationToken,
        [Description("How many inspections to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var page, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var inspections = id == Guid.Empty ? null : await store.ListAsync(new ColonyId(id), page.Skip, page.Fetch, cancellationToken);
        return inspections is null
            ? NoSuchColony()
            : TypedResults.Ok(new InspectionPage([.. inspections.Take(page.Take).Select(ToResult)], page.NextCursor(inspections.Count)));
    }

    private static async Task<Results<Ok<InspectionResult>, ProblemHttpResult>> GetAsync(
        [Description("The inspection's id.")] Guid id, IInspectionStore store, CancellationToken cancellationToken)
    {
        var inspection = id == Guid.Empty ? null : await store.FindAsync(new InspectionId(id), cancellationToken);
        return inspection is null
            ? ProblemResults.NotFound("no inspection with that id.")
            : TypedResults.Ok(ToResult(inspection));
    }

    private static ProblemHttpResult NoSuchColony() => ProblemResults.NotFound("no colony with that id.");

    private static InspectionResult ToResult(Inspection inspection) => new(
        inspection.InspectionId.Value,
        inspection.ColonyId.Value,
        inspection.InspectedOn,
        inspection.RecordedAt,
        inspection.Findings.Values,
        inspection.Notes);
}
