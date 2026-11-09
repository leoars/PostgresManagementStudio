using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>Where one table sits in a diagram. The OID is a fallback for a table renamed outside the diagram; the name wins after dump and restore.</summary>
public sealed record DiagramTablePlacement(string Schema, string Name, double X, double Y)
{
    public uint Oid { get; init; }
    /// <summary>Table View of this table (Standard, Keys, NameOnly, Custom); null is Column Names, the default.</summary>
    public string? View { get; init; }
    /// <summary>The fields of the Custom view, when they differ from the default.</summary>
    public IReadOnlyList<string>? CustomColumns { get; init; }
}

/// <summary>A text annotation placed on a diagram.</summary>
public sealed record DiagramAnnotationPlacement(string Text, double X, double Y);

/// <summary>A relationship line placed by hand, found by the table that owns the foreign key and the key name. Sides are Left, Right, Top or Bottom;
/// an offset is the distance along that side from its top or left end; Bends are where the segments between the two end segments sit.</summary>
public sealed record DiagramRelationshipRoute(string Schema, string Table, string Name, string PrimarySide, double PrimaryOffset, string ForeignSide, double ForeignOffset, IReadOnlyList<double> Bends);

/// <summary>What a diagram stores: which tables it shows and where, and the lines placed by hand. Never table data (decision of 07/11/2026, docs/MARCO-M1043.md).</summary>
public sealed record DiagramLayout
{
    public int Format { get; init; } = 1;
    public IReadOnlyList<DiagramTablePlacement> Tables { get; init; } = [];
    /// <summary>Only the lines moved by hand; the others are routed automatically. Older versions of PGMS ignore this list.</summary>
    public IReadOnlyList<DiagramRelationshipRoute> Relationships { get; init; } = [];
    public double Zoom { get; init; } = 1;
    public bool ShowRelationshipLabels { get; init; }
    public IReadOnlyList<DiagramAnnotationPlacement> Annotations { get; init; } = [];
    public bool ShowPageBreaks { get; init; }
    /// <summary>The page the breaks were last calculated for, in diagram units; 0 when they never were. Kept so opening a diagram does not have to ask the printer.</summary>
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static DiagramLayout FromJson(string json) => JsonSerializer.Deserialize<DiagramLayout>(json, Options) ?? new();
}

public sealed record DiagramInfo(int Id, string Name, string Owner, int Version, DateTimeOffset Modified);
public sealed record DatabaseDiagram(DiagramInfo Info, DiagramLayout Layout);
public sealed record DiagramTableRef(string Schema, string Name, uint Oid)
{
    public string DisplayName => Schema + "." + Name;
}

/// <summary>Delete Tables from Database in a diagram. Refused when the name now points at another table than the one the diagram read.</summary>
public sealed record DropTableEdit(string Schema, string Name, uint Oid) : IAdministrativeEdit
{
    public string ToSql() => $"DROP TABLE {ObjectSql.Relation(Schema, Name)} RESTRICT;";
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await using var command = new NpgsqlCommand("SELECT c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p')", connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", Schema); command.Parameters.AddWithValue("name", Name);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not uint oid || oid != Oid)
            throw new InvalidOperationException($"Table {Schema}.{Name} changed since the diagram read it. Refresh the diagram before saving.");
    }
}

/// <summary>The foreign keys of a table created in the same save, added once every new table exists, so two new tables can reference each other.</summary>
public sealed record NewTableForeignKeysEdit(TableDesign Table) : IAdministrativeEdit
{
    public string ToSql() => string.Join("\n", TableDesignScript.AddForeignKeys(Table));
}

/// <summary>The schema changes of a diagram, ordered so they run in one transaction: new tables first (Oid 0 marks a table the database does not have yet), a table gets
/// its new key before another table references it, a foreign key goes away before the key it uses, the foreign keys of new tables follow, and dropped tables come last.</summary>
public static class DiagramSchemaChanges
{
    public sealed record Result(IReadOnlyList<IAdministrativeEdit> Edits, TableDesignChange Script);

