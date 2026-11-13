using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record TableProperties(uint Oid, string Schema, string Name, string Owner, string Kind,
    string Persistence, double EstimatedRows, bool RowSecurity, bool ForceRowSecurity, string? Comment)
{
    public IReadOnlyList<InspectedTableTrigger> Triggers { get; init; } = [];
    public IReadOnlyList<IncomingForeignKeyInfo> IncomingForeignKeys { get; init; } = [];
    public IReadOnlyList<TableDependencyInfo> Dependents { get; init; } = [];
    public bool DependentsTruncated { get; init; }
    public IReadOnlyList<InspectedTableConstraint> Constraints { get; init; } = [];
    public IReadOnlyList<TableIndexInfo> Indexes { get; init; } = [];
    public IReadOnlyList<TableColumnInfo> Columns { get; init; } = [];
}
public sealed record TableColumnInfo(short Position, string Name, string Type, bool Nullable,
    string Identity, string Generated, string? DefaultExpression, string? Comment)
{
    public ColumnTypeIdentity? TypeIdentity { get; init; }
}
public sealed record ColumnTypeIdentity(uint Oid, int Modifier);

public sealed record InspectedTableTrigger(uint Oid, string Name, string Enabled, bool Internal, string Definition);

public sealed record IncomingForeignKeyInfo(uint Oid, string Schema, string Table, string Name, bool Validated, string Definition);

public sealed record InspectedTableConstraint(uint Oid, string Name, string Kind, bool Validated, bool Deferrable, bool InitiallyDeferred, string Definition);

public sealed record TableIndexInfo(uint Oid, string Name, string Method, bool Unique, bool Primary, bool Valid, bool Ready, string Definition);

public sealed record TableDependencyInfo(string Description, string Reference, string Kind);

