using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Balalaika.Server;
using Balalaika.Workspace;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace Balalaika.Tests;

/// <summary>The per-call line of <c>Balalaika.Server.ToolCalls</c>: its pieces, and the real server writing it.</summary>
public sealed class ToolCallLogTests
{
    private static IReadOnlyDictionary<string, JsonElement> Args(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void FormatArguments_QuotesStrings_AndKeepsNumbersAndNullRaw_InTheOrderReceived()
    {
        ToolCallLog.FormatArguments(Args("""{"pattern":"Foo","limit":20,"scope":null}""")).Should().Be("pattern=\"Foo\" limit=20 scope=null");
    }

    [Fact]
    public void FormatArguments_CutsALongStringTo120CharactersAndAnEllipsis()
    {
        string formatted = ToolCallLog.FormatArguments(Args($$"""{"pattern":"{{new string('x', 200)}}"}"""));

        formatted.Should().Be($"pattern=\"{new string('x', 120)}…\"");
    }

    [Fact]
    public void FormatArguments_ArraysAndObjects_AreCompactJsonWhateverTheClientWhitespace()
    {
        string json = "{\"scope\": {\n  \"kind\": [ \"creation\",\n \"base\" ]\n }}";

        ToolCallLog.FormatArguments(Args(json)).Should().Be("scope={\"kind\":[\"creation\",\"base\"]}");
    }

    [Fact]
    public void FormatArguments_EscapesQuotesBackslashesAndLineBreaks_InStrings()
    {
        string formatted = ToolCallLog.FormatArguments(Args("""{"pattern":"say \"hi\"\\there\nnow\r"}"""));

        formatted.Should().Be("pattern=\"say \\\"hi\\\"\\\\there\\nnow\\r\"");
        formatted.Should().NotContainAny("\n", "\r");
    }

    [Fact]
    public void Client_EscapesQuotesInTheName()
    {
        ToolCallLog.Client(new Implementation { Name = "cli \"x\"", Version = "1" }).Should().Be("\"cli \\\"x\\\" 1\"");
    }

    [Fact]
    public void Caller_IsCutFirstAndEscapedAfter()
    {
        DefaultHttpContext http = new();
        http.Request.QueryString = new QueryString("?caller=" + Uri.EscapeDataString(new string('"', 150)));

        ToolCallLog.Caller(http).Should().Be("\"" + string.Concat(Enumerable.Repeat("\\\"", 100)) + "…\"");
    }

    [Fact]
    public void Describe_CutsTheErrorTo200CharactersIncludingTheEllipsis()
    {
        CallToolResult result = new() { IsError = true, Content = [new TextContentBlock { Text = new string('e', 500) + "\nsecond" }] };

        ToolCallRecord record = ToolCallLog.Describe("status", null, result, null, null, null, 1);

        record.Error.Should().HaveLength(200).And.EndWith("…");
    }

    [Fact]
    public void FormatArguments_WithoutArguments_IsEmpty()
    {
        ToolCallLog.FormatArguments(null).Should().BeEmpty();
        ToolCallLog.FormatArguments(new Dictionary<string, JsonElement>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Scope_WithoutBegin_IsANoOp()
    {
        await Task.Run(() =>
        {
            ToolCallScope.Add(3);
            ToolCallScope.Report(0);

            ToolCallScope.Current.Should().BeNull();
        });
    }

    [Fact]
    public async Task Scope_AddsUp_ReportsZero_AndStaysNullWithoutAReport()
    {
        await Task.Run(() =>
        {
            ToolCallScope.Counter counter = ToolCallScope.Begin();
            counter.Found.Should().BeNull();
            ToolCallScope.Add(3);
            ToolCallScope.Add(4);
            counter.Found.Should().Be(7);
            ToolCallScope.Begin().Should().BeSameAs(counter);
        });

        await Task.Run(() =>
        {
            ToolCallScope.Counter counter = ToolCallScope.Begin();
            ToolCallScope.Report(0);
            counter.Found.Should().Be(0);
        });
    }
}

public sealed partial class SlotPartTests
{
    private static readonly Regex TotalLine = new(@"Total: (\d+) references", RegexOptions.Compiled);

    public SlotPartTests(ITestOutputHelper output)
    {
        this.Output = output;
    }

    private static (LogLevel Level, string Message) LastLineOf(PartServer server, string tool) =>
        server.ToolCallLines.Last(l => l.Message.StartsWith(tool + " ", StringComparison.Ordinal));

    [GitFact]
    public async Task ToolCallLine_CarriesTheFoundCount_ZeroForNoHits_AndDashForToolsWithoutAList()
    {
        await using PartServer server = await PartServer.StartAsync();
        string query = "part=" + Uri.EscapeDataString(server.R1);
        (await server.CallAsync(query, "load_solution", new { solution = server.R1Solution })).Text.Should().Contain("projects loaded");

        string references = (await server.CallAsync(query, "find_references", new { symbol = "Acme.Orders.Shared.Run" })).Text;
        int total = int.Parse(TotalLine.Match(references).Groups[1].Value);
        total.Should().BeGreaterThan(0, references);
        (await server.CallAsync(query, "search_symbols", new { pattern = "ZzzNoSuchSymbolAnywhere" })).Text.Should().Contain("No symbols match");
        await server.CallAsync(query, "status");

        (LogLevel level, string line) = LastLineOf(server, "find_references");
        level.Should().Be(LogLevel.Information);
        line.Should().StartWith("find_references symbol=\"Acme.Orders.Shared.Run\" found=" + total + " client=");
        line.Should().Contain($"part=\"{server.R1.Replace("\\", "\\\\")}\"").And.Contain("caller=-").And.MatchRegex(@" \d+ms$");
        LastLineOf(server, "search_symbols").Message.Should().Contain("pattern=\"ZzzNoSuchSymbolAnywhere\" found=0 ");
        LastLineOf(server, "status").Message.Should().Contain(" found=- ");

        Output?.WriteLine(line);
        Output?.WriteLine(LastLineOf(server, "search_symbols").Message);
    }

    [GitFact]
    public async Task ToolCallLine_NamesTheCaller_WhenGiven_AndDashOtherwise_AndTheResultIsTheSame()
    {
        await using PartServer server = await PartServer.StartAsync(withRepositories: false);

        McpResult with = await server.CallAsync("caller=worker%2Fsonnet%2Ft1", "status");
        string withLine = LastLineOf(server, "status").Message;
        McpResult without = await server.CallAsync("", "status");
        string withoutLine = LastLineOf(server, "status").Message;

        withLine.Should().Contain("caller=\"worker/sonnet/t1\"");
        withoutLine.Should().Contain("caller=-");
        with.IsError.Should().BeFalse();
        without.IsError.Should().BeFalse();
        with.Text.Should().NotContain("worker/sonnet");
        Output?.WriteLine(withLine);
        Output?.WriteLine(withoutLine);
    }

    [GitFact]
    public async Task ToolCallLine_EscapesAForgedCaller_SoNoFieldIsForged()
    {
        await using PartServer server = await PartServer.StartAsync(withRepositories: false);

        await server.CallAsync("caller=" + Uri.EscapeDataString("a\" part=\"x"), "status");
        string line = LastLineOf(server, "status").Message;

        line.Should().Contain("caller=\"a\\\" part=\\\"x\"").And.Contain("part=-");
        line.Should().MatchRegex(@"^status\s+found=- client=\S+ caller=""(?:[^""\\]|\\.)*"" part=- \d+ms$");
        Output?.WriteLine(line);
    }

    private ITestOutputHelper Output { get; }
}

/// <summary>Collects what the <c>Balalaika.Server.ToolCalls</c> logger writes; other categories are dropped.</summary>
internal sealed class ToolCallCaptureProvider(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        categoryName == "Balalaika.Server.ToolCalls" ? new Capture(lines) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
