using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// Every account-scoped endpoint acts for the signed-in caller's account and as the user of it whose ExternalSubject is the
// caller's subject, and fails closed - before any store is touched - when there is no caller (503: sign-in off), the
// account has no row or no user of it holds the subject (403). Through the real host in memory, the account read from
// the principal as in production, the directory faked (CONVENTIONS.md §8.1).
public sealed class TenantReadinessEndpointsTests
{
    private const string AccountId = "11111111-1111-4111-8111-111111111111";
    private const string KnownId = "0199b2a4-0000-7000-8000-000000000001";

    private readonly IYardStore _yards = A.Fake<IYardStore>();
    private readonly IColonyStore _colonies = A.Fake<IColonyStore>();
    private readonly IInspectionStore _inspections = A.Fake<IInspectionStore>();
    private readonly IInspectionMediaStore _media = A.Fake<IInspectionMediaStore>();
    private readonly IMediaStorage _storage = A.Fake<IMediaStorage>();

    public TenantReadinessEndpointsTests() => A.CallTo(() => _storage.IsConfigured).Returns(true);

    // One request per route group: yards (read and write), colonies, inspections and media.
    public static TheoryData<string, string> OneEndpointPerGroup() => new()
    {
        { "GET", "/api/yards" },
        { "POST", "/api/yards" },
        { "GET", "/api/colonies" },
        { "GET", $"/api/inspections/{KnownId}" },
        { "GET", $"/api/inspections/{KnownId}/media" },
    };

    [Theory] // regression risk: a token whose account has no row answered 200 empty or 500 instead of failing closed
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeRefused_WhenTheCallersAccountHasNoRow(string method, string path)
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId));
        A.CallTo(() => factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.AccountMissing);

        using var response = await SendAsync(factory, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:").And.Contain("your account is not set up on this server");
        AssertNothingTouched();
    }

    [Theory] // invariant: an unknown subject - or another account's user - is refused, and nothing is stamped with it
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeRefused_WhenNoUserOfTheAccountHasTheCallersSubject(string method, string path)
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId));
        A.CallTo(() => factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.SubjectUnknown);

        using var response = await SendAsync(factory, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:").And.Contain("you are not a user of this account");
        AssertNothingTouched();
    }

    [Fact] // invariant: a signed-in user of the account reaches the handler, looked up by the token's subject
    public async Task Endpoint_ShouldReachTheHandler_WhenTheCallerIsAUserOfTheAccount()
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId, subject: "synthetic-subject-of-the-caller"));

        using var response = await SendAsync(factory, "GET", "/api/yards");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => factory.TenantDirectory.FindActingUserAsync("synthetic-subject-of-the-caller", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _yards.ListAsync(A<int>._, A<int>._, A<CancellationToken>._)).MustHaveHappened();
    }

    [Theory] // invariant: with no signed-in caller (sign-in off) every group fails closed with 503 and the directory is not asked
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeUnavailable_WhenThereIsNoSignedInCaller(string method, string path)
    {
        using var factory = Factory(caller: null);

        using var response = await SendAsync(factory, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:").And.Contain("sign-in is not set up on this server");
        A.CallTo(factory.TenantDirectory).MustNotHaveHappened();
        AssertNothingTouched();
    }

    [Fact] // invariant: only a principal the sign-in built counts; one claiming the same claims under another scheme is no caller
    public async Task Endpoint_ShouldBeUnavailable_WhenThePrincipalIsNotTheSignInsOwn()
    {
        var foreign = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", ApiFactory.CallerSubject), new Claim("account_id", AccountId), new Claim("role", "owner")], "some-other-scheme"));
        using var factory = Factory(foreign);

        using var response = await SendAsync(factory, "GET", "/api/yards");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        A.CallTo(factory.TenantDirectory).MustNotHaveHappened();
        AssertNothingTouched();
    }

    [Fact] // invariant: a signed-in principal with an account but no subject acts as no one: refused, nothing touched
    public async Task Endpoint_ShouldBeRefused_WhenThePrincipalHasNoSubject()
    {
        var noSubject = new ClaimsPrincipal(new ClaimsIdentity([new Claim("account_id", AccountId), new Claim("role", "owner")], "apiary-keeper", "sub", "role"));
        using var factory = Factory(noSubject);

        using var response = await SendAsync(factory, "POST", "/api/yards");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().Contain("not signed in as a user");
        A.CallTo(factory.TenantDirectory).MustNotHaveHappened();
        AssertNothingTouched();
    }

    private ApiFactory Factory(ClaimsPrincipal? caller) => new(
        caller: caller,
        services: s => s
            .AddScoped(_ => _yards)
            .AddScoped(_ => _colonies)
            .AddScoped(_ => _inspections)
            .AddScoped(_ => _media)
            .AddSingleton(_storage));

    private static async Task<HttpResponseMessage> SendAsync(ApiFactory factory, string method, string path)
    {
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new
            {
                name = "Mesquite Flat",
                location = new { latitude = 30.2672, longitude = -97.7431 },
                state = "TX",
                county = "Travis",
                landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
            });
        }

        return await client.SendAsync(request);
    }

    private void AssertNothingTouched()
    {
        A.CallTo(_yards).MustNotHaveHappened();
        A.CallTo(_colonies).MustNotHaveHappened();
        A.CallTo(_inspections).MustNotHaveHappened();
        A.CallTo(_media).MustNotHaveHappened();
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("title").GetString();
    }
}
