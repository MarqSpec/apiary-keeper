using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Media;

/// <summary>
/// Fails closed: with no media bucket configured the endpoint answers 503 <c>Unavailable</c> and its handler never runs,
/// so nothing is read, written or signed (DEPLOYMENT.md §3).
/// </summary>
public sealed class RequireMediaStorageFilter(IMediaStorage storage) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return storage.IsConfigured
            ? await next(context)
            : ProblemResults.Unavailable("no media bucket is set up on this server yet; photos and videos cannot be uploaded or read.");
    }
}
