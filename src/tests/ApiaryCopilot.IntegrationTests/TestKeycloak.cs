using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiaryCopilot.IntegrationTests;

// A throwaway Keycloak for one test class: the image compose.yaml runs, pinned by the same digest (DEPLOYMENT.md §1), in
// development mode, importing the committed synthetic realm (keycloak/import/) as it starts, published on a free loopback
// port. The realm's passwords and client secrets are synthetic test values for that realm only, read from the file.
// Given a docker network, it also joins it under its container name, with its issuer pinned to the loopback address the
// test's browser uses and its back-channel endpoints answering for the address they are asked on, so a server in a
// container on that network signs in through it too (BackChannelMetadataAddress).
public sealed class TestKeycloak : IAsyncLifetime, IDisposable
{
    public const string Image = "quay.io/keycloak/keycloak:26.8.0@sha256:b0f60d489d51c5d113390bdf5461d4c06e6051be026c05549f2e1e10ec352bcc";
    public const string RealmName = "apiary-keeper-dev";
    public const string BrowserClientId = "apiary-keeper-pwa";
    public const string BffClientId = "apiary-keeper-bff";
    public const string TestClientId = "apiary-keeper-integration-tests";
    public const string ApiAudience = "apiary-keeper-api";

    /// <summary>The API's service account's client (D-22): client credentials only, its grant in the realm's admin permissions.</summary>
    public const string ServiceAccountClientId = "apiary-keeper-api-admin";

    /// <summary>The group owners hold <c>owner</c> through, whose members the service account cannot disable or edit (D-22).</summary>
    public const string OwnersGroup = "owners";

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private const string AdminUsername = "synthetic-it-admin";

    private readonly int _hostPort = FreePort();
    private readonly string _adminPassword = Guid.NewGuid().ToString("N");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _network;
    private bool _containerCreated;

    public TestKeycloak()
        : this(network: null)
    {
    }

    internal TestKeycloak(string? network)
    {
        _network = network;
        ContainerName = $"apiary-it-kc-{_hostPort}";
        Realm = SeededRealm.Load(Path.Combine(FindRepoRoot(), "keycloak", "import", $"{RealmName}-realm.json"));
    }

    /// <summary>The container's name, unique per run; its host name on the network it joined.</summary>
    public string ContainerName { get; }

    /// <summary>The committed realm file this Keycloak imported.</summary>
    public SeededRealm Realm { get; }

    /// <summary>The realm's URL from this machine, which is also its issuer.</summary>
    public string RealmUrl => $"http://127.0.0.1:{_hostPort}/realms/{RealmName}";

    /// <summary>The discovery document from a container on the joined network.</summary>
    public string BackChannelMetadataAddress => $"http://{ContainerName}:8080/realms/{RealmName}/.well-known/openid-configuration";

    public async Task InitializeAsync()
    {
        try
        {
            var network = _network is null
                ? string.Empty
                : $"--network {_network} -e KC_HOSTNAME=http://127.0.0.1:{_hostPort} -e KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true ";
            // A bootstrap administrator made up per run, so a test can change a user through the admin API as an administrator would.
            var admin = $"-e KC_BOOTSTRAP_ADMIN_USERNAME={AdminUsername} -e KC_BOOTSTRAP_ADMIN_PASSWORD={_adminPassword} ";
            await DockerAsync($"create --name {ContainerName} {network}{admin}-p 127.0.0.1:{_hostPort}:8080 {Image} start-dev --import-realm");
            _containerCreated = true;
            await DockerAsync($"cp \"{Path.GetDirectoryName(Realm.Path)}{Path.DirectorySeparatorChar}.\" {ContainerName}:/opt/keycloak/data/import");
            await DockerAsync($"start {ContainerName}");
            await WaitUntilReadyAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>Signs in as a synthetic user through the test client's direct-access grant, asking for an ID token too.</summary>
    public Task<HttpResponseMessage> PasswordGrantAsync(string username, string password) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = TestClientId,
            ["client_secret"] = Realm.TestClientSecret,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        });

