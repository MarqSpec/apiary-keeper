using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ApiaryCopilot.IntegrationTests;

// The request timeout is sized for a starved 2-core runner (CONVENTIONS.md §8.2), which makes it long, so
// these guard the other half of its job: a request the server never answers still ends the test, in bounded time.
// No server and no database: a loopback listener that accepts and says nothing stands in for the hang.
// Deployment=None: nothing leaves the machine.
[Trait("Deployment", "None")]
public sealed class RequestTimeoutTests
{
    [Fact] // regression risk: the timeout is cut back toward the 5 s that flaked under CPU starvation, or stretched until a hang stalls CI
    public void RequestTimeout_ShouldBeSizedForAStarvedRunnerYetBounded()
    {
        AppServerFixture.RequestTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact] // regression risk: the fixture's client must end a request nobody answers at RequestTimeout, not wait forever or give up early
    public async Task Request_ShouldFailAtTheRequestTimeout_WhenTheServerNeverAnswers()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        // Accepted and held open without writing a byte; kept in a field so nothing finalizes it and closes the connection.
        TcpClient? held = null;
        var accepted = Task.Run(async () => held = await silent.AcceptTcpClientAsync());
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var client = AppServerFixture.CreateClient(new Uri($"http://127.0.0.1:{port}"));
        // The test's own bound: if the client's timeout is lost, this ends the test instead of CI.
        using var bound = new CancellationTokenSource(AppServerFixture.RequestTimeout + TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew();

        var hang = async () => await client.GetAsync("/health", bound.Token);

        var thrown = await hang.Should().ThrowAsync<TaskCanceledException>();
        clock.Stop();
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>("HttpClient.Timeout ended it, not the test's own bound");
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(AppServerFixture.RequestTimeout - TimeSpan.FromSeconds(1));
        clock.Elapsed.Should().BeLessThan(AppServerFixture.RequestTimeout + TimeSpan.FromSeconds(10));
        accepted.IsCompletedSuccessfully.Should().BeTrue("the connection was accepted and held, so the client waited on a peer that had it, not on a refused connect");
        held?.Dispose();
        silent.Stop();
    }
}
