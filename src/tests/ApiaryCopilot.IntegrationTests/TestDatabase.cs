using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ApiaryCopilot.IntegrationTests;

// A throwaway PostgreSQL for one fixture: a postgres:17 container on its own docker network, published on a
// free loopback port for the fixture and reachable by name from an application container on the same network. Its
// password is made up per run; nothing is stored. The fixture migrates it with the committed migrations through the
// production registrations and seeds it with synthetic accounts.
internal sealed class TestDatabase
{
    private const string Image = "postgres:17";
    private const string Database = "apiary_copilot_it";
    private const string User = "apiary_it";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(1);

    private readonly string _password = Guid.NewGuid().ToString("N");
    private readonly int _hostPort = FreePort();
    private bool _networkCreated;
    private bool _containerStarted;

    public TestDatabase(string name)
    {
        NetworkName = name;
        ContainerName = $"{name}-db";
    }

    /// <summary>The docker network an application container joins to reach the database.</summary>
    public string NetworkName { get; }

    /// <summary>The database container's name, which is its host name on <see cref="NetworkName"/>.</summary>
    public string ContainerName { get; }

    /// <summary>The connection string from this machine (the fixture, or a server run from source).</summary>
    public string HostConnectionString => ConnectionString("127.0.0.1", _hostPort);

    /// <summary>The connection string from a container on <see cref="NetworkName"/>.</summary>
    public string NetworkConnectionString => ConnectionString(ContainerName, 5432);

    public async Task StartAsync()
    {
        await DockerAsync($"network create {NetworkName}");
        _networkCreated = true;
        await DockerAsync(
            $"run -d --rm --name {ContainerName} --network {NetworkName} -p 127.0.0.1:{_hostPort}:5432 " +
            $"-e POSTGRES_DB={Database} -e POSTGRES_USER={User} -e POSTGRES_PASSWORD={_password} {Image}");
        _containerStarted = true;
        await WaitUntilReadyAsync();
    }

    /// <summary>
    /// The synthetic user who acts for <paramref name="accountId"/>: its id shares the account's value, so a test names it
    /// without a lookup.
    /// </summary>
    public static UserId ActingUserOf(AccountId accountId) => new(accountId.Value);

