using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PlanIndexSortKey(string Column, bool Descending, string? Nulls)
{
    public string Sql => QueryDesignSql.Identifier(Column) + (Descending ? " DESC" : "") + (Nulls is null ? "" : " NULLS " + Nulls);
}

/// <summary>What a scan of an analyzed plan says about an index that could have avoided the rows it discarded (PGMS-041). Read from the plan alone;
/// nothing here was checked against the catalog. <paramref name="Schema"/> is null when the plan does not carry it (EXPLAIN without VERBOSE).</summary>
public sealed record PlanIndexCandidate(string? Schema, string Relation, string NodeType, IReadOnlyList<string> Equality, string? Range, IReadOnlyList<PlanIndexSortKey> Sort,
    bool RangeLeftOut, IReadOnlyList<string> CastToText, IReadOnlyList<string> Ignored, double RemovedRows, double ReturnedRows)
{
    /// <summary>Key columns in index order: equality first, then the ordering or one range column.</summary>
    public IReadOnlyList<PlanIndexSortKey> Keys => [.. Equality.Select(c => new PlanIndexSortKey(c, false, null)), .. Sort, .. Range is null ? [] : new[] { new PlanIndexSortKey(Range, false, null) }];
}

/// <summary>A candidate whose table and columns exist and that no valid index already serves. Still a heuristic: PostgreSQL has not been asked whether it would use the index.</summary>
public sealed record PlanIndexSuggestion(string Schema, string Table, string IndexName, bool Partitioned, PlanIndexCandidate Source)
{
    public string QualifiedTable => QueryDesignSql.Identifier(Schema) + "." + QueryDesignSql.Identifier(Table);
    public string KeyList => string.Join(", ", Source.Keys.Select(k => k.Sql));
    /// <summary>The line under the statement text, where SSMS shows "Missing Index (Impact …)". There is no impact figure: the server computed nothing.</summary>
    public string HeaderLine => "Missing Index (heuristic, not verified): CREATE INDEX ON " + QualifiedTable + " (" + KeyList + ")";
}

