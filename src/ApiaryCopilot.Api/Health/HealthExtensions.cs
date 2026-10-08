namespace ApiaryCopilot.Api.Health;

public static class HealthExtensions
{
    public const string Path = "/health";

    // Answers only once the host is serving; anonymous so the smoke check needs no credentials.
    public static IEndpointConventionBuilder MapHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(Path, () => Results.Ok(new { version = AppVersion.Current }))
            .WithName("GetHealth")
            .AllowAnonymous();
}