    /// <summary>Renews the tokens with a refresh token, through the test client.</summary>
    public Task<HttpResponseMessage> RefreshGrantAsync(string refreshToken) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = TestClientId,
            ["client_secret"] = Realm.TestClientSecret,
            ["refresh_token"] = refreshToken,
        });

    /// <summary>Posts a form to the realm's token endpoint.</summary>
    public async Task<HttpResponseMessage> TokenRequestAsync(IReadOnlyDictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        return await _http.PostAsync(new Uri($"{RealmUrl}/protocol/openid-connect/token"), content);
    }

    /// <summary>
    /// Creates a synthetic user through the admin API, with one realm role, an account and a password made up here. Its
    /// Keycloak id is the <c>sub</c> its tokens carry.
    /// </summary>
    public async Task<CreatedUser> CreateUserAsync(string role, string accountId)
    {
        var username = $"synthetic-it-{Guid.NewGuid():N}";
        var password = $"synthetic-it-{Guid.NewGuid():N}";
        using var created = await AdminAsync(HttpMethod.Post, "users", new
        {
            username,
            enabled = true,
            emailVerified = true,
            email = $"{username}@apiary-keeper.test",
            firstName = "Synthetic",
            lastName = "Tester",
            attributes = new Dictionary<string, string[]> { ["account_id"] = [accountId] },
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        });
        var id = created.Headers.Location!.Segments[^1];
        await AddRealmRoleAsync(id, role);
        return new CreatedUser(id, username, password);
    }

    /// <summary>A user's Keycloak id - the <c>sub</c> its tokens carry - by user name.</summary>
    public async Task<string> UserIdOfAsync(string username)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users?exact=true&username={Uri.EscapeDataString(username)}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Single()!["id"]!.GetValue<string>();
    }

    /// <summary>Gives a user a realm role.</summary>
    public async Task AddRealmRoleAsync(string userId, string role)
    {
        using var added = await AdminAsync(HttpMethod.Post, $"users/{userId}/role-mappings/realm", new[] { await RealmRoleAsync(role) });
    }

    /// <summary>Takes a realm role away from a user.</summary>
    public async Task RemoveRealmRoleAsync(string userId, string role)
    {
        using var removed = await AdminAsync(HttpMethod.Delete, $"users/{userId}/role-mappings/realm", new[] { await RealmRoleAsync(role) });
    }

    /// <summary>Changes a user's <c>account_id</c> attribute, keeping the rest of the user as it is.</summary>
    public Task SetAccountAsync(string userId, string accountId) =>
        UpdateUserAsync(userId, user => user["attributes"] = new JsonObject { ["account_id"] = new JsonArray(accountId) });

    /// <summary>Disables a user, as an administrator (or the API's service account, D-22) would.</summary>
    public Task DisableUserAsync(string userId) => UpdateUserAsync(userId, user => user["enabled"] = false);

    /// <summary>How many Keycloak sessions the user has.</summary>
    public async Task<int> SessionCountAsync(string userId)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users/{userId}/sessions");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Count;
    }

    /// <summary>Sets one client's access-token lifespan, so a test can reach a renewal in seconds rather than minutes.</summary>
    public async Task SetAccessTokenLifespanAsync(string clientId, TimeSpan lifespan)
    {
        using var found = await AdminAsync(HttpMethod.Get, $"clients?clientId={Uri.EscapeDataString(clientId)}");
        var client = JsonNode.Parse(await found.Content.ReadAsStringAsync())!.AsArray().Single()!.AsObject();
        var attributes = client["attributes"] as JsonObject ?? [];
        attributes["access.token.lifespan"] = ((int)lifespan.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        client["attributes"] = attributes.DeepClone();
        using var updated = await AdminAsync(HttpMethod.Put, $"clients/{client["id"]!.GetValue<string>()}", client);
    }

    /// <summary>
    /// The settings the API's service account takes (DEPLOYMENT.md §3), for this realm: the client, its synthetic secret
    /// and the member roles' ids, all read from the committed file.
    /// </summary>
    public IReadOnlyDictionary<string, string> ServiceAccountSettings() => new Dictionary<string, string>
    {
        ["KeycloakAdmin__ClientId"] = ServiceAccountClientId,
        ["KeycloakAdmin__ClientSecret"] = Realm.ClientSecretOf(ServiceAccountClientId),
        ["KeycloakAdmin__HelperRoleId"] = Realm.RoleIdOf("helper"),
        ["KeycloakAdmin__GrowerRoleId"] = Realm.RoleIdOf("grower"),
        ["KeycloakAdmin__CustomerRoleId"] = Realm.RoleIdOf("customer"),
    };

    /// <summary>One admin request to this realm as the API's service account, answered as Keycloak answers it.</summary>
    public async Task<HttpResponseMessage> AsServiceAccountAsync(HttpMethod method, string path, string? json = null)
    {
        using var tokenResponse = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ServiceAccountClientId,
            ["client_secret"] = Realm.ClientSecretOf(ServiceAccountClientId),
        });
        tokenResponse.EnsureSuccessStatusCode();
        var token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
        using var request = new HttpRequestMessage(method, new Uri($"http://127.0.0.1:{_hostPort}/admin/realms/{RealmName}/{path}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request);
    }

    /// <summary>How many users have exactly this user name, as the administrator reads it.</summary>
    public async Task<int> UsersNamedAsync(string username)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users?exact=true&username={Uri.EscapeDataString(username)}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Count;
    }

    /// <summary>A user as the administrator reads it.</summary>
    public async Task<JsonObject> UserAsync(string userId)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users/{userId}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    /// <summary>A user's effective realm roles - those its groups give included - as the administrator reads them.</summary>
    public async Task<IReadOnlyList<string>> EffectiveRealmRolesAsync(string userId)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users/{userId}/role-mappings/realm/composite");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToList();
    }

    /// <summary>The names of a user's groups, as the administrator reads them.</summary>
    public async Task<IReadOnlyList<string>> GroupsOfAsync(string userId)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"users/{userId}/groups");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Select(g => g!["name"]!.GetValue<string>()).ToList();
    }

    /// <summary>
    /// Does what an invited user does through the emailed link and the first sign-in's profile page - verifies the address,
    /// fills in the last name the realm's profile asks for and sets a password, here one made up per run - so a test can
    /// sign in as the user without driving Keycloak's pages.
    /// </summary>
    public async Task<CreatedUser> CompleteInvitationAsync(string userId)
    {
        var password = $"synthetic-it-{Guid.NewGuid():N}";
        await UpdateUserAsync(userId, user =>
        {
            user["emailVerified"] = true;
            user["lastName"] = "Tester";
        });
        using var reset = await AdminAsync(HttpMethod.Put, $"users/{userId}/reset-password", new { type = "password", value = password, temporary = false });
        var user = await UserAsync(userId);
        return new CreatedUser(userId, user["username"]!.GetValue<string>(), password);
    }

    private async Task UpdateUserAsync(string userId, Action<JsonObject> change)
    {
        using var read = await AdminAsync(HttpMethod.Get, $"users/{userId}");
        var user = JsonNode.Parse(await read.Content.ReadAsStringAsync())!.AsObject();
        change(user);
        using var written = await AdminAsync(HttpMethod.Put, $"users/{userId}", user);
    }

    private async Task<JsonNode> RealmRoleAsync(string role)
    {
        using var response = await AdminAsync(HttpMethod.Get, $"roles/{role}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    // One admin request to this realm, as the bootstrap administrator; a non-success answer fails the test there.
    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUsername,
            ["password"] = _adminPassword,
        });
        using var tokenResponse = await _http.PostAsync(new Uri($"http://127.0.0.1:{_hostPort}/realms/master/protocol/openid-connect/token"), form);
        tokenResponse.EnsureSuccessStatusCode();
        var adminToken = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();

        using var request = new HttpRequestMessage(method, new Uri($"http://127.0.0.1:{_hostPort}/admin/realms/{RealmName}/{path}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body, body.GetType());
        }

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException($"Keycloak's admin API answered {(int)response.StatusCode} to {method} {path}: {detail}");
        }

        return response;
    }

    public void Dispose() => _http.Dispose();

    public async Task DisposeAsync()
    {
        if (_containerCreated)
        {
            await DockerAsync($"rm -f {ContainerName}", throwOnFailure: false);
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            try
            {
                // The realm's discovery document answers only once the import has finished.
                using var response = await _http.GetAsync(new Uri($"{RealmUrl}/.well-known/openid-configuration"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && DateTime.UtcNow < deadline)
            {
                // Not listening yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Keycloak's realm {RealmName} was not ready within {ReadyTimeout}.");
            }

            await Task.Delay(1000);
        }
    }

    private static async Task DockerAsync(string arguments, bool throwOnFailure = true)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start docker: the integration tier needs Docker for Keycloak.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        // A first run pulls the image.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Never leave a hung docker CLI behind the test run.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"docker {arguments.Split(' ')[0]} did not finish within 5 minutes and was killed.");
        }

        await stdout;
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments.Split(' ')[0]} failed ({process.ExitCode}): {await stderr}");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Directory.Build.props not found above the test output.");
    }

    /// <summary>The committed realm export: its settings, and the synthetic credentials the tests sign in with.</summary>
    public sealed class SeededRealm
    {
        private SeededRealm(string path, JsonElement root)
        {
            Path = path;
            Root = root;
        }

        public string Path { get; }

        public JsonElement Root { get; }

        public string TestClientSecret => ClientSecretOf(TestClientId);

        public string ClientSecretOf(string clientId) => Client(clientId).GetProperty("secret").GetString()!;

        public JsonElement Client(string clientId) => Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == clientId);

        public static SeededRealm Load(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return new SeededRealm(path, document.RootElement.Clone());
        }

        /// <summary>A realm role's id, pinned in the file so the API can be configured with it.</summary>
        public string RoleIdOf(string role) => Root.GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == role).GetProperty("id").GetString()!;

        /// <summary>A group's id, pinned in the file.</summary>
        public string GroupIdOf(string group) => Root.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("name").GetString() == group).GetProperty("id").GetString()!;

        /// <summary>A synthetic user as the file seeds it.</summary>
        public JsonElement User(string username) => Root.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username);

        public string AccountIdOf(string username) => Root.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .GetProperty("attributes").GetProperty("account_id")[0].GetString()!;

        public string PasswordOf(string username) => Root.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .GetProperty("credentials").EnumerateArray()
            .Single(c => c.GetProperty("type").GetString() == "password")
            .GetProperty("value").GetString()!;
    }
}

/// <summary>A user created through the admin API: its Keycloak id (its tokens' <c>sub</c>) and its synthetic credentials.</summary>
public sealed record CreatedUser(string Id, string Username, string Password);
