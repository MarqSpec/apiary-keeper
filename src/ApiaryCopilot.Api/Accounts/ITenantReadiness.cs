using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>Resolves the signed-in caller to the user it acts as, remembering a found user briefly.</summary>
public interface ITenantReadiness
{
    /// <summary>The caller's status in <paramref name="account"/>; only <see cref="TenantStatus.Ready"/> lets a request through.</summary>
    Task<TenantLookup> ResolveAsync(AccountId account, string subject, CancellationToken cancellationToken);
}
