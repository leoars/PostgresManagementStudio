using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Structure of one database as compact text for an AI provider: schemas, tables, views, columns, types,
/// keys, enum labels and comments. Read from the catalog only; never reads table rows.</summary>
public sealed record SchemaContext(string Text, string Database, string ServerVersion, int Relations, int Columns, int OmittedRelations)
{
    public const int DefaultMaximumCharacters = 60_000;
}

public sealed class SchemaContextService(string connectionString)
{
    private sealed record Column(string Name, string Type, bool NotNull, string? Comment);
    // Schema, QuotedName and column names come quoted by quote_ident; Name stays raw for matching the request.
    private sealed class Relation(string schema, string name, string quotedName, string kind, string? comment)
    {
        public string Schema { get; } = schema; public string Name { get; } = name; public string Kind { get; } = kind; public string? Comment { get; } = comment;
        public List<Column> Columns { get; } = [];
        public List<string> Constraints { get; } = [];
        public string QualifiedName { get; } = schema + "." + quotedName;
    }

    private const string SchemaFilter = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_toast' AND n.nspname !~ '^pg_temp_'";

    /// <summary>Builds the context. When it exceeds <paramref name="maximumCharacters"/>, relations named in <paramref name="request"/>
    /// are kept first and the rest are listed by name only, as far as the budget allows.</summary>
    public async Task<SchemaContext> BuildAsync(string request, int maximumCharacters = SchemaContext.DefaultMaximumCharacters, CancellationToken token = default)
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

        var relations = new Dictionary<uint, Relation>();
        await using (var command = new NpgsqlCommand($"""
            SELECT c.oid, pg_catalog.quote_ident(n.nspname), c.relname, c.relkind::text, pg_catalog.obj_description(c.oid, 'pg_class'),
                   pg_catalog.quote_ident(a.attname), pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_catalog.col_description(c.oid, a.attnum),
                   pg_catalog.quote_ident(c.relname)
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
                    relations[oid] = relation = new(reader.GetString(1), reader.GetString(2), reader.GetString(9), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
                if (!reader.IsDBNull(5)) relation.Columns.Add(new(reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }
        await using (var command = new NpgsqlCommand($"""
            SELECT con.conrelid, pg_catalog.pg_get_constraintdef(con.oid, true)
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype IN ('p', 'u', 'f') AND {SchemaFilter}
            ORDER BY con.conrelid, con.contype DESC, con.conname
            """, connection) { CommandTimeout = 30 })
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (relations.TryGetValue(reader.GetFieldValue<uint>(0), out var relation)) relation.Constraints.Add(reader.GetString(1));
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

        // Relations named in the request first, then catalog order.
        var ordered = relations.Values
            .OrderByDescending(r => request.Contains(r.Name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Schema, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var text = new StringBuilder();
        text.Append("-- PostgreSQL ").Append(version).Append(", database ").Append(quotedDatabase).Append('\n');
        foreach (string line in enums) text.Append(line).Append('\n');
        int included = 0, columns = 0;
        var omitted = new List<Relation>();
        foreach (var relation in ordered)
        {
            string block = Describe(relation);
            if (text.Length + block.Length > maximumCharacters) { omitted.Add(relation); continue; }
            text.Append(block); included++; columns += relation.Columns.Count;
        }
        if (omitted.Count > 0)
        {
            text.Append("-- ").Append(omitted.Count).Append(" more relations omitted for size; names only:\n");
            int listed = 0;
            foreach (var relation in omitted)
            {
                string name = "--   " + relation.QualifiedName + "\n";
                if (text.Length + name.Length > maximumCharacters + 4_000) { text.Append("--   …\n"); break; }
                text.Append(name); listed++;
            }
        }
        return new(text.ToString(), database, version, included, columns, omitted.Count);
    }

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

    private static string OneLine(string value)
    {
        string line = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
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
