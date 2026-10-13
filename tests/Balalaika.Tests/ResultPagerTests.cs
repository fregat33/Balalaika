using System.Text;
using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class ResultPagerTests
{
    private static List<string> Lines(int count)
    {
        return Enumerable.Range(1, count).Select(i => $"  line{i}").ToList();
    }

    [Fact]
    public void AppendPage_WhenLinesFit_PrintsAllAndStoresNothing()
    {
        // Arrange
        ResultPager pager = new();
        StringBuilder sb = new();

        // Act
        int remaining = pager.AppendPage(sb, Lines(3), 5, "test");

        // Assert
        remaining.Should().Be(0);
        pager.Count.Should().Be(0);
        sb.ToString().Should().NotContain("next_page");
        sb.ToString().Should().Contain("  line3");
    }

    [Fact]
    public void AppendPage_WhenLinesOverflow_PrintsTrailerWithCursorThatPagesToTheEnd()
    {
        // Arrange
        ResultPager pager = new();
        StringBuilder sb = new();

        // Act
        int remaining = pager.AppendPage(sb, Lines(7), 3, "find_references Foo");

        // Assert
        remaining.Should().Be(4);
        string first = sb.ToString();
        first.Should().Contain("  line3").And.NotContain("  line4");
        string cursor = ExtractCursor(first);

        ResultPage? second = pager.Take(cursor);
        second.Should().NotBeNull();
        second!.Label.Should().Be("find_references Foo");
        second.Lines.Should().Equal("  line4", "  line5", "  line6");
        second.Remaining.Should().Be(1);
        second.NextCursor.Should().NotBeNull();

        ResultPage? third = pager.Take(second.NextCursor!);
        third.Should().NotBeNull();
        third!.Lines.Should().Equal("  line7");
        third.Remaining.Should().Be(0);
        third.NextCursor.Should().BeNull();
    }

    [Fact]
    public void Take_WhenPageSizeIsGiven_UsesItForThisPageAndTheNext()
    {
        // Arrange
        ResultPager pager = new();
        StringBuilder sb = new();
        pager.AppendPage(sb, Lines(7), 3, "find_references Foo");
        string cursor = ExtractCursor(sb.ToString());

        // Act
        ResultPage? second = pager.Take(cursor, 1);
        ResultPage? third = pager.Take(second!.NextCursor!);
        ResultPage? fourth = pager.Take(third!.NextCursor!, 0);

        // Assert
        second.Lines.Should().Equal("  line4");
        second.Remaining.Should().Be(3);
        third.Lines.Should().Equal("  line5");
        fourth!.Lines.Should().Equal("  line6");
        fourth.Remaining.Should().Be(1);
    }

    [Fact]
    public void Take_WhenCursorUnknown_ReturnsNull()
    {
        // Arrange
        ResultPager pager = new();

        // Act
        ResultPage? page = pager.Take("nope");

        // Assert
        page.Should().BeNull();
    }

    [Fact]
    public void Take_WhenCursorExpired_ReturnsNull()
    {
        // Arrange
        ResultPager pager = new(ttl: TimeSpan.Zero);
        StringBuilder sb = new();
        pager.AppendPage(sb, Lines(5), 2, "test");
        string cursor = ExtractCursor(sb.ToString());

        // Act: storing another page runs eviction
        pager.AppendPage(new StringBuilder(), Lines(5), 2, "other");

        // Assert
        pager.Take(cursor).Should().BeNull();
    }

    [Fact]
    public void Store_WhenOverCapacity_EvictsOldestEntries()
    {
        // Arrange
        ResultPager pager = new(capacity: 3);

        // Act
        for (int i = 0; i < 10; i++)
        {
            pager.AppendPage(new StringBuilder(), Lines(5), 2, "test" + i);
        }

        // Assert
        pager.Count.Should().BeLessThanOrEqualTo(3);
    }

    private static string ExtractCursor(string text)
    {
        const string marker = "next_page cursor=";
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        start += marker.Length;
        int end = text.IndexOf(')', start);
        return text[start..end];
    }
}
