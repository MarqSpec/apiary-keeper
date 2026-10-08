using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace ApiaryCopilot.Api.Paging;

/// <summary>The opaque, URL-safe cursor of a page of any list: the position the next page starts at.</summary>
public static class PageCursor
{
    /// <summary>The cursor for the page that starts at <paramref name="offset"/> (at least 1).</summary>
    public static string Encode(int offset) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Reads a cursor this API issued. No cursor is the first page (offset 0); anything the API would not have
    /// issued is refused.
    /// </summary>
    public static bool TryDecode(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(bytes);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            && offset > 0
            && Encode(offset) == cursor;
    }
}
