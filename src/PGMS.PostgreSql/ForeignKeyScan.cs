using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record ScanColumn(string Name, string Type);
/// <summary>A table as the scan sees it: Keys are the single-column primary key and unique constraints; ForeignKeyColumns are the columns already in any foreign key.</summary>
public sealed record ScanTable(string Schema, string Name, IReadOnlyList<ScanColumn> Columns, string? PrimaryKey, IReadOnlyList<string> Keys, IReadOnlyList<string> ForeignKeyColumns);

public sealed record ForeignKeyCandidate(string SourceSchema, string SourceTable, string SourceColumn, string SourceType, string TargetSchema, string TargetTable, string TargetColumn, string TargetType, string Evidence);

public enum ForeignKeyDataState { Matches, Orphans, Empty, TimedOut, Failed }
/// <summary>What the rows say about a candidate. NonNull and Orphans are meaningful for Matches, Orphans and Empty.</summary>
public sealed record ForeignKeyDataCheck(ForeignKeyDataState State, long NonNull, long Orphans, string? Message = null);

/// <summary>Finds relations that exist only by convention (PGMS-037): names and types point to a key of another table, and the rows confirm it. It never changes anything except in <see cref="ApplyAsync"/>.</summary>
public static class ForeignKeyScan
{
    private static readonly HashSet<string> KeyWords = new(["id", "cod", "codigo", "code", "fk", "pk", "key", "chave"], StringComparer.Ordinal);

