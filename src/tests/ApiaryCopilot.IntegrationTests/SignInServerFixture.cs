namespace ApiaryCopilot.IntegrationTests;

// A server with sign-in on against the seeded realm, of its own: since a separate change every AppServerFixture starts its own
// Keycloak and turns sign-in on (the D-18 stopgap is gone), so this is one more of those, apart from the shared server's,
// for the classes that sign browsers in, change users through the admin API or shorten the BFF client's token lifetime -
// changes that must not reach the shared server's tests.
public sealed class SignInServerFixture : IAsyncLifetime, IDisposable
{
    private readonly AppServerFixture _app = new();

    public TestKeycloak Keycloak => _app.Keycloak;

    /// <summary>The server with sign-in on, from this machine.</summary>
    public Uri Address => _app.Address;

    /// <summary>The account the realm's synthetic owner - and so its signed-in browser - acts for.</summary>
    public string OwnerAccountId => _app.CurrentAccountId.ToString();

    internal TestDatabase Database => _app.Database;

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    public void Dispose() => _app.Dispose();
}
