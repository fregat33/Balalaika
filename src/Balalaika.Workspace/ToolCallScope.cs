namespace Balalaika.Workspace;

/// <summary>
/// Ambient per-call counter of what a tool call found. The server begins a scope before the tool runs and reads it
/// afterwards; outside a scope every member is a no-op, so direct callers of <see cref="QueryService"/> are unaffected.
/// </summary>
public static class ToolCallScope
{
    public sealed class Counter
    {
        /// <summary>The number of results found, or null when the tool reported nothing.</summary>
        public int? Found { get; private set; }

        internal void Add(int count) => this.Found = (this.Found ?? 0) + count;

        internal void Report(int count) => this.Found = count;
    }

    private static readonly AsyncLocal<Counter?> Ambient = new();

    public static Counter? Current => Ambient.Value;

    /// <summary>Starts a scope for the current async flow, or returns the one already begun.</summary>
    public static Counter Begin() => Ambient.Value ??= new Counter();

    /// <summary>Adds to the found count; does nothing without a scope.</summary>
    public static void Add(int count) => Ambient.Value?.Add(count);

    /// <summary>Sets the found count (an empty result is reported as 0); does nothing without a scope.</summary>
    public static void Report(int count) => Ambient.Value?.Report(count);
}
