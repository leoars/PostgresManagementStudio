using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>One key of an index as the catalog has it. Column is the column name, null when the key is an expression; Expression is the text PostgreSQL
/// shows (quoted when needed); OperatorClass is set only when it is not the default for the type and method (gin_trgm_ops, jsonb_path_ops, text_pattern_ops).</summary>
public sealed record DiagramIndexKey(string Expression)
{
    public string? Column { get; init; }
    public string? OperatorClass { get; init; }
    public bool Descending { get; init; }

    [JsonIgnore] public string Text => Expression + (OperatorClass is { } opclass ? " " + opclass : "") + (Descending ? " DESC" : "");
}

/// <summary>An index of a table in a diagram (PGMS-044). Read from the catalog, never edited here: creating or changing an index stays in the existing dialogs.</summary>
public sealed record DiagramIndex(string Name, string Method, IReadOnlyList<DiagramIndexKey> Keys)
{
    public bool Primary { get; init; }
    public bool Unique { get; init; }
    /// <summary>The index of an EXCLUDE constraint.</summary>
    public bool Exclusion { get; init; }
    /// <summary>False for an index a failed CREATE INDEX CONCURRENTLY left behind: the planner does not use it.</summary>
    public bool Valid { get; init; } = true;
    public IReadOnlyList<string> Include { get; init; } = [];
    /// <summary>The WHERE of a partial index, as PostgreSQL shows it.</summary>
    public string? Predicate { get; init; }
    public string Definition { get; init; } = "";

    /// <summary>The method in the short form of the diagram: nothing for b-tree, the usual name of the others.</summary>
    [JsonIgnore] public string MethodLabel => Method switch { "btree" => "", "hash" => "Hash", "gin" => "GIN", "gist" => "GiST", "spgist" => "SP-GiST", "brin" => "BRIN", _ => Method };
    [JsonIgnore] public string Kind => Primary ? "Primary key" : Exclusion ? "Exclusion constraint" : Unique ? "Unique" : "Index";
    /// <summary>The keys in parentheses, then INCLUDE; the predicate stays in the tooltip.</summary>
    [JsonIgnore] public string KeysText => "(" + string.Join(", ", Keys.Select(k => k.Text)) + ")" + (Include.Count > 0 ? " INCLUDE (" + string.Join(", ", Include) + ")" : "");
    [JsonIgnore] public IReadOnlyList<string> Flags => [.. new[] { Primary ? "PRIMARY KEY" : Exclusion ? "EXCLUDE" : Unique ? "UNIQUE" : null, Predicate is not null ? "partial" : null, Valid ? null : "invalid" }.OfType<string>()];

    internal ScanIndex ForScan() => new(Name, Keys.Select(k => k.Column).ToList(), Valid, Predicate is not null, Method);
}

/// <summary>A foreign key of the table that no index serves, by the rule of Find Unindexed Foreign Keys (PGMS-039); NearIndexes says which indexes almost do.</summary>
public sealed record DiagramUnindexedKey(string Name, IReadOnlyList<string> Columns)
{
    public IReadOnlyList<string> NearIndexes { get; init; } = [];
}

/// <summary>What the catalog says about the indexes of one table.</summary>
public sealed record DiagramTableIndexes(IReadOnlyList<DiagramIndex> Indexes, IReadOnlyList<DiagramUnindexedKey> UnindexedForeignKeys)
{
    public static readonly DiagramTableIndexes Empty = new([], []);
}

/// <summary>Reads the indexes of the tables of a diagram from the catalog (PGMS-044). Read only.</summary>
public static class DiagramIndexReader
{
    private const string IndexSql = """
        SELECT i.indrelid, c.relname::text, am.amname::text, i.indisprimary, i.indisunique, COALESCE(con.contype = 'x', false), i.indisvalid,
               pg_catalog.pg_get_expr(i.indpred, i.indrelid, true), pg_catalog.pg_get_indexdef(i.indexrelid), i.indnkeyatts::int,
               ARRAY(SELECT pg_catalog.pg_get_indexdef(i.indexrelid, k, true) FROM pg_catalog.generate_series(1, i.indnatts::int) k ORDER BY k),
               ARRAY(SELECT a.attname::text FROM pg_catalog.generate_series(1, i.indnatts::int) k LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[k - 1] AND i.indkey[k - 1] > 0 ORDER BY k),
               ARRAY(SELECT CASE WHEN oc.opcdefault THEN NULL WHEN pg_catalog.pg_opclass_is_visible(oc.oid) THEN pg_catalog.quote_ident(oc.opcname) ELSE pg_catalog.quote_ident(ocn.nspname) || '.' || pg_catalog.quote_ident(oc.opcname) END
                     FROM pg_catalog.generate_series(1, i.indnkeyatts::int) k LEFT JOIN pg_catalog.pg_opclass oc ON oc.oid = i.indclass[k - 1] LEFT JOIN pg_catalog.pg_namespace ocn ON ocn.oid = oc.opcnamespace ORDER BY k),
               ARRAY(SELECT (i.indoption[k - 1] & 1) = 1 FROM pg_catalog.generate_series(1, i.indnkeyatts::int) k ORDER BY k)
        FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid JOIN pg_catalog.pg_am am ON am.oid = c.relam
        LEFT JOIN pg_catalog.pg_constraint con ON con.conindid = i.indexrelid AND con.conrelid = i.indrelid AND con.contype IN ('p','u','x')
        WHERE i.indrelid = ANY(@oids) AND i.indislive
        ORDER BY i.indrelid, i.indisprimary DESC, i.indisunique DESC, c.relname
        """;

