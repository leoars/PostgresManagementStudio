using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>An index as the scan sees it. KeyColumns holds the key columns in order, null where the key is an expression; INCLUDE columns are left out.</summary>
public sealed record ScanIndex(string Name, IReadOnlyList<string?> KeyColumns, bool Valid, bool Partial, string Method);

/// <summary>A foreign key and what is known about its tables. Sizes and counters come from the catalog and the statistics, never from the rows.</summary>
public sealed record ScanForeignKey(string Schema, string Table, string Name, IReadOnlyList<string> Columns, string TargetSchema, string TargetTable, bool Partitioned, long TableBytes, double EstimatedRows, long? ParentDeletes, long? ParentUpdates, IReadOnlyList<ScanIndex> Indexes);

/// <summary>A foreign key that no index covers. NearIndexes says which indexes almost do and why they do not. Keys on the same columns of the same table share IndexName.</summary>
public sealed record UnindexedForeignKey(ScanForeignKey Key, string IndexName, IReadOnlyList<string> NearIndexes);

/// <summary>What Apply did. InvalidIndex names an index a failed CREATE INDEX CONCURRENTLY left behind.</summary>
public sealed record IndexApplyResult(int Created, string? Error, string? InvalidIndex, string? InvalidIndexSchema = null);

/// <summary>Finds foreign keys whose columns have no usable index (PGMS-039). PostgreSQL indexes the referenced side only, so deleting or updating a parent row scans the child table. Reads the catalog only; changes nothing except in <see cref="ApplyAsync"/>.</summary>
public static class ForeignKeyIndexScan
{
    /// <summary>An index serves the key when it is valid, covers every row, and its first key columns are exactly the columns of the key, in any order.</summary>
    public static bool Covers(IReadOnlyList<string> keyColumns, ScanIndex index) =>
        index.Valid && !index.Partial && index.Method is "btree" or "hash" && LeadsWith(keyColumns, index);

    private static bool LeadsWith(IReadOnlyList<string> keyColumns, ScanIndex index) =>
        index.KeyColumns.Count >= keyColumns.Count && index.KeyColumns.Take(keyColumns.Count).All(c => c is not null && keyColumns.Contains(c)) && index.KeyColumns.Take(keyColumns.Count).Distinct().Count() == keyColumns.Count;

    /// <summary>Why an index that touches the key does not serve it; null when the index has nothing to do with the key.</summary>
    internal static string? NearReason(IReadOnlyList<string> keyColumns, ScanIndex index)
    {
        if (LeadsWith(keyColumns, index))
            return !index.Valid ? $"{index.Name} is invalid"
                : index.Partial ? $"{index.Name} is partial"
                : $"{index.Name} is {index.Method}, not b-tree";
        if (keyColumns.All(c => index.KeyColumns.Contains(c))) return $"{index.Name} has the columns, but not first";
        if (keyColumns.Count > 1 && index.KeyColumns.Count > 0 && index.KeyColumns[0] is { } first && keyColumns.Contains(first)) return $"{index.Name} covers only part of the key";
        return null;
    }

