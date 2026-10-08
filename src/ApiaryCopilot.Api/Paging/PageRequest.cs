namespace ApiaryCopilot.Api.Paging;

/// <summary>
/// One bounded page of a list: where it starts and how many it holds (CONVENTIONS.md §4.2). Every list
/// takes the same <c>limit</c> and <c>cursor</c>.
/// </summary>
public readonly record struct PageRequest(int Skip, int Take)
{
    /// <summary>The page size when the caller gives none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page a caller may ask for.</summary>
    public const int MaxLimit = 100;

    /// <summary>How many rows to fetch: one more than the page, to learn whether another page follows.</summary>
    public int Fetch => Take + 1;

    /// <summary>
    /// Reads the caller's <c>limit</c> and <c>cursor</c>. Each invalid one is reported, keyed by its parameter name,
    /// with a message that starts <c>Validation:</c>.
    /// </summary>
    public static bool TryRead(int? limit, string? cursor, out PageRequest page, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Validation: limit must be between 1 and {MaxLimit}."];
        }

        if (!PageCursor.TryDecode(cursor, out var skip))
        {
            errors["cursor"] = ["Validation: cursor must be a nextCursor this API returned; omit it for the first page."];
        }

        page = new PageRequest(skip, take);
        return errors.Count == 0;
    }

    /// <summary>The next page's cursor, or <see langword="null"/> when <paramref name="fetched"/> shows this is the last.</summary>
    public string? NextCursor(int fetched) => fetched > Take ? PageCursor.Encode(Skip + Take) : null;
}
