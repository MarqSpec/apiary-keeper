using System.Security.Claims;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Domain;
using Microsoft.AspNetCore.Http;

namespace ApiaryCopilot.UnitTests.Api;

// The account every query is filtered to (NFR-8) comes from the authenticated principal's account_id and nothing else, and
// the acting user is set once per request; both replace the D-18 configuration stopgap.
public sealed class SignedInTenantTests
{
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";

    [Fact] // invariant: the current account is the signed-in caller's account_id
    public void AccountId_ShouldBeTheCallersAccount_WhenTheRequestIsSignedIn()
    {
        var account = AccountFor(ApiFactory.SignedIn(AccountId));

        account.AccountId.Should().Be(new AccountId(Guid.Parse(AccountId)));
    }

    [Fact] // invariant: an anonymous request has no account, so every query returns nothing
    public void AccountId_ShouldBeNull_WhenTheRequestIsAnonymous()
    {
        AccountFor(new ClaimsPrincipal(new ClaimsIdentity())).AccountId.Should().BeNull();
    }

    [Fact] // invariant: a principal with the right claims but not built by the sign-in (another scheme) is no account
    public void AccountId_ShouldBeNull_WhenThePrincipalIsNotTheSignInsOwn()
    {
        var foreign = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x"), new Claim("account_id", AccountId)], "some-other-scheme"));

        AccountFor(foreign).AccountId.Should().BeNull();
    }

    [Fact] // boundary: outside a request there is no account
    public void AccountId_ShouldBeNull_WhenThereIsNoRequest()
    {
        new SignedInAccount(new HttpContextAccessor()).AccountId.Should().BeNull();
    }

    [Fact] // invariant: a request acts as one user only; setting another is refused
    public void Set_ShouldRefuseASecondUser_WhenOneIsAlreadySet()
    {
        var user = new ActingUser();
        var first = new UserId(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        user.Set(first);

        var again = () => user.Set(first);
        var other = () => user.Set(new UserId(Guid.Parse("44444444-4444-4444-8444-444444444444")));

        again.Should().NotThrow();
        other.Should().Throw<InvalidOperationException>();
        user.UserId.Should().Be(first);
    }

    [Fact] // boundary: until it is set there is no acting user, so nothing can be written
    public void UserId_ShouldBeNull_WhenNotSet()
    {
        new ActingUser().UserId.Should().BeNull();
    }

    private static SignedInAccount AccountFor(ClaimsPrincipal principal) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
}
