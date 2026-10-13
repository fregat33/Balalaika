namespace Balalaika.Index;

/// <summary>A csproj known to the index. One row per project path, shared by all target frameworks. <paramref name="Version"/> is the project's own package version when it declares one.</summary>
public sealed record ProjectRecord(int Id, string Path, string Name, string AssemblyName, string SolutionPath, string? Version = null, int IndexedSchema = 0);

/// <summary>A NuGet package a project compiles against: the assembly it contributes, the package id and the resolved version.</summary>
public sealed record PackageRefRecord(string AssemblyName, string PackageId, string Version);

/// <summary>A consumer project whose package version of <paramref name="PackageId"/> differs from the version the source project currently declares.</summary>
public sealed record PackageDriftRow(ProjectRecord Consumer, string PackageId, string ConsumedVersion, ProjectRecord Source);

/// <summary>
/// A declaration found in one document. DocId is the Roslyn documentation comment id (T:, M:, P:, F:, E:). Line and
/// Column point at the identifier; <paramref name="EndLine"/> is the last line of the whole declaration (0 when the
/// blob was extracted before it was recorded).
/// </summary>
public sealed record SymbolRecord(
    string DocId,
    string Kind,
    string Name,
    string FqName,
    string Signature,
    string Accessibility,
    string? ContainingDocId,
    int Line,
    int Column,
    int EndLine = 0);

/// <summary>
/// A usage of a symbol found in one document. TargetAssembly disambiguates doc ids shared by several assemblies.
/// <paramref name="FromDocId"/> is the innermost declaration the usage sits in (a member, or the type for base lists
/// and attributes), <see cref="EnclosingDisplay.TopLevel"/> for top-level statements, null when unknown.
/// </summary>
public sealed record ReferenceRecord(string TargetDocId, string TargetAssembly, string Kind, int Line, int Column, string? FromDocId = null);

/// <summary>An inherits / implements / overrides edge declared in one document.</summary>
public sealed record RelationRecord(string FromDocId, string ToDocId, string ToAssembly, string Kind);

/// <summary>Everything extracted from a single document content. Keyed by content hash, independent of path or branch.</summary>
public sealed record DocumentExtraction(
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<ReferenceRecord> References,
    IReadOnlyList<RelationRecord> Relations)
{
    public static DocumentExtraction Empty { get; } = new([], [], []);
}

/// <summary>Current working-tree mapping of a file path to the blob that describes its content.</summary>
public sealed record TreeEntry(string Path, string ContentHash, long Mtime, long Size);

/// <summary>
/// One indexed usage. <paramref name="Enclosing"/> is the display name of the declaration the usage sits in
/// (<see cref="EnclosingDisplay.Format"/>), null for blobs extracted before it was recorded.
/// </summary>
public sealed record ReferenceHit(
    string ProjectName,
    string SolutionPath,
    string FilePath,
    int Line,
    int Column,
    string Kind,
    string ContentHash,
    string? FromDocId = null,
    string? Enclosing = null);

/// <summary>A declaration of one indexed file, for outlines of files whose solution is not loaded.</summary>
public sealed record FileSymbolHit(
    string ProjectName,
    string SolutionPath,
    string FilePath,
    SymbolRecord Symbol,
    string ContentHash);

public sealed record RelationHit(
    string ProjectName,
    string SolutionPath,
    string FromDocId,
    string Kind,
    string? FilePath,
    int Line,
    int Column,
    string? Signature,
    string ContentHash);

public sealed record SymbolHit(
    string ProjectName,
    string SolutionPath,
    string FilePath,
    string DocId,
    string Kind,
    string Name,
    string FqName,
    string Signature,
    int Line,
    int Column,
    string ContentHash);

public sealed record IndexStats(int Projects, int Blobs, int Symbols, int References, int Relations, int TreeEntries);

public static class RelationKinds
{
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string Overrides = "overrides";
}

public static class ReferenceKinds
{
    public const string Usage = "usage";
    public const string Invocation = "invocation";
    public const string Creation = "creation";
    public const string Base = "base";
    public const string Attribute = "attribute";
}
