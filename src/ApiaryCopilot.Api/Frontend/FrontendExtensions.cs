namespace ApiaryCopilot.Api.Frontend;

/// <summary>Serves the built family PWA from the web root (<c>wwwroot</c>), beside the API (D-5).</summary>
public static class FrontendExtensions
{
    /// <summary>
    /// Serves the web root's files, and <c>index.html</c> at <c>/</c>. Any other path is left to the endpoints, so an
    /// unknown path is still 404 rather than the app shell; with no built frontend, <c>/</c> is 404 too.
    /// </summary>
    public static WebApplication UseFrontend(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }
}
