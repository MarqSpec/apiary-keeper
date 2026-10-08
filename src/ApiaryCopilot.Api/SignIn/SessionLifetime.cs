using System.Globalization;
using Microsoft.AspNetCore.Authentication;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// A browser session's two limits: the idle timeout, which is the cookie's sliding expiry, and the maximum lifetime,
/// counted from sign-in. A sliding renewal moves the ticket's issue time, so the start is kept with the session itself.
/// </summary>
public static class SessionLifetime
{
    /// <summary>The session's own item holding when it was signed in, round-trip format.</summary>
    public const string StartedKey = ".apiary-keeper.session.started";

    /// <summary>Records the sign-in instant on a new session.</summary>
    public static void Stamp(AuthenticationProperties properties, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(properties);
        properties.Items[StartedKey] = now.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>When the session was signed in, or <see langword="null"/> when that is missing or unreadable.</summary>
    public static DateTimeOffset? StartedAt(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(StartedKey, out var value)
            && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
                ? started
                : null;
    }

    /// <summary>Whether the session has reached its maximum lifetime; one with no readable start has.</summary>
    public static bool HasEnded(AuthenticationProperties properties, DateTimeOffset now, TimeSpan maxLifetime) =>
        StartedAt(properties) is not { } started || now >= started + maxLifetime;

    /// <summary>When the stored session must be gone: the cookie's expiry or the maximum lifetime, whichever is first.</summary>
    public static DateTimeOffset EntryExpiry(AuthenticationProperties properties, DateTimeOffset now, TimeSpan idleTimeout, TimeSpan maxLifetime)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var expiry = properties.ExpiresUtc ?? now + idleTimeout;
        var end = (StartedAt(properties) ?? now) + maxLifetime;
        return expiry < end ? expiry : end;
    }
}
