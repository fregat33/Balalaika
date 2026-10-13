namespace Balalaika.Index;

/// <summary>
/// Tells a test project from a production one by name and path, with no build information: the rule
/// <c>search_symbols</c> ranks by and the <c>scope</c> filter of <c>find_references</c> and <c>get_call_graph</c> cuts by.
/// </summary>
public static class ProjectKinds
{
    /// <summary>A project whose name has a <c>Test</c> or <c>Tests</c> hump (<c>Acme.UnitTests</c>, <c>TestUtilities</c>, not <c>Latest</c>), or a file under a <c>test</c>/<c>tests</c> folder.</summary>
    public static bool IsTestProject(string projectName, string filePath)
    {
        foreach (string segment in projectName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            List<int> starts = SymbolRanker.HumpStarts(segment);
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : segment.Length;
                ReadOnlySpan<char> hump = segment.AsSpan(starts[i], end - starts[i]).TrimEnd('_');
                if (hump.Equals("Test", StringComparison.OrdinalIgnoreCase) || hump.Equals("Tests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (string segment in filePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("test", StringComparison.OrdinalIgnoreCase) || segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
