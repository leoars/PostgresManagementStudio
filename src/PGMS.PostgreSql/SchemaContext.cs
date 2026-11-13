using System.Globalization;
using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Structure of one database as compact text for an AI provider: schemas, tables, views, columns, types,
/// keys, enum labels and comments. Read from the catalog only; never reads table rows.</summary>
/// <param name="Relations">Relations described with columns and keys.</param>
/// <param name="OmittedRelations">Relations left without detail for size.</param>
/// <param name="ListedByName">How many of the omitted ones still appear by name.</param>
/// <param name="ChosenRelations">How many relations of the caller's choice were recognized and put first.</param>
public sealed record SchemaContext(string Text, string Database, string ServerVersion, int Relations, int Columns, int OmittedRelations, int ListedByName = 0, int ChosenRelations = 0)
{
    public const int DefaultMaximumCharacters = 200_000, MinimumCharacters = 2_000, MaximumCharacters = 5_000_000;
}

/// <summary>The structure read once from the catalog. Decides, without touching the database again, which relations
/// go into a context of limited size: the ones chosen by the caller, then the ones whose names, columns and comments
/// share words with the request, then the tables those reference, then catalog order.</summary>
public sealed class SchemaCatalog
{
    internal sealed record Column(string Name, string RawName, string Type, bool NotNull, string? Comment);
    // Schema, QuotedName and column names come quoted by quote_ident; Name stays raw for matching the request.
    internal sealed class Relation(string schema, string rawSchema, string name, string quotedName, string kind, string? comment)
    {
        private string? block;
        private HashSet<string>? nameWords, columnWords, commentWords;
        public string Schema { get; } = schema; public string Name { get; } = name; public string Kind { get; } = kind; public string? Comment { get; } = comment;
        public List<Column> Columns { get; } = [];
        public List<string> Constraints { get; } = [];
        public List<uint> References { get; } = [];
        public string QualifiedName { get; } = schema + "." + quotedName;
        public string RawQualifiedName { get; } = rawSchema + "." + name;
        public string NameLine => "--   " + QualifiedName + "\n";
        public string Block => block ??= Describe(this);
        public HashSet<string> NameWords => nameWords ??= Words(Name).ToHashSet(StringComparer.Ordinal);
        public HashSet<string> ColumnWords => columnWords ??= Columns.SelectMany(c => Words(c.RawName)).ToHashSet(StringComparer.Ordinal);
        public HashSet<string> CommentWords => commentWords ??= Columns.Select(c => c.Comment).Append(Comment).Where(c => c is not null).SelectMany(c => Words(c!)).ToHashSet(StringComparer.Ordinal);
    }

    private readonly string version, database, header;
    private readonly Dictionary<uint, Relation> byOid;
    private readonly List<Relation> relations; // Catalog order: schema, then name.
    private readonly List<string> enums;

