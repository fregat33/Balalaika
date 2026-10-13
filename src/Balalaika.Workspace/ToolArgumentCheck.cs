using System.Text.Json;

namespace Balalaika.Workspace;

/// <summary>
/// Explains a tool call whose arguments do not fit the tool's input schema. The MCP SDK rejects such a call before the
/// tool method runs and answers with a bare "An error occurred invoking 'tool'", which gives a calling agent nothing to
/// correct. This produces the message the agent needs instead: which required argument is missing, what it sent, and
/// what the tool accepts. When the mistake is unambiguous (one required argument missing, one unknown argument sent,
/// such as <c>query</c> for <c>pattern</c>) <see cref="Remap"/> names the correction so the call can run as intended.
/// </summary>
public static class ToolArgumentCheck
{
    /// <summary>An unknown argument name that stands in for the one missing required argument.</summary>
    public sealed record ArgumentRemap(string From, string To);

    /// <summary>
    /// The one unknown argument to rename when it is the only unknown one and exactly one required argument is missing;
    /// null when the arguments fit the schema or the correction would be a guess.
    /// </summary>
    public static ArgumentRemap? Remap(JsonElement inputSchema, IEnumerable<string> providedNames)
    {
        HashSet<string> provided = new(providedNames, StringComparer.Ordinal);
        List<string> required = RequiredNames(inputSchema);
        List<string> accepted = AcceptedNames(inputSchema);
        List<string> missing = required.Where(r => !provided.Contains(r)).ToList();
        List<string> unknown = provided.Where(p => !accepted.Contains(p)).ToList();
        return missing.Count == 1 && unknown.Count == 1 ? new ArgumentRemap(unknown[0], missing[0]) : null;
    }

    /// <summary>Null when every required argument is present; otherwise the error text to return to the caller.</summary>
    public static string? Describe(string toolName, JsonElement inputSchema, IEnumerable<string> providedNames)
    {
        HashSet<string> provided = new(providedNames, StringComparer.Ordinal);
        List<string> required = RequiredNames(inputSchema);
        List<string> missing = required.Where(r => !provided.Contains(r)).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        List<string> accepted = AcceptedNames(inputSchema);
        List<string> unknown = provided.Where(p => !accepted.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        string message = $"{toolName}: missing required argument {Quote(missing)}.";
        if (unknown.Count > 0)
        {
            message += $" Not accepted: {Quote(unknown)}; the value probably belongs in {Quote(missing)}.";
        }

        if (accepted.Count > 0)
        {
            message += $" Accepted arguments: {string.Join(", ", accepted.Select(a => required.Contains(a) ? a + " (required)" : a))}.";
        }

        return message;
    }

    private static List<string> RequiredNames(JsonElement inputSchema)
    {
        List<string> required = [];
        if (inputSchema.ValueKind == JsonValueKind.Object && inputSchema.TryGetProperty("required", out JsonElement requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in requiredElement.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } s)
                {
                    required.Add(s);
                }
            }
        }

        return required;
    }

    private static List<string> AcceptedNames(JsonElement inputSchema)
    {
        List<string> accepted = [];
        if (inputSchema.ValueKind == JsonValueKind.Object && inputSchema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                accepted.Add(property.Name);
            }
        }

        return accepted;
    }

    private static string Quote(IEnumerable<string> names)
    {
        return string.Join(", ", names.Select(n => "'" + n + "'"));
    }
}
