namespace ApiaryCopilot.IntegrationTests;

// One server and one database for every class that needs them: two fixtures would build and start the server
// side by side, and concurrent builds of one project race on its output.
// Its fixture starts a Keycloak too since the server signs every caller in, so like KeycloakAlone it runs with
// parallelization off: after the parallel classes, never beside Keycloak's JVM start.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedServer : ICollectionFixture<AppServerFixture>
{
    public const string Name = "Server";
}
