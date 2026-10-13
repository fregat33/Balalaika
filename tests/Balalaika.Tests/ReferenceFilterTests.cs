using FluentAssertions;
using Balalaika.Index;

namespace Balalaika.Tests;

public sealed class ReferenceFilterTests
{
    [Fact]
    public void TryParse_WhenNothingGiven_IsInactive()
    {
        // Act
        string? error = ReferenceFilter.TryParse(null, null, out ReferenceFilter filter);

        // Assert
        error.Should().BeNull();
        filter.IsActive.Should().BeFalse();
        filter.Describe().Should().BeEmpty();
        filter.Matches(ReferenceKinds.Base, "Acme.Tests", @"C:\x\A.cs").Should().BeTrue();
    }

    [Fact]
    public void TryParse_WhenAllAndBlank_IsInactive()
    {
        // Act / Assert
        ReferenceFilter.TryParse("", "all", out ReferenceFilter filter).Should().BeNull();
        filter.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TryParse_OneKind_KeepsThatKindOnly()
    {
        // Act
        string? error = ReferenceFilter.TryParse("Creation", "all", out ReferenceFilter filter);

        // Assert
        error.Should().BeNull();
        filter.IsActive.Should().BeTrue();
        filter.MatchesKind(ReferenceKinds.Creation).Should().BeTrue();
        filter.MatchesKind(ReferenceKinds.Usage).Should().BeFalse();
        filter.Describe().Should().Be("kind=creation");
    }

    [Fact]
    public void TryParse_SeveralKinds_AreCommaSeparatedAndPrintedInCanonicalOrder()
    {
        // Act
        string? error = ReferenceFilter.TryParse(" base , creation", "all", out ReferenceFilter filter);

        // Assert
        error.Should().BeNull();
        filter.MatchesKind(ReferenceKinds.Base).Should().BeTrue();
        filter.MatchesKind(ReferenceKinds.Creation).Should().BeTrue();
        filter.MatchesKind(ReferenceKinds.Invocation).Should().BeFalse();
        filter.Describe().Should().Be("kind=creation,base");
    }

    [Fact]
    public void TryParse_UnknownKind_NamesTheAcceptedList()
    {
        // Act
        string? error = ReferenceFilter.TryParse("creations", "all", out ReferenceFilter filter);

        // Assert
        error.Should().Be("unknown kind 'creations'. Accepted: usage, invocation, creation, base, attribute; several are comma-separated.");
        filter.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TryParse_UnknownScope_NamesTheAcceptedList()
    {
        // Act
        string? error = ReferenceFilter.TryParse(null, "prod", out _);

        // Assert
        error.Should().Be("unknown scope 'prod'. Accepted: all, production, tests.");
    }

    [Fact]
    public void MatchesScope_SplitsOnTheProjectNameAndPathRule()
    {
        // Arrange
        ReferenceFilter.TryParse(null, "production", out ReferenceFilter production).Should().BeNull();
        ReferenceFilter.TryParse(null, "tests", out ReferenceFilter tests).Should().BeNull();

        // Act / Assert
        production.MatchesScope("Acme.Orders", @"C:\src\Acme.Orders\A.cs").Should().BeTrue();
        production.MatchesScope("Acme.Orders.Tests", @"C:\src\Acme.Orders.Tests\A.cs").Should().BeFalse();
        production.MatchesScope("Acme.Orders", @"C:\src\tests\Acme.Orders\A.cs").Should().BeFalse();
        tests.MatchesScope("Acme.Orders", @"C:\src\Acme.Orders\A.cs").Should().BeFalse();
        tests.MatchesScope("Acme.UnitTests", @"C:\src\x\A.cs").Should().BeTrue();
        production.Describe().Should().Be("scope=production");
        tests.Describe().Should().Be("scope=tests");
    }

    [Fact]
    public void Describe_WithBothParts_SeparatesThemWithASemicolon()
    {
        // Act
        ReferenceFilter.TryParse("attribute", "tests", out ReferenceFilter filter).Should().BeNull();

        // Assert
        filter.Describe().Should().Be("kind=attribute; scope=tests");
    }
}
