using System.Text.RegularExpressions;
using Npgsql;

namespace PGMS.PostgreSql;

public enum SqlCompletionKind { Keyword, Schema, Table, View, Column, Function, Join }

/// <summary>One entry of the IntelliSense list; Text is what the editor inserts, Description the tip beside the list, and Filter what typing is matched against when that is not Text.</summary>
public sealed record SqlCompletionItem(string Text, SqlCompletionKind Kind, string Description, string? Filter = null);

/// <summary>What to offer at a position: the text from ReplaceStart to the caret is replaced by the chosen item.</summary>
public sealed record SqlCompletionResult(int ReplaceStart, string Prefix, IReadOnlyList<SqlCompletionItem> Items);

public sealed record SqlCatalogRelation(string Schema, string Name, bool IsView);
public sealed record SqlCatalogColumn(string Name, string Type, bool Nullable);
/// <summary>ReturnsRows: a set-returning function, the kind that belongs in FROM.</summary>
public sealed record SqlCatalogFunction(string Schema, string Name, string Arguments, string Result, bool ReturnsRows = false);

/// <summary>Columns and TargetColumns are paired by position; a composite key has several.</summary>
public sealed record SqlCatalogForeignKey(string Name, string Schema, string Table, IReadOnlyList<string> Columns, string TargetSchema, string TargetTable, IReadOnlyList<string> TargetColumns);

/// <summary>Names IntelliSense can offer, read once per connection and refreshed on request (Refresh Local Cache).</summary>
public sealed class SqlCompletionCatalog
{
    public const int MaxRelations = 20000, MaxColumns = 200000, MaxFunctions = 20000, MaxForeignKeys = 50000;
    public string Database { get; init; } = "";
    public IReadOnlyList<string> SearchPath { get; init; } = [];
    public IReadOnlyList<string> Schemas { get; init; } = [];
    public IReadOnlyList<SqlCatalogRelation> Relations { get; init; } = [];
    public IReadOnlyDictionary<(string Schema, string Relation), IReadOnlyList<SqlCatalogColumn>> Columns { get; init; } = new Dictionary<(string, string), IReadOnlyList<SqlCatalogColumn>>();
    public IReadOnlyList<SqlCatalogFunction> Functions { get; init; } = [];
    public IReadOnlyList<SqlCatalogForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Reads on a connection of its own: the tab's session may be running a query or be in a failed transaction.</summary>
    public static async Task<SqlCompletionCatalog> LoadAsync(string connectionString, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        async Task Each(string sql, Action<NpgsqlDataReader> read)
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) read(reader);
        }
        const string Visible = "n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp\\_%'";
        var searchPath = new List<string>(); var schemas = new List<string>(); var relations = new List<SqlCatalogRelation>(); var functions = new List<SqlCatalogFunction>(); var foreignKeys = new List<SqlCatalogForeignKey>();
        var columns = new Dictionary<(string, string), List<SqlCatalogColumn>>();
        await Each("SELECT unnest(pg_catalog.current_schemas(true))::text", r => searchPath.Add(r.GetString(0))).ConfigureAwait(false);
        await Each($"SELECT n.nspname::text FROM pg_catalog.pg_namespace n WHERE {Visible} ORDER BY 1", r => schemas.Add(r.GetString(0))).ConfigureAwait(false);
        await Each($"SELECT n.nspname::text, c.relname::text, c.relkind IN ('v','m') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r','p','v','m','f') AND {Visible} ORDER BY n.nspname = 'pg_catalog', 1, 2 LIMIT {MaxRelations}",
            r => relations.Add(new(r.GetString(0), r.GetString(1), r.GetBoolean(2)))).ConfigureAwait(false);
        await Each($"SELECT n.nspname::text, c.relname::text, a.attname::text, pg_catalog.format_type(a.atttypid, a.atttypmod), NOT a.attnotnull FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid = a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r','p','v','m','f') AND {Visible} ORDER BY n.nspname = 'pg_catalog', 1, 2, a.attnum LIMIT {MaxColumns}",
            r =>
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!columns.TryGetValue(key, out var list)) columns[key] = list = [];
                list.Add(new(r.GetString(2), r.GetString(3), r.GetBoolean(4)));
            }).ConfigureAwait(false);
        await Each($"SELECT n.nspname::text, p.proname::text, pg_catalog.pg_get_function_arguments(p.oid), COALESCE(pg_catalog.pg_get_function_result(p.oid), ''), p.prokind = 'f' AND p.proretset FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE p.prokind IN ('f','a','w') AND {Visible} ORDER BY n.nspname = 'pg_catalog', 1, 2 LIMIT {MaxFunctions}",
            r => functions.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4)))).ConfigureAwait(false);
        static string ColumnsOf(string relation, string keys) => $"ARRAY(SELECT a.attname::text FROM unnest(con.{keys}) WITH ORDINALITY k(attnum, position) JOIN pg_catalog.pg_attribute a ON a.attrelid = con.{relation} AND a.attnum = k.attnum ORDER BY k.position)";
        await Each($"SELECT con.conname::text, n.nspname::text, c.relname::text, {ColumnsOf("conrelid", "conkey")}, tn.nspname::text, t.relname::text, {ColumnsOf("confrelid", "confkey")} FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace JOIN pg_catalog.pg_class t ON t.oid = con.confrelid JOIN pg_catalog.pg_namespace tn ON tn.oid = t.relnamespace WHERE con.contype = 'f' AND con.conparentid = 0 AND {Visible} ORDER BY 2, 3, 1 LIMIT {MaxForeignKeys}",
            r => foreignKeys.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4), r.GetString(5), r.GetFieldValue<string[]>(6)))).ConfigureAwait(false);
        return new SqlCompletionCatalog { Database = connection.Database, SearchPath = searchPath, Schemas = schemas, Relations = relations, Functions = functions, ForeignKeys = foreignKeys, Columns = columns.ToDictionary(p => p.Key, p => (IReadOnlyList<SqlCatalogColumn>)p.Value) };
    }
}