    public static Result Build(IReadOnlyList<(TableDesign Original, TableDesign Current)> tables, IReadOnlyList<TableDesign> dropped)
    {
        // Columns renamed in one table are renamed in the foreign keys of the others by PostgreSQL itself: those keys are compared, and checked
        // before saving, with the new names, so they are not rebuilt.
        var fresh = tables.Where(t => t.Original.Oid == 0).Select(t => t.Current).ToArray();
        tables = tables.Where(t => t.Original.Oid != 0).ToArray();
        var renamed = tables.Select(t => (t.Original, Renames: TableDesignColumns.Renames(t.Original, t.Current.Columns))).Where(r => r.Renames.Count > 0).ToArray();
        TableDesign Adjusted(TableDesign original) => renamed.Aggregate(original, (design, r) => TableDesignColumns.WithReferencedRenames(design, r.Original.Schema, r.Original.Name, r.Renames));
        var changed = new List<(TableDesign Original, TableDesign Current, TableDesignChange Change)>();
        foreach (var (read, current) in tables)
        {
            var original = Adjusted(read);
            var change = TableDesignScript.Build(original, current);
            if (change.Statements.Count > 0) changed.Add((original, current, change));
        }
        static string Key(DesignForeignKey f) => string.Join("\u0001", [string.Join(",", f.Columns), f.ReferencedSchema, f.ReferencedTable, string.Join(",", f.ReferencedColumns), f.OnUpdate.ToString(), f.OnDelete.ToString(), f.CheckExisting.ToString()]);
        static IEnumerable<DesignForeignKey> Added(TableDesign before, TableDesign after) =>
            after.ForeignKeys.Where(f => f.OriginalName is null || before.ForeignKeys.FirstOrDefault(b => b.Name == f.OriginalName) is not { } b || b.FixedDefinition is null && Key(b) != Key(f));
        static IEnumerable<DesignForeignKey> Removed(TableDesign before, TableDesign after) =>
            before.ForeignKeys.Where(b => after.ForeignKeys.FirstOrDefault(f => f.OriginalName == b.Name) is not { } f || b.FixedDefinition is null && Key(b) != Key(f));
        static bool Is(TableDesign table, string schema, string name) => table.Schema == schema && table.Name == name;

        // before[i] holds the indexes that must run before i.
        var before = changed.Select(_ => new HashSet<int>()).ToArray();
        for (int i = 0; i < changed.Count; i++)
            for (int j = 0; j < changed.Count; j++)
            {
                if (i == j) continue;
                var target = changed[j].Original;
                if (Added(changed[i].Original, changed[i].Current).Any(f => Is(target, f.ReferencedSchema, f.ReferencedTable))) before[i].Add(j);
                if (Removed(changed[i].Original, changed[i].Current).Any(f => Is(target, f.ReferencedSchema, f.ReferencedTable))) before[j].Add(i);
                // A table whose columns are renamed goes first: the others are checked against the names after the rename.
                if (renamed.Any(r => r.Original.Oid == target.Oid) && changed[i].Original.ForeignKeys.Any(f => Is(target, f.ReferencedSchema, f.ReferencedTable))) before[i].Add(j);
            }
        var order = new List<int>(); var placed = new bool[changed.Count];
        while (order.Count < changed.Count)
        {
            int next = Enumerable.Range(0, changed.Count).FirstOrDefault(i => !placed[i] && before[i].All(b => placed[b]), -1);
            // A cycle has no order that always works; PostgreSQL reports the failing statement and the transaction rolls back.
            if (next < 0) next = Enumerable.Range(0, changed.Count).First(i => !placed[i]);
            placed[next] = true; order.Add(next);
        }

        var edits = new List<IAdministrativeEdit>(); var statements = new List<string>(); var warnings = new List<string>();
        foreach (var table in fresh)
        {
            TableDesignScript.Build(null, table); // checks the foreign keys too, which the CREATE below leaves out
            var bare = table with { ForeignKeys = [] };
            edits.Add(new TableDesignEdit(null, bare));
            statements.Add($"-- {table.Schema}.{table.Name}");
            statements.AddRange(TableDesignScript.Build(null, bare).Statements);
        }
        foreach (int i in order)
        {
            edits.Add(new TableDesignEdit(changed[i].Original, changed[i].Current));
            statements.Add($"-- {changed[i].Original.Schema}.{changed[i].Original.Name}");
            statements.AddRange(changed[i].Change.Statements); warnings.AddRange(changed[i].Change.Warnings);
        }
        foreach (var table in fresh.Where(t => t.ForeignKeys.Count > 0))
        {
            edits.Add(new NewTableForeignKeysEdit(table));
            statements.Add($"-- {table.Schema}.{table.Name}");
            statements.AddRange(TableDesignScript.AddForeignKeys(table));
        }
        foreach (var table in dropped)
        {
            var drop = new DropTableEdit(table.Schema, table.Name, table.Oid);
            edits.Add(drop); statements.Add(drop.ToSql());
            warnings.Add($"Deleting table {table.Schema}.{table.Name} deletes it and its data.");
        }
        return new(edits, new(statements, warnings));
    }
}