    /// <summary>Lower-case words of a name: separated by underscores or at camel-case changes.</summary>
    internal static List<string> Words(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        void Flush() { if (word.Length > 0) { words.Add(word.ToString().ToLowerInvariant()); word.Clear(); } }
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (char.IsUpper(c) && word.Length > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]))) Flush();
            word.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>The part of a column name that names a table: cliente_id, id_cliente, ClienteId and CodigoCliente give "cliente". Empty when no key word marks it.</summary>
    internal static string Stem(string column)
    {
        var words = Words(column);
        if (words.Count < 2) return "";
        if (KeyWords.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        else if (KeyWords.Contains(words[0])) words.RemoveAt(0);
        else return "";
        return string.Concat(words);
    }

    /// <summary>A table name and its singular forms in English and Portuguese, without the usual tb/tbl prefix.</summary>
    internal static HashSet<string> TableForms(string table)
    {
        var words = Words(table);
        if (words.Count > 1 && words[0] is "tb" or "tbl") words.RemoveAt(0);
        string name = string.Concat(words);
        var forms = new HashSet<string>(StringComparer.Ordinal) { name };
        void Ending(string suffix, string replacement) { if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length) forms.Add(name[..^suffix.Length] + replacement); }
        Ending("s", ""); Ending("es", ""); Ending("ies", "y"); Ending("oes", "ao"); Ending("aes", "ao"); Ending("ns", "m"); Ending("eis", "el"); Ending("ais", "al");
        return forms;
    }

    private static string BaseType(string type) { int at = type.IndexOf('('); return (at >= 0 ? type[..at] : type).Trim(); }
    private static string? Family(string type) => BaseType(type) switch
    {
        "smallint" or "integer" or "bigint" => "integer",
        "text" or "character varying" or "character" => "text",
        _ => null
    };
    internal static bool Compatible(string a, string b) => Family(a) is { } family ? family == Family(b) : BaseType(a) == BaseType(b);

    /// <summary>Candidates from names and types only; best evidence first per column, then in table order.</summary>
    public static IReadOnlyList<ForeignKeyCandidate> FindCandidates(IReadOnlyList<ScanTable> tables)
    {
        var found = new List<ForeignKeyCandidate>();
        foreach (var source in tables)
            foreach (var column in source.Columns)
            {
                if (source.ForeignKeyColumns.Contains(column.Name)) continue;
                string stem = Stem(column.Name);
                bool generic = Words(column.Name) is [{ } only] && KeyWords.Contains(only);
                var seen = new HashSet<(string, string)>();
                void Add(ScanTable target, string key, string evidence)
                {
                    if (ReferenceEquals(target, source) && key == column.Name) return;
                    var type = target.Columns.FirstOrDefault(c => c.Name == key);
                    if (type is null || !Compatible(column.Type, type.Type) || !seen.Add((target.Schema, target.Name))) return;
                    found.Add(new(source.Schema, source.Name, column.Name, column.Type, target.Schema, target.Name, key, type.Type, evidence));
                }
                // Same name as the key of another table is the strongest sign; "id" alone says nothing.
                if (!generic)
                    foreach (var target in tables)
                        foreach (string key in target.Keys.Where(k => string.Equals(k, column.Name, StringComparison.OrdinalIgnoreCase)))
                            if (!ReferenceEquals(target, source)) Add(target, key, $"Same name as the key of {target.Name}");
                if (stem.Length > 0)
                    foreach (var target in tables.Where(t => t.PrimaryKey is not null && TableForms(t.Name).Contains(stem)))
                        Add(target, target.PrimaryKey!, $"Name points to table {target.Name}");
            }
        return found;
    }

    /// <summary>Reads ordinary tables outside the system schemas. Partitions are left out: their parent carries the constraint.</summary>
    public static async Task<IReadOnlyList<ScanTable>> ReadTablesAsync(string connectionString, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        var columns = new Dictionary<uint, List<ScanColumn>>();
        var names = new Dictionary<uint, (string Schema, string Name)>();
        var attributes = new Dictionary<(uint, short), string>();
        const string Tables = "c.relkind IN ('r','p') AND NOT c.relispartition AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_%'";
        await using (var command = new NpgsqlCommand($"SELECT c.oid, n.nspname::text, c.relname::text, a.attnum, a.attname::text, pg_catalog.format_type(a.atttypid, a.atttypmod) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped WHERE {Tables} ORDER BY n.nspname, c.relname, a.attnum", connection) { CommandTimeout = 60 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                uint oid = reader.GetFieldValue<uint>(0);
                names[oid] = (reader.GetString(1), reader.GetString(2));
                if (!columns.TryGetValue(oid, out var list)) columns[oid] = list = [];
                list.Add(new(reader.GetString(4), reader.GetString(5)));
                attributes[(oid, reader.GetInt16(3))] = reader.GetString(4);
            }
        var primary = new Dictionary<uint, string>();
        var keys = new Dictionary<uint, List<string>>();
        var foreign = new Dictionary<uint, HashSet<string>>();
        await using (var command = new NpgsqlCommand("SELECT conrelid, contype::text, conkey FROM pg_catalog.pg_constraint WHERE contype IN ('p','u','f') AND conrelid <> 0", connection) { CommandTimeout = 60 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                uint oid = reader.GetFieldValue<uint>(0);
                if (!names.ContainsKey(oid)) continue;
                string type = reader.GetString(1);
                short[] key = reader.GetFieldValue<short[]>(2);
                if (type == "f")
                {
                    if (!foreign.TryGetValue(oid, out var set)) foreign[oid] = set = [];
                    foreach (short number in key) if (attributes.TryGetValue((oid, number), out var name)) set.Add(name);
                }
                else if (key.Length == 1 && attributes.TryGetValue((oid, key[0]), out var name))
                {
                    if (!keys.TryGetValue(oid, out var list)) keys[oid] = list = [];
                    list.Add(name);
                    if (type == "p") primary[oid] = name;
                }
            }
        return names.Select(n => new ScanTable(n.Value.Schema, n.Value.Name, columns[n.Key], primary.GetValueOrDefault(n.Key), keys.TryGetValue(n.Key, out var k) ? k : [], foreign.TryGetValue(n.Key, out var f) ? [.. f] : [])).OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private static string Id(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Table(string schema, string name) => Id(schema) + "." + Id(name);

    /// <summary>Counts the rows without a match, read-only and with a time limit per candidate, so one huge table cannot hold the scan. Cancelling the token stops the scan.</summary>
    public static async Task<ForeignKeyDataCheck> CheckDataAsync(NpgsqlConnection connection, ForeignKeyCandidate candidate, int timeoutSeconds, CancellationToken token = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        try
        {
            await using (var setup = new NpgsqlCommand($"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '{timeoutSeconds * 1000}ms'", connection, transaction) { CommandTimeout = 15 })
                await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            string source = Id(candidate.SourceColumn);
            string sql = $"SELECT count(s.{source}), count(*) FILTER (WHERE s.{source} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {Table(candidate.TargetSchema, candidate.TargetTable)} t WHERE t.{Id(candidate.TargetColumn)} = s.{source})) FROM {Table(candidate.SourceSchema, candidate.SourceTable)} s";
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds + 15 };
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            long nonNull = reader.GetInt64(0), orphans = reader.GetInt64(1);
            return new(nonNull == 0 ? ForeignKeyDataState.Empty : orphans == 0 ? ForeignKeyDataState.Matches : ForeignKeyDataState.Orphans, nonNull, orphans);
        }
        catch (PostgresException ex) when (ex.SqlState == "57014" && !token.IsCancellationRequested) { return new(ForeignKeyDataState.TimedOut, 0, 0, $"Not checked: no answer in {timeoutSeconds} s."); }
        catch (PostgresException ex) when (!token.IsCancellationRequested) { return new(ForeignKeyDataState.Failed, 0, 0, ex.MessageText); }
    }

    /// <summary>A valid name of at most 63 bytes; a long one keeps a hash of the full name so two long names do not collide.</summary>
    internal static string ConstraintName(ForeignKeyCandidate candidate)
    {
        string name = $"fk_{candidate.SourceTable}_{candidate.SourceColumn}";
        if (Encoding.UTF8.GetByteCount(name) <= 63) return name;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        var kept = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(kept.ToString() + rune) > 54) break;
            kept.Append(rune.ToString());
        }
        return kept + "_" + hash;
    }

    /// <summary>The statement for one candidate. NOT VALID leaves existing rows unchecked and enforces the key on new ones; rows without a match would make it fail otherwise.</summary>
    public static string Statement(ForeignKeyCandidate candidate, bool notValid) =>
        $"ALTER TABLE {Table(candidate.SourceSchema, candidate.SourceTable)} ADD CONSTRAINT {Id(ConstraintName(candidate))} FOREIGN KEY ({Id(candidate.SourceColumn)}) REFERENCES {Table(candidate.TargetSchema, candidate.TargetTable)} ({Id(candidate.TargetColumn)})" + (notValid ? " NOT VALID" : "") + ";";

    /// <summary>All statements in one transaction: either every key is created or none.</summary>
    public static async Task ApplyAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (string statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
