using Npgsql;

namespace PGMS.PostgreSql;

public enum ForeignKeyOperation { Add, Drop, Validate }
public enum ReferentialAction { NoAction, Restrict, Cascade, SetNull, SetDefault }
public sealed record ForeignKeyInfo(string Name, string Definition) { public ConstraintIdentity? Identity { get; init; } }

public sealed record ForeignKeyEdit(ForeignKeyOperation Operation, string Schema, string Table, string Name,
    IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnUpdate, ReferentialAction OnDelete) : IAdministrativeEdit
{
    public bool NotValid { get; init; }
    public ConstraintIdentity? ExpectedIdentity { get; init; }

    public string ToSql()
    {
        string table = ObjectSql.Relation(Schema, Table);
        string name = SqlIdentifier.Quote(Name);
        if (Operation == ForeignKeyOperation.Validate) return $"ALTER TABLE {table} VALIDATE CONSTRAINT {name};";
        if (Operation == ForeignKeyOperation.Drop) return $"ALTER TABLE {table} DROP CONSTRAINT {name} RESTRICT;";
        if (Columns.Count is < 1 or > 32 || Columns.Count != ReferencedColumns.Count || Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Count || ReferencedColumns.Distinct(StringComparer.Ordinal).Count() != ReferencedColumns.Count)
            throw new ArgumentException("Enter 1 to 32 distinct local and referenced column names with matching counts.");
        string local = string.Join(", ", Columns.Select(SqlIdentifier.Quote));
        string referenced = string.Join(", ", ReferencedColumns.Select(SqlIdentifier.Quote));
        return $"ALTER TABLE {table} ADD CONSTRAINT {name} FOREIGN KEY ({local}) REFERENCES {ObjectSql.Relation(ReferencedSchema, ReferencedTable)} ({referenced}) ON UPDATE {Keyword(OnUpdate)} ON DELETE {Keyword(OnDelete)}{(NotValid ? " NOT VALID" : "")};";
    }
    private static string Keyword(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION", ReferentialAction.Restrict => "RESTRICT", ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL", ReferentialAction.SetDefault => "SET DEFAULT", _ => throw new ArgumentException("Unsupported referential action.")
    };
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (Operation == ForeignKeyOperation.Add) return;
        string table = ObjectSql.Relation(Schema, Table);
        string lockMode = Operation == ForeignKeyOperation.Validate ? "SHARE UPDATE EXCLUSIVE" : "ACCESS EXCLUSIVE";
        await using (var acquireLock = new NpgsqlCommand($"LOCK TABLE {table} IN {lockMode} MODE", connection, transaction) { CommandTimeout = 30 })
            await acquireLock.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        const string sql = "SELECT c.contype::text FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND c.conname=@name";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", Schema); command.Parameters.AddWithValue("table", Table); command.Parameters.AddWithValue("name", Name);
        var type = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (type is null) throw new InvalidOperationException("The named FOREIGN KEY was not found on this table. Inspect the current constraints and try again.");
        if (type != "f") throw new InvalidOperationException("This operation supports FOREIGN KEY constraints only.");
        if (ExpectedIdentity is not null) await ExpectedIdentity.ValidateAsync(connection, transaction, Schema, Table, Name, token).ConfigureAwait(false);
    }
}

public sealed class ForeignKeyService(string connectionString)
{
    public async Task<IReadOnlyList<ForeignKeyInfo>> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await ConstraintInspection.RequireTableAsync(connection, transaction, schema, table, token).ConfigureAwait(false);
        const string sql = "SELECT c.conname, pg_catalog.pg_get_constraintdef(c.oid, true), c.conrelid, c.oid FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname=@schema AND r.relname=@table AND c.contype='f' ORDER BY c.conname";
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<ForeignKeyInfo>(); while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1)) { Identity = new(reader.GetFieldValue<uint>(2), reader.GetFieldValue<uint>(3)) });
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }
}
