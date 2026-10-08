using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// Starts the application from APP_SERVER_COMMAND (CONVENTIONS.md §8.2); unset, builds from source.
// "{port}" in the command is replaced with a free loopback port the host must listen on; for a source run the
// same port reaches the child as ASPNETCORE_URLS. Before it, the fixture starts its own PostgreSQL (TestDatabase),
// migrates it, seeds two synthetic accounts and a yard, a colony and an inspection in the other one, starts its own
// MinIO as the media bucket (TestMediaBucket) and its own Keycloak with the seeded realm (TestKeycloak), and passes the
// server ConnectionStrings__ApiaryCopilot, the Media__ settings and sign-in on - the SignIn__ settings and a key ring
// certificate made for the run (DEPLOYMENT.md §3) - and,, the service account's KeycloakAdmin__ settings.
// Every apiary endpoint needs a signed-in caller since a separate change replaced the D-18 configuration stopgap, so the account the
// server acts for is the realm's synthetic owner's (its account_id), its acting user is mapped to that owner's Keycloak
// subject, and Client calls as that owner with a bearer token from the realm's test client.
public sealed class AppServerFixture : IAsyncLifetime, IDisposable
{
    public const string OtherAccountYardName = "Synthetic Other Account's Yard";

    public const string OtherAccountColonyTag = "SYNTHETIC-OTHER-01";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    // Per request, sized for a starved 2-core runner, not for a healthy one: the first request after a server starts
    // pays for the first database connection, the EF model and cold JIT, and took over 5 s there (§8.2). A server that
    // never answers still fails the test at this bound (RequestTimeoutTests).
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // How long one readiness poll waits: the poll loop retries, so this stays short and StartupTimeout bounds the whole wait.
    private static readonly TimeSpan ReadinessPollTimeout = TimeSpan.FromSeconds(5);

    // Short, so a test can watch a URL expire; long enough for every other test to use one at once.
    public static readonly TimeSpan MediaUrlLifetime = TimeSpan.FromSeconds(15);

    /// <summary>The realm's synthetic owner, whom <see cref="Client"/> calls as.</summary>
    public const string Owner = "synthetic-owner";

    // Everything a server in a container must be passed to reach the database, the bucket and Keycloak on the fixture's network.
    private static readonly string[] ContainerSettings =
    [
        "ConnectionStrings__ApiaryCopilot",
        "Media__ServiceUrl", "Media__PublicUrl", "Media__Bucket", "Media__AccessKeyId", "Media__SecretAccessKey",
        "Media__ForcePathStyle", "Media__AllowHttp", "Media__UploadUrlLifetime", "Media__DownloadUrlLifetime",
        "SignIn__Authority", "SignIn__MetadataAddress", "SignIn__ClientId", "SignIn__ClientSecret", "SignIn__Audience",
        "SignIn__RequireHttps", "DataProtection__Certificate", "DataProtection__CertificatePassword",
    ];

    // The fixture's database is new each run, so the realm's fixed account is safe to use; the other one is per run.
    private AccountId _currentAccountId;
    private readonly AccountId _otherAccountId = new(Guid.NewGuid());

    private RunningServer? _server;
    private string? _template;
    private string? _repoRoot;
    private string? _networkName;
    private string? _containerName;
    private TestDatabase? _database;
    private TestMediaBucket? _media;
    private TestKeycloak? _keycloak;
    private bool _inContainer;
    private readonly bool _withMailbox;
    private TestMailbox? _mailbox;
    private string _command = string.Empty;

    public AppServerFixture()
        : this(withMailbox: false)
    {
    }

    // With a mailbox, a Mailpit joins the fixture's network under the SMTP host the realm names, so Keycloak's invitations
    // are kept for the test to read.
    internal AppServerFixture(bool withMailbox) => _withMailbox = withMailbox;

    /// <summary>The mail Keycloak sent, when the fixture was made with a mailbox.</summary>
    internal TestMailbox Mailbox => _mailbox ?? throw new InvalidOperationException("This fixture was made without a mailbox.");

    /// <summary>
    /// Whether the server was given the service account's <c>KeycloakAdmin__</c> settings: always from source; in a
    /// container only when APP_SERVER_COMMAND passes each of them with <c>-e</c>.
    /// </summary>
    public bool InvitationsConfigured => !_inContainer || ServiceAccountSettingNames.All(s => _command.Contains($"-e {s}", StringComparison.Ordinal));

