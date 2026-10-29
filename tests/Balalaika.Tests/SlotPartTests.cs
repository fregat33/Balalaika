using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>
/// The <c>part</c> view of <c>/mcp</c>: the real server (<c>Program</c>, in memory) over a temp root of real git
/// repositories with linked worktrees; only the MSBuild load is replaced by a project built from the <c>.cs</c> files on disk.
/// </summary>
public sealed partial class SlotPartTests
{
    // ---------------------------------------------------------------- step 0: the request is readable in a tool call

    [Fact]
    public async Task Probe_TwoConcurrentRequests_EachSeeTheirOwnQueryStringInTheToolAndInTheFilter()
    {
        // Arrange
        await using PartServer server = await PartServer.StartAsync(withRepositories: false);

        // Act: the tool waits, so the two calls are certainly in flight together
        for (int round = 0; round < 5; round++)
        {
            ProbeTools.SeenByFilter.Clear();
            Task<McpResult> x = server.CallAsync("probe=x", "probe_query");
            Task<McpResult> y = server.CallAsync("probe=y", "probe_query");
            McpResult[] results = await Task.WhenAll(x, y);

            // Assert
            results[0].Text.Should().Be("?probe=x");
            results[1].Text.Should().Be("?probe=y");
            ProbeTools.SeenByFilter.Should().BeEquivalentTo(["?probe=x", "?probe=y"]);
        }
    }

    // ---------------------------------------------------------------- the view

    [GitFact]
    public async Task NoPart_ShowsBaseCheckoutsOnly_AndNothingFromAnyWorktree()
    {
        await using PartServer server = await PartServer.StartAsync();

        server.SolutionsOf((await server.CallAsync("", "status")).Text).Should().BeEquivalentTo([server.RSolution, server.QSolution]);
        server.SolutionsOf(await server.HealthAsync("")).Should().BeEquivalentTo([server.RSolution, server.QSolution]);
        foreach (string hidden in new[] { "OnlyOne", "OnlyTwo", "OnlyQ" })
        {
            (await server.SearchAsync("", hidden)).Should().Be(NoMatch(hidden));
        }

        Count(await server.SearchAsync("", "Shared"), "Acme.Orders.Shared").Should().Be(1);
    }

