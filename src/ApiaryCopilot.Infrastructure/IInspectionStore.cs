using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The current account's inspections of its colonies (UC-1; FR-7). An inspection is recorded once and never
/// changed or removed: nothing here updates or deletes one. Every read and write is scoped to
/// <see cref="ICurrentAccount"/>; another account's colony or inspection is never found.
/// </summary>
public interface IInspectionStore
{
    /// <summary>
    /// Records an inspection of one of the account's colonies, and returns it, or returns
    /// <see langword="null"/> and writes nothing when the account has no colony with that id.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no current account; nothing is written.</exception>
    Task<Inspection?> AddAsync(ColonyId colonyId, InspectionDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the colony's inspections, newest first: by the day inspected, then by when recorded.
    /// <see langword="null"/> when the account has no colony with that id.
    /// </summary>
    Task<IReadOnlyList<Inspection>?> ListAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The inspection, or <see langword="null"/> when the account has none with that id.</summary>
    Task<Inspection?> FindAsync(InspectionId inspectionId, CancellationToken cancellationToken);
}