    /// <summary>The keys without an index, in table order. Keys on the same columns of one table get the same index name, so one index serves them.</summary>
    public static IReadOnlyList<UnindexedForeignKey> FindUnindexed(IReadOnlyList<ScanForeignKey> keys, IReadOnlySet<(string Schema, string Name)> relationNames)
    {
        var taken = new HashSet<(string, string)>(relationNames);
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new List<UnindexedForeignKey>();
        foreach (var key in keys)
        {
            if (key.Columns.Count == 0 || key.Indexes.Any(i => Covers(key.Columns, i))) continue;
            string same = key.Schema + "\0" + key.Table + "\0" + string.Join("\0", key.Columns);
            if (!named.TryGetValue(same, out var name))
            {
                string wanted = $"ix_{key.Table}_{string.Join("_", key.Columns)}";
                name = Fit(wanted, "");
                for (int n = 1; !taken.Add((key.Schema, name)); n++) name = Fit(wanted, "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                named[same] = name;
            }
            found.Add(new(key, name, key.Indexes.Select(i => NearReason(key.Columns, i)).OfType<string>().ToList()));
        }
        return found;
    }

    /// <summary>A name of at most 63 bytes; a long one keeps a hash of the full name so two long names do not collide.</summary>
    internal static string Fit(string name, string suffix)
    {
        if (Encoding.UTF8.GetByteCount(name + suffix) <= 63) return name + suffix;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        int room = 63 - 9 - Encoding.UTF8.GetByteCount(suffix);
        var kept = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(kept.ToString() + rune) > room) break;
            kept.Append(rune.ToString());
        }
        return kept + "_" + hash + suffix;
    }

    private const string UserTables = "c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_%'";

    /// <summary>Reads every foreign key of ordinary and partitioned tables outside the system schemas, with the indexes of its table. A key inherited by a partition is left out: the one on the parent stands for it.</summary>
    public static async Task<(IReadOnlyList<ScanForeignKey> Keys, IReadOnlySet<(string Schema, string Name)> RelationNames)> ReadAsync(string connectionString, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        var attributes = new Dictionary<(uint, short), string>();
        await using (var command = new NpgsqlCommand("SELECT a.attrelid, a.attnum, a.attname::text FROM pg_catalog.pg_attribute a WHERE a.attnum > 0 AND NOT a.attisdropped AND a.attrelid IN (SELECT conrelid FROM pg_catalog.pg_constraint WHERE contype = 'f')", connection, transaction) { CommandTimeout = 60 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                attributes[(reader.GetFieldValue<uint>(0), reader.GetInt16(1))] = reader.GetString(2);

        var indexes = new Dictionary<uint, List<ScanIndex>>();
        await using (var command = new NpgsqlCommand("SELECT i.indrelid, ic.relname::text, i.indkey::int2[], i.indnkeyatts, i.indisvalid, i.indpred IS NOT NULL, am.amname::text FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid JOIN pg_catalog.pg_am am ON am.oid = ic.relam WHERE i.indislive AND i.indrelid IN (SELECT conrelid FROM pg_catalog.pg_constraint WHERE contype = 'f') ORDER BY ic.relname", connection, transaction) { CommandTimeout = 60 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                uint table = reader.GetFieldValue<uint>(0);
                short[] key = reader.GetFieldValue<short[]>(2);
                var columns = key.Take(reader.GetInt16(3)).Select(n => n > 0 && attributes.TryGetValue((table, n), out var name) ? name : null).ToList();
                if (!indexes.TryGetValue(table, out var list)) indexes[table] = list = [];
                list.Add(new(reader.GetString(1), columns, reader.GetBoolean(4), reader.GetBoolean(5), reader.GetString(6)));
            }

        var names = new HashSet<(string, string)>();
        await using (var command = new NpgsqlCommand("SELECT n.nspname::text, c.relname::text FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_%'", connection, transaction) { CommandTimeout = 60 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                names.Add((reader.GetString(0), reader.GetString(1)));

        // A partitioned table holds no rows itself: its size and row estimate are those of its leaves.
        const string Sql = $"""
            SELECT con.conrelid, n.nspname::text, c.relname::text, con.conname::text, con.conkey, c.relkind = 'p', fn.nspname::text, fc.relname::text,
                   CASE WHEN c.relkind = 'p' THEN (SELECT COALESCE(sum(pg_catalog.pg_table_size(t.relid)), 0) FROM pg_catalog.pg_partition_tree(c.oid) t WHERE t.isleaf) ELSE pg_catalog.pg_table_size(c.oid) END::bigint,
                   CASE WHEN c.relkind = 'p' THEN (SELECT COALESCE(sum(GREATEST(l.reltuples, 0)), 0) FROM pg_catalog.pg_partition_tree(c.oid) t JOIN pg_catalog.pg_class l ON l.oid = t.relid WHERE t.isleaf) ELSE GREATEST(c.reltuples, 0) END::float8,
                   s.n_tup_del, s.n_tup_upd
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_class fc ON fc.oid = con.confrelid JOIN pg_catalog.pg_namespace fn ON fn.oid = fc.relnamespace
            LEFT JOIN pg_catalog.pg_stat_all_tables s ON s.relid = con.confrelid
            WHERE con.contype = 'f' AND con.conparentid = 0 AND {UserTables}
            ORDER BY n.nspname, c.relname, con.conname
            """;
        var keys = new List<ScanForeignKey>();
        await using (var command = new NpgsqlCommand(Sql, connection, transaction) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                uint table = reader.GetFieldValue<uint>(0);
                var columns = reader.GetFieldValue<short[]>(4).Select(n => attributes.GetValueOrDefault((table, n))).OfType<string>().ToList();
                keys.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), columns, reader.GetString(6), reader.GetString(7), reader.GetBoolean(5), reader.GetInt64(8), reader.GetDouble(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetInt64(11), indexes.TryGetValue(table, out var list) ? list : []));
            }
        return (keys, names);
    }

    private static string Id(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>True when the statement for this key carries CONCURRENTLY: a partitioned table does not accept it.</summary>
    public static bool IsConcurrent(UnindexedForeignKey key, bool concurrently) => concurrently && !key.Key.Partitioned;

    /// <summary>The statement for one key. CONCURRENTLY does not block writes but cannot run in a transaction.</summary>
    public static string Statement(UnindexedForeignKey key, bool concurrently) =>
        $"CREATE INDEX {(IsConcurrent(key, concurrently) ? "CONCURRENTLY " : "")}{Id(key.IndexName)} ON {Id(key.Key.Schema)}.{Id(key.Key.Table)} ({string.Join(", ", key.Key.Columns.Select(Id))});";

    /// <summary>One statement per index: keys that share an index give a single statement.</summary>
    public static IReadOnlyList<string> Statements(IEnumerable<UnindexedForeignKey> keys, bool concurrently) =>
        keys.Select(k => Statement(k, concurrently)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>In one transaction, every index is created or none. One at a time (needed by CONCURRENTLY), it stops at the first failure: the indexes before it stay, and the failed one may be left invalid.</summary>
    public static async Task<IndexApplyResult> ApplyAsync(string connectionString, IReadOnlyList<UnindexedForeignKey> keys, bool concurrently, CancellationToken token = default)
    {
        var indexes = keys.GroupBy(k => Statement(k, concurrently), StringComparer.Ordinal).Select(g => g.First()).ToList();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        if (!concurrently)
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            try
            {
                foreach (var index in indexes)
                {
                    await using var command = new NpgsqlCommand(Statement(index, false), connection, transaction) { CommandTimeout = 0 };
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new(indexes.Count, null, null);
            }
            catch (PostgresException ex) { return new(0, $"{ex.SqlState}: {ex.MessageText}", null); }
        }
        int created = 0;
        foreach (var index in indexes)
        {
            try
            {
                await using var command = new NpgsqlCommand(Statement(index, true), connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                created++;
            }
            catch (NpgsqlException ex) when (!token.IsCancellationRequested)
            {
                // A failed concurrent build leaves the index in the catalog, marked invalid. The failure may have taken the session with it, so the catalog is read on a new one.
                await using var other = new NpgsqlConnection(connectionString);
                await other.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = @name AND NOT i.indisvalid)", other) { CommandTimeout = 30 };
                check.Parameters.AddWithValue("schema", index.Key.Schema); check.Parameters.AddWithValue("name", index.IndexName);
                bool invalid = (bool)(await check.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))!;
                return new(created, ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message, invalid ? index.IndexName : null, invalid ? index.Key.Schema : null);
            }
        }
        return new(created, null, null);
    }

    /// <summary>Removes an invalid index left by a failed concurrent build.</summary>
    public static string DropInvalid(string schema, string index) => $"DROP INDEX CONCURRENTLY {Id(schema)}.{Id(index)};";
}
