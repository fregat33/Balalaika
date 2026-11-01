using System.Text.Json;
using Stopwatch = System.Diagnostics.Stopwatch;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Balalaika.Index;
using Balalaika.Server;
using Balalaika.Workspace;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u4} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "balalaika.json"), optional: true, reloadOnChange: false)
    .AddJsonFile("balalaika.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "balalaika.local.json"), optional: true, reloadOnChange: false)
    .AddJsonFile("balalaika.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("BALALAIKA_");

builder.Services.Configure<BalalaikaOptions>(builder.Configuration.GetSection("Balalaika"));

builder.Host.UseSerilog((context, services, configuration) =>
{
    BalalaikaOptions options = services.GetRequiredService<IOptions<BalalaikaOptions>>().Value;
    string logDirectory = options.ResolveLogDirectory();
    Directory.CreateDirectory(logDirectory);

    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
        .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u4} {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            new CompactJsonFormatter(),
            Path.Combine(logDirectory, "balalaika-.clef"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: options.LogRetainedDays,
            shared: true);
});
builder.Services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IOptions<BalalaikaOptions>>().Value.ResolveIndexPath()));
builder.Services.AddSingleton(sp => new SourceFilter(sp.GetRequiredService<IOptions<BalalaikaOptions>>().Value.IgnoredFolders));
builder.Services.AddSingleton<IndexJobQueue>();
builder.Services.AddSingleton<IndexActivity>();
builder.Services.AddSingleton<WarmUpTracker>();
builder.Services.AddSingleton<PendingRefreshes>();
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<SolutionIndexer>();
builder.Services.AddSingleton<SymbolResolver>();
builder.Services.AddSingleton<ResultPager>();
builder.Services.AddSingleton<IBaseContentProvider, GitRepository>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<IndexingHostedService>();

// Assigned once the host is built; the filter below runs only after that.
Microsoft.Extensions.Logging.ILogger? toolCallLogger = null;

builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "balalaika", Version = ServerVersion.Current })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<BalalaikaTools>()
    // Clients probe for prompts and resources after initialize; the server has none, so say so instead of
    // letting the SDK answer with an exception and two Warnings per probe.
    .WithListPromptsHandler((_, _) => ValueTask.FromResult(new ListPromptsResult()))
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult()))
    .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(new ListResourceTemplatesResult()))
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
    {
        // The view of this call: the solutions the request's 'part' parameters leave visible. It follows the async flow
        // into the tool and everything it awaits; nothing else holds it.
        if (RequestView.Apply(request.Services, request.Services!.GetRequiredService<WorkspaceManager>()) is { } viewError)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = viewError }] };
        }

        // The SDK rejects a call with a missing required argument before the tool runs and tells the client only
        // "An error occurred invoking 'tool'". When the mistake is unambiguous (one unknown name sent, one required
        // name missing, typically 'query' for 'pattern' or 'file' for 'target') rename the argument and run the call;
        // otherwise say which argument is missing and which names the tool accepts. Both are logged: the SDK's own
        // "completed. IsError = true" line does not say why.
        if (request.MatchedPrimitive is McpServerTool tool && request.Params is { } call)
        {
            IReadOnlyCollection<string> received = call.Arguments?.Keys.ToList() ?? [];
            if (ToolArgumentCheck.Remap(tool.ProtocolTool.InputSchema, received) is { } remap && call.Arguments is not null)
            {
                toolCallLogger?.LogInformation("Accepted {Tool} argument '{From}' as '{To}'", call.Name, remap.From, remap.To);
                Dictionary<string, JsonElement> renamed = call.Arguments.ToDictionary(a => a.Key == remap.From ? remap.To : a.Key, a => a.Value, StringComparer.Ordinal);
                call.Arguments = renamed;
            }
            else if (ToolArgumentCheck.Describe(call.Name, tool.ProtocolTool.InputSchema, received) is { } problem)
            {
                toolCallLogger?.LogWarning("Rejected {Tool} call with arguments [{Arguments}]: {Problem}", call.Name, string.Join(", ", received.Select(ToolCallLog.FormatKey)), problem);
                CallToolResult rejected = new() { IsError = true, Content = [new TextContentBlock { Text = problem }] };
                LogToolCall(request, call, rejected, 0);
                return rejected;
            }
        }

        // One line per call, written after it ran. The scope collects the found count from the pager and the tools.
        ToolCallScope.Counter scope = ToolCallScope.Begin();
        long started = Stopwatch.GetTimestamp();
        CallToolResult result = await next(request, cancellationToken).ConfigureAwait(false);
        LogToolCall(request, request.Params, result, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, scope.Found);
        return result;
    }));

int port = builder.Configuration.GetValue<int?>("Balalaika:Port") ?? 5301;
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));

WebApplication app = builder.Build();
toolCallLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Balalaika.Server.ToolCalls");

app.MapMcp("/mcp");
app.MapGet("/health", (HttpContext http, WorkspaceManager workspaces, QueryService query) =>
    RequestView.Apply(http, workspaces) is { } viewError ? Results.Text(viewError, statusCode: 400) : Results.Text(query.Status()));

app.Logger.LogInformation(
    "balalaika listening on http://localhost:{Port}/mcp, logging to {LogDirectory}",
    port,
    app.Services.GetRequiredService<IOptions<BalalaikaOptions>>().Value.ResolveLogDirectory());

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "balalaika terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

void LogToolCall(RequestContext<CallToolRequestParams> request, CallToolRequestParams? call, CallToolResult result, long elapsedMs, int? found = null)
{
    if (toolCallLogger is null || call is null)
    {
        return;
    }

    try
    {
        HttpContext? http = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        ToolCallLog.Write(toolCallLogger, ToolCallLog.Describe(call.Name, call.Arguments, result, request.Server?.ClientInfo, http, found, elapsedMs));
    }
    catch (Exception ex)
    {
        toolCallLogger.LogWarning(ex, "Could not log the call of {Tool}", call.Name);
    }
}

/// <summary>Lets the test project start the real server in memory.</summary>
public partial class Program;
