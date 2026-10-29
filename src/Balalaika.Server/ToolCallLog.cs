using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Balalaika.Server;

/// <summary>What one tool call logs: see <see cref="ToolCallLog.Write"/> for the line.</summary>
public sealed record ToolCallRecord(
    string Tool,
    string Arguments,
    int? Found,
    string Client,
    string Caller,
    string Part,
    long ElapsedMs,
    string? Error);

/// <summary>Formats the single per-call log line of the <c>Balalaika.Server.ToolCalls</c> logger.</summary>
public static class ToolCallLog
{
    public const string CallerParameter = "caller";

    private const int ArgumentLimit = 120;
    private const int CallerLimit = 100;
    private const int ErrorLimit = 200;
    private const string None = "-";

    public static string FormatArguments(IEnumerable<KeyValuePair<string, JsonElement>>? arguments) =>
        arguments is null ? string.Empty : string.Join(' ', arguments.Select(a => $"{FormatKey(a.Key)}={FormatValue(a.Value)}"));

    /// <summary>A plain identifier (<c>[A-Za-z0-9_]+</c>, at most 120 characters) as is; any other key, even an empty one, cut and quoted like a value.</summary>
    public static string FormatKey(string key) =>
        key.Length is > 0 and <= ArgumentLimit && key.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? key
            : Quote(Cut(key, ArgumentLimit));

    /// <summary><c>"name version"</c> in quotes, or <c>-</c> when the client did not identify itself.</summary>
    public static string Client(Implementation? client) =>
        client is null || string.IsNullOrWhiteSpace(client.Name)
            ? None
            : Quote($"{client.Name} {client.Version}".Trim());

    /// <summary>The <c>caller</c> query parameter (first value, trimmed, cut to 100 characters) in quotes, or <c>-</c>.</summary>
    public static string Caller(HttpContext? http)
    {
        string? value = http?.Request.Query[CallerParameter].FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(value) ? None : Quote(Cut(value, CallerLimit));
    }

    /// <summary>The <c>part</c> values joined by <c>;</c> in quotes, or <c>-</c>.</summary>
    public static string Parts(HttpContext? http)
    {
        string[] parts = http is null
            ? []
            : http.Request.Query[RequestView.PartParameter].Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToArray();
        return parts.Length == 0 ? None : Quote(string.Join(';', parts));
    }

    public static ToolCallRecord Describe(
        string tool,
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
        CallToolResult? result,
        Implementation? client,
        HttpContext? http,
        int? found,
        long elapsedMs)
    {
        string? error = null;
        if (result?.IsError == true)
        {
            string text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
            string firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
            error = Cut(firstLine, ErrorLimit - 1);
        }

        return new ToolCallRecord(tool, FormatArguments(arguments), found, Client(client), Caller(http), Parts(http), elapsedMs, error);
    }

    /// <summary>Writes the line: Information, or Warning with <c>error=</c> appended when the call failed.</summary>
    public static void Write(Microsoft.Extensions.Logging.ILogger logger, ToolCallRecord r)
    {
        object found = r.Found is { } n ? n : None;
        if (r.Error is null)
        {
            logger.LogInformation("{Tool} {Arguments} found={Found} client={Client} caller={Caller} part={Part} {ElapsedMs}ms",
                r.Tool, r.Arguments, found, r.Client, r.Caller, r.Part, r.ElapsedMs);
        }
        else
        {
            logger.LogWarning("{Tool} {Arguments} found={Found} client={Client} caller={Caller} part={Part} {ElapsedMs}ms error={Error}",
                r.Tool, r.Arguments, found, r.Client, r.Caller, r.Part, r.ElapsedMs, r.Error);
        }
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Quote(Cut(value.GetString() ?? string.Empty, ArgumentLimit)),
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => Cut(JsonSerializer.Serialize(value), ArgumentLimit),
    };

    private static string Quote(string text) =>
        $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")}\"";

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}