    internal SchemaCatalog(string version, string database, string quotedDatabase, Dictionary<uint, Relation> byOid, List<string> enums)
    {
        this.version = version; this.database = database; this.byOid = byOid; this.enums = enums;
        relations = byOid.Values.OrderBy(r => r.Schema, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var text = new StringBuilder();
        text.Append("-- PostgreSQL ").Append(version).Append(", database ").Append(quotedDatabase).Append('\n');
        foreach (string line in enums) text.Append(line).Append('\n');
        header = text.ToString();
    }

    public string Database => database;
    public int Relations => relations.Count;

    /// <summary>True when every relation fits with full detail, so nothing has to be chosen.</summary>
    public bool Fits(int maximumCharacters) => header.Length + relations.Sum(r => (long)r.Block.Length) <= maximumCharacters;

    /// <summary>Qualified names with the table comment, one per line, most similar to the request first:
    /// what a provider needs to say which relations a request is about.</summary>
    public string NameList(string request, int maximumCharacters)
    {
        var text = new StringBuilder();
        var ordered = Rank(request, null, out _);
        for (int i = 0; i < ordered.Count; i++)
        {
            var relation = ordered[i];
            string line = relation.QualifiedName + (string.IsNullOrWhiteSpace(relation.Comment) ? "" : " -- " + OneLine(relation.Comment, 80)) + "\n";
            if (text.Length + line.Length > maximumCharacters) { text.Append("-- ").Append(ordered.Count - i).Append(" more relations not listed for size\n"); break; }
            text.Append(line);
        }
        return text.ToString();
    }

    /// <summary>Builds the context. When the structure exceeds <paramref name="maximumCharacters"/>, the relations in
    /// <paramref name="chosen"/> (qualified names, as <see cref="NameList"/> prints them) come first, then the ones that
    /// share words with <paramref name="request"/>; the rest are listed by name only, as far as the budget allows.</summary>
    public SchemaContext Build(string request, int maximumCharacters = SchemaContext.DefaultMaximumCharacters, IReadOnlyList<string>? chosen = null)
    {
        bool everything = Fits(maximumCharacters);
        int recognized = 0;
        var ordered = everything ? relations : Rank(request, chosen, out recognized);
        var text = new StringBuilder(header);
        int included = 0, columns = 0;
        long namesLeft = everything ? 0 : ordered.Sum(r => (long)r.NameLine.Length);
        var omitted = new List<Relation>();
        foreach (var relation in ordered)
        {
            // Keep room for the names of what stays out, so the provider at least knows those relations exist.
            long reserve = everything ? 0 : Math.Min(namesLeft - relation.NameLine.Length, maximumCharacters / 3) + 80;
            if (text.Length + relation.Block.Length + reserve > maximumCharacters) { omitted.Add(relation); continue; }
            text.Append(relation.Block); included++; columns += relation.Columns.Count; namesLeft -= relation.NameLine.Length;
        }
        int listed = 0;
        if (omitted.Count > 0)
        {
            text.Append("-- ").Append(omitted.Count).Append(" more relations omitted for size; names only:\n");
            foreach (var relation in omitted)
            {
                if (text.Length + relation.NameLine.Length > maximumCharacters - 8) { text.Append("--   …\n"); break; }
                text.Append(relation.NameLine); listed++;
            }
        }
        return new(text.ToString(), database, version, included, columns, omitted.Count, listed, recognized);
    }

    // Chosen relations first, in the order given; then by words shared with the request; then catalog order.
    private List<Relation> Rank(string request, IReadOnlyList<string>? chosen, out int recognized)
    {
        var words = Words(request).Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relation in relations)
            foreach (string word in words)
                if (relation.NameWords.Contains(word) || relation.ColumnWords.Contains(word) || relation.CommentWords.Contains(word))
                    frequency[word] = frequency.GetValueOrDefault(word) + 1;
        var score = new Dictionary<Relation, double>();
        foreach (var relation in relations)
        {
            double value = 0;
            foreach (string word in words)
            {
                int weight = relation.NameWords.Contains(word) ? 8 : relation.ColumnWords.Contains(word) ? 3 : relation.CommentWords.Contains(word) ? 2 : 0;
                // A word that many relations share (id, name, date) says little about which one is meant.
                if (weight > 0) value += weight * Math.Log(1 + (double)relations.Count / frequency[word]);
            }
            if (relation.Name.Length >= 3 && request.Contains(relation.Name, StringComparison.OrdinalIgnoreCase)) value += 100;
            score[relation] = value;
        }
        recognized = 0;
        if (chosen is not null)
        {
            var exact = new Dictionary<string, Relation>(StringComparer.Ordinal);
            var loose = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            foreach (var relation in relations) { exact[relation.QualifiedName] = relation; loose.TryAdd(relation.QualifiedName, relation); loose.TryAdd(relation.RawQualifiedName, relation); }
            var seen = new HashSet<Relation>();
            foreach (string name in chosen)
            {
                if (!exact.TryGetValue(name, out var relation) && !loose.TryGetValue(name, out relation)) continue;
                if (!seen.Add(relation)) continue;
                score[relation] += 1_000_000 - recognized; recognized++;
            }
        }
        // A table referenced by a relevant one is usually needed for the join.
        var bonus = new Dictionary<Relation, double>();
        foreach (var relation in relations)
        {
            if (score[relation] <= 0) continue;
            foreach (uint oid in relation.References)
                if (byOid.TryGetValue(oid, out var target) && target != relation)
                    bonus[target] = Math.Max(bonus.GetValueOrDefault(target), score[relation] / 2);
        }
        return relations.OrderByDescending(r => score[r] + bonus.GetValueOrDefault(r)).ToList(); // Stable: ties keep catalog order.
    }