/// <summary>Reads index candidates out of a plan and writes the Missing Index Details script. PostgreSQL reports no missing index, unlike SQL Server:
/// this is PGMS's own reading of Filter, Index Cond and Sort Key, limited to simple comparisons joined by AND.</summary>
public static partial class PlanIndexAdvisor
{
    public const int MaximumCandidates = 20;
    private const string Ident = """(?:[a-z_][a-z0-9_$]*|"(?:[^"]|"")+")""";
    [GeneratedRegex("^(?:(?<alias>" + Ident + @")\.)?(?<col>" + Ident + ")$", RegexOptions.CultureInvariant)] private static partial Regex ColumnRef();
    [GeneratedRegex(@"^(?<left>\S+) (?<op><=|>=|=|<|>) (?<right>.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)] private static partial Regex Comparison();
    [GeneratedRegex(@"^(?<key>\S+?)(?<desc> DESC)?(?: NULLS (?<nulls>FIRST|LAST))?$", RegexOptions.CultureInvariant)] private static partial Regex SortKey();
    [GeneratedRegex("""::(?:[a-z_][a-z0-9_]*|"(?:[^"]|"")+")(?: [a-z]+)*(?:\(\d+(?:,\d+)?\))?(?:\[\])*""", RegexOptions.CultureInvariant)] private static partial Regex Cast();
    [GeneratedRegex("""(?<![\w$."])(?:[A-Za-z_][A-Za-z0-9_$]*|"(?:[^"]|"")+")(?:\.(?:[A-Za-z_][A-Za-z0-9_$]*|"(?:[^"]|"")+"))*""", RegexOptions.CultureInvariant)] private static partial Regex Word();
    private static readonly HashSet<string> ConstantWords = new(StringComparer.OrdinalIgnoreCase) { "CURRENT_DATE", "CURRENT_TIMESTAMP", "CURRENT_TIME", "LOCALTIME", "LOCALTIMESTAMP", "CURRENT_USER", "SESSION_USER", "true", "false", "NULL", "ANY", "ARRAY" };
    private static readonly string[] Scans = ["Seq Scan", "Index Scan", "Index Only Scan", "Bitmap Heap Scan"];

    /// <summary>Null when the scan gives nothing to build an index from. <paramref name="ancestors"/> starts at the parent.</summary>
    public static PlanIndexCandidate? Candidate(QueryPlanNode node, IReadOnlyList<QueryPlanNode> ancestors, double removedRows, double returnedRows)
    {
        var p = node.Properties;
        if (!Scans.Contains(p.GetValueOrDefault("Node Type")) || !p.TryGetValue("Relation Name", out var relation)) return null;
        string alias = p.GetValueOrDefault("Alias", relation);
        List<string> equality = [], ranges = [], castToText = [], ignored = [];
        foreach (string property in new[] { "Index Cond", "Recheck Cond", "Filter" })
        {
            if (!p.TryGetValue(property, out var text)) continue;
            if (text.EndsWith(" [truncated]", StringComparison.Ordinal)) return null;
            foreach (string term in Terms(text))
            {
                if (ReadComparison(term, alias, relation) is not { } read) { if (!ignored.Contains(term)) ignored.Add(term); continue; }
                var list = read.Equality ? equality : ranges;
                if (!list.Contains(read.Column)) list.Add(read.Column);
                if (read.Cast && !castToText.Contains(read.Column)) castToText.Add(read.Column);
            }
        }
        ranges.RemoveAll(equality.Contains);
        if (equality.Count == 0 && ranges.Count == 0) return null;

        var (sort, forLimit) = Ordering(ancestors, alias, relation);
        sort = sort.Where(k => !equality.Contains(k.Column)).ToList();
        string? range = ranges.FirstOrDefault();
        bool rangeLeftOut = false;
        // An index gives the order only while its columns before the ordering are fixed by equality, so a range column and an ordering do not both fit.
        // With a LIMIT above the sort the ordering is worth more; without one, the range is.
        if (sort.Count > 0 && range is not null)
        {
            if (forLimit) { rangeLeftOut = sort[0].Column != range; range = null; }
            else sort = [];
        }
        foreach (string other in ranges) if (other != range && sort.All(k => k.Column != other)) ignored.Add("range on " + other);
        return new(p.GetValueOrDefault("Schema"), relation, p["Node Type"], equality, range, sort, rangeLeftOut, castToText, ignored, removedRows, returnedRows);
    }

    private static (List<PlanIndexSortKey> Keys, bool ForLimit) Ordering(IReadOnlyList<QueryPlanNode> ancestors, string alias, string relation)
    {
        int i = 0;
        string Type(int index) => index < ancestors.Count ? ancestors[index].Properties.GetValueOrDefault("Node Type", "") : "";
        while (Type(i) == "Gather") i++;
        if (Type(i) is not ("Sort" or "Incremental Sort") || !ancestors[i].Properties.TryGetValue("Sort Key", out var raw)) return ([], false);
        var keys = new List<PlanIndexSortKey>();
        try
        {
            using var document = JsonDocument.Parse(raw);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var match = SortKey().Match(item.GetString() ?? "");
                if (!match.Success || Column(match.Groups["key"].Value, alias, relation) is not { } column || keys.Any(k => k.Column == column)) return ([], false);
                keys.Add(new(column, match.Groups["desc"].Success, match.Groups["nulls"].Success ? match.Groups["nulls"].Value : null));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ([], false); }
        int above = i + 1;
        while (Type(above) is "Gather Merge" or "Gather") above++;
        return (keys, Type(above) == "Limit");
    }

    /// <summary>The conditions joined by AND at the top of a deparsed qualification, each without its outer parentheses. An OR at the top leaves one term, which nothing reads.</summary>
    internal static List<string> Terms(string text)
    {
        text = StripOuter(text.Trim());
        var terms = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\'' or '"') { i = SkipQuoted(text, i); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(text, i, " OR ", 0, 4) == 0) return [text];
            else if (depth == 0 && string.CompareOrdinal(text, i, " AND ", 0, 5) == 0) { terms.Add(StripOuter(text[start..i])); start = i + 5; i += 4; }
        }
        terms.Add(StripOuter(text[start..]));
        return terms;
    }

