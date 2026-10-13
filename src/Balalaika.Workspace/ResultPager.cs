using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Balalaika.Workspace;

public sealed record ResultPage(string Label, IReadOnlyList<string> Lines, int Remaining, string? NextCursor);

/// <summary>
/// Keeps the lines a tool did not print because of its <c>limit</c>, so the agent can fetch them with <c>next_page</c>
/// instead of re-running the query with a larger limit. Entries are immutable; taking a page yields a new cursor for
/// the rest. The store is bounded by count and age and lives only in memory.
/// </summary>
public sealed class ResultPager(int capacity = 128, TimeSpan? ttl = null)
{
    private sealed record Entry(string Label, IReadOnlyList<string> Lines, int Offset, int PageSize, DateTime CreatedUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(30);

    public int Count => this._entries.Count;

    /// <summary>
    /// Appends up to <paramref name="pageSize"/> lines to <paramref name="sb"/>. When more remain, stores them and
    /// appends a trailer naming the cursor. Returns the number of lines that were not printed.
    /// </summary>
    public int AppendPage(StringBuilder sb, IReadOnlyList<string> lines, int pageSize, string label)
    {
        pageSize = Math.Max(1, pageSize);
        int shown = Math.Min(pageSize, lines.Count);
        for (int i = 0; i < shown; i++)
        {
            sb.AppendLine(lines[i]);
        }

        int remaining = lines.Count - shown;
        if (remaining > 0)
        {
            string cursor = this.Store(label, lines, shown, pageSize);
            sb.Append("  ... ").Append(remaining).Append(" more (next_page cursor=").Append(cursor).Append(')').AppendLine();
        }

        return remaining;
    }

    /// <summary>
    /// Returns the page behind a cursor, or null when the cursor is unknown or expired. <paramref name="pageSize"/>
    /// overrides the page size of the original call for this page and the cursors that follow it.
    /// </summary>
    public ResultPage? Take(string cursor, int? pageSize = null)
    {
        if (string.IsNullOrWhiteSpace(cursor) || !this._entries.TryGetValue(cursor.Trim(), out Entry? entry))
        {
            return null;
        }

        int size = pageSize is > 0 ? pageSize.Value : entry.PageSize;
        int end = Math.Min(entry.Lines.Count, entry.Offset + size);
        List<string> page = [];
        for (int i = entry.Offset; i < end; i++)
        {
            page.Add(entry.Lines[i]);
        }

        int remaining = entry.Lines.Count - end;
        string? next = remaining > 0 ? this.Store(entry.Label, entry.Lines, end, size) : null;
        return new ResultPage(entry.Label, page, remaining, next);
    }

    private string Store(string label, IReadOnlyList<string> lines, int offset, int pageSize)
    {
        this.Evict();
        string cursor = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        this._entries[cursor] = new Entry(label, lines, offset, pageSize, DateTime.UtcNow);
        return cursor;
    }

    private void Evict()
    {
        DateTime now = DateTime.UtcNow;
        foreach ((string key, Entry entry) in this._entries)
        {
            if (now - entry.CreatedUtc > this._ttl)
            {
                this._entries.TryRemove(key, out _);
            }
        }

        while (this._entries.Count >= capacity)
        {
            KeyValuePair<string, Entry> oldest = this._entries.MinBy(kv => kv.Value.CreatedUtc);
            if (oldest.Key is null || !this._entries.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }
    }
}
