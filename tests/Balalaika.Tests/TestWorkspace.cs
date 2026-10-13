using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Balalaika.Tests;

/// <summary>Builds an in-memory C# project with a real file path so the indexer treats it like an MSBuild project.</summary>
public static class TestWorkspace
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        string[] wanted = ["System.Runtime.dll", "System.Private.CoreLib.dll", "netstandard.dll", "System.Collections.dll", "System.Linq.dll", "System.Console.dll"];
        return tpa.Split(Path.PathSeparator)
            .Where(p => wanted.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    });

    /// <summary>The framework references every test project binds against.</summary>
    public static IReadOnlyList<MetadataReference> DefaultReferences => References.Value;

    public static (AdhocWorkspace Workspace, Project Project) CreateProject(string projectDir, string name, params (string FileName, string Text)[] documents)
    {
        return CreateProject(projectDir, name, References.Value, documents);
    }

    /// <summary>Same as <see cref="CreateProject(string, string, ValueTuple{string, string}[])"/> but bound against the given references only.</summary>
    public static (AdhocWorkspace Workspace, Project Project) CreateProject(string projectDir, string name, IReadOnlyList<MetadataReference> metadataReferences, params (string FileName, string Text)[] documents)
    {
        AdhocWorkspace workspace = new();
        Project project = AddProject(workspace, projectDir, name, [], metadataReferences, documents);
        return (workspace, project);
    }

    /// <summary>Adds another project to an existing workspace, referencing the given projects, so a solution has a dependency graph.</summary>
    public static Project AddProject(AdhocWorkspace workspace, string projectDir, string name, IReadOnlyList<ProjectId> projectReferences, params (string FileName, string Text)[] documents)
    {
        return AddProject(workspace, projectDir, name, projectReferences, References.Value, documents);
    }

    private static Project AddProject(
        AdhocWorkspace workspace,
        string projectDir,
        string name,
        IReadOnlyList<ProjectId> projectReferences,
        IReadOnlyList<MetadataReference> metadataReferences,
        (string FileName, string Text)[] documents)
    {
        ProjectId projectId = ProjectId.CreateNewId();
        ProjectInfo info = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: Path.Combine(projectDir, name + ".csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            projectReferences: projectReferences.Select(id => new ProjectReference(id)),
            metadataReferences: metadataReferences);
        workspace.AddProject(info);

        foreach ((string fileName, string text) in documents)
        {
            string path = Path.Combine(projectDir, fileName);
            DocumentInfo doc = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                fileName,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create(), path)),
                filePath: path);
            workspace.AddDocument(doc);
        }

        return workspace.CurrentSolution.GetProject(projectId)!;
    }

    public static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "balalaika-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
