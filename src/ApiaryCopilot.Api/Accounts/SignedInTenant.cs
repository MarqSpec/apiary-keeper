using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// The account the request acts for: the signed-in caller's <c>account_id</c> (D-22), read from the principal the
/// request authenticated with (<see cref="ApiaryClaims"/>) and nothing else. No principal - sign-in off, or an anonymous
/// caller - is no account, so every query returns nothing (NFR-8). Replaces the D-18 configuration stopgap.
/// </summary>
public sealed class SignedInAccount(IHttpContextAccessor http) : ICurrentAccount
{
    /// <inheritdoc />
    public AccountId? AccountId => ApiaryClaims.AccountOf(http.HttpContext?.User) is { } account ? new AccountId(account) : null;
}

/// <summary>
/// The user the request acts as: set once per request by <see cref="RequireActingUserFilter"/>, from the user whose
/// <c>ExternalSubject</c> is the token's subject, before the handler runs. Unset, nothing can be written.
/// </summary>
public sealed class ActingUser : ICurrentUser
{
    /// <inheritdoc />
    public UserId? UserId { get; private set; }

    /// <summary>Sets the acting user; a second, different one is refused, so a request never acts as two users.</summary>
    /// <exception cref="InvalidOperationException">Another user was already set for this request.</exception>
    public void Set(UserId userId)
    {
        if (UserId is { } current && current != userId)
        {
            throw new InvalidOperationException("The acting user is already set for this request.");
        }

        UserId = userId;
    }
}

/// <summary>Registers the account and acting user taken from the signed-in caller.</summary>
public static class SignedInTenantExtensions
{
    /// <summary>
    /// Registers <see cref="ICurrentAccount"/> from the principal, <see cref="ICurrentUser"/> as the request's
    /// <see cref="ActingUser"/>, and the cached lookup of the acting user (<see cref="ITenantReadiness"/>), in place of the
    /// persistence layer's defaults whichever is registered first.
    /// </summary>
    public static IServiceCollection AddSignedInTenant(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentAccount, SignedInAccount>();
        services.AddScoped<ActingUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ActingUser>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ReadyTenants>();
        services.AddScoped<ITenantReadiness, CachedTenantReadiness>();
        return services;
    }
}
