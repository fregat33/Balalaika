namespace Balalaika.Index;

/// <summary>
/// What one request may see: the solutions it must not (<see cref="Hidden"/>). It travels with the call's async flow,
/// set by the MCP tool filter and read by the workspace manager and the index queries; nothing is stored on the
/// singletons. Code that runs outside a request (the hosted service, watchers, index jobs) has no view and sees everything.
/// </summary>
public sealed class ViewContext(IReadOnlySet<string> hidden, IReadOnlySet<string> parts, IReadOnlySet<string> replacedRepositories)
{
    private static readonly AsyncLocal<ViewContext?> Ambient = new();

    /// <summary>The view of the current async flow; null outside a request.</summary>
    public static ViewContext? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    /// <summary>Full solution paths (compared case-insensitively) the view excludes.</summary>
    public IReadOnlySet<string> Hidden { get; } = hidden;

    /// <summary>The worktree directories the request asked for.</summary>
    public IReadOnlySet<string> Parts { get; } = parts;

    /// <summary>Common git directories of the repositories a part stands in for; their base checkouts are hidden.</summary>
    public IReadOnlySet<string> ReplacedRepositories { get; } = replacedRepositories;

    public static bool IsHidden(string solutionPath)
    {
        return Current is { } view && view.Hidden.Contains(solutionPath);
    }

    /// <summary>A <c>AND column COLLATE NOCASE NOT IN (...)</c> fragment (case-insensitive like <see cref="IsHidden"/>) for the current view, or empty without one. The values are literals: they are file paths of this server, quoted here.</summary>
    internal static string ExcludeSql(string column)
    {
        if (Current is not { Hidden.Count: > 0 } view)
        {
            return "";
        }

        return $" AND {column} COLLATE NOCASE NOT IN ({string.Join(", ", view.Hidden.Select(h => "'" + h.Replace("'", "''") + "'"))}) ";
    }
}
