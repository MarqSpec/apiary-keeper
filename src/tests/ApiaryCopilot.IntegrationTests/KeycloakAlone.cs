namespace ApiaryCopilot.IntegrationTests;

// Keycloak's JVM takes the CPU for its first half-minute; started beside the server on a 2-core CI runner, it pushed an
// unrelated request past its 5-second timeout. With parallelization off, xunit runs this collection after the parallel
// ones have finished, so nothing else is running while Keycloak starts.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeycloakAlone
{
    public const string Name = "Keycloak, alone";
}
