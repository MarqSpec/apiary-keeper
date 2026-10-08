using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authentication;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// A browser session ends after its idle timeout (the cookie's sliding expiry) and, however active, after its maximum
// lifetime, counted from sign-in rather than from the last renewal.
public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset SignedIn = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Max = TimeSpan.FromHours(12);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    [Fact] // regression risk: the start is kept with the session, and read back as stamped
    public void StartedAt_ShouldReturnTheStampedInstant_WhenStamped()
    {
        var properties = new AuthenticationProperties();

        SessionLifetime.Stamp(properties, SignedIn);

        SessionLifetime.StartedAt(properties).Should().Be(SignedIn);
    }

    [Fact] // boundary: a session is still on one tick before its maximum lifetime
    public void HasEnded_ShouldBeFalse_WhenTheMaximumLifetimeHasNotPassed()
    {
        var properties = Stamped();

        SessionLifetime.HasEnded(properties, SignedIn + Max - TimeSpan.FromTicks(1), Max).Should().BeFalse();
    }

    [Fact] // invariant: a session ends at its maximum lifetime however recently it was renewed
    public void HasEnded_ShouldBeTrue_WhenTheMaximumLifetimeHasPassed()
    {
        var properties = Stamped();
        properties.IssuedUtc = SignedIn + Max - TimeSpan.FromMinutes(1); // a sliding renewal moves the issue time

        SessionLifetime.HasEnded(properties, SignedIn + Max, Max).Should().BeTrue();
    }

    [Theory] // boundary: a session whose start is missing or unreadable is ended, never kept for ever
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    public void HasEnded_ShouldBeTrue_WhenTheStartIsMissingOrUnreadable(string? started)
    {
        var properties = new AuthenticationProperties();
        if (started is not null)
        {
            properties.Items[SessionLifetime.StartedKey] = started;
        }

        SessionLifetime.HasEnded(properties, SignedIn, Max).Should().BeTrue();
    }

    [Fact] // invariant: the stored session goes no later than the cookie's own expiry
    public void EntryExpiry_ShouldBeTheCookiesExpiry_WhenThatComesFirst()
    {
        var properties = Stamped();
        var now = SignedIn + TimeSpan.FromHours(1);
        properties.ExpiresUtc = now + Idle;

        SessionLifetime.EntryExpiry(properties, now, Idle, Max).Should().Be(now + Idle);
    }

    [Fact] // invariant: and no later than the maximum lifetime, however recently it was renewed
    public void EntryExpiry_ShouldBeTheMaximumLifetime_WhenThatComesFirst()
    {
        var properties = Stamped();
        var now = SignedIn + Max - TimeSpan.FromMinutes(5);
        properties.ExpiresUtc = now + Idle;

        SessionLifetime.EntryExpiry(properties, now, Idle, Max).Should().Be(SignedIn + Max);
    }

    [Fact] // boundary: a ticket with no expiry set still expires after the idle timeout
    public void EntryExpiry_ShouldBeTheIdleTimeout_WhenTheTicketHasNoExpiry()
    {
        var properties = Stamped();

        SessionLifetime.EntryExpiry(properties, SignedIn, Idle, Max).Should().Be(SignedIn + Idle);
    }

    private static AuthenticationProperties Stamped()
    {
        var properties = new AuthenticationProperties();
        SessionLifetime.Stamp(properties, SignedIn);
        return properties;
    }
}
