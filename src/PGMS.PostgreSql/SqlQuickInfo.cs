namespace PGMS.PostgreSql;

/// <summary>Quick Info: the text of the tip and the range of the name it describes.</summary>
public sealed record SqlQuickInfo(int Start, int End, string Text);

/// <summary>One signature of Parameter Info, split so the current argument can be shown in bold.</summary>
public sealed record SqlSignature(string Prefix, IReadOnlyList<string> Arguments, string Suffix);

/// <summary>Parameter Info: the function overloads, the argument at the caret and the offset of the opening parenthesis.</summary>
public sealed record SqlParameterInfo(int OpenParenthesis, int ArgumentIndex, IReadOnlyList<SqlSignature> Signatures);

/// <summary>Quick Info (Ctrl+K, Ctrl+I) and Parameter Info (Ctrl+Shift+Space), PGMS-058, with the SSMS 22.11 texts piloted in M1079:
/// "view db.schema.name", "column name(type, not null)" and "NAME(arg,arg) RETURNS type" with the current argument in bold.</summary>
public static partial class SqlCompletionEngine
{
    public const int MaxOverloads = 20;

    /// <summary>Null on keywords, aliases, blanks, strings and comments, or when the name is not in the catalog.</summary>
    public static SqlQuickInfo? QuickInfo(string text, int offset, SqlCompletionCatalog catalog)
    {
        if (offset < 0 || offset > text.Length || text.Length > MaxText) return null;
        if (Tokens(text, offset) is not { } tokens) return null;
        int index = tokens.FindIndex(t => t.IsName && t.Start <= offset && offset < t.End);
        if (index < 0) index = tokens.FindIndex(t => t.IsName && t.End == offset);
        if (index < 0) return null;
        var token = tokens[index];
        if (token.Kind == TokenKind.Word && Reserved.Contains(token.Upper)) return null;
        bool Dot(int i) => i >= 0 && i < tokens.Count && tokens[i] is { Kind: TokenKind.Symbol, Text: "." };
        string? qualifier = Dot(index - 1) && index >= 2 && tokens[index - 2].IsName ? tokens[index - 2].Name : null;
        string? outer = qualifier is not null && Dot(index - 3) && index >= 4 && tokens[index - 4].IsName ? tokens[index - 4].Name : null;
        bool call = index + 1 < tokens.Count && tokens[index + 1] is { Kind: TokenKind.Symbol, Text: "(" };
        string name = token.Name;
        var range = (token.Start, token.End);
        SqlQuickInfo Info(string tip) => new(range.Start, range.End, tip);

        if (call)
        {
            var functions = Functions(catalog, qualifier, name).ToList();
            if (functions.Count == 0) return null;
            return Info(string.Join("\n", functions.Take(MaxOverloads).Select(f => $"function {catalog.Database}.{f.Schema}.{f.Name}({f.Arguments}) RETURNS {f.Result}")));
        }
        int first = tokens.FindLastIndex(Math.Max(index - 1, 0), t => t is { Kind: TokenKind.Symbol, Text: ";" }) + 1;
        int last = tokens.FindIndex(index, t => t is { Kind: TokenKind.Symbol, Text: ";" });
        var statement = tokens.Skip(first).Take((last < 0 ? tokens.Count : last) - first).ToList();
        var references = References(statement, -1);

        // schema.table.column or alias.column
        if (outer is not null && catalog.Columns.TryGetValue((outer, qualifier!), out var qualifiedColumns) && qualifiedColumns.FirstOrDefault(c => c.Name == name) is { } qc) return Info(Column(qc));
        if (qualifier is not null)
        {
            foreach (var reference in references.Where(r => r.Alias == qualifier || r.Alias is null && r.Name == qualifier))
                if (Resolve(catalog, reference.Schema, reference.Name) is { } s && catalog.Columns.TryGetValue((s, reference.Name), out var cols) && cols.FirstOrDefault(c => c.Name == name) is { } col) return Info(Column(col));
            if (catalog.Relations.FirstOrDefault(r => r.Schema == qualifier && r.Name == name) is { } qualifiedRelation) return Info(Relation(catalog, qualifiedRelation));
            if (Resolve(catalog, null, qualifier) is { } visible && catalog.Columns.TryGetValue((visible, qualifier), out var visibleColumns) && visibleColumns.FirstOrDefault(c => c.Name == name) is { } vc) return Info(Column(vc));
            return null;
        }
        // A bare name: a table in FROM/JOIN, the schema before a dot, a column of a table of the statement, or a visible table.
        if (Dot(index + 1) && catalog.Schemas.Contains(name) && !references.Any(r => r.Alias == name || r.Alias is null && r.Name == name)) return Info($"schema {catalog.Database}.{name}");
        if (references.Any(r => r.Start == token.Start) || Dot(index + 1) == false && IsAfterRelationKeyword(statement, token.Start))
        {
            if (catalog.SearchPath.Select(s => catalog.Relations.FirstOrDefault(r => r.Schema == s && r.Name == name)).FirstOrDefault(r => r is not null) is { } relation) return Info(Relation(catalog, relation));
        }
        if (references.Any(r => r.Alias == name)) return null; // aliases have no tip, as in SSMS
        foreach (var reference in references)
            if (Resolve(catalog, reference.Schema, reference.Name) is { } s && catalog.Columns.TryGetValue((s, reference.Name), out var cols) && cols.FirstOrDefault(c => c.Name == name) is { } col) return Info(Column(col));
        if (catalog.SearchPath.Select(s => catalog.Relations.FirstOrDefault(r => r.Schema == s && r.Name == name)).FirstOrDefault(r => r is not null) is { } visibleRelation) return Info(Relation(catalog, visibleRelation));
        if (catalog.Schemas.Contains(name)) return Info($"schema {catalog.Database}.{name}");
        return null;
    }

