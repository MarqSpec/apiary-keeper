using ApiaryCopilot.Api.Paging;

namespace ApiaryCopilot.UnitTests.Api.Paging;

public sealed class PageRequestTests
{
    [Fact] // boundary: no arguments is the first page of the default size
    public void TryRead_ShouldReturnTheFirstDefaultPage_WhenNoArgumentsAreGiven()
    {
        PageRequest.TryRead(null, null, out var page, out var errors).Should().BeTrue();

        page.Should().Be(new PageRequest(0, PageRequest.DefaultLimit));
        PageRequest.DefaultLimit.Should().Be(50);
        errors.Should().BeEmpty();
    }

    [Theory] // boundary: limit is 1 to 100, inclusive
    [InlineData(1)]
    [InlineData(100)]
    public void TryRead_ShouldAcceptTheLimit_WhenItIsWithinTheBounds(int limit)
    {
        PageRequest.TryRead(limit, null, out var page, out _).Should().BeTrue();

        page.Take.Should().Be(limit);
    }

    [Theory] // boundary: a limit outside 1-100 is a validation error naming limit
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public void TryRead_ShouldReportTheLimit_WhenItIsOutOfBounds(int limit)
    {
        PageRequest.TryRead(limit, null, out _, out var errors).Should().BeFalse();

        errors.Keys.Should().Equal("limit");
        errors["limit"][0].Should().Be("Validation: limit must be between 1 and 100.");
    }

    [Fact] // boundary: a cursor the API did not issue is a validation error naming cursor
    public void TryRead_ShouldReportTheCursor_WhenTheApiDidNotIssueIt()
    {
        PageRequest.TryRead(null, "bm90LWEtbnVtYmVy", out _, out var errors).Should().BeFalse();

        errors.Keys.Should().Equal("cursor");
        errors["cursor"][0].Should().StartWith("Validation:");
    }

    [Fact] // invariant: an issued cursor resumes exactly where the previous page ended
    public void TryRead_ShouldStartAtTheCursor_WhenTheCursorWasIssued()
    {
        PageRequest.TryRead(2, new PageRequest(0, 2).NextCursor(3), out var page, out _).Should().BeTrue();

        page.Should().Be(new PageRequest(2, 2));
    }

    [Fact] // invariant: one more row than the page is fetched, to learn whether another page follows
    public void Fetch_ShouldBeOneMoreThanThePage_WhenRead()
    {
        new PageRequest(10, 5).Fetch.Should().Be(6);
    }

    [Theory] // boundary: the next cursor exists only when more rows came back than the page holds
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void NextCursor_ShouldExistOnlyWhenMoreRowsRemain_WhenGivenTheFetchedCount(int fetched, bool expected)
    {
        var next = new PageRequest(10, 5).NextCursor(fetched);

        (next is not null).Should().Be(expected);
        if (next is not null)
        {
            PageCursor.TryDecode(next, out var offset).Should().BeTrue();
            offset.Should().Be(15);
        }
    }
}
