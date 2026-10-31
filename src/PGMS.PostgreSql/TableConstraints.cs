using Npgsql;

namespace PGMS.PostgreSql;

public enum TableConstraintOperation { AddPrimaryKey, AddUnique, Drop }

public sealed record TableConstraintInfo(string Name, string Kind, string Definition)
{
    public ConstraintIdentity? Identity { get; init; }
    public string Summary => $"{Kind}: {Name} — {Definition}";
}

public sealed record TableConstraintEdit(
    TableConstraintOperation Operation,
    string Schema,
    string Table,
    string Name,
    IReadOnlyList<string> Columns) : IAdministrativeEdit
{
    public ConstraintIdentity? ExpectedIdentity { get; init; }

    public static string[] ParseColumns(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

    public string ToSql()
    {
        string table = ObjectSql.Relation(Schema, Table);
        string name = SqlIdentifier.Quote(Name);
        if (Operation == TableConstraintOperation.Drop)
            return $"ALTER TABLE {table} DROP CONSTRAINT {name} RESTRICT;";

        if (Operation is not (TableConstraintOperation.AddPrimaryKey or TableConstraintOperation.AddUnique))
            throw new ArgumentException("Unsupported table constraint operation.");
        if (Columns.Count is < 1 or > 32 || Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Count)
            throw new ArgumentException("Enter 1 to 32 distinct column names, one exact name per line.");

        string kind = Operation == TableConstraintOperation.AddPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
        string columns = string.Join(", ", Columns.Select(SqlIdentifier.Quote));
        return $"ALTER TABLE {table} ADD CONSTRAINT {name} {kind} ({columns});";
    }

    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (Operation != TableConstraintOperation.Drop) return;
        string table = ObjectSql.Relation(Schema, Table);
        await using (var acquireLock = new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", connection, transaction) { CommandTimeout = 30 })
            await acquireLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        const string sql = """
            SELECT c.contype::text
            FROM pg_catalog.pg_constraint AS c
            JOIN pg_catalog.pg_class AS r ON r.oid = c.conrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = r.relnamespace
            WHERE n.nspname = @schema AND r.relname = @table AND c.conname = @name
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", Schema);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("name", Name);
        var constraintType = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (constraintType is null)
            throw new InvalidOperationException("The named constraint was not found on this table. Inspect the current constraints and try again.");
        if (constraintType is not ("p" or "u"))
            throw new InvalidOperationException("This operation can drop PRIMARY KEY and UNIQUE constraints only.");
        if (ExpectedIdentity is not null) await ExpectedIdentity.ValidateAsync(connection, transaction, Schema, Table, Name, token).ConfigureAwait(false);
    }
}

public sealed class TableConstraintService(string connectionString)
{
    public async Task<IReadOnlyList<TableConstraintInfo>> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await ConstraintInspection.RequireTableAsync(connection, transaction, schema, table, token).ConfigureAwait(false);
        const string sql = """
            SELECT c.conname,
                   CASE c.contype WHEN 'p' THEN 'PRIMARY KEY' WHEN 'u' THEN 'UNIQUE' END,
                   pg_catalog.pg_get_constraintdef(c.oid, true), c.conrelid, c.oid
            FROM pg_catalog.pg_constraint AS c
            JOIN pg_catalog.pg_class AS r ON r.oid = c.conrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = r.relnamespace
            WHERE n.nspname = @schema AND r.relname = @table AND c.contype IN ('p', 'u')
            ORDER BY CASE c.contype WHEN 'p' THEN 0 ELSE 1 END, c.conname
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var constraints = new List<TableConstraintInfo>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            constraints.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) { Identity = new(reader.GetFieldValue<uint>(3), reader.GetFieldValue<uint>(4)) });
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return constraints;
    }
}