    private const string ForeignKeySql = """
        SELECT con.conrelid, con.conname::text,
               ARRAY(SELECT a.attname::text FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ord) JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum ORDER BY k.ord)
        FROM pg_catalog.pg_constraint con
        WHERE con.contype = 'f' AND con.conparentid = 0 AND con.conrelid = ANY(@oids)
        ORDER BY con.conrelid, con.conname
        """;

    /// <summary>The indexes and the foreign keys without an index of each table, in a stable order: primary key, unique, the others, each by name.</summary>
    public static async Task<IReadOnlyDictionary<uint, DiagramTableIndexes>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<uint> oids, CancellationToken token = default)
    {
        var result = new Dictionary<uint, DiagramTableIndexes>();
        var wanted = oids.Where(o => o != 0).Distinct().ToArray();
        if (wanted.Length == 0) return result;
        var indexes = wanted.ToDictionary(o => o, _ => new List<DiagramIndex>());
        await using (var command = new NpgsqlCommand(IndexSql, connection, transaction) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, wanted);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                int keyCount = reader.GetInt32(9);
                var texts = reader.GetFieldValue<string[]>(10);
                var columns = reader.GetFieldValue<string?[]>(11);
                var classes = reader.GetFieldValue<string?[]>(12);
                var descending = reader.GetFieldValue<bool[]>(13);
                var keys = Enumerable.Range(0, Math.Min(keyCount, texts.Length)).Select(k => new DiagramIndexKey(texts[k])
                {
                    Column = k < columns.Length ? columns[k] : null, OperatorClass = k < classes.Length ? classes[k] : null, Descending = k < descending.Length && descending[k]
                }).ToArray();
                indexes[reader.GetFieldValue<uint>(0)].Add(new(reader.GetString(1), reader.GetString(2), keys)
                {
                    Primary = reader.GetBoolean(3), Unique = reader.GetBoolean(4), Exclusion = reader.GetBoolean(5), Valid = reader.GetBoolean(6),
                    Predicate = reader.IsDBNull(7) ? null : reader.GetString(7), Definition = reader.GetString(8),
                    Include = texts.Skip(keyCount).ToArray()
                });
            }
        }
        var keysByTable = wanted.ToDictionary(o => o, _ => new List<(string Name, string[] Columns)>());
        await using (var command = new NpgsqlCommand(ForeignKeySql, connection, transaction) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, wanted);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                keysByTable[reader.GetFieldValue<uint>(0)].Add((reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }
        foreach (uint oid in wanted) result[oid] = new(indexes[oid], Unindexed(keysByTable[oid], indexes[oid]));
        return result;
    }

    /// <summary>The same rule as Find Unindexed Foreign Keys: a valid, non-partial b-tree or hash index whose first keys are the columns of the key, in any order.</summary>
    public static IReadOnlyList<DiagramUnindexedKey> Unindexed(IEnumerable<(string Name, string[] Columns)> foreignKeys, IReadOnlyList<DiagramIndex> indexes)
    {
        var scan = indexes.Select(i => i.ForScan()).ToArray();
        return foreignKeys.Where(k => k.Columns.Length > 0 && !scan.Any(i => ForeignKeyIndexScan.Covers(k.Columns, i)))
            .Select(k => new DiagramUnindexedKey(k.Name, k.Columns) { NearIndexes = scan.Select(i => ForeignKeyIndexScan.NearReason(k.Columns, i)).OfType<string>().ToArray() }).ToArray();
    }
}
