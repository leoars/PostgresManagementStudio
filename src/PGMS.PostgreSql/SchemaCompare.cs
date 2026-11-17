using System.Globalization;
using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

// Schema Compare (PGMS-075, M1102). SSMS 22.11 Tools ▸ Schema Compare was piloted (docs/MARCO-M1102.md): a source and a target, Compare,
// a grid of differences (Include, Type, Source Name, Action, Target Name) and Generate Script, which opens the script in a query window on the target.
// Here the catalogs are only read (a READ ONLY transaction); the script is only produced, never run.

/// <summary>Object types in the order a script creates them.</summary>
public enum SchemaObjectType { Schema, Extension, Type, Domain, Sequence, Table, Function, Procedure, View, MaterializedView, Index, Constraint, Trigger }

public enum SchemaDifferenceAction { Add, Change, Delete }

public sealed record SchemaColumn(string Name, string Definition, string Type, string? Collation, bool NotNull, string? Default, string Identity, string Generated);

/// <summary>One object read from a database. <see cref="Definition"/> is what is compared and shown; the other fields let a change be scripted without recreating.</summary>
public sealed record SchemaObject(SchemaObjectType Type, string Schema, string Name, string? Parent, string Definition, uint Oid)
{
    public IReadOnlyList<SchemaColumn> Columns { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    /// <summary>Constraint type (p, u, f, c, x), routine identity arguments, sequence options or the partition parent, depending on <see cref="Type"/>.</summary>
    public string Detail { get; init; } = "";
    public string PartitionBound { get; init; } = "";
    /// <summary>What is compared when it is not the definition itself: a table's columns sorted by name, since PostgreSQL cannot reorder columns.</summary>
    public string? CompareText { get; init; }
    public string Key => $"{Type}|{Schema}|{Parent}|{Name}";
    public string DisplayName => Type switch
    {
        SchemaObjectType.Schema or SchemaObjectType.Extension => Name,
        _ when Parent is not null => $"{Schema}.{Parent}.{Name}",
        _ => $"{Schema}.{Name}"
    };
}

public sealed record SchemaSnapshot(string Server, string Database, IReadOnlyList<SchemaObject> Objects)
{
    public string Label => $"{Server}.{Database}";
}

public sealed class SchemaDifference(SchemaObjectType type, SchemaDifferenceAction action, SchemaObject? source, SchemaObject? target)
{
    public SchemaObjectType Type { get; } = type;
    public SchemaDifferenceAction Action { get; } = action;
    public SchemaObject? Source { get; } = source;
    public SchemaObject? Target { get; } = target;
    public bool Include { get; set; } = true;
    public string TypeName => SchemaCompare.TypeName(Type);
    public string SourceName => Source?.DisplayName ?? "";
    public string TargetName => Target?.DisplayName ?? "";
    public string Schema => (Source ?? Target)!.Schema;
}

public static class SchemaCompare
{
    private const string NL = "\r\n";

    public static string TypeName(SchemaObjectType type) => type switch
    {
        SchemaObjectType.MaterializedView => "Materialized View",
        SchemaObjectType.Procedure => "Procedure",
        _ => type.ToString()
    };

