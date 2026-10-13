using System.Text;

namespace Balalaika.Index;

/// <summary>
/// How the declaration enclosing a reference is printed: <c>Type.Member</c>, no namespace, no parameter list. The live
/// path (Roslyn symbols) and the index path (symbol rows) both go through this class so the same source prints the
/// same words either way.
/// </summary>
public static class EnclosingDisplay
{
    /// <summary>Stored as <c>from_doc_id</c> for references inside top-level statements, which declare no symbol the index records.</summary>
    public const string TopLevel = "<top-level>";

    /// <summary>
    /// <c>Containing.Name</c> for a member or nested type, <c>Name</c> for a top-level type. Constructor names lose
    /// their leading dot (<c>.ctor</c> becomes <c>ctor</c>) so the result stays one dotted path.
    /// </summary>
    public static string Format(string? containingTypeName, string name)
    {
        string own = name.TrimStart('.');
        return string.IsNullOrEmpty(containingTypeName) ? own : containingTypeName + "." + own;
    }

    /// <summary>Number of parameters a method doc id declares: commas at brace depth zero inside its parentheses, plus one. Zero for ids without a parameter list.</summary>
    public static int ParameterCount(string docId)
    {
        int open = docId.IndexOf('(');
        if (open < 0)
        {
            return 0;
        }

        int count = 1;
        int depth = 0;
        for (int i = open + 1; i < docId.Length; i++)
        {
            switch (docId[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    count++;
                    break;
                case ')' when depth == 0:
                    return count;
            }
        }

        return count;
    }

    /// <summary>
    /// Display name per doc id, made distinct wherever two doc ids would otherwise print the same name. Names that
    /// collide across different containing types (two <c>InvoicesController</c> in different namespaces, or
    /// members of them) are printed namespace-qualified from the doc id; names that still collide after that are
    /// overloads of one method and get <c>(n params)</c>. Names that occur once stay bare.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Disambiguate(IEnumerable<(string DocId, string Display)> enclosings)
    {
        Dictionary<string, string> byDocId = new(StringComparer.Ordinal);
        foreach ((string docId, string display) in enclosings)
        {
            byDocId.TryAdd(docId, display);
        }

        // Pass 1: a display shared by doc ids of different containing types is qualified with its namespace.
        Dictionary<string, HashSet<string>> typesPerDisplay = new(StringComparer.Ordinal);
        foreach ((string docId, string display) in byDocId)
        {
            if (!typesPerDisplay.TryGetValue(display, out HashSet<string>? types))
            {
                types = new HashSet<string>(StringComparer.Ordinal);
                typesPerDisplay[display] = types;
            }

            types.Add(ContainingTypeOf(docId));
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string docId, string display) in byDocId)
        {
            result[docId] = typesPerDisplay[display].Count > 1 ? QualifiedNameOf(docId) : display;
        }

        // Pass 2: what still collides is overloads of one method.
        Dictionary<string, int> owners = new(StringComparer.Ordinal);
        foreach (string name in result.Values)
        {
            owners[name] = owners.GetValueOrDefault(name) + 1;
        }

        foreach (string docId in byDocId.Keys)
        {
            if (owners[result[docId]] > 1)
            {
                result[docId] = $"{result[docId]}({ParameterCount(docId)} params)";
            }
        }

        return result;
    }

    /// <summary>The doc id without its prefix, parameter list and generic arity: <c>M:Ns.Foo`1.#ctor(System.Int32)</c> gives <c>Ns.Foo.ctor</c>.</summary>
    public static string QualifiedNameOf(string docId)
    {
        string name = docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;
        int paren = name.IndexOf('(');
        if (paren > 0)
        {
            name = name[..paren];
        }

        StringBuilder sb = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '`')
            {
                while (i + 1 < name.Length && (name[i + 1] == '`' || char.IsDigit(name[i + 1])))
                {
                    i++;
                }

                continue;
            }

            if (name[i] != '#')
            {
                sb.Append(name[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>The qualified type a doc id declares or belongs to: the id itself for <c>T:</c>, the part before the member name otherwise.</summary>
    private static string ContainingTypeOf(string docId)
    {
        string name = QualifiedNameOf(docId);
        if (docId.StartsWith("T:", StringComparison.Ordinal) || docId == TopLevel)
        {
            return name;
        }

        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