    /// <summary>Index of the closing quote; doubled quotes stay inside.</summary>
    private static int SkipQuoted(string text, int open)
    {
        char quote = text[open];
        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] != quote) continue;
            if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i;
        }
        return text.Length - 1;
    }

    private static string StripOuter(string text)
    {
        while (text.Length >= 2 && text[0] == '(')
        {
            int depth = 0, close = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '\'' or '"') { i = SkipQuoted(text, i); continue; }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) { close = i; break; }
            }
            if (close != text.Length - 1) break;
            text = text[1..^1].Trim();
        }
        return text;
    }

    private static string? Column(string reference, string alias, string relation)
    {
        var match = ColumnRef().Match(reference);
        if (!match.Success) return null;
        if (match.Groups["alias"].Success && Unquote(match.Groups["alias"].Value) is var owner && owner != alias && owner != relation) return null;
        return Unquote(match.Groups["col"].Value);
    }

    private static string Unquote(string identifier) => identifier.StartsWith('"') ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : identifier;

    /// <summary>column = constant, column &lt; constant and the like; (column)::text is how a varchar column is shown and is accepted too.</summary>
    private static (string Column, bool Equality, bool Cast)? ReadComparison(string term, string alias, string relation)
    {
        var match = Comparison().Match(term);
        if (!match.Success) return null;
        string left = match.Groups["left"].Value, right = match.Groups["right"].Value, op = match.Groups["op"].Value;
        bool cast = left.StartsWith('(') && left.EndsWith(")::text", StringComparison.Ordinal);
        if (cast) left = left[1..^7];
        if (Column(left, alias, relation) is not { } column) return null;
        if (right.StartsWith("ANY (", StringComparison.Ordinal) && op != "=") return null;
        return IsColumnFree(right) ? (column, op == "=", cast) : null;
    }

    /// <summary>True when the text holds literals, parameters, casts and function calls only. A bare name would be a column, possibly of another table.</summary>
    internal static bool IsColumnFree(string expression)
    {
        var plain = new StringBuilder();
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (c == '\'') { i = SkipQuoted(expression, i); plain.Append('0'); }
            else if (c is 'E' or 'e' && i + 1 < expression.Length && expression[i + 1] == '\'' && (i == 0 || !char.IsLetterOrDigit(expression[i - 1]) && expression[i - 1] != '_')) continue; // E'…'
            else plain.Append(c);
        }
        string text = Cast().Replace(plain.ToString(), "");
        foreach (Match word in Word().Matches(text))
        {
            int after = word.Index + word.Length;
            bool call = after < text.Length && text[after] == '(';
            if (!call && !ConstantWords.Contains(word.Value)) return false;
        }
        return true;
    }

    private static string Plain(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Missing Index Details: every statement is inside a comment, as in SSMS 22.11, so opening the tab and pressing Execute changes nothing.</summary>
    public static string Script(string source, IReadOnlyList<PlanIndexSuggestion> suggestions)
    {
        static string Safe(string text) => text.Replace("*/", "* /", StringComparison.Ordinal).Replace("/*", "/ *", StringComparison.Ordinal);
        var script = new StringBuilder();
        script.Append("/*\nMissing Index Details from ").Append(Safe(source)).Append('\n');
        script.Append("PGMS read the index below out of the execution plan. It is a heuristic, not a recommendation from PostgreSQL:\n");
        script.Append("the server did not verify it, the planner may not use it, and every index takes space and slows down writes.\n");
        script.Append("Check the plan again after creating it. PGMS only confirmed that the columns exist and that no valid index already starts with them.\n*/\n");
        foreach (var suggestion in suggestions)
        {
            var c = suggestion.Source;
            script.Append("\n/*\n").Append(c.NodeType).Append(" on ").Append(Safe(suggestion.QualifiedTable)).Append(" discarded ").Append(Plain(c.RemovedRows)).Append(" rows to return ").Append(Plain(c.ReturnedRows)).Append(" (Rows Removed by Filter).\n");
            var used = new List<string>();
            if (c.Equality.Count > 0) used.Add("equality on " + string.Join(", ", c.Equality));
            if (c.Sort.Count > 0) used.Add("ordering by " + string.Join(", ", c.Sort.Select(k => k.Sql)));
            if (c.Range is not null) used.Add("range on " + c.Range);
            script.Append("Used: ").Append(Safe(string.Join("; ", used))).Append(".\n");
            if (c.RangeLeftOut) script.Append("A range condition was left out so that the index can return the rows already ordered for the LIMIT.\n");
            if (c.Ignored.Count > 0) script.Append("Not considered: ").Append(Safe(string.Join("; ", c.Ignored))).Append('\n');
            if (suggestion.Partitioned) script.Append("The table is partitioned: CREATE INDEX CONCURRENTLY is not available on it, and a plain CREATE INDEX blocks writes while it runs.\n");
            script.Append('\n').Append(suggestion.Partitioned ? "CREATE INDEX " : "CREATE INDEX CONCURRENTLY ").Append(Safe(QueryDesignSql.Identifier(suggestion.IndexName)))
                .Append("\nON ").Append(Safe(suggestion.QualifiedTable)).Append(" (").Append(Safe(suggestion.KeyList)).Append(");\n*/\n");
        }
        return script.ToString();
    }

    /// <summary>The candidates of a statement, in display order.</summary>
    internal static List<PlanIndexCandidate> Candidates(ExecutionPlanOperator root)
    {
        var found = new List<PlanIndexCandidate>();
        void Visit(ExecutionPlanOperator op) { if (op.IndexCandidate is { } candidate) found.Add(candidate); foreach (var child in op.Children) Visit(child); }
        Visit(root);
        return found;
    }
}