    /// <summary>The <c>KeycloakAdmin__</c> settings a container command must pass for the invitation to be configured.</summary>
    public static IReadOnlyList<string> ServiceAccountSettingNames { get; } =
        ["KeycloakAdmin__ClientId", "KeycloakAdmin__ClientSecret", "KeycloakAdmin__HelperRoleId", "KeycloakAdmin__GrowerRoleId", "KeycloakAdmin__CustomerRoleId"];

    /// <summary>A client calling as the realm's synthetic owner, a user of <see cref="CurrentAccountId"/>, with a bearer token.</summary>
    public HttpClient Client => _server!.Client;

    /// <summary>The server's address from this machine.</summary>
    public Uri Address => _server!.Client.BaseAddress!;

    /// <summary>The Keycloak the server signs in through, with the seeded synthetic realm.</summary>
    public TestKeycloak Keycloak => _keycloak!;

    public string ExpectedVersion { get; private set; } = null!;

    /// <summary>A yard that belongs to an account the server does not act for.</summary>
    public YardId OtherAccountYardId { get; private set; }

    /// <summary>A colony that belongs to an account the server does not act for.</summary>
    public ColonyId OtherAccountColonyId { get; private set; }

    /// <summary>An inspection of the other account's colony.</summary>
    public InspectionId OtherAccountInspectionId { get; private set; }

    /// <summary>The database the server uses, to read or write it as an account or straight in SQL.</summary>
    internal TestDatabase Database => _database!;

    /// <summary>The media bucket the server uses, to read or write it straight with the root credential.</summary>
    internal TestMediaBucket MediaBucket => _media!;

    /// <summary>The account the server acts for with <see cref="Client"/>: the realm owner's, to write as it straight to the database.</summary>
    public AccountId CurrentAccountId => _currentAccountId;

    /// <summary>The other account, to read its records straight from the database.</summary>
    public AccountId OtherAccountId => _otherAccountId;

    /// <summary>The other account's yard's name, read as that account straight from the database.</summary>
    public async Task<string?> ReadOtherAccountYardNameAsync() =>
        (await _database!.FindYardAsync(_otherAccountId, OtherAccountYardId))?.Details.Name;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        ExpectedVersion = ReadVersion(repoRoot);

