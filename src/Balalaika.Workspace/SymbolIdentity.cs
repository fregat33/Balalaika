using Microsoft.CodeAnalysis;

namespace Balalaika.Workspace;

/// <summary>
/// The assembly half of symbol identity (doc id + assembly name). Framework types live in whichever reference
/// assembly a compilation binds against: <c>System.String</c> is in <c>netstandard</c> for netstandard2.0, in
/// <c>System.Runtime</c> for net8.0 and later, in <c>mscorlib</c> for net48, and in <c>System.Private.CoreLib</c> at
/// run time; <c>List&lt;T&gt;</c> and <c>Console</c> move between <c>netstandard</c>, <c>mscorlib</c>,
/// <c>System.Collections</c> and <c>System.Console</c> the same way. Every framework assembly therefore collapses to
/// one token, so references extracted from solutions on different target frameworks merge under one identity.
/// Only identity uses this; display paths keep the real <c>ContainingAssembly.Name</c>.
/// </summary>
public static class SymbolIdentity
{
    /// <summary>The identity assembly name recorded for every framework type.</summary>
    public const string FrameworkAssembly = "<framework>";

    private static readonly HashSet<string> FrameworkRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "netstandard",
        "mscorlib",
        "System",
        "System.Core",
        "System.Private.CoreLib",
        "System.Runtime",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "Microsoft.VisualBasic.Core",
        "WindowsBase",
    };

    /// <summary>The identity assembly name of a symbol, or null when it has no containing assembly.</summary>
    public static string? AssemblyOf(ISymbol? symbol)
    {
        return Canonical(symbol?.ContainingAssembly?.Name);
    }

    /// <summary>
    /// Maps a containing assembly name to its identity form. Framework assemblies (the facades above and everything
    /// under <c>System.*</c>) become <see cref="FrameworkAssembly"/>; anything else is returned unchanged. Collapsing
    /// <c>System.*</c> also catches NuGet packages such as <c>System.Text.Json</c> on netstandard, which is harmless:
    /// the doc id alone is unique within the framework namespace.
    /// </summary>
    public static string? Canonical(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return assemblyName;
        }

        if (FrameworkRoots.Contains(assemblyName) || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
        {
            return FrameworkAssembly;
        }

        return assemblyName;
    }
}
