using ApiaryCopilot.Api.Problems;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// Fails closed: only a caller whose principal holds <c>owner</c> reaches the handler; anyone else is 403 <c>NotAllowed</c>
/// and nothing is done. Runs after <see cref="RequireActingUserFilter"/>.
/// </summary>
public sealed class RequireOwnerFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return context.HttpContext.User.IsInRole(AccountMemberAuthorization.OwnerRole)
            ? await next(context)
            : ProblemResults.NotAllowed("only the account's owner may do this; nothing was done.");
    }
}
