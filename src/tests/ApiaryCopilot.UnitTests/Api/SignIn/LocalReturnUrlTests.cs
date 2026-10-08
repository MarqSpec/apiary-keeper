using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// Where sign-in returns the browser: only a path on this host, so the login link cannot be made an open redirect.
public sealed class LocalReturnUrlTests
{
    [Theory] // boundary: a path on this host is kept, a PWA screen in the fragment included (D-20)
    [InlineData("/", "/")]
    [InlineData("/#/yards/7", "/#/yards/7")]
    [InlineData("/#/colonies/3/inspect", "/#/colonies/3/inspect")]
    [InlineData("/?tab=queens", "/?tab=queens")]
    public void Sanitize_ShouldKeepThePath_WhenItIsLocal(string returnUrl, string expected)
    {
        LocalReturnUrl.Sanitize(returnUrl).Should().Be(expected);
    }

    [Theory] // invariant: anything that could leave this host, or is not a path, falls back to the root
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("\\\\evil.example")]
    [InlineData("yards")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\tevil")]
    [InlineData("/\r\nSet-Cookie: x=1")]
    [InlineData(" /")]
    public void Sanitize_ShouldFallBackToTheRoot_WhenItIsNotLocal(string? returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).Should().Be("/");
    }

    [Fact] // boundary: an unreasonably long return URL is not carried through the sign-in state
    public void Sanitize_ShouldFallBackToTheRoot_WhenItIsTooLong()
    {
        LocalReturnUrl.Sanitize("/" + new string('a', LocalReturnUrl.MaxLength)).Should().Be("/");
    }
}
