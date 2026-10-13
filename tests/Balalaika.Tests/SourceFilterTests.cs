using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class SourceFilterTests
{
    [Theory]
    [InlineData(@"C:\repo\src\Db\Migrations\Init.cs", true)]
    [InlineData(@"C:\repo\src\Db\migrations\Init.cs", true)] // case-insensitive
    [InlineData("C:/repo/src/Db/Migrations/Init.cs", true)] // forward slashes
    [InlineData(@"C:\repo\src\Db\Migrations\Sub\Init.cs", true)] // anywhere on the path
    [InlineData(@"C:\repo\src\Db\MigrationsHelper.cs", false)] // whole folder names only
    [InlineData(@"C:\repo\src\Db\Migrations.cs", false)] // the file name is not a folder
    public void IsIgnored_MatchesWholeFolderNamesOnly(string path, bool expected)
    {
        // Arrange
        SourceFilter filter = new(["Migrations"]);

        // Act / Assert
        filter.IsIgnored(path).Should().Be(expected);
    }

    [Fact]
    public void IsIgnored_WhenNoFoldersConfigured_IsFalse()
    {
        // Act / Assert
        SourceFilter.None.IsIgnored(@"C:\repo\Migrations\Init.cs").Should().BeFalse();
    }

    [Fact]
    public void Constructor_NormalisesBlankAndSlashedNames()
    {
        // Act
        SourceFilter filter = new(["", "  ", @"\Migrations\", "Migrations"]);

        // Assert
        filter.IgnoredFolders.Should().ContainSingle().Which.Should().Be("Migrations");
    }

    [Fact]
    public void EnumerateSourceFiles_SkipsIgnoredFolders()
    {
        // Arrange
        string dir = TestWorkspace.NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Migrations"));
            File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }");
            File.WriteAllText(Path.Combine(dir, "Migrations", "M.cs"), "class M { }");

            // Act
            List<string> files = new SourceFilter(["Migrations"]).EnumerateSourceFiles(dir).ToList();

            // Assert
            files.Should().ContainSingle().Which.Should().EndWith("A.cs");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
