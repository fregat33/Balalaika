using System.Security.Cryptography;
using System.Text;

namespace Balalaika.Index;

/// <summary>
/// Hashes document content. Always hash the decoded text (not raw bytes) so that a file read from disk and a
/// Roslyn SourceText produce the same hash regardless of BOM or encoding.
/// </summary>
public static class ContentHasher
{
    public static string HashText(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
