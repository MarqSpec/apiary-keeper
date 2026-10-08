using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ApiaryCopilot.IntegrationTests;

// A throwaway Mailpit for one fixture, the mail server Keycloak sends invitations through (Interface G): a container on
// the fixture's docker network under the alias `mailpit`, the SMTP host the committed realm names, with its HTTP API
// published on a free loopback port so a test reads what was sent. It keeps every message and delivers none. The same
// image compose.yaml runs, pinned by the same digest (DEPLOYMENT.md §1).
internal sealed class TestMailbox : IDisposable
{
    public const string Image = "axllent/mailpit:v1.31.4@sha256:b68349e3a014b90c5610bfb26b2ae36f3892d7b8cf25ee140c6c71c98d2fcf48";

    // The realm's smtpServer host (keycloak/import/): compose's service name, and this container's alias on a test network.
    public const string Alias = "mailpit";

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    private readonly int _hostPort = FreePort();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool _containerStarted;

    public TestMailbox(string network)
    {
        NetworkName = network;
        ContainerName = $"{network}-mail";
    }

    /// <summary>The docker network it joins; the fixture created it.</summary>
    public string NetworkName { get; }

    /// <summary>The container's name.</summary>
    public string ContainerName { get; }

    private Uri Api => new($"http://127.0.0.1:{_hostPort}/api/v1/");

    public async Task StartAsync()
    {
        await DockerAsync($"run -d --rm --name {ContainerName} --network {NetworkName} --network-alias {Alias} -p 127.0.0.1:{_hostPort}:8025 {Image}");
        _containerStarted = true;
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync(new Uri(Api, "messages"));
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
                throw new TimeoutException($"Mailpit was not ready within {ReadyTimeout}.");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>The messages sent to <paramref name="address"/> so far, newest first: their subjects and text bodies.</summary>
    public async Task<IReadOnlyList<MailMessage>> MessagesToAsync(string address)
    {
        using var search = await _http.GetAsync(new Uri(Api, $"search?query={Uri.EscapeDataString($"to:\"{address}\"")}"));
        search.EnsureSuccessStatusCode();
        using var found = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var messages = new List<MailMessage>();
        foreach (var summary in found.RootElement.GetProperty("messages").EnumerateArray())
        {
            using var read = await _http.GetAsync(new Uri(Api, $"message/{summary.GetProperty("ID").GetString()}"));
            read.EnsureSuccessStatusCode();
            using var message = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var to = message.RootElement.GetProperty("To").EnumerateArray().Select(t => t.GetProperty("Address").GetString()!).ToList();
            messages.Add(new MailMessage(to, message.RootElement.GetProperty("Subject").GetString()!, message.RootElement.GetProperty("Text").GetString()!));
        }

        return messages;
    }

    /// <summary>Waits for the first message to <paramref name="address"/>; Keycloak sends it before it answers, but the store may lag.</summary>
    public async Task<MailMessage> WaitForMessageToAsync(string address)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (true)
        {
            var messages = await MessagesToAsync(address);
            if (messages.Count > 0)
            {
                return messages[0];
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"No message to {address} within {DeliveryTimeout}.");
            }

            await Task.Delay(250);
        }
    }

    public void Dispose() => _http.Dispose();

    public async Task StopAsync()
    {
        Dispose();
        if (_containerStarted)
        {
            await DockerAsync($"rm -f {ContainerName}", throwOnFailure: false);
        }
    }

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
        }) ?? throw new InvalidOperationException("Could not start docker: the integration tier needs Docker for Mailpit.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
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
}

/// <summary>A message Mailpit received: its recipients, subject and text body.</summary>
internal sealed record MailMessage(IReadOnlyList<string> To, string Subject, string Text);
