using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// Every apiary endpoint authorized from the signed-in principal against the seeded realm and the real PostgreSQL: the
// role (owner or helper only), the account from the token's account_id (the global query filters), the acting user from
// the token's sub through Users.ExternalSubject (the row stamps), and the fail-closed refusals that replaced the D-18
// stopgap's 503s - an account with no row, a subject no user of the account holds, another account's user. Each test makes
// its own Keycloak users through the admin API and its own rows, so the shared server's owner is never changed.
// Deployment=None: Keycloak, PostgreSQL and MinIO are loopback containers.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class EndpointAuthorizationTests(AppServerFixture server)
{
    private const string SomeId = "0199b2a4-0000-7000-8000-000000000001";

    // One read and one write per route group: yards, colonies, inspections and media.
    public static TheoryData<string, string> OneEndpointPerGroup() => new()
    {
        { "GET", "/api/yards" },
        { "POST", "/api/yards" },
        { "GET", "/api/colonies" },
        { "GET", $"/api/inspections/{SomeId}" },
        { "GET", $"/api/inspections/{SomeId}/media" },
    };

    [Theory] // invariant: no credential, no apiary data: 401 on every group
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeUnauthorized_WhenNotSignedIn(string method, string path)
    {
        using var anonymous = AppServerFixture.CreateClient(server.Address);

        using var response = await anonymous.SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory] // invariant (NFR-6, D-22): a grower or a customer is refused every group, though signed in and of the account
    [InlineData("grower")]
    [InlineData("customer")]
    public async Task Endpoint_ShouldBeForbidden_WhenTheRoleIsNotOwnerOrHelper(string role)
    {
        var user = await server.Keycloak.CreateUserAsync(role, server.CurrentAccountId.ToString());
        await server.Database.AddUserAsync(server.CurrentAccountId, new UserId(Guid.NewGuid()), user.Id);
        var yardsBefore = await YardsOfAsync(server.CurrentAccountId);
        using var client = server.ClientFor(user);

        foreach (var (method, path) in Groups())
        {
            using var response = await client.SendAsync(Request(method, path));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} on {method} {path}");
        }

        (await YardsOfAsync(server.CurrentAccountId)).Should().Be(yardsBefore, "nothing was written");
    }

    [Fact] // invariant (NFR-8): a helper of another account sees none of this account's records and writes only to its own
    public async Task Endpoint_ShouldKeepTheCallerInItsOwnAccount_WhenTheCallerBelongsToAnotherAccount()
    {
        var oursName = $"Synthetic Authorization Yard {Guid.NewGuid():N}";
        var ours = await CreateYardAsync(server.Client, oursName);
        var outsider = await server.Keycloak.CreateUserAsync("helper", server.OtherAccountId.ToString());
        var outsiderUser = new UserId(Guid.NewGuid());
        await server.Database.AddUserAsync(server.OtherAccountId, outsiderUser, outsider.Id);
        using var client = server.ClientFor(outsider);

        using var read = await client.GetAsync($"/api/yards/{ours}");
        using var list = await client.GetAsync("/api/yards?limit=100");
        var theirs = await CreateYardAsync(client, $"Synthetic Authorization Yard {Guid.NewGuid():N}");

        read.StatusCode.Should().Be(HttpStatusCode.NotFound, "another account's yard is not there for this caller");
        var names = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(y => y.GetProperty("name").GetString()).ToList();
        names.Should().Contain(AppServerFixture.OtherAccountYardName).And.NotContain(oursName);
        (await server.Database.CountAsync(
            $"SELECT count(*) FROM \"Yards\" WHERE \"YardId\" = '{theirs}' AND \"AccountId\" = '{server.OtherAccountId}' AND \"CreatedBy\" = '{outsiderUser}'"))
            .Should().Be(1, "the write is the outsider's own account's, stamped with the outsider");
    }

    [Fact] // invariant: the acting user is the token's: a helper's write is stamped with the helper's user, not the owner's
    public async Task Write_ShouldBeStampedWithTheSignedInUser_WhenAHelperWrites()
    {
        var helper = await server.Keycloak.CreateUserAsync("helper", server.CurrentAccountId.ToString());
        var helperUser = new UserId(Guid.NewGuid());
        await server.Database.AddUserAsync(server.CurrentAccountId, helperUser, helper.Id);
        using var client = server.ClientFor(helper);

        var yard = await CreateYardAsync(client, $"Synthetic Authorization Yard {Guid.NewGuid():N}");

        (await server.Database.CountAsync(
            $"SELECT count(*) FROM \"Yards\" WHERE \"YardId\" = '{yard}' AND \"AccountId\" = '{server.CurrentAccountId}' AND \"CreatedBy\" = '{helperUser}' AND \"LastModifiedBy\" = '{helperUser}'"))
            .Should().Be(1);
    }

    [Theory] // regression risk: a token whose account has no row answered 200 empty or 500 instead of failing closed
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeForbiddenAndWriteNothing_WhenTheTokensAccountHasNoRow(string method, string path)
    {
        var unseeded = new AccountId(Guid.NewGuid());
        var user = await server.Keycloak.CreateUserAsync("owner", unseeded.ToString());
        using var client = server.ClientFor(user);

        using var response = await client.SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().Contain("your account is not set up on this server");
        (await YardsOfAsync(unseeded)).Should().Be(0);
    }

    [Theory] // invariant: an unknown sub - an owner of the account no user row names - is refused, and nothing is written
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task Endpoint_ShouldBeForbiddenAndWriteNothing_WhenNoUserHasTheTokensSubject(string method, string path)
    {
        var user = await server.Keycloak.CreateUserAsync("owner", server.CurrentAccountId.ToString());
        var yardsBefore = await YardsOfAsync(server.CurrentAccountId);
        using var client = server.ClientFor(user);

        using var response = await client.SendAsync(Request(method, path));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().Contain("you are not a user of this account");
        (await YardsOfAsync(server.CurrentAccountId)).Should().Be(yardsBefore);
    }

    [Fact] // regression risk: a user of another account was accepted and stamped onto this account's rows
    public async Task Write_ShouldBeForbiddenAndStampNothing_WhenTheSubjectsUserBelongsToAnotherAccount()
    {
        // The token names this account, but its subject's row is the other account's: the account filter hides it.
        var user = await server.Keycloak.CreateUserAsync("owner", server.CurrentAccountId.ToString());
        var elsewhere = new UserId(Guid.NewGuid());
        await server.Database.AddUserAsync(server.OtherAccountId, elsewhere, user.Id);
        using var client = server.ClientFor(user);

        using var write = await client.SendAsync(Request("POST", "/api/yards"));

        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.Database.CountAsync($"SELECT count(*) FROM \"Yards\" WHERE \"CreatedBy\" = '{elsewhere}'")).Should().Be(0);
    }

    [Fact] // invariant: a refusal is never remembered, so a user whose row gets the subject later is served at once, no restart
    public async Task Endpoint_ShouldServeTheUser_WhenItsRowGetsTheSubjectAfterARefusal()
    {
        var user = await server.Keycloak.CreateUserAsync("helper", server.CurrentAccountId.ToString());
        var row = new UserId(Guid.NewGuid());
        await server.Database.AddUserAsync(server.CurrentAccountId, row, null);
        using var client = server.ClientFor(user);
        using var before = await client.GetAsync("/api/yards");

        await server.Database.SetExternalSubjectAsync(row, user.Id);
        using var after = await client.GetAsync("/api/yards");

        before.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static IEnumerable<(string Method, string Path)> Groups() =>
        OneEndpointPerGroup().Select(row => ((string)row[0], (string)row[1]));

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(YardBody($"Synthetic Authorization Yard {Guid.NewGuid():N}"));
        }

        return request;
    }

    private static object YardBody(string name) => new
    {
        name,
        location = new { latitude = 30.2672, longitude = -97.7431 },
        state = "TX",
        county = "Travis",
        landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
    };

    private static async Task<Guid> CreateYardAsync(HttpClient client, string name)
    {
        using var created = await client.PostAsJsonAsync("/api/yards", YardBody(name));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<long> YardsOfAsync(AccountId account) =>
        server.Database.CountAsync($"SELECT count(*) FROM \"Yards\" WHERE \"AccountId\" = '{account}'");

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
}