    /// <summary>Lower-case words without accents, split at punctuation, underscores and camelCase, with a final "s" dropped.</summary>
    internal static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        char previous = '\0';
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            bool part = char.IsLetterOrDigit(c);
            if ((!part || (char.IsUpper(c) && char.IsLower(previous))) && word.Length > 0) { yield return Stem(word.ToString()); word.Clear(); }
            if (part) word.Append(char.ToLowerInvariant(c));
            previous = c;
        }
        if (word.Length > 0) yield return Stem(word.ToString());
    }
    private static string Stem(string word) => word.Length > 3 && word[^1] == 's' ? word[..^1] : word;

    private static string Describe(Relation relation)
    {
        string kind = relation.Kind switch { "v" => "VIEW", "m" => "MATERIALIZED VIEW", "f" => "FOREIGN TABLE", "p" => "TABLE (partitioned)", _ => "TABLE" };
        var text = new StringBuilder();
        text.Append(kind).Append(' ').Append(relation.QualifiedName);
        if (!string.IsNullOrWhiteSpace(relation.Comment)) text.Append(" -- ").Append(OneLine(relation.Comment));
        text.Append('\n');
        foreach (var column in relation.Columns)
        {
            text.Append("  ").Append(column.Name).Append(' ').Append(column.Type);
            if (column.NotNull) text.Append(" NOT NULL");
            if (!string.IsNullOrWhiteSpace(column.Comment)) text.Append(" -- ").Append(OneLine(column.Comment));
            text.Append('\n');
        }
        foreach (string constraint in relation.Constraints) text.Append("  ").Append(constraint).Append('\n');
        return text.ToString();
    }

    private static string OneLine(string value, int maximum = 200)
    {
        string line = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > maximum ? line[..maximum] + "…" : line;
    }
}

public sealed class SchemaContextService(string connectionString)
{
    private const string SchemaFilter = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_toast' AND n.nspname !~ '^pg_temp_'";

    /// <summary>Reads the structure and builds the context in one step.</summary>
    public async Task<SchemaContext> BuildAsync(string request, int maximumCharacters = SchemaContext.DefaultMaximumCharacters, CancellationToken token = default) =>
        (await LoadAsync(token).ConfigureAwait(false)).Build(request, maximumCharacters);

    /// <summary>Reads the structure of the database: metadata only, in a read-only transaction.</summary>
    public async Task<SchemaCatalog> LoadAsync(CancellationToken token = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        // Metadata only, in a read-only transaction so nothing here can change the database.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await Execute(connection, "SET TRANSACTION READ ONLY", token).ConfigureAwait(false);
        string version = (string)(await Scalar(connection, "SELECT current_setting('server_version')", token).ConfigureAwait(false))!;
        string database = (string)(await Scalar(connection, "SELECT current_database()", token).ConfigureAwait(false))!;
        string quotedDatabase = (string)(await Scalar(connection, "SELECT pg_catalog.quote_ident(current_database())", token).ConfigureAwait(false))!;

        var relations = new Dictionary<uint, SchemaCatalog.Relation>();
        await using (var command = new NpgsqlCommand($"""
            SELECT c.oid, pg_catalog.quote_ident(n.nspname), c.relname, c.relkind::text, pg_catalog.obj_description(c.oid, 'pg_class'),
                   pg_catalog.quote_ident(a.attname), pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_catalog.col_description(c.oid, a.attnum),
                   pg_catalog.quote_ident(c.relname), n.nspname, a.attname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND NOT c.relispartition AND {SchemaFilter}
            ORDER BY n.nspname, c.relname, a.attnum
            """, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                uint oid = reader.GetFieldValue<uint>(0);
                if (!relations.TryGetValue(oid, out var relation))
                    relations[oid] = relation = new(reader.GetString(1), reader.GetString(10), reader.GetString(2), reader.GetString(9), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
                if (!reader.IsDBNull(5)) relation.Columns.Add(new(reader.GetString(5), reader.GetString(11), reader.GetString(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }
        await using (var command = new NpgsqlCommand($"""
            SELECT con.conrelid, pg_catalog.pg_get_constraintdef(con.oid, true), con.confrelid
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype IN ('p', 'u', 'f') AND {SchemaFilter}
            ORDER BY con.conrelid, con.contype DESC, con.conname
            """, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (relations.TryGetValue(reader.GetFieldValue<uint>(0), out var relation))
                {
                    relation.Constraints.Add(reader.GetString(1));
                    uint target = reader.GetFieldValue<uint>(2);
                    if (target != 0) relation.References.Add(target);
                }
        var enums = new List<string>();
        await using (var command = new NpgsqlCommand($"""
            SELECT pg_catalog.quote_ident(n.nspname) || '.' || pg_catalog.quote_ident(t.typname), string_agg(pg_catalog.quote_literal(e.enumlabel), ', ' ORDER BY e.enumsortorder)
            FROM pg_catalog.pg_type t
            JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_catalog.pg_enum e ON e.enumtypid = t.oid
            WHERE {SchemaFilter}
            GROUP BY n.nspname, t.typname ORDER BY n.nspname, t.typname
            """, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                enums.Add($"ENUM {reader.GetString(0)} ({reader.GetString(1)})");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(version, database, quotedDatabase, relations, enums);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
}
