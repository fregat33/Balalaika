namespace Balalaika.Index;

/// <summary>Which projects a reference or call-graph result keeps: every project, production only, or test projects only.</summary>
public enum ReferenceScope
{
    All,
    Production,
    Tests,
}

/// <summary>
/// The <c>kind</c> and <c>scope</c> arguments of <c>find_references</c> and <c>get_call_graph</c>, parsed once and
/// applied the same way on the live path and on the index path. <see cref="Kinds"/> is null when every usage kind is
/// kept; the project split follows <see cref="ProjectKinds.IsTestProject"/>.
/// </summary>
public sealed record ReferenceFilter(IReadOnlySet<string>? Kinds, ReferenceScope Scope)
{
    public static readonly ReferenceFilter None = new(null, ReferenceScope.All);

    private static readonly string[] AcceptedKinds = [ReferenceKinds.Usage, ReferenceKinds.Invocation, ReferenceKinds.Creation, ReferenceKinds.Base, ReferenceKinds.Attribute];

    public bool IsActive => this.Kinds is not null || this.Scope != ReferenceScope.All;

    /// <summary>
    /// Parses the tool arguments. <paramref name="kind"/> is one usage kind or a comma-separated list; <paramref name="scope"/>
    /// is <c>all</c>, <c>production</c> or <c>tests</c>; null and blank mean no filter. Returns the error text to answer
    /// with on a value outside the accepted list, null when the arguments are fine.
    /// </summary>
    public static string? TryParse(string? kind, string? scope, out ReferenceFilter filter)
    {
        filter = None;
        HashSet<string>? kinds = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            kinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string value = part.ToLowerInvariant();
                if (Array.IndexOf(AcceptedKinds, value) < 0)
                {
                    return $"unknown kind '{part}'. Accepted: {string.Join(", ", AcceptedKinds)}; several are comma-separated.";
                }

                kinds.Add(value);
            }

            if (kinds.Count == 0)
            {
                kinds = null;
            }
        }

        ReferenceScope parsedScope;
        if (string.IsNullOrWhiteSpace(scope) || scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            parsedScope = ReferenceScope.All;
        }
        else if (scope.Equals("production", StringComparison.OrdinalIgnoreCase))
        {
            parsedScope = ReferenceScope.Production;
        }
        else if (scope.Equals("tests", StringComparison.OrdinalIgnoreCase) || scope.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            parsedScope = ReferenceScope.Tests;
        }
        else
        {
            return $"unknown scope '{scope}'. Accepted: all, production, tests.";
        }

        filter = new ReferenceFilter(kinds, parsedScope);
        return null;
    }

    public bool MatchesKind(string kind)
    {
        return this.Kinds is null || this.Kinds.Contains(kind);
    }

    /// <summary>Whether a hit in <paramref name="projectName"/> at <paramref name="filePath"/> falls inside the scope.</summary>
    public bool MatchesScope(string projectName, string filePath)
    {
        return this.Scope switch
        {
            ReferenceScope.Production => !ProjectKinds.IsTestProject(projectName, filePath),
            ReferenceScope.Tests => ProjectKinds.IsTestProject(projectName, filePath),
            _ => true,
        };
    }

    public bool Matches(string kind, string projectName, string filePath)
    {
        return this.MatchesKind(kind) && this.MatchesScope(projectName, filePath);
    }

    /// <summary>The active parts for a group line: <c>kind=creation; scope=production</c>. Empty when nothing is filtered.</summary>
    public string Describe()
    {
        List<string> parts = [];
        if (this.Kinds is not null)
        {
            parts.Add("kind=" + string.Join(",", AcceptedKinds.Where(this.Kinds.Contains)));
        }

        if (this.Scope != ReferenceScope.All)
        {
            parts.Add("scope=" + (this.Scope == ReferenceScope.Production ? "production" : "tests"));
        }

        return string.Join("; ", parts);
    }
}