public sealed partial class QuerySession
{
    /// <summary>Checks the index candidates of the plans against the catalog, on this session so that its temporary tables are seen, and returns the statements
    /// with what survived. A candidate is dropped when its table cannot be told apart, a column is missing or a valid full btree index already starts with the
    /// same columns. Reads the catalog only. Statements without candidates cost no round trip.</summary>
    public async Task<IReadOnlyList<ExecutionPlanStatement>> AdviseIndexesAsync(IReadOnlyList<ExecutionPlanStatement> statements, CancellationToken token = default)
    {
        var candidates = statements.Select(s => PlanIndexAdvisor.Candidates(s.Root)).ToList();
        if (candidates.All(c => c.Count == 0)) return statements;
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await DisarmPlanCaptureAsync().ConfigureAwait(false);
            var result = new List<ExecutionPlanStatement>();
            var taken = new HashSet<(string, string)>();
            var named = new Dictionary<(string, string, string), string>();
            int budget = PlanIndexAdvisor.MaximumCandidates;
            for (int i = 0; i < statements.Count; i++)
            {
                var suggestions = new List<PlanIndexSuggestion>();
                foreach (var candidate in candidates[i])
                {
                    if (budget-- <= 0) break;
                    if (await VerifyIndexCandidateAsync(candidate, taken, named, token).ConfigureAwait(false) is { } suggestion && !suggestions.Any(s => s.QualifiedTable == suggestion.QualifiedTable && s.KeyList == suggestion.KeyList))
                        suggestions.Add(suggestion);
                }
                result.Add(suggestions.Count == 0 ? statements[i] : statements[i] with { MissingIndexes = suggestions });
            }
            return result;
        }
        finally { execution.Release(); }
    }

    private async Task<PlanIndexSuggestion?> VerifyIndexCandidateAsync(PlanIndexCandidate candidate, HashSet<(string, string)> taken, Dictionary<(string, string, string), string> named, CancellationToken token)
    {
        // Without VERBOSE the plan has no schema: the name must then belong to one table only, or the suggestion could point at the wrong one.
        // VERBOSE calls the session's temporary schema pg_temp, whatever its real name.
        // A scan names the partition it read; the index belongs on the partitioned table at the top, which hands it down to every partition.
        const string FindTable = """
            SELECT t.oid, tn.oid, CASE WHEN tn.oid = pg_catalog.pg_my_temp_schema() THEN 'pg_temp' ELSE tn.nspname::text END, t.relname::text, t.relkind = 'p'
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_class t ON t.oid = CASE WHEN c.relispartition THEN pg_catalog.pg_partition_root(c.oid) ELSE c.oid END
            JOIN pg_catalog.pg_namespace tn ON tn.oid = t.relnamespace
            WHERE c.relname = $1 AND c.relkind IN ('r','p','m') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
              AND (n.nspname = $2 OR $2 = 'pg_temp' AND n.oid = pg_catalog.pg_my_temp_schema() OR $2 IS NULL AND (n.nspname NOT LIKE 'pg\_temp\_%' OR n.oid = pg_catalog.pg_my_temp_schema()))
            LIMIT 2
            """;
        uint table, schemaOid; string schema, tableName; bool partitioned;
        await using (var command = new NpgsqlCommand(FindTable, connection) { CommandTimeout = 15, Parameters = { new NpgsqlParameter { Value = candidate.Relation, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, new NpgsqlParameter { Value = (object?)candidate.Schema ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text } } })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            table = reader.GetFieldValue<uint>(0); schemaOid = reader.GetFieldValue<uint>(1); schema = reader.GetString(2); tableName = reader.GetString(3); partitioned = reader.GetBoolean(4);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        }

        var columns = new Dictionary<string, (short Number, uint Type)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT a.attname::text, a.attnum, a.atttypid FROM pg_catalog.pg_attribute a WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped", connection) { CommandTimeout = 15, Parameters = { new NpgsqlParameter { Value = table, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid } } })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) columns[reader.GetString(0)] = (reader.GetInt16(1), reader.GetFieldValue<uint>(2));
        var keys = candidate.Keys;
        if (keys.Any(k => !columns.ContainsKey(k.Column))) return null;
        const uint Text = 25, Varchar = 1043;
        if (candidate.CastToText.Any(c => !columns.TryGetValue(c, out var column) || column.Type is not (Text or Varchar))) return null;

        var equality = candidate.Equality.Select(c => columns[c].Number).ToHashSet();
        var rest = keys.Skip(candidate.Equality.Count).Select(k => columns[k.Column].Number).ToList();
        await using (var command = new NpgsqlCommand("SELECT i.indkey::int2[], i.indnkeyatts FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid JOIN pg_catalog.pg_am am ON am.oid = ic.relam WHERE i.indrelid = $1 AND i.indisvalid AND i.indislive AND i.indpred IS NULL AND am.amname = 'btree'", connection) { CommandTimeout = 15, Parameters = { new NpgsqlParameter { Value = table, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid } } })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var index = reader.GetFieldValue<short[]>(0).Take(reader.GetInt16(1)).ToList();
                if (index.Count >= keys.Count && index.Take(equality.Count).ToHashSet().SetEquals(equality) && index.Skip(equality.Count).Take(rest.Count).SequenceEqual(rest)) return null;
            }

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT c.relname::text FROM pg_catalog.pg_class c WHERE c.relnamespace = $1", connection) { CommandTimeout = 15, Parameters = { new NpgsqlParameter { Value = schemaOid, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Oid } } })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(0));
        var identity = (schema, tableName, string.Join(", ", keys.Select(k => k.Sql)));
        if (!named.TryGetValue(identity, out var name))
        {
            string wanted = $"ix_{tableName}_{string.Join("_", keys.Select(k => k.Column))}";
            name = ForeignKeyIndexScan.Fit(wanted, "");
            for (int n = 1; names.Contains(name) || !taken.Add((schema, name)); n++) name = ForeignKeyIndexScan.Fit(wanted, "_" + n.ToString(CultureInfo.InvariantCulture));
            named[identity] = name;
        }
        return new(schema, tableName, name, partitioned, candidate);
    }
}
