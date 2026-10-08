using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>Whether the signed-in caller can act in its account, as the database sees it.</summary>
public enum TenantStatus
{
    /// <summary>The account has a row and one of its users has the caller's subject.</summary>
    Ready,

    /// <summary>No account is current, or it has no row: the caller's account is not set up on this server.</summary>
    AccountMissing,

    /// <summary>
    /// No user of the current account has the caller's subject: none has signed in as it yet, or the user belongs to another
    /// account (the account filter hides that row, and is not bypassed to tell the two apart).
    /// </summary>
    SubjectUnknown,
}

/// <summary>The outcome of resolving a signed-in caller: its status, and the acting user when it is <see cref="TenantStatus.Ready"/>.</summary>
public readonly record struct TenantLookup(TenantStatus Status, UserId? UserId)
{
    /// <summary>The caller acts as <paramref name="userId"/>.</summary>
    public static TenantLookup Ready(UserId userId) => new(TenantStatus.Ready, userId);

    /// <summary>The caller's account is not set up on this server.</summary>
    public static TenantLookup AccountMissing => new(TenantStatus.AccountMissing, null);

    /// <summary>No user of the caller's account has its subject.</summary>
    public static TenantLookup SubjectUnknown => new(TenantStatus.SubjectUnknown, null);
}