/// <summary>Decides what to offer from the text around the caret. It reads names and clauses only; it is not a SQL parser, so it never rejects a statement.</summary>
public static class SqlCompletionEngine
{
    private const int MaxText = 4 * 1024 * 1024;
    private enum TokenKind { Word, Quoted, Symbol }
    private readonly record struct Token(TokenKind Kind, int Start, int End, string Text)
    {
        public bool IsName => Kind is TokenKind.Word or TokenKind.Quoted;
        public string Name => Kind == TokenKind.Word ? Text.ToLowerInvariant() : Text;
        public string Upper => Kind == TokenKind.Word ? Text.ToUpperInvariant() : "";
    }

    private static readonly HashSet<string> Reserved = new(("ALL ANALYSE ANALYZE AND ANY ARRAY AS ASC ASYMMETRIC AUTHORIZATION BINARY BOTH CASE CAST CHECK COLLATE COLLATION COLUMN CONCURRENTLY CONSTRAINT CREATE CROSS CURRENT_CATALOG CURRENT_DATE CURRENT_ROLE CURRENT_SCHEMA CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER DEFAULT DEFERRABLE DESC DISTINCT DO ELSE END EXCEPT FALSE FETCH FOR FOREIGN FREEZE FROM FULL GRANT GROUP HAVING ILIKE IN INITIALLY INNER INTERSECT INTO IS ISNULL JOIN LATERAL LEADING LEFT LIKE LIMIT LOCALTIME LOCALTIMESTAMP NATURAL NOT NOTNULL NULL OFFSET ON ONLY OR ORDER OUTER OVERLAPS PLACING PRIMARY REFERENCES RETURNING RIGHT SELECT SESSION_USER SIMILAR SOME SYMMETRIC SYSTEM_USER TABLE TABLESAMPLE THEN TO TRAILING TRUE UNION UNIQUE USER USING VARIADIC VERBOSE WHEN WHERE WINDOW WITH").Split(' '), StringComparer.Ordinal);
    private static readonly string[] Statements = "ALTER BEGIN COMMIT DELETE DROP EXPLAIN INSERT ROLLBACK SET TRUNCATE UPDATE VALUES".Split(' ');
    private static readonly HashSet<string> NotAlias = new(Statements.Append("MATCHED"), StringComparer.Ordinal);
    private static readonly string[] StatementStarts = "ALTER ANALYZE BEGIN CALL COMMIT COPY CREATE DELETE DO DROP EXPLAIN GRANT INSERT LOCK MERGE REVOKE ROLLBACK SELECT SET SHOW TABLE TRUNCATE UPDATE VACUUM VALUES WITH".Split(' ');

