using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The current account's yards (UC-9). Every read and write is scoped to <see cref="ICurrentAccount"/>;
/// another account's yard is never found.
/// </summary>
public interface IYardStore
{
    /// <summary>Records a new yard for the current account.</summary>
    /// <exception cref="InvalidOperationException">There is no current account; nothing is written.</exception>
    Task<Yard> AddAsync(YardDetails details, CancellationToken cancellationToken);

    /// <summary>One page of the current account's yards, ordered by name.</summary>
    Task<IReadOnlyList<Yard>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>The yard, or <see langword="null"/> when the current account has none with that id.</summary>
    Task<Yard?> FindAsync(YardId yardId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the yard's details and returns it, or returns <see langword="null"/> and changes nothing when the
    /// current account has no yard with that id.
    /// </summary>
    Task<Yard?> UpdateAsync(YardId yardId, YardDetails details, CancellationToken cancellationToken);
}
