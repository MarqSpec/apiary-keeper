namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// Resolves a signed-in caller against the database: the current account (the token's <c>account_id</c>,
/// <see cref="ICurrentAccount"/>) has a row, and one of its users has the token's subject in <c>ExternalSubject</c> - that
/// user is the one the caller acts as. Fails closed, so an account with no row, or a subject no user of it holds, never
/// reads nothing or writes rows stamped with another account's user.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>Reads the current state for <paramref name="subject"/>; never cached here.</summary>
    Task<TenantLookup> FindActingUserAsync(string subject, CancellationToken cancellationToken);
}
