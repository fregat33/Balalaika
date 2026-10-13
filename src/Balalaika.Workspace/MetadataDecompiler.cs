using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace Balalaika.Workspace;

public static class MetadataDecompiler
{
    /// <summary>
    /// Decompiles the type that declares <paramref name="symbol"/> from the assembly the compilation references.
    /// Returns null when the symbol has no backing assembly file.
    /// </summary>
    public static string? DecompileContainingType(ISymbol symbol, Compilation compilation, int maxLines, out string? assemblyPath)
    {
        assemblyPath = null;
        INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null || symbol.ContainingAssembly is null)
        {
            return null;
        }

        if (compilation.GetMetadataReference(symbol.ContainingAssembly) is not PortableExecutableReference { FilePath: { } path })
        {
            return null;
        }

        assemblyPath = PreferImplementationAssembly(path);
        CSharpDecompiler decompiler = new(assemblyPath, new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = true,
            UseDebugSymbols = false,
        });

        string code = decompiler.DecompileTypeAsString(new FullTypeName(DisplayFormats.ReflectionName(type)));
        string[] lines = code.Split('\n');
        if (lines.Length <= maxLines)
        {
            return code;
        }

        return string.Join('\n', lines.Take(maxLines)) + $"\n// ... truncated, {lines.Length - maxLines} more lines. Increase maxLines to see them.";
    }

    /// <summary>NuGet reference assemblies (ref/) carry no bodies. Prefer the lib/ sibling when it exists.</summary>
    private static string PreferImplementationAssembly(string path)
    {
        int refIndex = path.IndexOf(@"\ref\", StringComparison.OrdinalIgnoreCase);
        if (refIndex < 0)
        {
            return path;
        }

        string candidate = path[..refIndex] + @"\lib\" + path[(refIndex + 5)..];
        return File.Exists(candidate) ? candidate : path;
    }
}
