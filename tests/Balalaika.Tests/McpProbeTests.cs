using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Balalaika.Tests;

/// <summary>Capability probes a client sends after <c>initialize</c>: empty answers, nothing in the SDK's log.</summary>
public sealed partial class SlotPartTests
{
    [Fact]
    public async Task Initialize_advertises_prompts_and_resources()
    {
        await using PartServer server = await PartServer.StartAsync(withRepositories: false);

        JsonElement response = await server.PostAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "probe", version = "1" },
        });

        this.Output.WriteLine(response.GetProperty("result").GetProperty("capabilities").GetRawText());
        JsonElement capabilities = response.GetProperty("result").GetProperty("capabilities");
        capabilities.TryGetProperty("prompts", out _).Should().BeTrue();
        capabilities.TryGetProperty("resources", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("prompts/list", "prompts")]
    [InlineData("resources/list", "resources")]
    [InlineData("resources/templates/list", "resourceTemplates")]
    public async Task Probe_gets_an_empty_list_and_no_sdk_warning(string method, string member)
    {
        await using PartServer server = await PartServer.StartAsync(withRepositories: false);

        JsonElement response = await server.PostAsync(method);

        this.Output.WriteLine(response.GetRawText());
        response.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement list = response.GetProperty("result").GetProperty(member);
        list.ValueKind.Should().Be(JsonValueKind.Array);
        list.GetArrayLength().Should().Be(0);
        server.SdkLines.Where(l => l.Level >= LogLevel.Warning).Should().BeEmpty();
    }
}

/// <summary>Collects what the <c>ModelContextProtocol.*</c> loggers write; other categories are dropped.</summary>
internal sealed class SdkCaptureProvider(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("ModelContextProtocol", StringComparison.Ordinal) ? new Capture(lines) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " :: " + exception)));
    }
}
