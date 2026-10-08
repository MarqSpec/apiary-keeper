using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// Fails closed: with no current account - sign-in is not set up on this server, so no caller has one - the endpoint
/// answers 503 <c>Unavailable</c> and its handler never runs, so nothing is read or written (NFR-8).
/// </summary>
public sealed class RequireCurrentAccountFilter(ICurrentAccount currentAccount) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return currentAccount.AccountId is null
            ? ProblemResults.Unavailable("no signed-in account: sign-in is not set up on this server; nothing can be read or written.")
            : await next(context);
    }
}
