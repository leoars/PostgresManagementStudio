using Npgsql;

namespace PGMS.PostgreSql;

public enum TableOperation { Rename, Drop, AddColumn, RenameColumn, DropColumn, SetNotNull, DropNotNull, SetDefault, DropDefault, SetSchema, CommentTable, CommentColumn }
public sealed record TableEdit(string Schema, string Table, TableOperation Operation, string NewName = "", string Column = "", ColumnType Type = ColumnType.Text, bool Nullable = true, string DefaultValue = "", string Comment = "", bool RemoveComment = false) : IAdministrativeEdit
{
    public uint? ExpectedTableOid { get; init; }
    public ColumnIdentity Identity { get; init; }
    public short? ExpectedColumnPosition { get; init; }
    public ColumnTypeIdentity? ExpectedColumnType { get; init; }
    public bool? ExpectedNullable { get; init; }
    public bool CheckDefault { get; init; }
    public string? ExpectedDefaultExpression { get; init; }
    public bool CheckComment { get; init; }
    public string? ExpectedComment { get; init; }

    public string ToSql()
    {
        string table = ObjectSql.Relation(Schema, Table);
        return Operation switch
        {
            TableOperation.Rename when Table == NewName => throw new ArgumentException("Choose a different table name."),
            TableOperation.Rename => $"ALTER TABLE {table} RENAME TO {SqlIdentifier.Quote(NewName)};",
            TableOperation.Drop => $"DROP TABLE {table} RESTRICT;",
            TableOperation.AddColumn => $"ALTER TABLE ONLY {table} ADD COLUMN {new TableColumn(Column, Type, Nullable){Identity=this.Identity}.ToSql()};",
            TableOperation.RenameColumn when Column == NewName => throw new ArgumentException("Choose a different column name."),
            TableOperation.RenameColumn => $"ALTER TABLE ONLY {table} RENAME COLUMN {SqlIdentifier.Quote(Column)} TO {SqlIdentifier.Quote(NewName)};",
            TableOperation.DropColumn => $"ALTER TABLE ONLY {table} DROP COLUMN {SqlIdentifier.Quote(Column)} RESTRICT;",
            TableOperation.SetNotNull => $"ALTER TABLE ONLY {table} ALTER COLUMN {SqlIdentifier.Quote(Column)} SET NOT NULL;",
            TableOperation.DropNotNull => $"ALTER TABLE ONLY {table} ALTER COLUMN {SqlIdentifier.Quote(Column)} DROP NOT NULL;",
            TableOperation.SetDefault => $"ALTER TABLE ONLY {table} ALTER COLUMN {SqlIdentifier.Quote(Column)} SET DEFAULT {Literal(DefaultValue)};",
            TableOperation.DropDefault => $"ALTER TABLE ONLY {table} ALTER COLUMN {SqlIdentifier.Quote(Column)} DROP DEFAULT;",
            TableOperation.SetSchema when Schema == NewName => throw new ArgumentException("Choose a different destination schema."),
            TableOperation.SetSchema => $"ALTER TABLE {table} SET SCHEMA {ObjectSql.UserSchema(NewName)};",
            TableOperation.CommentTable => $"COMMENT ON TABLE {table} IS {CommentSql()};",
            TableOperation.CommentColumn => $"COMMENT ON COLUMN {table}.{SqlIdentifier.Quote(Column)} IS {CommentSql()};",
            _ => throw new ArgumentException("Unsupported table operation.")
        };
    }

    private static string Literal(string value, string label = "Default")
    {
        if (value.Contains('\0') || value.Length > 10000)
            throw new ArgumentException($"{label} must contain no NUL and at most 10000 characters.");
        // Explicit E strings preserve backslashes regardless of standard_conforming_strings.
        return "E'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private string CommentSql()
    {
        if (RemoveComment) return "NULL";
        if (Comment.Length == 0) throw new ArgumentException("Enter a comment or select Remove existing comment.");
        return Literal(Comment, "Comment");
    }

    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        // Hold the target stable while checking its kind and applying DDL in the same transaction.
        await using var guard = new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(Schema, Table)} IN ACCESS EXCLUSIVE MODE;", connection, transaction) { CommandTimeout = 30 };
        await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        guard.CommandText = """
            SELECT c.relkind = 'r' AND NOT c.relispartition AND c.relpersistence <> 't'
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema AND c.relname=@table
            """;
        guard.Parameters.AddWithValue("schema", Schema); guard.Parameters.AddWithValue("table", Table);
        if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Only ordinary, non-inherited, non-partitioned permanent tables are supported.");
        if (ExpectedTableOid is { } expected)
        {
            guard.CommandText = "SELECT c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table";
            if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not uint actual || actual != expected)
                throw new InvalidOperationException("Table identity changed since inspection. Inspect again before applying.");
        }
        if (ExpectedColumnPosition is { } position)
        {
            guard.CommandText = "SELECT a.attnum FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped";
            guard.Parameters.AddWithValue("column", Column);
            if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not short actual || actual != position)
                throw new InvalidOperationException("Column identity changed since inspection. Inspect again before applying.");
        }
        if (ExpectedColumnType is { } type)
        {
            guard.CommandText = """
                SELECT a.atttypid=@typeOid AND a.atttypmod=@typeModifier FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid=a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped
                """;
            if (!guard.Parameters.Contains("column")) guard.Parameters.AddWithValue("column", Column);
            guard.Parameters.AddWithValue("typeOid", NpgsqlTypes.NpgsqlDbType.Oid, type.Oid);
            guard.Parameters.AddWithValue("typeModifier", type.Modifier);
            if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Column type changed since inspection. Inspect again and review the current type.");
        }
        if (ExpectedNullable is { } nullable && Operation is TableOperation.SetNotNull or TableOperation.DropNotNull)
        {
            guard.CommandText = """
                SELECT NOT a.attnotnull FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid=a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped
                """;
            if (!guard.Parameters.Contains("column")) guard.Parameters.AddWithValue("column", Column);
            if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not bool current || current != nullable)
                throw new InvalidOperationException("Nullability changed since inspection. Inspect again and review the current state.");
        }
        if (CheckDefault && Operation is TableOperation.SetDefault or TableOperation.DropDefault)
        {
            guard.CommandText = """
                SELECT pg_catalog.pg_get_expr(d.adbin,d.adrelid)
                FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped
                """;
            if (!guard.Parameters.Contains("column")) guard.Parameters.AddWithValue("column", Column);
            string? current = await guard.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (!string.Equals(current, ExpectedDefaultExpression, StringComparison.Ordinal))
                throw new InvalidOperationException("Default changed since inspection. Inspect again and review the current expression.");
        }
        if (CheckComment && Operation is TableOperation.CommentTable or TableOperation.CommentColumn)
        {
            if (Operation == TableOperation.CommentTable)
                guard.CommandText = "SELECT pg_catalog.obj_description(c.oid,'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table";
            else
            {
                guard.CommandText = "SELECT pg_catalog.col_description(c.oid,a.attnum) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND a.attnum>0 AND NOT a.attisdropped";
                if (!guard.Parameters.Contains("column")) guard.Parameters.AddWithValue("column", Column);
            }
            string? current = await guard.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (!string.Equals(current, ExpectedComment, StringComparison.Ordinal))
                throw new InvalidOperationException("Comment changed since inspection. Inspect again and review the current comment.");
        }
    }
}
