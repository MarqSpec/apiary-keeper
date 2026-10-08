using Microsoft.AspNetCore.Http.HttpResults;

namespace ApiaryCopilot.Api.Problems;

/// <summary>
/// The categorized results of CONVENTIONS.md §4.3, in one place: RFC 9457 problem details whose title is
/// the category message and whose status the category fixes.
/// </summary>
public static class ProblemResults
{
    /// <summary>400: one or more fields are invalid; each error names its field.</summary>
    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(errors, title: "Validation: one or more fields are invalid; correct them and retry.");

    /// <summary>403: the caller may not do this.</summary>
    public static ProblemHttpResult NotAllowed(string message) =>
        TypedResults.Problem(title: $"NotAllowed: {message}", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>404: no record the caller may see has that id.</summary>
    public static ProblemHttpResult NotFound(string message) =>
        TypedResults.Problem(title: $"NotFound: {message}", statusCode: StatusCodes.Status404NotFound);

    /// <summary>409: what the caller asked to create already exists.</summary>
    public static ProblemHttpResult Conflict(string message) =>
        TypedResults.Problem(title: $"Conflict: {message}", statusCode: StatusCodes.Status409Conflict);

    /// <summary>503: a dependency or setting the operation needs is not available.</summary>
    public static ProblemHttpResult Unavailable(string message) =>
        TypedResults.Problem(title: $"Unavailable: {message}", statusCode: StatusCodes.Status503ServiceUnavailable);
}
