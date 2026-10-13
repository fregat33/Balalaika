namespace Balalaika.Index;

/// <summary>How well a symbol name matches a search pattern, best first.</summary>
public enum MatchQuality
{
    /// <summary>The name is the pattern.</summary>
    Exact = 0,

    /// <summary>The name ends with the pattern at a hump boundary: <c>Job</c> matches <c>CloseCompletedOrdersJob</c>.</summary>
    LastHump = 1,

    /// <summary>The name starts with the pattern.</summary>
    Prefix = 2,

    /// <summary>The pattern's humps match the name's humps in order: <c>UOC</c> or <c>Retry</c> match <c>UpdateOrderCommand</c> and <c>CreateRetryOptions</c>.</summary>
    CamelHump = 3,

    /// <summary>The pattern occurs somewhere inside the name.</summary>
    Substring = 4,

    /// <summary>Returned by the live matcher under a rule this class does not model; kept so the live set does not shrink.</summary>
    Fuzzy = 5,
}

/// <summary>
/// Sort key of one <c>search_symbols</c> hit. Lower sorts first: production code before tests, then match quality,
/// then kind (types before members), then the shorter name. Callers break the remaining ties on the full name so the
/// live and the index path print the same order for the same symbols. Tests go last as a whole because test method
/// names (<c>Handle_WhenFileNameTooLong_ShouldTruncateAndUpload</c>) end in the search word so often that they would
/// otherwise take the best match tier away from the production symbol the agent is looking for.
/// </summary>
public readonly record struct SymbolScore(MatchQuality Match, int KindRank, bool IsTest, int NameLength) : IComparable<SymbolScore>
{
    public int CompareTo(SymbolScore other)
    {
        int c = this.IsTest.CompareTo(other.IsTest);
        if (c != 0)
        {
            return c;
        }

        c = this.Match.CompareTo(other.Match);
        if (c != 0)
        {
            return c;
        }

        c = this.KindRank.CompareTo(other.KindRank);
        return c != 0 ? c : this.NameLength.CompareTo(other.NameLength);
    }
}

/// <summary>
/// The one scoring function behind <c>search_symbols</c>, shared by the live Roslyn path and the SQLite path. It
/// works on strings only so both paths agree; the candidates come from the matcher of each path.
/// </summary>
public static class SymbolRanker
{
    /// <summary>
    /// Scores <paramref name="name"/> against <paramref name="pattern"/>, or returns null when no matching rule
    /// applies. <paramref name="fallback"/> is the quality to assume for a candidate another matcher accepted.
    /// </summary>
    public static SymbolScore? Score(string pattern, string name, string kind, string projectName, string filePath, MatchQuality? fallback = null)
    {
        MatchQuality? match = Match(pattern, name) ?? fallback;
        if (match is null)
        {
            return null;
        }

        return new SymbolScore(match.Value, KindRank(kind), ProjectKinds.IsTestProject(projectName, filePath), name.Length);
    }

    /// <summary>The last dotted or spaced term of a pattern is the part that names the symbol; the rest names containers.</summary>
    public static string NamePart(string pattern)
    {
        string[] terms = Terms(pattern);
        return terms.Length == 0 ? pattern.Trim() : terms[^1];
    }

    /// <summary>The terms before the name part; each must occur in the fully qualified name of a hit.</summary>
    public static IReadOnlyList<string> ContainerParts(string pattern)
    {
        string[] terms = Terms(pattern);
        return terms.Length <= 1 ? [] : terms[..^1];
    }

    public static string[] Terms(string pattern)
    {
        return pattern.Split([' ', '.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static MatchQuality? Match(string pattern, string name)
    {
        if (pattern.Length == 0 || name.Length == 0)
        {
            return null;
        }

        if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
        {
            return MatchQuality.Exact;
        }

        if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) && IsHumpStart(name, name.Length - pattern.Length))
        {
            return MatchQuality.LastHump;
        }

        if (name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
        {
            return MatchQuality.Prefix;
        }

        if (MatchesHumps(pattern, name))
        {
            return MatchQuality.CamelHump;
        }

        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ? MatchQuality.Substring : null;
    }

    /// <summary>
    /// Types and methods first (the code an agent is looking for), then properties and events, then fields and enum
    /// members. Types and methods share a rank on purpose: ranking every <c>Upload*</c> DTO above
    /// <c>IFileRepository.UploadFile</c> put the method at rank 11 on the baseline corpus; sharing the rank and letting
    /// the shorter name decide puts it at 5.
    /// </summary>
    public static int KindRank(string kind)
    {
        return kind switch
        {
            "class" or "interface" or "record" or "record struct" or "struct" or "enum" or "delegate" => 0,
            "method" or "constructor" or "operator" or "destructor" => 0,
            "property" or "indexer" or "event" => 1,
            "field" or "enum_member" => 2,
            _ => 3,
        };
    }

    /// <summary>Start offsets of the humps of an identifier: <c>HTTPClientFactory_v2</c> gives HTTP, Client, Factory, v, 2.</summary>
    public static List<int> HumpStarts(string name)
    {
        return HumpStarts(name, everyUpperStarts: false);
    }

    /// <summary>Start offsets of the humps of a search pattern, where every upper-case letter starts a hump: <c>UCC</c> gives U, C, C.</summary>
    public static List<int> PatternHumpStarts(string pattern)
    {
        return HumpStarts(pattern, everyUpperStarts: true);
    }

    private static List<int> HumpStarts(string name, bool everyUpperStarts)
    {
        List<int> starts = [];
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '_')
            {
                continue;
            }

            if (IsHumpStart(name, i, everyUpperStarts))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    private static bool IsHumpStart(string name, int i, bool everyUpperStarts = false)
    {
        if (i <= 0)
        {
            return i == 0;
        }

        char c = name[i];
        char prev = name[i - 1];
        if (prev == '_')
        {
            return c != '_';
        }

        if (char.IsUpper(c))
        {
            bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
            return everyUpperStarts || !char.IsUpper(prev) || nextIsLower;
        }

        if (char.IsDigit(c))
        {
            return !char.IsDigit(prev);
        }

        return char.IsLetter(c) && !char.IsLetter(prev);
    }

    /// <summary>
    /// Each hump of the pattern (split on its own upper-case letters) is a prefix of a later hump of the name, in
    /// order, not necessarily contiguous and not necessarily from the first hump. A single-hump pattern therefore
    /// matches at any hump start.
    /// </summary>
    private static bool MatchesHumps(string pattern, string name)
    {
        List<int> patternStarts = PatternHumpStarts(pattern);
        List<int> nameStarts = HumpStarts(name);
        if (patternStarts.Count == 0 || nameStarts.Count == 0)
        {
            return false;
        }

        int nameIndex = 0;
        for (int p = 0; p < patternStarts.Count; p++)
        {
            int start = patternStarts[p];
            int end = p + 1 < patternStarts.Count ? patternStarts[p + 1] : pattern.Length;
            ReadOnlySpan<char> hump = pattern.AsSpan(start, end - start).TrimEnd('_');
            bool found = false;
            for (; nameIndex < nameStarts.Count; nameIndex++)
            {
                int nameStart = nameStarts[nameIndex];
                int nameEnd = nameIndex + 1 < nameStarts.Count ? nameStarts[nameIndex + 1] : name.Length;
                ReadOnlySpan<char> nameHump = name.AsSpan(nameStart, nameEnd - nameStart);
                if (nameHump.StartsWith(hump, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    nameIndex++;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}