    /// <summary>Reads every user object of the database behind <paramref name="connectionString"/>: system schemas and objects that belong to an
    /// extension are left out (the extension itself is compared). Only reads, in one REPEATABLE READ READ ONLY transaction with search_path = pg_catalog,
    /// so every name in the definitions comes qualified and the two databases are described the same way.</summary>
    public static async Task<SchemaSnapshot> ReadAsync(string connectionString, CancellationToken token = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "PGMS schema compare" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await Run(connection, transaction, "SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog", token).ConfigureAwait(false);
        var objects = new List<SchemaObject>();
        var tables = new Dictionary<uint, (string Schema, string Name)>();

        async Task<List<object?[]>> Rows(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        static uint U(object? v) => Convert.ToUInt32(v, CultureInfo.InvariantCulture);
        static string S(object? v) => (string)v!;

        const string UserSchema = "n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp\\_%'";
        static string NotFromExtension(string catalog, string oid) => $"NOT EXISTS (SELECT 1 FROM pg_depend e WHERE e.classid = '{catalog}'::regclass AND e.objid = {oid} AND e.deptype = 'e')";

        foreach (var r in await Rows($"SELECT n.oid, n.nspname, quote_ident(n.nspname) FROM pg_namespace n WHERE {UserSchema} AND {NotFromExtension("pg_namespace", "n.oid")} ORDER BY n.oid").ConfigureAwait(false))
            objects.Add(new(SchemaObjectType.Schema, S(r[1]), S(r[1]), null, $"CREATE SCHEMA {r[2]};", U(r[0])));

        foreach (var r in await Rows("SELECT x.oid, n.nspname, x.extname, quote_ident(x.extname), quote_ident(n.nspname), x.extversion FROM pg_extension x JOIN pg_namespace n ON n.oid = x.extnamespace WHERE x.extname <> 'plpgsql' ORDER BY x.oid").ConfigureAwait(false))
            objects.Add(new(SchemaObjectType.Extension, S(r[1]), S(r[2]), null, $"CREATE EXTENSION IF NOT EXISTS {r[3]} WITH SCHEMA {r[4]} VERSION '{S(r[5]).Replace("'", "''", StringComparison.Ordinal)}';", U(r[0])) { Detail = S(r[5]) });

        // Enum and composite types; a composite type that is the row type of a table is part of the table.
        foreach (var r in await Rows($"""
            SELECT t.oid, n.nspname, t.typname, quote_ident(n.nspname) || '.' || quote_ident(t.typname), t.typtype::text,
                   ARRAY(SELECT e.enumlabel FROM pg_enum e WHERE e.enumtypid = t.oid ORDER BY e.enumsortorder)::text[],
                   ARRAY(SELECT quote_ident(a.attname) || ' ' || format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum)::text[]
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace LEFT JOIN pg_class c ON c.oid = t.typrelid
            WHERE {UserSchema} AND (t.typtype = 'e' OR (t.typtype = 'c' AND c.relkind = 'c')) AND {NotFromExtension("pg_type", "t.oid")}
            ORDER BY t.oid
            """).ConfigureAwait(false))
        {
            var labels = (string[])r[5]!;
            string body = S(r[4]) == "e" ? "ENUM (" + string.Join(", ", labels.Select(Literal)) + ")" : "(" + NL + string.Join("," + NL, ((string[])r[6]!).Select(a => "    " + a)) + NL + ")";
            objects.Add(new(SchemaObjectType.Type, S(r[1]), S(r[2]), null, $"CREATE TYPE {r[3]} AS {body};", U(r[0])) { Labels = S(r[4]) == "e" ? labels : [], Detail = S(r[4]) });
        }

        foreach (var r in await Rows($"""
            SELECT t.oid, n.nspname, t.typname, quote_ident(n.nspname) || '.' || quote_ident(t.typname), format_type(t.typbasetype, t.typtypmod),
                   CASE WHEN t.typcollation <> 0 AND t.typcollation <> b.typcollation THEN (SELECT quote_ident(cn.nspname) || '.' || quote_ident(co.collname) FROM pg_collation co JOIN pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = t.typcollation) END,
                   t.typdefault, t.typnotnull,
                   ARRAY(SELECT 'CONSTRAINT ' || quote_ident(k.conname) || ' ' || pg_get_constraintdef(k.oid) FROM pg_constraint k WHERE k.contypid = t.oid AND k.contype = 'c' ORDER BY k.conname)::text[]
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_type b ON b.oid = t.typbasetype
            WHERE {UserSchema} AND t.typtype = 'd' AND {NotFromExtension("pg_type", "t.oid")}
            ORDER BY t.oid
            """).ConfigureAwait(false))
        {
            var sql = new StringBuilder($"CREATE DOMAIN {r[3]} AS {r[4]}");
            if (r[5] is string collation) sql.Append(" COLLATE ").Append(collation);
            if (r[6] is string def) sql.Append(" DEFAULT ").Append(def);
            if (r[7] is true) sql.Append(" NOT NULL");
            foreach (var check in (string[])r[8]!) sql.Append(NL).Append("    ").Append(check);
            objects.Add(new(SchemaObjectType.Domain, S(r[1]), S(r[2]), null, sql.Append(';').ToString(), U(r[0])));
        }

        // Sequences of identity columns belong to the column.
        foreach (var r in await Rows($"""
            SELECT c.oid, n.nspname, c.relname, quote_ident(n.nspname) || '.' || quote_ident(c.relname),
                   'AS ' || format_type(s.seqtypid, NULL) || ' INCREMENT BY ' || s.seqincrement || ' MINVALUE ' || s.seqmin || ' MAXVALUE ' || s.seqmax || ' START WITH ' || s.seqstart || ' CACHE ' || s.seqcache || CASE WHEN s.seqcycle THEN ' CYCLE' ELSE ' NO CYCLE' END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_sequence s ON s.seqrelid = c.oid
            WHERE {UserSchema} AND c.relkind = 'S' AND {NotFromExtension("pg_class", "c.oid")}
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'i')
            ORDER BY c.oid
            """).ConfigureAwait(false))
            objects.Add(new(SchemaObjectType.Sequence, S(r[1]), S(r[2]), null, $"CREATE SEQUENCE {r[3]} {r[4]};", U(r[0])) { Detail = S(r[4]) });

        // Tables (ordinary, partitioned, partitions). Columns of a partition come from its parent.
        var tableRows = await Rows($"""
            SELECT c.oid, n.nspname, c.relname, quote_ident(n.nspname) || '.' || quote_ident(c.relname), c.relkind::text, c.relispartition,
                   CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END,
                   CASE WHEN c.relispartition THEN (SELECT quote_ident(pn.nspname) || '.' || quote_ident(p.relname) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent JOIN pg_namespace pn ON pn.oid = p.relnamespace WHERE i.inhrelid = c.oid) END,
                   CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound, c.oid) END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {UserSchema} AND c.relkind IN ('r','p') AND NOT c.relpersistence = 't' AND {NotFromExtension("pg_class", "c.oid")}
            ORDER BY c.oid
            """).ConfigureAwait(false);
        var columnRows = await Rows($"""
            SELECT a.attrelid, a.attname, quote_ident(a.attname), format_type(a.atttypid, a.atttypmod),
                   CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN (SELECT quote_ident(cn.nspname) || '.' || quote_ident(co.collname) FROM pg_collation co JOIN pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = a.attcollation) END,
                   a.attnotnull, pg_get_expr(d.adbin, d.adrelid), a.attidentity::text, a.attgenerated::text
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE {UserSchema} AND c.relkind IN ('r','p') AND NOT c.relispartition AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attrelid, a.attnum
            """).ConfigureAwait(false);
        var columns = columnRows.GroupBy(r => U(r[0])).ToDictionary(g => g.Key, g => g.Select(r => Column(S(r[1]), S(r[2]), S(r[3]), r[4] as string, r[5] is true, r[6] as string, S(r[7]), S(r[8]))).ToList());
        foreach (var r in tableRows)
        {
            uint oid = U(r[0]);
            tables[oid] = (S(r[1]), S(r[2]));
            string definition;
            List<SchemaColumn> cols = [];
            if (r[5] is true)
                definition = $"CREATE TABLE {r[3]} PARTITION OF {r[7]}{NL}    {r[8]};";
            else
            {
                cols = columns.TryGetValue(oid, out var list) ? list : [];
                definition = $"CREATE TABLE {r[3]} (" + (cols.Count == 0 ? "" : NL + string.Join("," + NL, cols.Select(c => "    " + c.Definition)) + NL) + ")" + (r[6] is string key ? NL + "PARTITION BY " + key : "") + ";";
            }
            string? compareText = r[5] is true ? null : string.Join(NL, cols.Select(c => c.Definition).Order(StringComparer.Ordinal)) + NL + (r[6] as string ?? "");
            objects.Add(new(SchemaObjectType.Table, S(r[1]), S(r[2]), null, definition, oid) { Columns = cols, Detail = r[7] as string ?? "", PartitionBound = r[8] as string ?? "", CompareText = compareText });
        }

        foreach (var r in await Rows($"""
            SELECT p.oid, n.nspname, p.proname, p.prokind::text, pg_get_function_identity_arguments(p.oid), quote_ident(n.nspname) || '.' || quote_ident(p.proname), pg_get_functiondef(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {UserSchema} AND p.prokind IN ('f','p') AND {NotFromExtension("pg_proc", "p.oid")}
            ORDER BY p.oid
            """).ConfigureAwait(false))
        {
            string definition = S(r[6]).TrimEnd();
            if (!definition.EndsWith(';')) definition += ";";
            objects.Add(new(S(r[3]) == "p" ? SchemaObjectType.Procedure : SchemaObjectType.Function, S(r[1]), $"{r[2]}({r[4]})", null, definition.Replace("\n", NL, StringComparison.Ordinal).Replace("\r\r", "\r", StringComparison.Ordinal), U(r[0])) { Detail = $"{r[5]}({r[4]})" });
        }

        foreach (var r in await Rows($"""
            SELECT c.oid, n.nspname, c.relname, quote_ident(n.nspname) || '.' || quote_ident(c.relname), c.relkind::text, pg_get_viewdef(c.oid, true)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {UserSchema} AND c.relkind IN ('v','m') AND {NotFromExtension("pg_class", "c.oid")}
            ORDER BY c.oid
            """).ConfigureAwait(false))
        {
            string body = S(r[5]).Trim().TrimEnd(';').Replace("\n", NL, StringComparison.Ordinal);
            bool materialized = S(r[4]) == "m";
            tables[U(r[0])] = (S(r[1]), S(r[2])); // indexes of materialized views, INSTEAD OF triggers of views
            objects.Add(new(materialized ? SchemaObjectType.MaterializedView : SchemaObjectType.View, S(r[1]), S(r[2]), null,
                materialized ? $"CREATE MATERIALIZED VIEW {r[3]} AS{NL}{body}{NL}WITH NO DATA;" : $"CREATE OR REPLACE VIEW {r[3]} AS{NL}{body};", U(r[0])) { Detail = r[3] as string ?? "" });
        }

        // Indexes that are not behind a constraint; a partition's copy of a partitioned index comes with the parent's.
        foreach (var r in await Rows($"""
            SELECT i.indexrelid, n.nspname, c.relname, i.indrelid, pg_get_indexdef(i.indexrelid), quote_ident(n.nspname) || '.' || quote_ident(c.relname)
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {UserSchema} AND NOT c.relispartition AND {NotFromExtension("pg_class", "c.oid")}
              AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.contype IN ('p','u','x'))
            ORDER BY i.indexrelid
            """).ConfigureAwait(false))
            if (tables.TryGetValue(U(r[3]), out var table))
                objects.Add(new(SchemaObjectType.Index, S(r[1]), S(r[2]), table.Name, S(r[4]) + ";", U(r[0])) { Detail = S(r[5]) });

        foreach (var r in await Rows($"""
            SELECT k.oid, k.conname, k.conrelid, k.contype::text, pg_get_constraintdef(k.oid), quote_ident(k.conname)
            FROM pg_constraint k
            WHERE k.conrelid <> 0 AND k.contype IN ('p','u','f','c','x') AND (k.conislocal OR k.coninhcount = 0) AND k.conparentid = 0
            ORDER BY k.oid
            """).ConfigureAwait(false))
            if (tables.TryGetValue(U(r[2]), out var table))
                objects.Add(new(SchemaObjectType.Constraint, table.Schema, S(r[1]), table.Name, $"ALTER TABLE {Qualified(table)} ADD CONSTRAINT {r[5]} {r[4]};", U(r[0])) { Detail = S(r[3]) });

        foreach (var r in await Rows("""
            SELECT g.oid, g.tgname, g.tgrelid, pg_get_triggerdef(g.oid), quote_ident(g.tgname)
            FROM pg_trigger g WHERE NOT g.tgisinternal AND g.tgparentid = 0
            ORDER BY g.oid
            """).ConfigureAwait(false))
            if (tables.TryGetValue(U(r[2]), out var table))
                objects.Add(new(SchemaObjectType.Trigger, table.Schema, S(r[1]), table.Name, S(r[3]) + ";", U(r[0])) { Detail = S(r[4]) });

        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new SchemaSnapshot(builder.Host is { Length: > 0 } host ? $"{host}:{builder.Port}" : "server", connection.Database, objects);
    }

    // Qualified names are re-quoted only when needed, like quote_ident, so built text matches what the server's own functions print.
    private static string Ident(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_$]*$") && !Reserved.Contains(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case", "cast", "check", "collate", "column", "constraint", "create", "current_catalog", "current_date", "current_role", "current_time", "current_timestamp", "current_user", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for", "foreign", "from", "grant", "group", "having", "in", "initially", "intersect", "into", "lateral", "leading", "limit", "localtime", "localtimestamp", "not", "null", "offset", "on", "only", "or", "order", "placing", "primary", "references", "returning", "select", "session_user", "some", "symmetric", "table", "then", "to", "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where", "window", "with" };
    private static string Qualified((string Schema, string Name) table) => Ident(table.Schema) + "." + Ident(table.Name);
    private static string Qualified(SchemaObject o) => Ident(o.Schema) + "." + Ident(o.Parent ?? o.Name);
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static SchemaColumn Column(string name, string quoted, string type, string? collation, bool notNull, string? @default, string identity, string generated)
    {
        var sql = new StringBuilder(quoted).Append(' ').Append(type);
        if (collation is not null) sql.Append(" COLLATE ").Append(collation);
        if (generated is "s" or "v") sql.Append(" GENERATED ALWAYS AS (").Append(@default).Append(')').Append(generated == "s" ? " STORED" : " VIRTUAL");
        else if (identity is "a" or "d") sql.Append(identity == "a" ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY");
        else if (@default is not null) sql.Append(" DEFAULT ").Append(@default);
        if (notNull && identity is not ("a" or "d")) sql.Append(" NOT NULL");
        return new SchemaColumn(name, sql.ToString(), type, collation, notNull, @default, identity, generated);
    }

    private static async Task Run(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>The differences that make <paramref name="target"/> look like <paramref name="source"/>, ordered by type and name.</summary>
    public static IReadOnlyList<SchemaDifference> Compare(SchemaSnapshot source, SchemaSnapshot target)
    {
        var inTarget = target.Objects.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.First());
        var inSource = source.Objects.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.First());
        var list = new List<SchemaDifference>();
        foreach (var s in inSource.Values)
        {
            if (!inTarget.TryGetValue(s.Key, out var t)) list.Add(new(s.Type, SchemaDifferenceAction.Add, s, null));
            else if (!string.Equals(s.CompareText ?? s.Definition, t.CompareText ?? t.Definition, StringComparison.Ordinal)) list.Add(new(s.Type, SchemaDifferenceAction.Change, s, t));
        }
        foreach (var t in inTarget.Values)
            if (!inSource.ContainsKey(t.Key)) list.Add(new(t.Type, SchemaDifferenceAction.Delete, null, t));
        return list.OrderBy(d => d.Type).ThenBy(d => (d.Source ?? d.Target)!.DisplayName, StringComparer.Ordinal).ToList();
    }

    public static string Summary(IReadOnlyList<SchemaDifference> differences) =>
        differences.Count == 0 ? "Comparison completed. No differences detected."
        : $"Comparison completed. Found {differences.Count:N0} difference{(differences.Count == 1 ? "" : "s")} ({differences.Count(d => d.Action == SchemaDifferenceAction.Add):N0} Add, {differences.Count(d => d.Action == SchemaDifferenceAction.Change):N0} Change, {differences.Count(d => d.Action == SchemaDifferenceAction.Delete):N0} Delete).";

    // Drops run in the reverse of the creation order; foreign keys go first so the tables and keys they point at can go.
    private static int DropRank(SchemaDifference d) => d.Type == SchemaObjectType.Constraint && (d.Target ?? d.Source)!.Detail == "f" ? -1 : -(int)d.Type;
    private static int CreateRank(SchemaDifference d) => d.Type == SchemaObjectType.Constraint && (d.Source ?? d.Target)!.Detail == "f" ? 100 : (int)d.Type;
    private static bool Recreates(SchemaDifference d) => d.Action == SchemaDifferenceAction.Change && d.Type is SchemaObjectType.Index or SchemaObjectType.Constraint or SchemaObjectType.Trigger or SchemaObjectType.MaterializedView or SchemaObjectType.Domain
        || d.Action == SchemaDifferenceAction.Change && d.Type == SchemaObjectType.Type && d.Source!.Detail != "e";

    /// <summary>The script that makes the target look like the source, for the included differences only. It is text for the user to review; nothing here runs it.</summary>
    public static string GenerateScript(SchemaSnapshot source, SchemaSnapshot target, IEnumerable<SchemaDifference> differences, DateTime? now = null)
    {
        var included = differences.Where(d => d.Include).ToList();
        var sql = new StringBuilder();
        sql.Append("/*").Append(NL)
           .Append("Schema Compare script for ").Append(target.Label).Append(NL)
           .Append("Source: ").Append(source.Label).Append(NL)
           .Append("Target: ").Append(target.Label).Append(NL)
           .Append("Generated by PGMS on ").Append((now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(NL).Append(NL)
           .Append("Running it makes the target look like the source. PGMS did not run it: review it first.").Append(NL)
           .Append("Statements marked \"Data loss\" drop rows or values. Owners, privileges, comments and statistics are not compared.").Append(NL)
           .Append("*/").Append(NL).Append(NL);
        if (included.Count == 0) return sql.Append("-- No differences are included.").Append(NL).ToString();
        // A new enum value cannot be used in the transaction that adds it (a default or a cast in the same script would fail), so these come first, on their own.
        bool IsEnumChange(SchemaDifference d) => d.Action == SchemaDifferenceAction.Change && d.Type == SchemaObjectType.Type && d.Source!.Detail == "e";
        var enums = included.Where(IsEnumChange).OrderBy(d => d.Source!.Oid).ToList();
        if (enums.Count > 0)
        {
            sql.Append("-- Enum values, outside the transaction: a new value can be used only after it is committed.").Append(NL);
            foreach (var d in enums) sql.Append(Change(d.Source!, d.Target!)).Append(NL);
            sql.Append(NL);
        }
        included.RemoveAll(IsEnumChange);
        sql.Append("BEGIN;").Append(NL);
        var drops = included.Where(d => d.Action == SchemaDifferenceAction.Delete || Recreates(d)).OrderBy(DropRank).ThenByDescending(d => (d.Target ?? d.Source)!.Oid).ToList();
        if (drops.Count > 0) sql.Append(NL).Append("-- Drop").Append(NL);
        foreach (var d in drops) sql.Append(Drop(d.Target!, d.Action == SchemaDifferenceAction.Change)).Append(NL);
        var creates = included.Where(d => d.Action != SchemaDifferenceAction.Delete).OrderBy(CreateRank).ThenBy(d => d.Action).ThenBy(d => d.Source!.Oid).ToList();
        if (creates.Count > 0) sql.Append(NL).Append("-- Create and change").Append(NL);
        foreach (var d in creates)
        {
            if (d.Action == SchemaDifferenceAction.Add || Recreates(d)) sql.Append(d.Source!.Definition).Append(NL);
            else sql.Append(Change(d.Source!, d.Target!)).Append(NL);
            if (d.Type == SchemaObjectType.MaterializedView) sql.Append($"-- Fill it with REFRESH MATERIALIZED VIEW {Qualified(d.Source!)};").Append(NL);
        }
        return sql.Append(NL).Append("COMMIT;").Append(NL).ToString();
    }

    private static string Drop(SchemaObject o, bool recreate)
    {
        string name = o.Type switch { SchemaObjectType.Schema or SchemaObjectType.Extension => Ident(o.Name), _ => Ident(o.Schema) + "." + Ident(o.Name) };
        string loss = recreate ? "" : o.Type is SchemaObjectType.Table or SchemaObjectType.MaterializedView ?"-- Data loss: the rows go with it." + NL : "";
        return loss + o.Type switch
        {
            SchemaObjectType.Schema => $"DROP SCHEMA {name};",
            SchemaObjectType.Extension => $"DROP EXTENSION {name};",
            SchemaObjectType.Type => $"DROP TYPE {name};",
            SchemaObjectType.Domain => $"DROP DOMAIN {name};",
            SchemaObjectType.Sequence => $"DROP SEQUENCE IF EXISTS {name}; -- a sequence owned by a dropped column is already gone",
            SchemaObjectType.Table => $"DROP TABLE {name};",
            SchemaObjectType.Function => $"DROP FUNCTION {o.Detail};",
            SchemaObjectType.Procedure => $"DROP PROCEDURE {o.Detail};",
            SchemaObjectType.View => $"DROP VIEW {name};",
            SchemaObjectType.MaterializedView => $"DROP MATERIALIZED VIEW {name};",
            SchemaObjectType.Index => $"DROP INDEX {name};",
            SchemaObjectType.Constraint => $"ALTER TABLE {Qualified(o)} DROP CONSTRAINT {Ident(o.Name)};",
            SchemaObjectType.Trigger => $"DROP TRIGGER {Ident(o.Name)} ON {Qualified(o)};",
            _ => throw new ArgumentOutOfRangeException(nameof(o))
        };
    }

    /// <summary>A change that keeps the object: columns altered one by one, CREATE OR REPLACE for views and routines, ALTER for sequences, enums and extensions.</summary>
    private static string Change(SchemaObject s, SchemaObject t)
    {
        switch (s.Type)
        {
            case SchemaObjectType.Table: return TableChange(s, t);
            case SchemaObjectType.Sequence: return $"ALTER SEQUENCE {Ident(s.Schema)}.{Ident(s.Name)} {s.Detail};";
            case SchemaObjectType.Extension: return $"ALTER EXTENSION {Ident(s.Name)} UPDATE TO {Literal(s.Detail)};";
            case SchemaObjectType.View: return "-- CREATE OR REPLACE VIEW fails when columns are removed, renamed or retyped; then drop the view first." + NL + s.Definition;
            case SchemaObjectType.Function or SchemaObjectType.Procedure: return "-- CREATE OR REPLACE fails when the result type changes; then drop the routine first." + NL + s.Definition;
            case SchemaObjectType.Type: // enum
                var sql = new StringBuilder();
                string type = Ident(s.Schema) + "." + Ident(s.Name);
                foreach (var label in s.Labels.Where(l => !t.Labels.Contains(l)))
                {
                    int at = s.Labels.ToList().IndexOf(label);
                    string? before = s.Labels.Skip(at + 1).FirstOrDefault(t.Labels.Contains);
                    sql.Append($"ALTER TYPE {type} ADD VALUE {Literal(label)}{(before is null ? "" : " BEFORE " + Literal(before))};").Append(NL);
                }
                foreach (var label in t.Labels.Where(l => !s.Labels.Contains(l)))
                    sql.Append($"-- The target value {Literal(label)} is not in the source; PostgreSQL cannot remove an enum value.").Append(NL);
                if (sql.Length == 0) sql.Append("-- The enum values are the same but in another order; PostgreSQL cannot reorder them.").Append(NL);
                return sql.ToString().TrimEnd();
            default: return s.Definition;
        }
    }

    private static string TableChange(SchemaObject s, SchemaObject t)
    {
        string table = Ident(s.Schema) + "." + Ident(s.Name);
        var sql = new StringBuilder();
        void Line(string text) => sql.Append(text).Append(NL);
        if (s.Detail.Length > 0 || t.Detail.Length > 0)
        {
            if (s.Detail != t.Detail || s.PartitionBound != t.PartitionBound)
            {
                if (t.Detail.Length > 0) Line($"ALTER TABLE {t.Detail} DETACH PARTITION {table};");
                if (s.Detail.Length > 0) Line($"ALTER TABLE {s.Detail} ATTACH PARTITION {table} {s.PartitionBound};");
                else Line($"-- {table} is a partition only in the target; it stays a table after the detach.");
            }
            return sql.ToString().TrimEnd();
        }
        string KeyOf(string definition) => definition.Contains(NL + "PARTITION BY ", StringComparison.Ordinal) ? definition[(definition.LastIndexOf(NL + "PARTITION BY ", StringComparison.Ordinal) + NL.Length)..] : "";
        if (KeyOf(s.Definition) != KeyOf(t.Definition)) Line($"-- The partitioning of {table} differs ({(KeyOf(s.Definition) is { Length: > 0 } a ? a.TrimEnd(';') : "none")} in the source); PostgreSQL cannot change it in place: create a new table and move the rows.");
        var targetColumns = t.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var sourceNames = s.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in t.Columns.Where(c => !sourceNames.Contains(c.Name)))
        {
            Line($"-- Data loss: the values of column {Ident(c.Name)} go with it.");
            Line($"ALTER TABLE {table} DROP COLUMN {Ident(c.Name)};");
        }
        foreach (var c in s.Columns)
        {
            if (!targetColumns.TryGetValue(c.Name, out var o)) { Line($"ALTER TABLE {table} ADD COLUMN {c.Definition};"); continue; }
            if (c.Definition == o.Definition) continue;
            string column = $"ALTER TABLE {table} ALTER COLUMN {Ident(c.Name)}";
            bool wasGenerated = o.Generated is "s" or "v", isGenerated = c.Generated is "s" or "v";
            if (isGenerated != wasGenerated && isGenerated)
            {
                Line($"-- Data loss: {Ident(c.Name)} becomes a generated column, so it is dropped and added again.");
                Line($"ALTER TABLE {table} DROP COLUMN {Ident(c.Name)};");
                Line($"ALTER TABLE {table} ADD COLUMN {c.Definition};");
                continue;
            }
            if (c.Type != o.Type || c.Collation != o.Collation)
                Line($"{column} TYPE {c.Type}{(c.Collation is null ? "" : " COLLATE " + c.Collation)}; -- add USING … if the values do not cast on their own");
            if (wasGenerated && !isGenerated) Line($"{column} DROP EXPRESSION;");
            else if (isGenerated && c.Default != o.Default) Line($"{column} SET EXPRESSION AS ({c.Default}); -- PostgreSQL 17 or later");
            bool wasIdentity = o.Identity is "a" or "d", isIdentity = c.Identity is "a" or "d";
            if (wasIdentity && !isIdentity) Line($"{column} DROP IDENTITY;");
            if (!isGenerated && !isIdentity && c.Default != o.Default) Line(c.Default is null ? $"{column} DROP DEFAULT;" : $"{column} SET DEFAULT {c.Default};");
            if (!wasIdentity && isIdentity)
            {
                if (o.Default is not null && !wasGenerated) Line($"{column} DROP DEFAULT;");
                Line($"{column} ADD GENERATED {(c.Identity == "a" ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY;");
            }
            else if (wasIdentity && isIdentity && c.Identity != o.Identity) Line($"{column} SET GENERATED {(c.Identity == "a" ? "ALWAYS" : "BY DEFAULT")};");
            bool sourceNotNull = c.NotNull || isIdentity, targetNotNull = o.NotNull || wasIdentity;
            if (sourceNotNull != targetNotNull && !(isIdentity && !wasIdentity)) Line(sourceNotNull ? $"{column} SET NOT NULL;" : $"{column} DROP NOT NULL;");
        }
        if (sql.Length == 0) Line($"-- Only the order of the columns of {table} differs; PostgreSQL cannot reorder columns in place.");
        return sql.ToString().TrimEnd();
    }
}