    /// <summary>What the grammar expects at the caret. Rows: a table, view or table function (FROM, JOIN). Relation: a table or view only (UPDATE, INSERT INTO, DELETE FROM...).</summary>
    private enum Place { Start, Rows, Relation, Expression }

    /// <summary>A name is quoted unless PostgreSQL would read it unchanged without quotes.</summary>
    public static string Quote(string name) =>
        Regex.IsMatch(name, "^[a-z_][a-z0-9_$]*$", RegexOptions.CultureInvariant) && !Reserved.Contains(name.ToUpperInvariant()) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Null when nothing can be offered: inside a string, a comment or a dollar-quoted body.</summary>
    public static SqlCompletionResult? Complete(string text, int caret, SqlCompletionCatalog catalog)
    {
        if (caret < 0 || caret > text.Length || text.Length > MaxText) return null;
        if (Tokens(text, caret) is not { } tokens) return null;

        int current = tokens.FindIndex(t => t.IsName && t.Start < caret && caret <= t.End);
        string prefix = "";
        int replaceStart = caret, before = tokens.FindLastIndex(t => t.End <= caret);
        if (current >= 0)
        {
            var word = tokens[current];
            replaceStart = word.Start;
            prefix = word.Kind == TokenKind.Quoted ? text[(word.Start + 1)..caret] : text[word.Start..caret];
            before = current - 1;
        }

        // Only the statement at the caret counts for its tables and aliases.
        int first = tokens.Count == 0 ? 0 : tokens.FindLastIndex(Math.Max(before, 0), t => t.Text == ";" && t.Kind == TokenKind.Symbol && t.End <= replaceStart) + 1;
        int last = tokens.FindIndex(Math.Min(first, tokens.Count), t => t.Text == ";" && t.Kind == TokenKind.Symbol && t.Start >= caret);
        var statement = tokens.Skip(first).Take((last < 0 ? tokens.Count : last) - first).ToList();
        var references = References(statement, replaceStart);

        var items = new List<SqlCompletionItem>();
        if (before >= first && tokens[before] is { Kind: TokenKind.Symbol, Text: "." })
        {
            if (before - 1 < first || !tokens[before - 1].IsName) return null;
            string qualifier = tokens[before - 1].Name;
            var place = PlaceAt(tokens, before - 2, first);
            if (place is Place.Rows or Place.Relation)
            {
                // Where a table is expected, the name before the dot can only be its schema.
                if (catalog.Schemas.Contains(qualifier))
                {
                    AddRelations(items, catalog, catalog.Relations.Where(r => r.Schema == qualifier));
                    if (place == Place.Rows) AddFunctions(items, catalog, catalog.Functions.Where(f => f.Schema == qualifier && f.ReturnsRows));
                    if (before >= 2 && tokens[before - 2].Upper == "JOIN") AddJoins(items, catalog, references, replaceStart, qualifier);
                }
            }
            else if (before - 3 >= first && tokens[before - 2] is { Kind: TokenKind.Symbol, Text: "." } && tokens[before - 3].IsName)
                AddColumns(items, catalog, tokens[before - 3].Name, qualifier);
            else
            {
                bool alias = false;
                foreach (var reference in references.Where(r => r.Alias == qualifier || r.Alias is null && r.Name == qualifier))
                {
                    alias = true;
                    if (Resolve(catalog, reference.Schema, reference.Name) is { } schema) AddColumns(items, catalog, schema, reference.Name);
                }
                if (!alias && Resolve(catalog, null, qualifier) is { } visible) AddColumns(items, catalog, visible, qualifier);
                if (catalog.Schemas.Contains(qualifier))
                {
                    AddRelations(items, catalog, catalog.Relations.Where(r => r.Schema == qualifier));
                    AddFunctions(items, catalog, catalog.Functions.Where(f => f.Schema == qualifier));
                }
            }
        }
        else
        {
            var place = PlaceAt(tokens, before, first);
            if (place is Place.Rows or Place.Relation)
            {
                AddSchemas(items, catalog);
                AddEveryRelation(items, catalog);
                // pg_catalog holds generate_series and unnest, but also a long list of internal pg_* ones.
                if (place == Place.Rows) AddFunctions(items, catalog, catalog.Functions.Where(f => f.ReturnsRows && catalog.SearchPath.Contains(f.Schema) && !(f.Schema == "pg_catalog" && f.Name.StartsWith("pg_", StringComparison.Ordinal))));
                if (tokens[before].Upper == "JOIN") AddJoins(items, catalog, references, replaceStart, null);
            }
            else if (place == Place.Start)
            {
                foreach (string keyword in StatementStarts) items.Add(new(keyword, SqlCompletionKind.Keyword, "keyword"));
            }
            else
            {
                if (before >= first && tokens[before].Upper == "ON") AddJoinConditions(items, catalog, references, replaceStart);
                foreach (var reference in references)
                {
                    string? schema = Resolve(catalog, reference.Schema, reference.Name);
                    if (schema is not null) AddColumns(items, catalog, schema, reference.Name);
                    AddReference(items, catalog, schema, reference);
                }
                AddFunctions(items, catalog, catalog.Functions.Where(f => catalog.SearchPath.Contains(f.Schema)));
                foreach (string keyword in Reserved.Concat(Statements)) items.Add(new(keyword, SqlCompletionKind.Keyword, "keyword"));
            }
        }
        var ordered = items.DistinctBy(i => (i.Text, i.Kind)).OrderBy(i => i.Kind == SqlCompletionKind.Join ? 0 : 1).ThenBy(i => (i.Filter ?? i.Text).TrimStart('"'), StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Text.Length).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Kind).ToList();
        return new(replaceStart, prefix, ordered);
    }

    private static string? Resolve(SqlCompletionCatalog catalog, string? schema, string name) =>
        schema is not null ? schema : catalog.SearchPath.FirstOrDefault(s => catalog.Columns.ContainsKey((s, name)));

    private static void AddColumns(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, string schema, string relation)
    {
        if (catalog.Columns.TryGetValue((schema, relation), out var columns))
            foreach (var column in columns) items.Add(new(Quote(column.Name), SqlCompletionKind.Column, $"column {column.Name}({column.Type}, {(column.Nullable ? "null" : "not null")})"));
    }

    private static void AddSchemas(List<SqlCompletionItem> items, SqlCompletionCatalog catalog)
    {
        foreach (string schema in catalog.Schemas) items.Add(new(Quote(schema), SqlCompletionKind.Schema, $"schema {catalog.Database}.{schema}"));
    }

    private static void AddRelations(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, IEnumerable<SqlCatalogRelation> relations)
    {
        foreach (var relation in relations) items.Add(new(Quote(relation.Name), relation.IsView ? SqlCompletionKind.View : SqlCompletionKind.Table, $"{(relation.IsView ? "view" : "table")} {catalog.Database}.{relation.Schema}.{relation.Name}"));
    }

    /// <summary>Where a table is expected, the name is searched in every user schema. Filter is the bare name, so what is typed never has to match the schema; only a table a bare name would not reach is written with its schema.</summary>
    private static void AddEveryRelation(List<SqlCompletionItem> items, SqlCompletionCatalog catalog)
    {
        var path = new Dictionary<string, int>();
        foreach (string schema in catalog.SearchPath) path.TryAdd(schema, path.Count);
        var reached = new Dictionary<string, string>();
        foreach (var relation in catalog.Relations.Where(r => path.ContainsKey(r.Schema)))
            if (!reached.TryGetValue(relation.Name, out string? current) || path[relation.Schema] < path[current]) reached[relation.Name] = relation.Schema;
        foreach (var relation in catalog.Relations.Where(r => r.Schema is not ("pg_catalog" or "information_schema")))
        {
            bool bare = reached.GetValueOrDefault(relation.Name) == relation.Schema;
            items.Add(new(bare ? Quote(relation.Name) : $"{Quote(relation.Schema)}.{Quote(relation.Name)}", relation.IsView ? SqlCompletionKind.View : SqlCompletionKind.Table, $"{(relation.IsView ? "view" : "table")} {catalog.Database}.{relation.Schema}.{relation.Name}", Quote(relation.Name)));
        }
    }

    private static string Condition(string left, IReadOnlyList<string> leftColumns, string right, IReadOnlyList<string> rightColumns) =>
        string.Join(" AND ", leftColumns.Zip(rightColumns, (l, r) => $"{Quote(left)}.{Quote(l)} = {Quote(right)}.{Quote(r)}"));

    /// <summary>After JOIN: every table linked by a foreign key to a table already in the statement, as "table alias ON condition". Plain tables stay in the list for joins no key describes.</summary>
    private static void AddJoins(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, List<Reference> references, int caret, string? onlySchema)
    {
        var earlier = references.Where(r => r.Start < caret).ToList();
        bool useAliases = earlier.Any(r => r.Alias is not null);
        var taken = earlier.Select(r => r.Alias ?? r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in earlier)
        {
            if (Resolve(catalog, reference.Schema, reference.Name) is not { } schema) continue;
            foreach (var key in catalog.ForeignKeys)
            {
                if (key.TargetSchema == schema && key.TargetTable == reference.Name) Add(reference, key.Schema, key.Table, key.Columns, key.TargetColumns, key.Name);
                if (key.Schema == schema && key.Table == reference.Name) Add(reference, key.TargetSchema, key.TargetTable, key.TargetColumns, key.Columns, key.Name);
            }
        }

        void Add(Reference existing, string schema, string table, IReadOnlyList<string> columns, IReadOnlyList<string> existingColumns, string keyName)
        {
            if (onlySchema is not null && schema != onlySchema) return;
            string? alias = useAliases || taken.Contains(table) ? NewAlias(table, taken) : null;
            string name = onlySchema is null && !catalog.SearchPath.Contains(schema) ? $"{Quote(schema)}.{Quote(table)}" : Quote(table);
            string condition = Condition(alias ?? table, columns, existing.Alias ?? existing.Name, existingColumns);
            items.Add(new($"{name}{(alias is null ? "" : " " + Quote(alias))} ON {condition}", SqlCompletionKind.Join, $"join {catalog.Database}.{schema}.{table} by foreign key {keyName}"));
        }
    }

    /// <summary>After ON: the condition between the table just joined and the earlier ones, from their foreign keys.</summary>
    private static void AddJoinConditions(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, List<Reference> references, int caret)
    {
        var earlier = references.Where(r => r.Start < caret).ToList();
        if (earlier.Count < 2 || Resolve(catalog, earlier[^1].Schema, earlier[^1].Name) is not { } joinedSchema) return;
        var joined = earlier[^1];
        foreach (var other in earlier.SkipLast(1))
        {
            if (Resolve(catalog, other.Schema, other.Name) is not { } otherSchema) continue;
            foreach (var key in catalog.ForeignKeys)
            {
                if (key.Schema == joinedSchema && key.Table == joined.Name && key.TargetSchema == otherSchema && key.TargetTable == other.Name) Add(key.Columns, key.TargetColumns, key.Name);
                if (key.TargetSchema == joinedSchema && key.TargetTable == joined.Name && key.Schema == otherSchema && key.Table == other.Name) Add(key.TargetColumns, key.Columns, key.Name);

                void Add(IReadOnlyList<string> joinedColumns, IReadOnlyList<string> otherColumns, string keyName) =>
                    items.Add(new(Condition(joined.Alias ?? joined.Name, joinedColumns, other.Alias ?? other.Name, otherColumns), SqlCompletionKind.Join, $"condition from foreign key {keyName}"));
            }
        }
    }

    /// <summary>The initials of the name's words, numbered when already in use or when they would be a reserved word.</summary>
    private static string NewAlias(string table, HashSet<string> taken)
    {
        string initials = string.Concat(table.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0]));
        if (!Regex.IsMatch(initials, "^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant)) initials = "t";
        string alias = initials;
        for (int i = 2; taken.Contains(alias) || Reserved.Contains(alias.ToUpperInvariant()); i++) alias = initials + i;
        return alias;
    }

    /// <summary>In an expression only the tables of the statement can be named, by their alias when they have one.</summary>
    private static void AddReference(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, string? schema, Reference reference)
    {
        bool view = catalog.Relations.Any(r => r.Schema == schema && r.Name == reference.Name && r.IsView);
        string target = schema is null ? reference.Name : $"{catalog.Database}.{schema}.{reference.Name}";
        string description = reference.Alias is { } alias ? $"alias {alias} of {target}" : $"{(view ? "view" : "table")} {target}";
        items.Add(new(Quote(reference.Alias ?? reference.Name), view ? SqlCompletionKind.View : SqlCompletionKind.Table, description));
    }

    private static void AddFunctions(List<SqlCompletionItem> items, SqlCompletionCatalog catalog, IEnumerable<SqlCatalogFunction> functions)
    {
        foreach (var function in functions) items.Add(new(Quote(function.Name), SqlCompletionKind.Function, $"function {catalog.Database}.{function.Schema}.{function.Name}({function.Arguments}) RETURNS {function.Result}"));
    }

    /// <summary>Decided by the token before the caret: a keyword that takes a table, a comma in a FROM list, or nothing at all at the start of the statement.</summary>
    private static Place PlaceAt(List<Token> tokens, int before, int first)
    {
        if (before < first) return Place.Start;
        switch (tokens[before].Upper)
        {
            case "FROM": return before > first && tokens[before - 1].Upper == "DELETE" ? Place.Relation : Place.Rows;
            case "JOIN" or "LATERAL": return Place.Rows;
            case "UPDATE" or "INTO" or "TABLE" or "ONLY" or "TRUNCATE": return Place.Relation;
        }
        if (tokens[before] is not { Kind: TokenKind.Symbol, Text: "," }) return Place.Expression;
        int depth = 0;
        for (int i = before - 1; i >= first; i--)
        {
            if (tokens[i] is { Kind: TokenKind.Symbol, Text: ")" }) depth++;
            else if (tokens[i] is { Kind: TokenKind.Symbol, Text: "(" }) { if (depth-- == 0) return Place.Expression; }
            else if (depth == 0 && tokens[i].Upper is "FROM") return Place.Rows;
            else if (depth == 0 && tokens[i].Upper is "SELECT" or "WHERE" or "SET" or "ON" or "GROUP" or "ORDER" or "VALUES" or "HAVING") return Place.Expression;
        }
        return Place.Expression;
    }

    private sealed record Reference(string? Schema, string Name, string? Alias, int Start);

    private static List<Reference> References(List<Token> statement, int exclude)
    {
        var found = new List<Reference>();
        bool Symbol(int index, string text) => index < statement.Count && statement[index].Kind == TokenKind.Symbol && statement[index].Text == text;
        for (int i = 0; i < statement.Count; i++)
        {
            string upper = statement[i].Upper;
            if (upper is not ("FROM" or "JOIN" or "UPDATE" or "INTO")) continue;
            int at = i + 1;
            while (true)
            {
                while (at < statement.Count && statement[at].Upper is "ONLY" or "LATERAL") at++;
                if (at >= statement.Count || !statement[at].IsName || statement[at].Start == exclude || Reserved.Contains(statement[at].Upper) && statement[at].Kind == TokenKind.Word) break;
                string? schema = null;
                string name = statement[at].Name;
                int start = statement[at].Start;
                at++;
                if (Symbol(at, ".") && at + 1 < statement.Count && statement[at + 1].IsName && statement[at + 1].Start != exclude) { schema = name; name = statement[at + 1].Name; at += 2; }
                else if (Symbol(at, ".")) break;
                if (Symbol(at, "(")) break; // A function in FROM has no catalog columns here.
                string? alias = null;
                if (at < statement.Count && statement[at].Upper == "AS") at++;
                if (at < statement.Count && statement[at].IsName && statement[at].Start != exclude && (statement[at].Kind == TokenKind.Quoted || !Reserved.Contains(statement[at].Upper) && !NotAlias.Contains(statement[at].Upper))) { alias = statement[at].Name; at++; }
                found.Add(new(schema, name, alias, start));
                if (upper == "FROM" && Symbol(at, ",")) { at++; continue; }
                break;
            }
        }
        return found;
    }

    /// <summary>Words, quoted names and punctuation of the whole text; null when the caret is inside a string, comment or dollar-quoted body.</summary>
    private static List<Token>? Tokens(string text, int caret)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int end = text.IndexOf('\n', i); end = end < 0 ? text.Length : end;
                if (i < caret && caret <= end) return null;
                i = end; continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int depth = 1, j = i + 2;
                while (j < text.Length && depth > 0)
                {
                    if (j + 1 < text.Length && text[j] == '/' && text[j + 1] == '*') { depth++; j += 2; }
                    else if (j + 1 < text.Length && text[j] == '*' && text[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                }
                if (i < caret && (caret < j || depth > 0)) return null;
                i = j; continue;
            }
            if (c == '\'' || (c is 'e' or 'E') && i + 1 < text.Length && text[i + 1] == '\'')
            {
                bool escapes = c != '\'';
                int j = escapes ? i + 2 : i + 1; bool closed = false;
                while (j < text.Length)
                {
                    if (escapes && text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '\'') { if (j + 1 < text.Length && text[j + 1] == '\'') { j += 2; continue; } j++; closed = true; break; }
                    j++;
                }
                j = Math.Min(j, text.Length);
                if (i < caret && (caret < j || !closed)) return null;
                i = j; continue;
            }
            if (c == '$' && Regex.Match(text[i..Math.Min(i + 130, text.Length)], @"^\$[A-Za-z_]?[A-Za-z0-9_]*\$", RegexOptions.CultureInvariant) is { Success: true } tag)
            {
                int close = text.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + tag.Length;
                if (i < caret && (caret < end || close < 0)) return null;
                i = end; continue;
            }
            if (c == '"')
            {
                int j = i + 1; var name = new System.Text.StringBuilder(); bool closed = false;
                while (j < text.Length)
                {
                    if (text[j] == '"') { if (j + 1 < text.Length && text[j + 1] == '"') { name.Append('"'); j += 2; continue; } j++; closed = true; break; }
                    name.Append(text[j]); j++;
                }
                // An unclosed quote at the caret is a name being typed; it ends at the line.
                if (!closed) { j = text.IndexOf('\n', i); j = j < 0 ? text.Length : j; name.Clear().Append(text[(i + 1)..j]); }
                tokens.Add(new(TokenKind.Quoted, i, j, name.ToString()));
                i = j; continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '$')) j++;
                tokens.Add(new(TokenKind.Word, i, j, text[i..j]));
                i = j; continue;
            }
            tokens.Add(new(TokenKind.Symbol, i, i + 1, c.ToString()));
            i++;
        }
        return tokens;
    }
}
