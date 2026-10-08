using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ApiaryCopilot.IntegrationTests;

// The seeded realm with somewhere to send mail, and no server: a docker network of its own, a Mailpit on it under the
// SMTP host the realm names, and a Keycloak on it importing the committed realm. For the tests that call Keycloak's admin
// interface as the API's service account directly, so what Keycloak itself refuses is shown without the API in between.
public sealed class ServiceAccountFixture : IAsyncLifetime, IDisposable
{
    private readonly string _network = $"apiary-it-sa-{FreePort()}";
    private bool _networkCreated;
    private TestKeycloak? _keycloak;
    private TestMailbox? _mailbox;

    public TestKeycloak Keycloak => _keycloak!;

    internal TestMailbox Mailbox => _mailbox!;

    public async Task InitializeAsync()
    {
        try
        {
            await DockerAsync($"network create {_network}");
            _networkCreated = true;
            _mailbox = new TestMailbox(_network);
            await _mailbox.StartAsync();
            _keycloak = new TestKeycloak(_network);
            await _keycloak.InitializeAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }

        if (_mailbox is not null)
        {
            await _mailbox.StopAsync();
        }

        // Last: the containers on it are gone.
        if (_networkCreated)
        {
            await DockerAsync($"network rm {_network}", throwOnFailure: false);
        }
    }

    public void Dispose() => _keycloak?.Dispose();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task DockerAsync(string arguments, bool throwOnFailure = true)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start docker: the integration tier needs Docker.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments.Split(' ')[0]} failed ({process.ExitCode}): {await stderr}");
        }
    }
}
