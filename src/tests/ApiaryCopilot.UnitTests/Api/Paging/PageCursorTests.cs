using ApiaryCopilot.Api.Paging;

namespace ApiaryCopilot.UnitTests.Api.Paging;

public sealed class PageCursorTests
{
    [Theory] // invariant: a cursor the API issued reads back as the position it encodes
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(int.MaxValue)]
    public void TryDecode_ShouldReturnTheOffset_WhenTheCursorWasEncodedByTheApi(int offset)
    {
        PageCursor.TryDecode(PageCursor.Encode(offset), out var decoded).Should().BeTrue();

        decoded.Should().Be(offset);
    }

    [Theory] // boundary: no cursor is the first page
    [InlineData(null)]
    [InlineData("")]
    public void TryDecode_ShouldReturnTheFirstPage_WhenThereIsNoCursor(string? cursor)
    {
        PageCursor.TryDecode(cursor, out var decoded).Should().BeTrue();

        decoded.Should().Be(0);
    }

    [Theory] // boundary: a cursor the API did not issue is refused, not read as some other page
    [InlineData("not base64!")]
    [InlineData("LTE")] // "-1"
    [InlineData("YWJj")] // "abc"
    [InlineData("MA")] // "0": the API never issues the first page as a cursor
    [InlineData("MSAy")] // "1 2"
    public void TryDecode_ShouldFail_WhenTheCursorIsMalformed(string cursor)
    {
        PageCursor.TryDecode(cursor, out _).Should().BeFalse();
    }

    [Fact] // invariant: the cursor is opaque and URL-safe
    public void Encode_ShouldReturnAUrlSafeToken_WhenGivenAnOffset()
    {
        var cursor = PageCursor.Encode(1234567);

        cursor.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }
}
