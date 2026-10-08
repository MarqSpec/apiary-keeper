namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The Data Protection key ring's rows in the app's PostgreSQL (ARCHITECTURE.md D-19): each element as the XML the key
/// manager writes, already encrypted with the certificate before it reaches the store. Not account data, so it is kept
/// out of <see cref="ApiaryCopilotDbContext"/>, whose every entity is the account's (NFR-8). Never logged or exported.
/// Synchronous because the key manager that calls it is.
/// </summary>
public interface IDataProtectionKeyStore
{
    /// <summary>Every stored element, oldest first.</summary>
    IReadOnlyList<string> GetAll();

    /// <summary>Stores one element under its friendly name.</summary>
    void Add(string? friendlyName, string xml);
}