    private static bool IsAfterRelationKeyword(List<Token> statement, int start)
    {
        int i = statement.FindIndex(t => t.Start == start);
        while (i > 0 && statement[i - 1].Upper is "ONLY" or "LATERAL") i--;
        return i > 0 && statement[i - 1].Upper is "FROM" or "JOIN" or "UPDATE" or "INTO" or "TABLE";
    }

    private static string Column(SqlCatalogColumn column) => $"column {column.Name}({column.Type}, {(column.Nullable ? "null" : "not null")})";
    private static string Relation(SqlCompletionCatalog catalog, SqlCatalogRelation relation) => $"{(relation.IsView ? "view" : "table")} {catalog.Database}.{relation.Schema}.{relation.Name}";

    private static IEnumerable<SqlCatalogFunction> Functions(SqlCompletionCatalog catalog, string? schema, string name)
    {
        if (schema is not null) return catalog.Functions.Where(f => f.Schema == schema && f.Name == name);
        // The first schema of the search path that has the name, as PostgreSQL resolves it.
        foreach (var s in catalog.SearchPath)
        {
            var found = catalog.Functions.Where(f => f.Schema == s && f.Name == name).ToList();
            if (found.Count > 0) return found;
        }
        return [];
    }

    /// <summary>Null when the caret is not inside the parentheses of a function the catalog knows.</summary>
    public static SqlParameterInfo? ParameterInfo(string text, int caret, SqlCompletionCatalog catalog)
    {
        if (caret < 0 || caret > text.Length || text.Length > MaxText) return null;
        if (Tokens(text, caret) is not { } tokens) return null;
        var open = new Stack<(int Index, int Commas)>();
        for (int i = 0; i < tokens.Count && tokens[i].End <= caret; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Symbol) continue;
            if (t.Text == "(") open.Push((i, 0));
            else if (t.Text == ")" && open.Count > 0) open.Pop();
            else if (t.Text == "," && open.Count > 0) { var top = open.Pop(); open.Push((top.Index, top.Commas + 1)); }
            else if (t.Text == ";") open.Clear();
        }
        while (open.Count > 0)
        {
            var (paren, commas) = open.Pop();
            if (paren == 0 || !tokens[paren - 1].IsName) continue;
            var nameToken = tokens[paren - 1];
            string? schema = paren >= 3 && tokens[paren - 2] is { Kind: TokenKind.Symbol, Text: "." } && tokens[paren - 3].IsName ? tokens[paren - 3].Name : null;
            var functions = Functions(catalog, schema, nameToken.Name).Take(MaxOverloads).ToList();
            if (functions.Count == 0) continue;
            var signatures = functions.Select(f => new SqlSignature(f.Name.ToUpperInvariant() + "(", SplitArguments(f.Arguments), ") RETURNS " + f.Result)).ToList();
            // Overloads with enough arguments for the caret come first.
            signatures = [.. signatures.OrderBy(s => s.Arguments.Count > commas || s.Arguments.Count > 0 && s.Arguments[^1].StartsWith("VARIADIC ", StringComparison.Ordinal) ? 0 : 1)];
            return new SqlParameterInfo(tokens[paren].Start, commas, signatures);
        }
        return null;
    }

    /// <summary>Splits pg_get_function_arguments at the commas outside parentheses and quotes (defaults may hold commas).</summary>
    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        int depth = 0, start = 0; bool quoted = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(arguments[start..i].Trim()); start = i + 1; }
        }
        if (arguments.Trim().Length > 0) parts.Add(arguments[start..].Trim());
        return parts;
    }
}
