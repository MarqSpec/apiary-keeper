using System.Reflection;

namespace ApiaryCopilot.Api.Health;

public static class AppVersion
{
    public static string Current { get; } = Normalize(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // The SDK appends "+<commit>" to the informational version; the contract is bare X.Y.Z.
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        var plus = raw.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? raw : raw[..plus];
    }
}
