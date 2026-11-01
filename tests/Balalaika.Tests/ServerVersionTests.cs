using Balalaika.Server;

namespace Balalaika.Tests;

public class ServerVersionTests
{
    [Fact]
    public void Current_IsDeclaredVersionWithoutCommitSuffix()
    {
        Assert.Equal("0.2.0", ServerVersion.Current);
    }
}