    [GitFact]
    public async Task OnePart_ReplacesItsBaseCheckout_KeepsOtherRepositoriesBase_AndHidesOtherWorktrees()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);

        server.SolutionsOf((await server.CallAsync(query, "status")).Text).Should().BeEquivalentTo([server.R1Solution, server.QSolution]);
        server.SolutionsOf(await server.HealthAsync(query)).Should().BeEquivalentTo([server.R1Solution, server.QSolution]);
        (await server.SearchAsync(query, "OnlyOne")).Should().Contain("OnlyOne");
        (await server.SearchAsync(query, "OnlyTwo")).Should().Be(NoMatch("OnlyTwo"));
        (await server.SearchAsync(query, "OnlyQ")).Should().Be(NoMatch("OnlyQ"));
        (await server.SearchAsync(query, "Other")).Should().Contain("Acme.Billing.Other");
    }

    [GitFact]
    public async Task TwoPartsOfDifferentRepositories_ShowBothWorktrees_AndNoBaseOfEither()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1) + "&part=" + Uri.EscapeDataString(server.Q1);

        server.SolutionsOf((await server.CallAsync(query, "status")).Text).Should().BeEquivalentTo([server.R1Solution, server.Q1Solution]);
        (await server.SearchAsync(query, "OnlyOne")).Should().Contain("OnlyOne");
        (await server.SearchAsync(query, "OnlyQ")).Should().Contain("OnlyQ");
        (await server.SearchAsync(query, "OnlyTwo")).Should().Be(NoMatch("OnlyTwo"));
        Count(await server.SearchAsync(query, "Shared"), "Acme.Orders.Shared").Should().Be(1);
    }

    [GitFact]
    public async Task OnePart_ReferencesCallGraphAndImplementations_NeverLeaveTheWorktree_LiveOrFromTheIndex()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);

        // Nothing is loaded yet (warm-up evicted everything): these answer from the index, where r/2 and r are present too
        await AssertOnlyWorktreeAsync("index only");

        // Loaded: live results for r/1
        (await server.CallAsync(query, "load_solution", new { solution = server.R1Solution })).Text.Should().Contain("projects loaded");
        await AssertOnlyWorktreeAsync("live");

        async Task AssertOnlyWorktreeAsync(string phase)
        {
            foreach ((string tool, object args) in new (string, object)[]
            {
                ("find_references", new { symbol = "Acme.Orders.Shared" }),
                ("get_call_graph", new { symbol = "Acme.Orders.Caller.UseShared" }),
                ("find_implementations", new { symbol = "Acme.Orders.IWidget" }),
            })
            {
                string text = (await server.CallAsync(query, tool, args)).Text;
                text.Should().Contain(server.R1, $"{tool} ({phase}) has hits in the part");
                text.Should().NotContain(server.R + Path.DirectorySeparatorChar, $"{tool} ({phase}) never shows the base checkout");
                text.Should().NotContain(server.R2, $"{tool} ({phase}) never shows another worktree");
            }
        }
    }

    [GitFact]
    public async Task OnePart_ReviewChanges_ReviewsTheWorktreeBranch()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);
        (await server.CallAsync(query, "load_solution", new { solution = server.R1Solution })).Text.Should().Contain("projects loaded");

        string explicitBase = (await server.CallAsync(query, "review_changes", new { solution = server.R1Solution, @base = "main" })).Text;
        explicitBase.Should().StartWith("Review: Demo  working tree vs main", explicitBase);
        explicitBase.Split('\n').Should().Contain(l => l.Contains("Only1.cs") && l.Contains("added"), "the branch's file is reviewed as added");
        explicitBase.Should().Contain("OnlyOne").And.Contain("Total:");
        explicitBase.Should().NotContain(server.R + Path.DirectorySeparatorChar, "never shows the base checkout");
        explicitBase.Should().NotContain("Only2", "never shows another worktree");

        string defaultBase = (await server.CallAsync(query, "review_changes", new { solution = server.R1Solution })).Text;
        defaultBase.Split('\n')[0].Should().Contain("vs main", "the default base is resolved in the worktree");
        defaultBase.Should().Contain("Only1.cs").And.NotContain("Only2");
    }

    [GitFact]
    public async Task OnePart_APathOrAHintOutsideTheView_IsUnknown_AndLoadSolutionDoesNotBringItBack()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);
        List<string> knownBefore = server.Workspaces.KnownSolutions.ToList();

        string[] outside =
        [
            (await server.CallAsync(query, "go_to_definition", new { symbol = Path.Combine(server.R, "A.cs") + ":3" })).Text,
            (await server.CallAsync(query, "go_to_definition", new { symbol = Path.Combine(server.R2, "A.cs") + ":3" })).Text,
            (await server.CallAsync(query, "get_outline", new { target = Path.Combine(server.R, "A.cs") })).Text,
            (await server.CallAsync(query, "get_outline", new { target = Path.Combine(server.R2, "A.cs") })).Text,
            (await server.CallAsync(query, "review_changes", new { solution = server.RSolution })).Text,
            (await server.CallAsync(query, "load_solution", new { solution = server.RSolution })).Text,
            (await server.CallAsync(query, "load_solution", new { solution = server.R2Solution })).Text,
        ];

        // find_references ignores a hint that names no known solution (as it does without a view) and resolves among the visible ones
        string hinted = (await server.CallAsync(query, "find_references", new { symbol = "Acme.Orders.Shared", solution = server.RSolution })).Text;
        hinted.Should().Contain(server.R1).And.NotContain(server.R + Path.DirectorySeparatorChar);
        outside[4].Should().StartWith("Unknown solution");
        outside[5].Should().StartWith("Unknown solution");
        outside[6].Should().StartWith("Unknown solution");
        outside.Take(2).Should().OnlyContain(t => t.Contains("not under any known solution"), string.Join(" | ", outside.Take(2)));

        // get_outline falls back to the first visible solution (the part), which does not contain the file
        outside.Skip(2).Take(2).Should().OnlyContain(t => t.Contains("is not part of Demo"), string.Join(" | ", outside.Skip(2).Take(2)));
        server.Workspaces.KnownSolutions.Should().BeEquivalentTo(knownBefore, "load_solution did not change the registry");
        server.Workspaces.TryGetLoaded(server.RSolution, out _).Should().BeFalse();
        server.SolutionsOf((await server.CallAsync(query, "status")).Text).Should().BeEquivalentTo([server.R1Solution, server.QSolution]);
    }

    [GitFact]
    public async Task ForgetSolution_OfASolutionHiddenByThePartView_AnswersUnknown_AndKeepsItKnown()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);

        string text = (await server.CallAsync(query, "forget_solution", new { solution = server.RSolution })).Text;

        text.Should().StartWith("Unknown solution: " + server.RSolution);
        server.Workspaces.KnownSolutions.Should().Contain(server.RSolution);
    }

    [GitFact]
    public async Task Parts_TwoOfOneRepository_AndANonWorktree_AreRefused()
    {
        await using PartServer server = await PartServer.StartAsync();

        McpResult twice = await server.CallAsync("part=" + Uri.EscapeDataString(server.R1) + "&part=" + Uri.EscapeDataString(server.R2), "status");
        McpResult notAWorktree = await server.CallAsync("part=" + Uri.EscapeDataString(server.Q), "status");

        twice.IsError.Should().BeTrue();
        twice.Text.Should().Be($"Two parts of one repository: {server.R1}, {server.R2}");
        notAWorktree.IsError.Should().BeTrue();
        notAWorktree.Text.Should().Be("Unknown part: " + server.Q);
    }

    [GitFact]
    public async Task Parts_ConcurrentRequestsWithDifferentParts_GetDifferentResults()
    {
        await using PartServer server = await PartServer.StartAsync();

        for (int round = 0; round < 8; round++)
        {
            Task<string> one = server.SearchAsync("part=" + Uri.EscapeDataString(server.R1), "Only");
            Task<string> two = server.SearchAsync("part=" + Uri.EscapeDataString(server.R2), "Only");
            Task<string> none = server.SearchAsync("", "Only");
            await Task.WhenAll(one, two, none);

            one.Result.Should().Contain("OnlyOne").And.NotContain("OnlyTwo");
            two.Result.Should().Contain("OnlyTwo").And.NotContain("OnlyOne");
            none.Result.Should().NotContain("OnlyOne").And.NotContain("OnlyTwo");
        }
    }

    private static string NoMatch(string pattern)
    {
        return $"No symbols match {pattern}. Patterns support camel-case humps (e.g. UOC for UpdateOrderCommand).";
    }

    private static int Count(string text, string needle)
    {
        return Regex.Matches(text, Regex.Escape(needle)).Count;
    }

    // ---------------------------------------------------------------- helpers

    private sealed record McpResult(bool IsError, string Text);

    [McpServerToolType]
    public sealed class ProbeTools(IHttpContextAccessor accessor)
    {
        public static ConcurrentBag<string> SeenByFilter { get; } = [];

        [McpServerTool(Name = "probe_query")]
        public async Task<string> Probe()
        {
            await Task.Delay(300);
            return accessor.HttpContext?.Request.QueryString.Value ?? "<no request>";
        }
    }

    /// <summary>The real server over a temp root; <c>r</c> with worktrees 1 and 2, <c>q</c> with worktree 1.</summary>
    private sealed class PartServer : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;
        private int _id;

        private PartServer(string root, bool withRepositories)
        {
            this.Root = root;
            string state = Path.GetFullPath(TestWorkspace.NewTempDir());
            this._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    // the machine's balalaika.local.json is copied beside the tests; its extra Roots and Preload would
                    // survive the overrides below and point the server at real repositories
                    foreach (FileConfigurationSource local in config.Sources.OfType<FileConfigurationSource>()
                        .Where(s => string.Equals(Path.GetFileName(s.Path), "balalaika.local.json", StringComparison.OrdinalIgnoreCase))
                        .ToList())
                    {
                        config.Sources.Remove(local);
                    }

                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Balalaika:Roots:0"] = root,
                        ["Balalaika:IndexPath"] = Path.Combine(state, "index.db"),
                        ["Balalaika:WarmUp"] = withRepositories ? "true" : "false",
                        ["Balalaika:UnloadedRefreshSeconds"] = "0",
                        ["Balalaika:DiscoverySweepSeconds"] = "0",
                        ["Balalaika:RestoreOnProjectChange"] = "false",
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    // captures the per-call lines of Balalaika.Server.ToolCalls (see ToolCallLogTests.cs)
                    services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([new ToolCallCaptureProvider(this.ToolCallLines), new SdkCaptureProvider(this.SdkLines)]));
                    services.AddSingleton(sp => new WorkspaceManager(
                        sp.GetRequiredService<IndexStore>(),
                        sp.GetRequiredService<PendingRefreshes>(),
                        sp.GetRequiredService<IOptions<BalalaikaOptions>>(),
                        sp.GetRequiredService<ILogger<WorkspaceManager>>()) { Loader = LoadFromDisk });
                    services.AddMcpServer()
                        .WithTools<ProbeTools>()
                        .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
                        {
                            if (request.Params?.Name == "probe_query")
                            {
                                ProbeTools.SeenByFilter.Add(request.Services?.GetService<IHttpContextAccessor>()?.HttpContext?.Request.QueryString.Value ?? "<no request>");
                            }

                            return await next(request, ct);
                        }));
                });
            });
            this._client = this._factory.CreateClient();
            this._client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            this._client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        public string Root { get; }

        public ConcurrentQueue<(LogLevel Level, string Message)> ToolCallLines { get; } = new();

        /// <summary>Lines of the <c>ModelContextProtocol.*</c> categories (see McpProbeTests.cs).</summary>
        public ConcurrentQueue<(LogLevel Level, string Message)> SdkLines { get; } = new();

        public string R => Path.Combine(this.Root, "r");

        public string R1 => Path.Combine(this.Root, "slots", "r", "1");

        public string R2 => Path.Combine(this.Root, "slots", "r", "2");

        public string Q => Path.Combine(this.Root, "q");

        public string Q1 => Path.Combine(this.Root, "slots", "q", "1");

        public string RSolution => Path.Combine(this.R, "Demo.slnx");

        public string R1Solution => Path.Combine(this.R1, "Demo.slnx");

        public string R2Solution => Path.Combine(this.R2, "Demo.slnx");

        public string QSolution => Path.Combine(this.Q, "Other.slnx");

        public string Q1Solution => Path.Combine(this.Q1, "Other.slnx");

        public WorkspaceManager Workspaces => this._factory.Services.GetRequiredService<WorkspaceManager>();

        public static async Task<PartServer> StartAsync(bool withRepositories = true)
        {
            string root = Path.GetFullPath(TestWorkspace.NewTempDir());
            if (withRepositories)
            {
                CreateRepositories(root);
            }

            PartServer server = new(root, withRepositories);
            if (withRepositories)
            {
                IndexStore index = server._factory.Services.GetRequiredService<IndexStore>();
                await WaitUntilAsync(async () =>
                    index.GetProjects().Select(p => p.SolutionPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5
                    && (await server.CallAsync("", "status")).Text.Contains("Ready: yes"));
            }

            return server;
        }

        /// <summary>One <c>tools/call</c> as its own stateless POST to <c>/mcp</c>.</summary>
        public async Task<McpResult> CallAsync(string query, string tool, object? arguments = null)
        {
            string body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref this._id),
                method = "tools/call",
                @params = new { name = tool, arguments = arguments ?? new { } },
            });
            using HttpResponseMessage response = await this._client.PostAsync("/mcp" + (query.Length == 0 ? "" : "?" + query), new StringContent(body, Encoding.UTF8, "application/json"));
            string raw = await response.Content.ReadAsStringAsync();
            string json = raw.TrimStart().StartsWith('{')
                ? raw
                : raw.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("data:")).Substring(5).Trim();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement result = doc.RootElement.GetProperty("result");
            bool isError = result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean();
            string text = string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
            return new McpResult(isError, text);
        }

        /// <summary>One JSON-RPC request of any method as its own stateless POST to <c>/mcp</c>; returns the whole response object.</summary>
        public async Task<JsonElement> PostAsync(string method, object? parameters = null)
        {
            string body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref this._id),
                method,
                @params = parameters ?? new { },
            });
            using HttpResponseMessage response = await this._client.PostAsync("/mcp", new StringContent(body, Encoding.UTF8, "application/json"));
            string raw = await response.Content.ReadAsStringAsync();
            string json = raw.TrimStart().StartsWith('{')
                ? raw
                : raw.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("data:")).Substring(5).Trim();
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        public async Task<string> SearchAsync(string query, string pattern)
        {
            return (await this.CallAsync(query, "search_symbols", new { pattern })).Text;
        }

        public async Task<string> HealthAsync(string query)
        {
            return await this._client.GetStringAsync("/health" + (query.Length == 0 ? "" : "?" + query));
        }

        /// <summary>The solution paths listed under <c>Solutions:</c> of a status text.</summary>
        public IReadOnlyList<string> SolutionsOf(string status)
        {
            return status.Split('\n').SkipWhile(l => !l.StartsWith("Solutions:"))
                .Skip(1)
                .Select(l => Regex.Match(l.TrimEnd('\r'), @"  (\S+)$"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .ToList();
        }

        public async ValueTask DisposeAsync()
        {
            this._client.Dispose();
            await this._factory.DisposeAsync();
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException("the server did not discover and index the fixture solutions");
        }

        private static Task<LoadedSolution> LoadFromDisk(string solutionPath, CancellationToken ct)
        {
            string dir = Path.GetDirectoryName(solutionPath)!;
            (string, string)[] documents = Directory.EnumerateFiles(dir, "*.cs").Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToArray();
            (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(dir, Path.GetFileNameWithoutExtension(solutionPath), documents);
            return Task.FromResult(new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, []));
        }

        private static void CreateRepositories(string root)
        {
            string r = Path.Combine(root, "r");
            WriteSolution(r, "Demo", ("A.cs", "namespace Acme.Orders;\npublic interface IWidget { void Run(); }\npublic class Shared : IWidget { public void Run() { } }\npublic class Caller { public void UseShared() { new Shared().Run(); } }\n"));
            Commit(r, "init", "-b", "main");
            AddWorktree(r, Path.Combine(root, "slots", "r", "1"), "slot1", "Only1.cs", "namespace Acme.Orders; public class OnlyOne { }");
            AddWorktree(r, Path.Combine(root, "slots", "r", "2"), "slot2", "Only2.cs", "namespace Acme.Orders; public class OnlyTwo { }");

            string q = Path.Combine(root, "q");
            WriteSolution(q, "Other", ("Other.cs", "namespace Acme.Billing; public class Other { }"));
            Commit(q, "init", "-b", "main");
            AddWorktree(q, Path.Combine(root, "slots", "q", "1"), "slot1", "OnlyQ.cs", "namespace Acme.Billing; public class OnlyQ { }");
        }

        private static void WriteSolution(string dir, string name, (string File, string Text) source)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".slnx"), $"<Solution><Project Path=\"{name}.csproj\" /></Solution>");
            File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(dir, source.File), source.Text);
        }

        private static void Commit(string dir, params string[] init)
        {
            Git(dir, init);
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "initial");
        }

        private static void AddWorktree(string repository, string path, string branch, string file, string text)
        {
            Git(repository, "worktree", "add", "-b", branch, path);
            File.WriteAllText(Path.Combine(path, file), text);
            Git(path, "add", file);
            Git(path, "commit", "-m", "add " + file);
        }

        private static void Git(string dir, params string[] args)
        {
            ProcessStartInfo psi = new("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args))
            {
                psi.ArgumentList.Add(a);
            }

            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} said: {err.Result}");
            }
        }
    }
}