/// <summary>Database diagrams kept in the table pgms.diagrams of each database, like sysdiagrams in SQL Server. The table is created only after the user confirms (Install Diagram Support).</summary>
public sealed class DiagramService(string connectionString)
{
    public const string SchemaName = "pgms", TableName = "diagrams";
    public const string InstallSql = """
        CREATE SCHEMA IF NOT EXISTS pgms;
        CREATE TABLE IF NOT EXISTS pgms.diagrams (
            diagram_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name text NOT NULL CONSTRAINT diagrams_name_key UNIQUE CONSTRAINT diagrams_name_check CHECK (length(btrim(name)) BETWEEN 1 AND 128),
            owner text NOT NULL DEFAULT current_user,
            version integer NOT NULL DEFAULT 1,
            definition jsonb NOT NULL,
            modified timestamp with time zone NOT NULL DEFAULT now()
        );
        COMMENT ON TABLE pgms.diagrams IS 'Database diagrams of Postgres Management Studio (PGMS): tables shown and their positions, never table data.';
        """;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        return connection;
    }

    internal static async Task<bool> IsInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.to_regclass('pgms.diagrams') IS NOT NULL", connection, transaction) { CommandTimeout = 15 };
        return (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }
    public async Task<bool> IsInstalledAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        return await IsInstalledAsync(connection, null, token).ConfigureAwait(false);
    }

    /// <summary>Creates the schema and table. Needs CREATE on the database (or an existing pgms schema the user can create in).</summary>
    public async Task InstallAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(InstallSql, connection, transaction) { CommandTimeout = 30 }) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private const string InfoColumns = "diagram_id, name, owner, version, modified";
    private static DiagramInfo Info(NpgsqlDataReader reader) => new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4));

    public async Task<IReadOnlyList<DiagramInfo>> ListAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        if (!await IsInstalledAsync(connection, null, token).ConfigureAwait(false)) return [];
        await using var command = new NpgsqlCommand($"SELECT {InfoColumns} FROM pgms.diagrams ORDER BY name, diagram_id", connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<DiagramInfo>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Info(reader));
        return result;
    }

    public async Task<DatabaseDiagram> ReadAsync(int id, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {InfoColumns}, definition::text FROM pgms.diagrams WHERE diagram_id=@id", connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The diagram was not found. Refresh Object Explorer.");
        DiagramLayout layout;
        try { layout = DiagramLayout.FromJson(reader.GetString(5)); }
        catch (JsonException ex) { throw new InvalidOperationException("The diagram definition could not be read: " + ex.Message, ex); }
        return new(Info(reader), layout);
    }

    /// <summary>Tables a diagram can show: ordinary and partitioned tables outside the system schemas, without the diagram table itself.</summary>
    public async Task<IReadOnlyList<DiagramTableRef>> ListTablesAsync(CancellationToken token = default)
    {
        const string sql = """
            SELECT n.nspname, c.relname, c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind IN ('r','p') AND NOT c.relispartition AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
              AND NOT (n.nspname='pgms' AND c.relname='diagrams')
            ORDER BY n.nspname, c.relname
            """;
        return await TablesAsync(sql, null, token).ConfigureAwait(false);
    }

    /// <summary>Tables that reference, or are referenced by, any of the given tables through a foreign key (Add Related Tables).</summary>
    public async Task<IReadOnlyList<DiagramTableRef>> RelatedTablesAsync(IReadOnlyList<uint> tables, CancellationToken token = default)
    {
        const string sql = """
            SELECT DISTINCT n.nspname, c.relname, c.oid FROM pg_catalog.pg_constraint k
            JOIN pg_catalog.pg_class c ON c.oid = CASE WHEN k.conrelid = ANY(@oids) THEN k.confrelid ELSE k.conrelid END
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE k.contype='f' AND (k.conrelid = ANY(@oids) OR k.confrelid = ANY(@oids)) AND c.relkind IN ('r','p') AND NOT c.relispartition
            ORDER BY n.nspname, c.relname
            """;
        return await TablesAsync(sql, tables.ToArray(), token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DiagramTableRef>> TablesAsync(string sql, uint[]? oids, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        if (oids is not null) command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, oids);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<DiagramTableRef>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<uint>(2)));
        return result;
    }

    /// <summary>Reads the tables of a diagram in one snapshot. A table that no longer exists is left out and named in Missing.</summary>
    public async Task<(IReadOnlyList<TableDesign> Tables, IReadOnlyList<string> Missing)> ReadTablesAsync(IReadOnlyList<DiagramTablePlacement> placements, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var tables = new List<TableDesign>(); var missing = new List<string>();
        foreach (var placement in placements)
        {
            var (schema, name) = (placement.Schema, placement.Name);
            if (!await ExistsAsync(connection, transaction, schema, name, token).ConfigureAwait(false) && placement.Oid != 0)
            {
                await using var byOid = new NpgsqlCommand("SELECT n.nspname, c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=@oid AND c.relkind IN ('r','p')", connection, transaction) { CommandTimeout = 15 };
                byOid.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, placement.Oid);
                await using var reader = await byOid.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) (schema, name) = (reader.GetString(0), reader.GetString(1));
            }
            if (!await ExistsAsync(connection, transaction, schema, name, token).ConfigureAwait(false)) { missing.Add(placement.Schema + "." + placement.Name); continue; }
            tables.Add(await TableDesignService.ReadAsync(connection, transaction, schema, name, token).ConfigureAwait(false));
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return (tables, missing);
    }
    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p'))", connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    /// <summary>Applies the schema changes and writes the diagram in one transaction (decision of 07/11/2026). A null existing creates the diagram.
    /// The version guard refuses a save over a diagram changed or deleted elsewhere since it was read.</summary>
    public async Task<DiagramInfo> SaveAsync(DiagramInfo? existing, string name, DiagramLayout layout, IReadOnlyList<IAdministrativeEdit> edits, CancellationToken token = default)
    {
        name = ValidName(name);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        string? notice = null;
        connection.Notice += (_, e) => notice ??= e.Notice.MessageText;
        if (!await IsInstalledAsync(connection, transaction, token).ConfigureAwait(false)) throw new InvalidOperationException("Diagram support is not installed in this database. Use Install Diagram Support first.");
        foreach (var edit in edits)
        {
            await edit.ValidateAsync(connection, transaction, token).ConfigureAwait(false);
            await using (var command = new NpgsqlCommand(edit.ToSql(), connection, transaction) { CommandTimeout = edit.CommandTimeoutSeconds }) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await edit.VerifyAppliedAsync(connection, transaction, token).ConfigureAwait(false);
        }
        if (notice is not null) throw new InvalidOperationException("The save was rolled back after PostgreSQL notice: " + notice);
        string sql = existing is null
            ? $"INSERT INTO pgms.diagrams (name, definition) VALUES (@name, @definition::jsonb) RETURNING {InfoColumns}"
            : $"UPDATE pgms.diagrams SET name=@name, definition=@definition::jsonb, version=version+1, modified=now() WHERE diagram_id=@id AND version=@version RETURNING {InfoColumns}";
        DiagramInfo saved;
        try
        {
            await using var save = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
            save.Parameters.AddWithValue("name", name); save.Parameters.AddWithValue("definition", layout.ToJson());
            if (existing is not null) { save.Parameters.AddWithValue("id", existing.Id); save.Parameters.AddWithValue("version", existing.Version); }
            await using var reader = await save.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The diagram was changed or deleted since it was opened. Nothing was saved.");
            saved = Info(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new InvalidOperationException($"A diagram named {name} already exists in this database. Nothing was saved.", ex); }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Renames a saved diagram. With expectedVersion (the version an open tab read), a diagram changed or deleted since then is not renamed,
    /// so the tab never saves over it; the returned info carries the new version for that tab.</summary>
    public async Task<DiagramInfo> RenameAsync(int id, string name, int? expectedVersion = null, CancellationToken token = default)
    {
        name = ValidName(name);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE pgms.diagrams SET name=@name, version=version+1, modified=now() WHERE diagram_id=@id AND (@version::integer IS NULL OR version=@version) RETURNING {InfoColumns}", connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("name", name);
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)expectedVersion ?? DBNull.Value });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) return Info(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new InvalidOperationException($"A diagram named {name} already exists in this database.", ex); }
        throw new InvalidOperationException(expectedVersion is null ? "The diagram was not found. Refresh Object Explorer." : "The diagram was changed or deleted since it was opened. It was not renamed.");
    }

    public async Task DeleteAsync(int id, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("DELETE FROM pgms.diagrams WHERE diagram_id=@id", connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0) throw new InvalidOperationException("The diagram was not found. Refresh Object Explorer.");
    }

    public static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 128 || name.Contains('\0')) throw new ArgumentException("Enter a diagram name of 1 to 128 characters.");
        return name;
    }
}
