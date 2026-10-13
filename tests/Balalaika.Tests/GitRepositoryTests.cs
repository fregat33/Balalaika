using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class GitRepositoryTests
{
    [Fact]
    public void ParseNameStatus_MapsLettersToStatusesAndPathsToAbsolute()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "repo");
        const string output = "M\tsrc/App/Service.cs\nA\tsrc/App/New.cs\r\nD\tsrc/App/Old.cs\nT\tsrc/App/Link.cs\n\n";

        // Act
        List<ChangedFile> files = GitRepository.ParseNameStatus(root, output);

        // Assert
        files.Should().Equal(
            new ChangedFile(Path.Combine(root, "src", "App", "Service.cs"), FileStatus.Modified),
            new ChangedFile(Path.Combine(root, "src", "App", "New.cs"), FileStatus.Added),
            new ChangedFile(Path.Combine(root, "src", "App", "Old.cs"), FileStatus.Deleted),
            new ChangedFile(Path.Combine(root, "src", "App", "Link.cs"), FileStatus.Modified));
    }

    [Fact]
    public void FindRepositoryRoot_WalksUpToTheDotGitEntry_OrReturnsNull()
    {
        // Arrange: a .git file (as in a worktree) two levels above the solution
        string root = TestWorkspace.NewTempDir();
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
        string solution = Path.Combine(root, "src", "App", "App.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(solution)!);
        GitRepository git = new(Microsoft.Extensions.Options.Options.Create(new BalalaikaOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<GitRepository>.Instance);

        // Act / Assert
        git.FindRepositoryRoot(solution).Should().Be(root);
        git.FindRepositoryRoot(Path.Combine(Path.GetTempPath(), "balalaika-tests", "no-repo-here", "x.sln")).Should().BeNull();
    }
}
