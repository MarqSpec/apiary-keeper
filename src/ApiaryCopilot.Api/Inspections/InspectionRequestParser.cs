using ApiaryCopilot.Api.Colonies;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Api.Inspections;

/// <summary>
/// Parses an <see cref="InspectionRequest"/> into <see cref="InspectionDetails"/> (FR-7); never throws. The fields
/// themselves are checked by <see cref="InspectionFields"/>; this adds the day, the notes and the rule that an
/// inspection says something. The day may be neither later than the <c>today</c> given nor before
/// <see cref="InspectionDetails.EarliestInspectedOn"/>. Notes are trimmed, and blank notes are null. Every invalid field is reported, keyed by
/// its JSON path, with a message that starts <c>Validation:</c>.
/// </summary>
public static class InspectionRequestParser
{
    /// <summary>Parses the request; <paramref name="today"/> is the server's date in UTC.</summary>
    public static ParseResult<InspectionDetails> Parse(InspectionRequest request, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.InspectedOn is null)
        {
            errors["inspectedOn"] = ["Validation: inspectedOn is required, as yyyy-MM-dd."];
        }
        else if (request.InspectedOn.Value > today)
        {
            // UTC is on or after today anywhere in the Americas, so a Texas entry for its own today is never refused.
            errors["inspectedOn"] = ["Validation: inspectedOn cannot be in the future; record an inspection on or after the day it happened."];
        }
        else if (request.InspectedOn.Value < InspectionDetails.EarliestInspectedOn)
        {
            errors["inspectedOn"] = [$"Validation: inspectedOn cannot be before {InspectionDetails.EarliestInspectedOn:yyyy-MM-dd}."];
        }

        var findings = InspectionFields.Check(request.Findings);
        foreach (var (field, messages) in findings.Errors)
        {
            errors[field] = messages;
        }

        var notes = Notes(request.Notes, errors);
        if (errors.Count == 0 && findings.Findings!.Values.Count == 0 && notes is null)
        {
            errors["findings"] = ["Validation: an inspection needs at least one finding or a note."];
        }

        return errors.Count > 0
            ? new ParseResult<InspectionDetails>(null, errors)
            : new ParseResult<InspectionDetails>(
                new InspectionDetails { InspectedOn = request.InspectedOn!.Value, Findings = findings.Findings!, Notes = notes },
                errors);
    }

    private static string? Notes(string? notes, Dictionary<string, string[]> errors)
    {
        var trimmed = notes?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= InspectionDetails.NotesMaxLength)
        {
            return trimmed;
        }

        errors["notes"] = [$"Validation: notes must be at most {InspectionDetails.NotesMaxLength} characters."];
        return null;
    }
}
