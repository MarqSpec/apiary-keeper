using ApiaryCopilot.Api.SignIn;
using Microsoft.Extensions.Primitives;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// A write that carries the session cookie must also carry X-CSRF: 1. A browser adds a custom header only to a
// same-origin request (a cross-site one needs a CORS preflight the API never grants), so another site cannot make a
// signed-in browser write.
public sealed class CsrfHeaderCheckTests
{
    [Theory] // invariant: every write method with the session cookie and no header is refused
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("post")]
    [InlineData("PROPFIND")]
    public void IsAllowed_ShouldRefuse_WhenAWriteCarriesTheCookieButNoHeader(string method)
    {
        CsrfHeaderCheck.IsAllowed(method, hasSessionCookie: true, StringValues.Empty).Should().BeFalse();
    }

    [Theory] // boundary: only the exact value 1 counts; a header that is present but different is refused
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData(" 1")]
    public void IsAllowed_ShouldRefuse_WhenTheHeaderHasAnotherValue(string value)
    {
        CsrfHeaderCheck.IsAllowed("POST", hasSessionCookie: true, new StringValues(value)).Should().BeFalse();
    }

    [Fact] // boundary: a repeated header is refused, not read as its first value
    public void IsAllowed_ShouldRefuse_WhenTheHeaderIsRepeated()
    {
        CsrfHeaderCheck.IsAllowed("POST", hasSessionCookie: true, new StringValues(["1", "1"])).Should().BeFalse();
    }

    [Theory] // boundary: with the header, a write with the cookie goes through
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsAllowed_ShouldAllow_WhenAWriteCarriesTheCookieAndTheHeader(string method)
    {
        CsrfHeaderCheck.IsAllowed(method, hasSessionCookie: true, new StringValues("1")).Should().BeTrue();
    }

    [Theory] // boundary: reads are never refused, cookie or not (a read changes nothing an attacker could use)
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public void IsAllowed_ShouldAllow_WhenTheMethodIsSafe(string method)
    {
        CsrfHeaderCheck.IsAllowed(method, hasSessionCookie: true, StringValues.Empty).Should().BeTrue();
    }

    [Fact] // boundary: a write without the cookie (an agent's bearer call, or an anonymous one) is not this check's
    public void IsAllowed_ShouldAllow_WhenAWriteCarriesNoSessionCookie()
    {
        CsrfHeaderCheck.IsAllowed("POST", hasSessionCookie: false, StringValues.Empty).Should().BeTrue();
    }
}
