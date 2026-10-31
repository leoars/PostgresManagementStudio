using Npgsql;

namespace PGMS.PostgreSql;

public enum CheckConstraintOperation { Add, Drop, Validate }

public sealed record CheckConstraintInfo(string Name, string Definition) { public ConstraintIdentity? Identity { get; init; } }

public sealed record CheckConstraintEdit(CheckConstraintOperation Operation, string Schema, string Table, string Name, string Expression) : IAdministrativeEdit
{
    public ConstraintIdentity? ExpectedIdentity { get; init; }
    public bool NotValid { get; init; }

    public string ToSql()
    {
        string table = ObjectSql.Relation(Schema, Table);
        string name = SqlIdentifier.Quote(Name);
        if (Operation == CheckConstraintOperation.Validate) return $"ALTER TABLE {table} VALIDATE CONSTRAINT {name};";
        if (Operation == CheckConstraintOperation.Drop) return $"ALTER TABLE {table} DROP CONSTRAINT {name} RESTRICT;";
        string expression = Expression.Trim();
        if (string.IsNullOrWhiteSpace(expression) || expression.Contains(';') || expression.Contains('\0'))
            throw new ArgumentException("Enter one CHECK expression without a statement terminator.");
        return $"ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({expression}){(NotValid ? " NOT VALID" : "")};";
    }

    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (Operation == CheckConstraintOperation.Add) return;
        string table = ObjectSql.Relation(Schema, Table);
        string lockMode = Operation == CheckConstraintOperation.Validate ? "SHARE UPDATE EXCLUSIVE" : "ACCESS EXCLUSIVE";
        await using (var acquireLock = new NpgsqlCommand($"LOCK TABLE {table} IN {lockMode} MODE", connection, transaction) { CommandTimeout = 30 })
            await acquireLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        const string sql = "SELECT c.contype::text FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND c.conname=@name";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", Schema); command.Parameters.AddWithValue("table", Table); command.Parameters.AddWithValue("name", Name);
        var type = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (type is null) throw new InvalidOperationException("The named CHECK constraint was not found on this table. Inspect the current constraints and try again.");
        if (type != "c") throw new InvalidOperationException("This operation supports CHECK constraints only.");
        if (ExpectedIdentity is not null) await ExpectedIdentity.ValidateAsync(connection, transaction, Schema, Table, Name, token).ConfigureAwait(false);
    }
}

public sealed class CheckConstraintService(string connectionString)
{
    public async Task<IReadOnlyList<CheckConstraintInfo>> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await ConstraintInspection.RequireTableAsync(connection, transaction, schema, table, token).ConfigureAwait(false);
        const string sql = "SELECT c.conname, pg_catalog.pg_get_constraintdef(c.oid, true), c.conrelid, c.oid FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND c.contype='c' ORDER BY c.conname";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<CheckConstraintInfo>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1)) { Identity = new(reader.GetFieldValue<uint>(2), reader.GetFieldValue<uint>(3)) });
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }
}
