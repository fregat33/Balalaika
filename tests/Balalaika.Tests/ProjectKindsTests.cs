using FluentAssertions;
using Balalaika.Index;

namespace Balalaika.Tests;

public sealed class ProjectKindsTests
{
    [Theory]
    [InlineData("Acme.Orders.Tests", @"C:\src\Acme.Orders.Tests\A.cs", true)]
    [InlineData("Acme.UnitTests", @"C:\src\x\A.cs", true)]
    [InlineData("Acme.Orders", @"C:\src\tests\Acme.Orders\A.cs", true)]
    [InlineData("Acme.Orders", @"C:\src\test\Acme.Orders\A.cs", true)]
    [InlineData("Acme.Orders", @"C:\src\Acme.Orders\A.cs", false)]
    [InlineData("Acme.Latest", @"C:\src\Acme.Latest\A.cs", false)]
    [InlineData("Acme.TestUtilities", @"C:\src\x\A.cs", true)]
    [InlineData("Acme.Contest", @"C:\src\x\A.cs", false)]
    [InlineData("", "", false)]
    public void IsTestProject_LooksAtProjectNameSegmentsAndFolders(string project, string path, bool expected)
    {
        // Act / Assert
        ProjectKinds.IsTestProject(project, path).Should().Be(expected);
    }
}
