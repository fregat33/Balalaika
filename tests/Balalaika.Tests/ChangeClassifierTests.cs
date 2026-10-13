using FluentAssertions;
using Balalaika.Watcher;

namespace Balalaika.Tests;

public sealed class ChangeClassifierTests
{
    [Theory]
    [InlineData(@"C:\repo\src\App\Foo.cs", ChangeKind.Source)]
    [InlineData(@"C:\repo\src\App\App.csproj", ChangeKind.ProjectFile)]
    [InlineData(@"C:\repo\Directory.Build.props", ChangeKind.ProjectFile)]
    [InlineData(@"C:\repo\Directory.Packages.props", ChangeKind.ProjectFile)]
    [InlineData(@"C:\repo\src\App\packages.lock.json", ChangeKind.ProjectFile)]
    [InlineData(@"C:\repo\App.sln", ChangeKind.SolutionFile)]
    [InlineData(@"C:\repo\App.slnx", ChangeKind.SolutionFile)]
    [InlineData(@"C:\repo\.git\HEAD", ChangeKind.Git)]
    [InlineData(@"C:\repo\.git\index.lock", ChangeKind.Ignored)] // git internals churn constantly
    [InlineData(@"C:\repo\.git\refs\heads\dev", ChangeKind.Ignored)]
    [InlineData(@"C:\repo\src\App\bin\Debug\App.dll", ChangeKind.Ignored)]
    [InlineData(@"C:\repo\src\App\obj\Foo.g.cs", ChangeKind.Ignored)] // generated into obj
    [InlineData(@"C:\repo\src\App\Foo.cs.tmp", ChangeKind.Ignored)]
    [InlineData(@"C:\repo\README.md", ChangeKind.Ignored)]
    [InlineData("C:/repo/src/App/Foo.cs", ChangeKind.Source)] // forward slashes are normalised
    public void Classify_ReturnsExpectedKind(string path, ChangeKind expected)
    {
        // Act
        ChangeKind actual = ChangeClassifier.Classify(path);

        // Assert
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\repo\src\Db\Migrations\20240101_Init.cs", ChangeKind.Ignored)]
    [InlineData(@"C:\repo\src\Db\migrations\20240101_Init.cs", ChangeKind.Ignored)] // case-insensitive
    [InlineData(@"C:\repo\src\Db\MigrationsHelper.cs", ChangeKind.Source)] // whole folder names only
    [InlineData(@"C:\repo\src\Db\Migrations.cs", ChangeKind.Source)] // a file named like the folder is not in it
    [InlineData(@"C:\repo\src\Db\Migrations\Db.csproj", ChangeKind.Ignored)] // every kind of file under the folder
    public void Classify_WithIgnoredFolders_IgnoresFilesUnderThem(string path, ChangeKind expected)
    {
        // Act
        ChangeKind actual = ChangeClassifier.Classify(path, ["Migrations"]);

        // Assert
        actual.Should().Be(expected);
    }
}