    /// <summary>Applies the committed migrations and adds a synthetic account with its acting user.</summary>
    public async Task MigrateAndAddAccountAsync(AccountId accountId, string name)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseMigrator>().MigrateAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<ApiaryCopilotDbContext>();
        db.Accounts.Add(new Account { AccountId = accountId, Name = name });
        db.Users.Add(new Domain.User { UserId = ActingUserOf(accountId), AccountId = accountId, Name = $"{name} beekeeper" });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Adds a yard for <paramref name="accountId"/> through the production store.</summary>
    public async Task<YardId> AddYardAsync(AccountId accountId, YardDetails details)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        var yard = await scope.ServiceProvider.GetRequiredService<IYardStore>().AddAsync(details, CancellationToken.None);
        return yard.YardId;
    }

    /// <summary>Reads a yard as <paramref name="accountId"/> sees it.</summary>
    public async Task<Yard?> FindYardAsync(AccountId accountId, YardId yardId)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IYardStore>().FindAsync(yardId, CancellationToken.None);
    }

    /// <summary>Registers a colony for <paramref name="accountId"/> through the production store.</summary>
    public async Task<ColonyId> AddColonyAsync(AccountId accountId, ColonyProfile colony)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IColonyStore>()
            .RegisterAsync(colony, null, new DateOnly(2026, 4, 1), CancellationToken.None);
        return result.Colony?.Colony.ColonyId ?? throw new InvalidOperationException($"Seeding a colony failed: {result.Outcome}.");
    }

    /// <summary>Reads a colony as <paramref name="accountId"/> sees it.</summary>
    public async Task<ColonyState?> FindColonyAsync(AccountId accountId, ColonyId colonyId)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IColonyStore>().FindAsync(colonyId, CancellationToken.None);
    }

    /// <summary>
    /// Makes a colony write as <paramref name="accountId"/> through the production store inside a transaction it
    /// leaves open, so a write the server makes meanwhile to the same rows waits on it until the hold is committed.
    /// Disposing an uncommitted hold rolls it back.
    /// </summary>
    public async Task<HeldColonyWrite> HoldColonyWriteAsync(AccountId accountId, Func<IColonyStore, Task<ColonyChangeResult>> write)
    {
        var provider = ProviderActingFor(accountId);
        var scope = provider.CreateAsyncScope();
        try
        {
            var transaction = await scope.ServiceProvider.GetRequiredService<ApiaryCopilotDbContext>().Database.BeginTransactionAsync();
            var result = await write(scope.ServiceProvider.GetRequiredService<IColonyStore>());
            return new HeldColonyWrite(provider, scope, transaction, result);
        }
        catch
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Whether another connection comes to wait on a row lock before <paramref name="request"/> ends or the time is up:
    /// the request's write is queued behind a held one.
    /// </summary>
    public async Task<bool> WaitsOnALockAsync(Task request)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (!request.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (await CountAsync("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()") > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>Records an inspection of <paramref name="colonyId"/> for <paramref name="accountId"/> through the production store.</summary>
    public async Task<InspectionId> AddInspectionAsync(AccountId accountId, ColonyId colonyId, InspectionDetails details)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        var inspection = await scope.ServiceProvider.GetRequiredService<IInspectionStore>().AddAsync(colonyId, details, CancellationToken.None);
        return inspection?.InspectionId ?? throw new InvalidOperationException("Seeding an inspection failed: no such colony.");
    }

    /// <summary>Attaches an object to an inspection for <paramref name="accountId"/> through the production store.</summary>
    public async Task<MediaId> AttachMediaAsync(AccountId accountId, InspectionId inspectionId, MediaObjectKey key, StoredObject stored)
    {
        await using var provider = ProviderActingFor(accountId);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IInspectionMediaStore>().AttachAsync(inspectionId, key, stored, CancellationToken.None);
        return result.Media?.MediaId ?? throw new InvalidOperationException($"Seeding media failed: {result.Outcome}.");
    }

    /// <summary>Sets a user's Keycloak subject straight in SQL, as accepting an invitation will (piece 4).</summary>
    public async Task SetExternalSubjectAsync(UserId userId, string? subject)
    {
        await using var connection = new NpgsqlConnection(HostConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE \"Users\" SET \"ExternalSubject\" = @subject WHERE \"UserId\" = @user", connection);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        if (await command.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException($"No user {userId} to give a subject to.");
        }
    }

    /// <summary>Adds a user to <paramref name="accountId"/> straight in SQL, with a Keycloak subject or none.</summary>
    public async Task AddUserAsync(AccountId accountId, UserId userId, string? subject)
    {
        await using var connection = new NpgsqlConnection(HostConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"Users\" (\"UserId\", \"AccountId\", \"Name\", \"ExternalSubject\") VALUES (@user, @account, 'Synthetic Beekeeper (integration)', @subject)",
            connection);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("account", accountId.Value);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs SQL as the application's own database login, bypassing the application.</summary>
    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(HostConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Counts rows as the application's own database login, bypassing the application.</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(HostConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Removes the container and its network; safe after a partial start.</summary>
    public async Task StopAsync()
    {
        if (_containerStarted)
        {
            await DockerAsync($"rm -f {ContainerName}", throwOnFailure: false);
        }

        if (_networkCreated)
        {
            await DockerAsync($"network rm {NetworkName}", throwOnFailure: false);
        }
    }

    private ServiceProvider ProviderActingFor(AccountId accountId) =>
        new ServiceCollection()
            .AddSingleton<ICurrentAccount>(new FixedAccount(accountId))
            .AddSingleton<ICurrentUser>(new FixedUser(ActingUserOf(accountId)))
            .AddInfrastructure(HostConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    private string ConnectionString(string host, int port) => new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = port,
        Database = Database,
        Username = User,
        Password = _password,
    }.ConnectionString;

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(HostConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception e) when (e is NpgsqlException or IOException && DateTime.UtcNow < deadline)
            {
                // The image initializes on a socket-only server first; TCP answers once it is ready.
                await Task.Delay(500);
            }
        }
    }

    private static async Task DockerAsync(string arguments, bool throwOnFailure = true)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start docker: the integration tier needs Docker for PostgreSQL.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        // A first run pulls the image.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
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

    /// <summary>A colony write made but not yet committed; see <see cref="HoldColonyWriteAsync"/>.</summary>
    internal sealed class HeldColonyWrite(
        ServiceProvider provider, AsyncServiceScope scope, IDbContextTransaction transaction, ColonyChangeResult result) : IAsyncDisposable
    {
        /// <summary>How the held write ended in its own transaction.</summary>
        public ColonyChangeResult Result { get; } = result;

        /// <summary>Commits the held write, releasing whatever waits on it.</summary>
        public Task CommitAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class FixedAccount(AccountId accountId) : ICurrentAccount
    {
        public AccountId? AccountId { get; } = accountId;
    }

    private sealed class FixedUser(UserId userId) : ICurrentUser
    {
        public UserId? UserId { get; } = userId;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ContainerName} on 127.0.0.1:{_hostPort}");
}