public sealed class TablePropertiesService(string connectionString)
{
    public async Task<TableProperties> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        const string sql = """
            SELECT c.oid, n.nspname, c.relname, pg_catalog.pg_get_userbyid(c.relowner),
                CASE c.relkind WHEN 'r' THEN 'Ordinary table' WHEN 'p' THEN 'Partitioned table' END,
                CASE c.relpersistence WHEN 'p' THEN 'Permanent' WHEN 'u' THEN 'Unlogged' ELSE 'Temporary' END,
                c.reltuples::double precision, c.relrowsecurity, c.relforcerowsecurity,
                pg_catalog.obj_description(c.oid, 'pg_class')
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema AND c.relname=@table AND c.relkind IN ('r','p')
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Table was not found. Check the exact schema and table names.");
        var info = new TableProperties(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetBoolean(7), reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
        await reader.DisposeAsync().ConfigureAwait(false);
        const string columnSql = """
            SELECT a.attnum, a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod), NOT a.attnotnull,
                CASE a.attidentity WHEN 'a' THEN 'Always' WHEN 'd' THEN 'By default' ELSE '' END,
                CASE a.attgenerated WHEN 's' THEN 'Stored' ELSE '' END,
                pg_catalog.pg_get_expr(d.adbin,d.adrelid), pg_catalog.col_description(a.attrelid,a.attnum), a.atttypid, a.atttypmod
            FROM pg_catalog.pg_attribute a
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """;
        await using var columnsCommand = new NpgsqlCommand(columnSql, connection, transaction) { CommandTimeout = 15 };
        columnsCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var columnsReader = await columnsCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await columnsReader.ReadAsync(token).ConfigureAwait(false))
            columns.Add(new(columnsReader.GetInt16(0), columnsReader.GetString(1), columnsReader.GetString(2),
                columnsReader.GetBoolean(3), columnsReader.GetString(4), columnsReader.GetString(5),
                columnsReader.IsDBNull(6) ? null : columnsReader.GetString(6), columnsReader.IsDBNull(7) ? null : columnsReader.GetString(7))
                { TypeIdentity = new(columnsReader.GetFieldValue<uint>(8), columnsReader.GetInt32(9)) });
        await columnsReader.DisposeAsync().ConfigureAwait(false);
        const string dependencySql = """
            SELECT COALESCE(pg_catalog.pg_describe_object(d.classid,d.objid,d.objsubid),'(unavailable)'),
                CASE WHEN d.refobjsubid=0 THEN '(table)' ELSE COALESCE(a.attname,'(column unavailable)') END,
                CASE d.deptype WHEN 'n' THEN 'Normal' WHEN 'a' THEN 'Automatic' WHEN 'i' THEN 'Internal'
                    WHEN 'P' THEN 'Primary partition' WHEN 'S' THEN 'Secondary partition'
                    WHEN 'e' THEN 'Extension' WHEN 'x' THEN 'Automatic extension' ELSE d.deptype::text END
            FROM pg_catalog.pg_depend d
            LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=d.refobjid AND a.attnum=d.refobjsubid
            WHERE d.refclassid='pg_catalog.pg_class'::regclass AND d.refobjid=@oid
            ORDER BY d.classid,d.objid,d.objsubid,d.refobjsubid,d.deptype LIMIT 1001
            """;
        await using var dependenciesCommand = new NpgsqlCommand(dependencySql, connection, transaction) { CommandTimeout = 15 };
        dependenciesCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var dependenciesReader = await dependenciesCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var dependencies = new List<TableDependencyInfo>();
        while (await dependenciesReader.ReadAsync(token).ConfigureAwait(false))
            dependencies.Add(new(dependenciesReader.GetString(0), dependenciesReader.GetString(1), dependenciesReader.GetString(2)));
        await dependenciesReader.DisposeAsync().ConfigureAwait(false);
        const string indexSql = """
            SELECT i.indexrelid, c.relname, am.amname, i.indisunique, i.indisprimary,
                i.indisvalid, i.indisready, pg_catalog.pg_get_indexdef(i.indexrelid)
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid
            JOIN pg_catalog.pg_am am ON am.oid=c.relam
            WHERE i.indrelid=@oid ORDER BY c.relname, i.indexrelid
            """;
        await using var indexesCommand = new NpgsqlCommand(indexSql, connection, transaction) { CommandTimeout = 15 };
        indexesCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var indexesReader = await indexesCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var indexes = new List<TableIndexInfo>();
        while (await indexesReader.ReadAsync(token).ConfigureAwait(false))
            indexes.Add(new(indexesReader.GetFieldValue<uint>(0), indexesReader.GetString(1), indexesReader.GetString(2),
                indexesReader.GetBoolean(3), indexesReader.GetBoolean(4), indexesReader.GetBoolean(5), indexesReader.GetBoolean(6), indexesReader.GetString(7)));
        await indexesReader.DisposeAsync().ConfigureAwait(false);
        const string constraintSql = """
            SELECT c.oid, c.conname,
                CASE c.contype WHEN 'p' THEN 'Primary key' WHEN 'u' THEN 'Unique' WHEN 'f' THEN 'Foreign key'
                    WHEN 'c' THEN 'Check' WHEN 'x' THEN 'Exclusion' WHEN 't' THEN 'Constraint trigger' ELSE c.contype::text END,
                c.convalidated, c.condeferrable, c.condeferred, pg_catalog.pg_get_constraintdef(c.oid)
            FROM pg_catalog.pg_constraint c WHERE c.conrelid=@oid AND c.contype<>'n' ORDER BY c.conname,c.oid
            """;
        await using var constraintsCommand = new NpgsqlCommand(constraintSql, connection, transaction) { CommandTimeout = 15 };
        constraintsCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var constraintsReader = await constraintsCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var constraints = new List<InspectedTableConstraint>();
        while (await constraintsReader.ReadAsync(token).ConfigureAwait(false))
            constraints.Add(new(constraintsReader.GetFieldValue<uint>(0), constraintsReader.GetString(1), constraintsReader.GetString(2),
                constraintsReader.GetBoolean(3), constraintsReader.GetBoolean(4), constraintsReader.GetBoolean(5), constraintsReader.GetString(6)));
        await constraintsReader.DisposeAsync().ConfigureAwait(false);
        const string incomingSql = """
            SELECT f.oid, n.nspname, c.relname, f.conname, f.convalidated, pg_catalog.pg_get_constraintdef(f.oid)
            FROM pg_catalog.pg_constraint f JOIN pg_catalog.pg_class c ON c.oid=f.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE f.contype='f' AND f.confrelid=@oid ORDER BY n.nspname,c.relname,f.conname,f.oid
            """;
        await using var incomingCommand = new NpgsqlCommand(incomingSql, connection, transaction) { CommandTimeout = 15 };
        incomingCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var incomingReader = await incomingCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var incoming = new List<IncomingForeignKeyInfo>();
        while (await incomingReader.ReadAsync(token).ConfigureAwait(false))
            incoming.Add(new(incomingReader.GetFieldValue<uint>(0), incomingReader.GetString(1), incomingReader.GetString(2),
                incomingReader.GetString(3), incomingReader.GetBoolean(4), incomingReader.GetString(5)));
        await incomingReader.DisposeAsync().ConfigureAwait(false);
        const string triggerSql = """
            SELECT t.oid, t.tgname,
                CASE t.tgenabled WHEN 'O' THEN 'Origin / local' WHEN 'D' THEN 'Disabled' WHEN 'R' THEN 'Replica' WHEN 'A' THEN 'Always' ELSE t.tgenabled::text END,
                t.tgisinternal, pg_catalog.pg_get_triggerdef(t.oid)
            FROM pg_catalog.pg_trigger t WHERE t.tgrelid=@oid ORDER BY t.tgname,t.oid
            """;
        await using var triggersCommand = new NpgsqlCommand(triggerSql, connection, transaction) { CommandTimeout = 15 };
        triggersCommand.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, info.Oid);
        await using var triggersReader = await triggersCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        var triggers = new List<InspectedTableTrigger>();
        while (await triggersReader.ReadAsync(token).ConfigureAwait(false))
            triggers.Add(new(triggersReader.GetFieldValue<uint>(0), triggersReader.GetString(1), triggersReader.GetString(2),
                triggersReader.GetBoolean(3), triggersReader.GetString(4)));
        await triggersReader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return info with { Triggers = triggers, IncomingForeignKeys = incoming, Constraints = constraints, Indexes = indexes, Columns = columns, Dependents = dependencies.Take(1000).ToArray(), DependentsTruncated = dependencies.Count > 1000 };
    }
}

