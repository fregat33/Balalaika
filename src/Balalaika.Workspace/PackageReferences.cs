using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>
/// The two facts behind package version drift: which NuGet package version a project compiles against (read from the
/// global packages folder layout <c>&lt;id&gt;/&lt;version&gt;/lib|ref/...</c> of its metadata references) and which
/// version a project itself declares (the <c>AssemblyInformationalVersion</c> the design-time build generates from the
/// MSBuild <c>Version</c> property).
/// </summary>
public static partial class PackageReferences
{
    [GeneratedRegex(@"^\d+\.\d+(\.\d+){0,2}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionSegment();

    /// <summary>Package id and version from a path inside a NuGet packages folder, e.g. <c>...\packages\acme.contracts\3.1.0\lib\net8.0\Acme.Contracts.dll</c>.</summary>
    public static bool TryParseNuGetPath(string path, out string packageId, out string version)
    {
        string[] segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < segments.Length - 1; i++)
        {
            string next = segments[i + 1];
            bool libOrRef = next.Equals("lib", StringComparison.OrdinalIgnoreCase) || next.Equals("ref", StringComparison.OrdinalIgnoreCase);
            if (libOrRef && VersionSegment().IsMatch(segments[i]))
            {
                packageId = segments[i - 1];
                version = segments[i];
                return true;
            }
        }

        packageId = "";
        version = "";
        return false;
    }

    /// <summary>The packages a project compiles against, one record per assembly, taken from its metadata reference paths.</summary>
    public static IReadOnlyList<PackageRefRecord> Collect(Project project)
    {
        Dictionary<string, PackageRefRecord> byAssembly = new(StringComparer.OrdinalIgnoreCase);
        foreach (MetadataReference reference in project.MetadataReferences)
        {
            if (reference is PortableExecutableReference { FilePath: { } filePath } && TryParseNuGetPath(filePath, out string id, out string version))
            {
                string assembly = Path.GetFileNameWithoutExtension(filePath);
                byAssembly.TryAdd(assembly, new PackageRefRecord(assembly, id, version));
            }
        }

        return byAssembly.Values.OrderBy(p => p.AssemblyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The version the project declares, from <c>AssemblyInformationalVersion</c> without any <c>+commit</c> suffix.
    /// Null when the project declares none: the SDK default <c>1.0.0</c> means the real version is set elsewhere, for
    /// example by the pipeline at pack time, and must not be compared.
    /// </summary>
    public static string? ReadVersion(Compilation compilation)
    {
        string? informational = null;
        foreach (AttributeData attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "AssemblyInformationalVersionAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string value)
            {
                informational = value;
                break;
            }
        }

        string version = informational ?? compilation.Assembly.Identity.Version.ToString();
        int plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }

        version = version.Trim();
        if (version.Length == 0)
        {
            return null;
        }

        // 1.0.0 is the SDK default when no Version property is set; 0.0.0.0 is a compilation with no version attributes at all.
        if (Version.TryParse(version.Contains('.') ? version : version + ".0", out Version? parsed)
            && parsed.Major is 0 or 1 && parsed.Minor == 0 && parsed.Build <= 0)
        {
            return null;
        }

        return version;
    }
}
