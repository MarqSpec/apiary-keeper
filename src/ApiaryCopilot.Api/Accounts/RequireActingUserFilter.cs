using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// Fails closed: the signed-in caller acts only as the user of its token's account whose <c>ExternalSubject</c> is the
/// token's subject. With no subject, an account that has no row, or a subject no user of the account holds - one of another
/// account's users included - the endpoint answers 403 <c>NotAllowed</c> and its handler never runs, so nothing is read or
/// written, and no row is ever stamped with another account's user. Runs after <see cref="RequireCurrentAccountFilter"/>.
/// </summary>
public sealed class RequireActingUserFilter(ICurrentAccount currentAccount, ITenantReadiness readiness, ActingUser actingUser) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (currentAccount.AccountId is not { } account)
        {
            return ProblemResults.Unavailable("no signed-in account; nothing can be read or written.");
        }

        if (ApiaryClaims.SubjectOf(context.HttpContext.User) is not { } subject)
        {
            return ProblemResults.NotAllowed("the caller is not signed in as a user; nothing was read or written.");
        }

        var lookup = await readiness.ResolveAsync(account, subject, context.HttpContext.RequestAborted);
        switch (lookup.Status)
        {
            case TenantStatus.Ready:
                actingUser.Set(lookup.UserId!.Value);
                return await next(context);
            case TenantStatus.AccountMissing:
                return ProblemResults.NotAllowed("your account is not set up on this server; nothing was read or written.");
            case TenantStatus.SubjectUnknown:
                return ProblemResults.NotAllowed("you are not a user of this account; nothing was read or written.");
            default:
                throw new InvalidOperationException($"Unhandled tenant status {lookup.Status}.");
        }
    }
}
