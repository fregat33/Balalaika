using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Pins the <c>Notice:</c> line of <c>find_references</c> that says the live answer may be incomplete.</summary>
public sealed class ReferenceNoticeTests : IDisposable
{
    private const string Source = """
        namespace Acme.Core;

        public class PriceCalculator { }

        public static class Use
        {
            public static PriceCalculator Make() => new PriceCalculator();
        }
        """;

    private const string DocId = "T:Acme.Core.PriceCalculator";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in this._disposables)
        {
            d.Dispose();
        }
    }

    private async Task<string> RunAsync(string[] diagnostics, Action<IndexJobQueue, IndexActivity>? shape = null)
    {
        string dir = TestWorkspace.NewTempDir();
        string solutionPath = Path.Combine(dir, "Acme.slnx");
        File.WriteAllText(solutionPath, "");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(Path.Combine(dir, "Core"), "Acme.Core", ("Core.cs", Source));
        IndexStore index = new(Path.Combine(TestWorkspace.NewTempDir(), "index.db"));
        IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions());
        PendingRefreshes pending = new();
        WorkspaceManager workspaces = new(index, pending, options, NullLogger<WorkspaceManager>.Instance);
        this._disposables.Add(index);
        this._disposables.Add(workspaces);
        workspaces.Adopt(new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, diagnostics));
        IndexJobQueue jobs = new();
        IndexActivity activity = new();
        shape?.Invoke(jobs, activity);
        QueryService query = new(
            workspaces,
            new SymbolResolver(workspaces, index, options),
            index,
            jobs,
            new ResultPager(),
            new WarmUpTracker(),
            pending,
            options,
            new SourceFilter([]),
            new GitRepository(options, NullLogger<GitRepository>.Instance),
            NullLogger<QueryService>.Instance,
            activity);
        return await query.FindReferencesAsync(DocId, null, false, null, null, 200, CancellationToken.None);
    }

    private static string[] Lines(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string LineBeforeTotal(string output)
    {
        string[] lines = Lines(output);
        int total = Array.FindIndex(lines, l => l.StartsWith("Total:", StringComparison.Ordinal));
        total.Should().BeGreaterThan(0, output);
        return lines[total - 1];
    }

    [Fact]
    public async Task HealthyFixture_PrintsNoNotice()
    {
        string output = await this.RunAsync([]);

        Lines(output).Should().NotContain(l => l.StartsWith("Notice:", StringComparison.Ordinal), output);
    }

    [Fact]
    public async Task OneFailureAndOneWarning_PrintsNoticeBeforeTotal()
    {
        string output = await this.RunAsync(["Failure: Project reference 'x' not found", "Warning: something"]);

        LineBeforeTotal(output).Should().Be("Notice: the live answer may be incomplete: Acme had 1 load failure (first: Project reference 'x' not found).");
    }

    [Fact]
    public async Task TwoFailures_PrintsCountAndFirstMessageOnly()
    {
        string output = await this.RunAsync(["Failure: first problem", "Failure: second problem"]);

        LineBeforeTotal(output).Should().Be("Notice: the live answer may be incomplete: Acme had 2 load failures (first: first problem).");
    }

    [Fact]
    public async Task NuGetAuditFailureAlone_PrintsNoNotice()
    {
        string output = await this.RunAsync(["Failure: Package 'Foo' 1.0.0 has a known high severity vulnerability"]);

        Lines(output).Should().NotContain(l => l.StartsWith("Notice:", StringComparison.Ordinal), output);
    }

    [Fact]
    public async Task OnePendingIndexJob_PrintsSingularReason()
    {
        string output = await this.RunAsync([], (jobs, _) => jobs.Enqueue(new ForgetMissingJob()));

        LineBeforeTotal(output).Should().Be("Notice: the live answer may be incomplete: the server has 1 index job pending.");
    }

    [Fact]
    public async Task FailureTwoJobsAndWatcherChanges_JoinsReasonsInOrder()
    {
        string output = await this.RunAsync(
            ["Failure: Project reference 'x' not found"],
            (jobs, activity) =>
            {
                jobs.Enqueue(new ForgetMissingJob());
                jobs.Enqueue(new ForgetMissingJob());
                activity.SetBuffered(3);
            });

        LineBeforeTotal(output).Should().Be("Notice: the live answer may be incomplete: Acme had 1 load failure (first: Project reference 'x' not found); the server has 2 index jobs pending; 3 file changes wait in the watcher.");
    }

    [Fact]
    public async Task OneBufferedFileChange_PrintsSingularReason()
    {
        string output = await this.RunAsync([], (_, activity) => activity.SetBuffered(1));

        LineBeforeTotal(output).Should().Be("Notice: the live answer may be incomplete: 1 file change waits in the watcher.");
    }
}