        var port = FreePort();
        var template = Environment.GetEnvironmentVariable("APP_SERVER_COMMAND");
        _template = string.IsNullOrWhiteSpace(template)
            ? "dotnet run --project src/ApiaryCopilot.Api --no-launch-profile"
            : template;
        var command = _template.Replace("{port}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        _command = command;
        // A docker command must pass --name apiary-it-{port}: killing the CLI leaves the container running, so Dispose removes it by that name.
        var name = $"apiary-it-{port}";
        if (command.Contains($"--name {name}", StringComparison.Ordinal))
        {
            _containerName = name;
            _inContainer = true;
            // The container reaches the database by name on the fixture's network, and takes its settings from the CLI's environment.
            foreach (var required in ContainerSettings.Select(setting => $"-e {setting}").Prepend($"--network {name}"))
            {
                if (!command.Contains(required, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"APP_SERVER_COMMAND runs a container, so it must pass `{required.Replace(name, "apiary-it-{port}", StringComparison.Ordinal)}` (CONVENTIONS.md §8.2).");
                }
            }
        }

        try
        {
            _repoRoot = repoRoot;
            _networkName = name;
            _keycloak = new TestKeycloak(name);
            _currentAccountId = new AccountId(Guid.Parse(_keycloak.Realm.AccountIdOf(Owner)));
            await StartDatabaseAsync(name);
            _media = new TestMediaBucket(name);
            await _media.StartAsync();
            if (_withMailbox)
            {
                _mailbox = new TestMailbox(name);
                await _mailbox.StartAsync();
            }

            await _keycloak.InitializeAsync();
            await _database!.SetExternalSubjectAsync(TestDatabase.ActingUserOf(_currentAccountId), await _keycloak.UserIdOfAsync(Owner));
            _server = await LaunchAsync(command, port, repoRoot, SignInSettings(), new BearerTokens(_keycloak, Owner, _keycloak.Realm.PasswordOf(Owner)));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task StartDatabaseAsync(string name)
    {
        _database = new TestDatabase(name);
        await _database.StartAsync();
        await _database.MigrateAndAddAccountAsync(_currentAccountId, "Synthetic Apiary (integration)");
        await _database.MigrateAndAddAccountAsync(_otherAccountId, "Synthetic Other Apiary (integration)");
        OtherAccountYardId = await _database.AddYardAsync(_otherAccountId, new YardDetails
        {
            Name = OtherAccountYardName,
            Location = new GeoLocation { Latitude = 31.5, Longitude = -98.5 },
            State = UsState.Texas,
            County = "Mills",
            Landowner = new Landowner { Contact = "Synthetic Other Landowner, 555-0199", Terms = LandownerTerms.Free },
        });
        OtherAccountColonyId = await _database.AddColonyAsync(_otherAccountId, new ColonyProfile
        {
            TagId = OtherAccountColonyTag,
            HiveNumber = "OTHER-0001",
            YardId = OtherAccountYardId,
            HiveType = "Langstroth 8-frame",
            BoxCount = 1,
        });
        OtherAccountInspectionId = await _database.AddInspectionAsync(_otherAccountId, OtherAccountColonyId, new InspectionDetails
        {
            InspectedOn = new DateOnly(2026, 4, 2),
            Findings = InspectionFindings.Empty,
            Notes = "Synthetic: another account's inspection",
        });
    }

    private async Task<RunningServer> LaunchAsync(string command, int port, string repoRoot, IReadOnlyDictionary<string, string> settings, BearerTokens tokens)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var flag = OperatingSystem.IsWindows() ? "/c" : "-c";
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(flag);
        info.ArgumentList.Add(command);
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        // Not Development, as the image runs: the fixture has migrated the database already.
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__ApiaryCopilot"] = _containerName is null
            ? _database!.HostConnectionString
            : _database!.NetworkConnectionString;
        info.Environment["Media__ServiceUrl"] = _containerName is null ? _media!.HostUrl : _media!.NetworkUrl;
        info.Environment["Media__PublicUrl"] = _media.HostUrl;
        info.Environment["Media__Bucket"] = TestMediaBucket.BucketName;
        info.Environment["Media__AccessKeyId"] = _media.AccessKeyId;
        info.Environment["Media__SecretAccessKey"] = _media.SecretAccessKey;
        info.Environment["Media__ForcePathStyle"] = "true";
        info.Environment["Media__AllowHttp"] = "true";
        info.Environment["Media__UploadUrlLifetime"] = MediaUrlLifetime.ToString("c", CultureInfo.InvariantCulture);
        info.Environment["Media__DownloadUrlLifetime"] = MediaUrlLifetime.ToString("c", CultureInfo.InvariantCulture);
        foreach (var (key, value) in settings)
        {
            info.Environment[key] = value;
        }

        var server = new RunningServer(
            Process.Start(info) ?? throw new InvalidOperationException($"Could not start: {command}"),
            CreateClient(new Uri($"http://127.0.0.1:{port}"), tokens),
            command.Contains($"--name apiary-it-{port}", StringComparison.Ordinal) ? $"apiary-it-{port}" : null);
        try
        {
            await server.WaitUntilReadyAsync(command);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        return server;
    }

    /// <summary>A client for the server at <paramref name="baseAddress"/>, whose requests end at <see cref="RequestTimeout"/>.</summary>
    internal static HttpClient CreateClient(Uri baseAddress) => new() { BaseAddress = baseAddress, Timeout = RequestTimeout };

    /// <summary>A client like <see cref="CreateClient(Uri)"/> whose every request carries a bearer token from <paramref name="tokens"/>.</summary>
    internal static HttpClient CreateClient(Uri baseAddress, BearerTokens tokens) =>
        new(tokens) { BaseAddress = baseAddress, Timeout = RequestTimeout };

    /// <summary>A client calling this server as <paramref name="user"/>, with a bearer token from the realm's test client.</summary>
    public HttpClient ClientFor(CreatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return CreateClient(Address, new BearerTokens(Keycloak, user.Username, user.Password));
    }

    // Sign-in on, against this fixture's Keycloak: the BFF client's synthetic secret from the committed realm, and a key
    // ring certificate made for this run (synthetic: a self-signed RSA key, exported under a password made up here, handed
    // to the server through its environment only, never written to disk). A server in a container reaches Keycloak by its
    // container name (SignIn__MetadataAddress).
    private Dictionary<string, string> SignInSettings()
    {
        var (certificate, password) = SyntheticKeyRingCertificate();
        var settings = new Dictionary<string, string>
        {
            ["SignIn__Authority"] = _keycloak!.RealmUrl,
            ["SignIn__ClientId"] = TestKeycloak.BffClientId,
            ["SignIn__ClientSecret"] = _keycloak.Realm.ClientSecretOf(TestKeycloak.BffClientId),
            ["SignIn__Audience"] = TestKeycloak.ApiAudience,
            // The test Keycloak is plain http on loopback; never off on staging or production (DEPLOYMENT.md §3).
            ["SignIn__RequireHttps"] = "false",
            ["DataProtection__Certificate"] = certificate,
            ["DataProtection__CertificatePassword"] = password,
        };
        if (_inContainer)
        {
            settings["SignIn__MetadataAddress"] = _keycloak.BackChannelMetadataAddress;
        }

        // The API's service account: the realm's synthetic secret and pinned role ids, read from the committed file.
        foreach (var (key, value) in _keycloak.ServiceAccountSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    private static (string Certificate, string Password) SyntheticKeyRingCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=apiary-keeper-integration-tests-data-protection-synthetic", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return (Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, password)), password);
    }

    public void Dispose() => _keycloak?.Dispose();

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_media is not null)
        {
            await _media.StopAsync();
        }

        if (_mailbox is not null)
        {
            await _mailbox.StopAsync();
        }

        // Before the database, which removes the network Keycloak joined.
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }

        // Last: it removes the network the other containers joined.
        if (_database is not null)
        {
            await _database.StopAsync();
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

    private static string ReadVersion(string repoRoot) =>
        XDocument.Load(Path.Combine(repoRoot, "Directory.Build.props"))
            .Descendants("Version").Single().Value;

    // A launched server: its process, the client for it and, for a container, the name to remove it by.
    internal sealed class RunningServer : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _output = new();
        private readonly Process _process;
        private readonly string? _containerName;

        public RunningServer(Process process, HttpClient client, string? containerName)
        {
            _process = process;
            Client = client;
            _containerName = containerName;
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public HttpClient Client { get; }

        public async Task WaitUntilReadyAsync(string command)
        {
            var deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The server exited with {_process.ExitCode} before it was ready: {command}{Environment.NewLine}{string.Join(Environment.NewLine, _output)}");
                }

                try
                {
                    using var poll = new CancellationTokenSource(ReadinessPollTimeout);
                    using var response = await Client.GetAsync("/health", poll.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                catch (TaskCanceledException)
                {
                    // Connected but not answering yet.
                }

                await Task.Delay(500);
            }

            throw new TimeoutException($"The server was not ready within {StartupTimeout}: {command}");
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
            if (_containerName is not null)
            {
                using var rm = Process.Start(new ProcessStartInfo("docker", $"rm -f {_containerName}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                rm?.WaitForExit(30_000);
            }

            return ValueTask.CompletedTask;
        }

        // The last lines the server wrote, for the failure message; synthetic data only, so nothing sensitive.
        private void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }

            _output.Enqueue(line);
            while (_output.Count > 40 && _output.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>
    /// Adds a Keycloak access token for one user to every request, from the realm's test client (its direct-access grant
    /// exists in the dev realm only), fetching a new one a minute before the last expires.
    /// </summary>
    internal sealed class BearerTokens(TestKeycloak keycloak, string username, string password) : DelegatingHandler(new HttpClientHandler())
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _token;
        private DateTimeOffset _renewAt;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Headers.Authorization ??= new AuthenticationHeaderValue("Bearer", await TokenAsync());
            return await base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gate.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task<string> TokenAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_token is null || DateTimeOffset.UtcNow >= _renewAt)
                {
                    using var response = await keycloak.PasswordGrantAsync(username, password);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    _token = json.RootElement.GetProperty("access_token").GetString()!;
                    _renewAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(json.RootElement.GetProperty("expires_in").GetInt32()) - TimeSpan.FromMinutes(1);
                }

                return _token;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
