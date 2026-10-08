using System.ComponentModel;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Paging;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ApiaryCopilot.Api.Yards;

/// <summary>Create, list, read and update the account's yards (UC-9, FR-1, FR-2).</summary>
public static class YardEndpoints
{
    /// <summary>The route the yard endpoints live under.</summary>
    public const string Path = "/api/yards";

    /// <summary>Registers the yard contracts' source-generated JSON.</summary>
    public static IServiceCollection AddYards(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, YardJsonContext.Default));

    /// <summary>Maps the yard endpoints, for a signed-in owner or helper acting in their own account (<see cref="AccountMemberAuthorization"/>); each fails closed.</summary>
    public static RouteGroupBuilder MapYards(this IEndpointRouteBuilder endpoints)
    {
        var yards = endpoints.MapGroup(Path)
            .WithTags("Yards")
            .RequireAccountMember();

        yards.MapPost("/", CreateAsync)
            .WithName("CreateYard")
            .WithSummary("Record a yard")
            .WithDescription("Records a new yard with its location, access and forage notes and its host landowner's terms. Returns the yard, with the id to read or update it by.")
            .ProducesValidationProblem();

        yards.MapGet("/", ListAsync)
            .WithName("ListYards")
            .WithSummary("List yards")
            .WithDescription("Lists the account's yards by name, one page at a time. Pass the returned nextCursor to get the next page.")
            .ProducesValidationProblem();

        yards.MapGet("/{id:guid}", GetAsync)
            .WithName("GetYard")
            .WithSummary("Read a yard")
            .WithDescription("Returns one yard with its landowner. A yard the account does not have is not found.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        yards.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateYard")
            .WithSummary("Update a yard")
            .WithDescription("Replaces every field of an existing yard, landowner included, and returns it. Fields left out are cleared or refused as missing; a yard the account does not have is not found.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return yards;
    }

    private static async Task<Results<Created<YardResult>, ValidationProblem>> CreateAsync(
        YardRequest request, IYardStore store, CancellationToken cancellationToken)
    {
        var parsed = YardRequestParser.Parse(request);
        if (parsed.Details is null)
        {
            return ProblemResults.Validation(Errors(parsed));
        }

        var yard = await store.AddAsync(parsed.Details, cancellationToken);
        return TypedResults.Created($"{Path}/{yard.YardId}", ToResult(yard));
    }

    private static async Task<Results<Ok<YardPage>, ValidationProblem>> ListAsync(
        IYardStore store,
        CancellationToken cancellationToken,
        [Description("How many yards to return: 1 to 100, default 50.")] int? limit = null,
        [Description("The nextCursor of the previous page; omit for the first page.")] string? cursor = null)
    {
        if (!PageRequest.TryRead(limit, cursor, out var request, out var errors))
        {
            return ProblemResults.Validation(errors);
        }

        var yards = await store.ListAsync(request.Skip, request.Fetch, cancellationToken);
        var page = yards.Take(request.Take).Select(ToResult).ToList();
        var nextCursor = request.NextCursor(yards.Count);
        return TypedResults.Ok(new YardPage(page, nextCursor));
    }

    private static async Task<Results<Ok<YardResult>, ProblemHttpResult>> GetAsync(
        [Description("The yard's id.")] Guid id, IYardStore store, CancellationToken cancellationToken)
    {
        var yard = id == Guid.Empty ? null : await store.FindAsync(new YardId(id), cancellationToken);
        return yard is null ? NoSuchYard() : TypedResults.Ok(ToResult(yard));
    }

    private static async Task<Results<Ok<YardResult>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        [Description("The yard's id.")] Guid id, YardRequest request, IYardStore store, CancellationToken cancellationToken)
    {
        var parsed = YardRequestParser.Parse(request);
        if (parsed.Details is null)
        {
            return ProblemResults.Validation(Errors(parsed));
        }

        var yard = id == Guid.Empty ? null : await store.UpdateAsync(new YardId(id), parsed.Details, cancellationToken);
        return yard is null ? NoSuchYard() : TypedResults.Ok(ToResult(yard));
    }

    private static ProblemHttpResult NoSuchYard() => ProblemResults.NotFound("no yard with that id.");

    private static Dictionary<string, string[]> Errors(YardParseResult parsed) =>
        parsed.Errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    private static YardResult ToResult(Yard yard)
    {
        var details = yard.Details;
        var landowner = details.Landowner;
        return new YardResult(
            yard.YardId.Value,
            details.Name,
            new LocationResult(details.Location.Latitude, details.Location.Longitude),
            YardRequestParser.StateCode(details.State),
            details.County,
            details.AccessNotes,
            details.ForageNotes,
            new LandownerResult(landowner.Contact, YardRequestParser.TermsCode(landowner.Terms), landowner.NearbyCrops, landowner.SprayContact));
    }
}
