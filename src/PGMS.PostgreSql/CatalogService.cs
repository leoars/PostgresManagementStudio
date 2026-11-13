using Npgsql;

namespace PGMS.PostgreSql;

public enum CatalogKind { Server, Databases, Database, Schemas, Schema, Tables, Views, MaterializedViews, Sequences, Functions, Procedures, Relation, Routine, Columns, Column, Indexes, Index, Roles, Role, Enums, Domains, DataType, DatabaseDiagrams, DatabaseDiagram, Keys, Key, Constraints, Constraint, Triggers, Trigger }
public sealed record CatalogEntry(CatalogKind Kind, string Name, string? Database = null, string? Schema = null, uint Oid = 0)
{
    public string? RelationType { get; init; }
    public string? DataTypeKind { get; init; }
    public string? RoutineKind { get; init; }
    public string? RoleName { get; init; }
    public string? Owner { get; init; }
    /// <summary>A database with ALLOW_CONNECTIONS off, shown as "name (Offline)" like SSMS; <see cref="Database"/> keeps the real name.</summary>
    public bool IsOffline { get; init; }
    public bool CanManageRole => Kind == CatalogKind.Role && RoleName is not null;
    public bool CanConfigureTls => Kind == CatalogKind.Server;
    public bool IsServer => Kind == CatalogKind.Server;
    public bool CanManageEnum => Kind == CatalogKind.DataType && DataTypeKind == "e" && Oid != 0;
    public bool CanManageDomain => Kind == CatalogKind.DataType && DataTypeKind == "d" && Oid != 0;
    public bool CanInspectObjectProperties => Kind is CatalogKind.Database or CatalogKind.Schema or CatalogKind.Routine || (Kind == CatalogKind.Relation && RelationType is "v" or "m");
    public bool CanManageRoutinePermissions => Kind == CatalogKind.Routine && Oid != 0;
    public bool CanInspectSequence => Kind == CatalogKind.Relation && RelationType == "S";
    public bool CanInspectTable => Kind == CatalogKind.Relation && RelationType is "r" or "p";
    public bool CanEditTable => Kind == CatalogKind.Relation && RelationType == "r";
    public bool CanManagePartitions => Kind == CatalogKind.Relation && RelationType == "p";
    public bool CanExpand => !IsOffline && Kind is not (CatalogKind.Routine or CatalogKind.Column or CatalogKind.Index or CatalogKind.Role or CatalogKind.DataType or CatalogKind.DatabaseDiagram or CatalogKind.Key or CatalogKind.Constraint or CatalogKind.Trigger);
    public bool IsDiagram => Kind == CatalogKind.DatabaseDiagram && Oid != 0;
}

public sealed class CatalogService(string connectionString)
{
    public string DefaultDatabase => new NpgsqlConnectionStringBuilder(connectionString).Database ?? "postgres";
    public string DisplayName
    {
        get { var c = new NpgsqlConnectionStringBuilder(connectionString); return $"{c.Host}:{c.Port} ({c.Username})"; }
    }
    public string ConnectionFor(string? database) => new NpgsqlConnectionStringBuilder(connectionString) { Database = database ?? DefaultDatabase }.ConnectionString;

    public async Task<IReadOnlyList<CatalogEntry>> ChildrenAsync(CatalogEntry parent, CancellationToken token = default)
    {
        CatalogEntry Folder(CatalogKind kind, string name) => new(kind, name, parent.Database, parent.Schema, parent.Oid);
        switch (parent.Kind)
        {
            case CatalogKind.Server: return [Folder(CatalogKind.Databases, "Databases"), Folder(CatalogKind.Roles, "Login/Group Roles")];
            // SSMS 22.11 lists Database Diagrams first under a database (M1043).
            case CatalogKind.Database: return [Folder(CatalogKind.DatabaseDiagrams, "Database Diagrams"), Folder(CatalogKind.Schemas, "Schemas")];
            case CatalogKind.DatabaseDiagrams: return await DiagramsAsync(parent, token).ConfigureAwait(false);
            case CatalogKind.Schema: return [Folder(CatalogKind.Tables, "Tables"), Folder(CatalogKind.Views, "Views"), Folder(CatalogKind.MaterializedViews, "Materialized Views"), Folder(CatalogKind.Sequences, "Sequences"), Folder(CatalogKind.Functions, "Functions"), Folder(CatalogKind.Procedures, "Procedures"), Folder(CatalogKind.Enums, "Enums"), Folder(CatalogKind.Domains, "Domains")];
            // SSMS 22.11 order under a table: Columns, Keys, Constraints, Triggers, Indexes (Statistics has no node here).
            case CatalogKind.Relation when parent.RelationType is "r" or "p": return [Folder(CatalogKind.Columns, "Columns"), Folder(CatalogKind.Keys, "Keys"), Folder(CatalogKind.Constraints, "Constraints"), Folder(CatalogKind.Triggers, "Triggers"), Folder(CatalogKind.Indexes, "Indexes")];
            case CatalogKind.Relation when parent.RelationType == "v": return [Folder(CatalogKind.Columns, "Columns"), Folder(CatalogKind.Triggers, "Triggers"), Folder(CatalogKind.Indexes, "Indexes")];
            case CatalogKind.Relation: return [Folder(CatalogKind.Columns, "Columns"), Folder(CatalogKind.Indexes, "Indexes")];
        }
        if (!parent.CanExpand) return [];
        string sql = parent.Kind switch
        {
            CatalogKind.Databases => "SELECT oid, datname || CASE WHEN datallowconn THEN '' ELSE ' (Offline)' END, datname, NOT datallowconn FROM pg_catalog.pg_database WHERE NOT datistemplate ORDER BY datname",
            CatalogKind.Keys => "SELECT oid, conname FROM pg_catalog.pg_constraint WHERE conrelid=@oid AND contype IN ('p','u','f') ORDER BY CASE contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 ELSE 2 END, conname",
            CatalogKind.Constraints => "SELECT oid, conname FROM pg_catalog.pg_constraint WHERE conrelid=@oid AND contype IN ('c','x') ORDER BY conname",
            CatalogKind.Triggers => "SELECT oid, tgname FROM pg_catalog.pg_trigger WHERE tgrelid=@oid AND NOT tgisinternal ORDER BY tgname",
            CatalogKind.Schemas => "SELECT oid, nspname FROM pg_catalog.pg_namespace WHERE nspname <> 'information_schema' AND nspname !~ '^pg_' ORDER BY nspname",
            CatalogKind.Roles => "SELECT oid, rolname || CASE WHEN rolcanlogin THEN ' (login)' ELSE '' END, rolname FROM pg_catalog.pg_roles ORDER BY rolname",
            CatalogKind.Enums or CatalogKind.Domains => "SELECT t.oid, t.typname, t.typtype::text FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname=@schema AND t.typtype::text=@kind ORDER BY t.typname, t.oid",
            CatalogKind.Functions or CatalogKind.Procedures => "SELECT p.oid, p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')', pg_catalog.pg_get_userbyid(p.proowner) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.prokind::text=@kind ORDER BY p.proname, p.oid",
            CatalogKind.Columns => "SELECT attnum::oid, attname || ' (' || pg_catalog.format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ', not null)' ELSE ', null)' END FROM pg_catalog.pg_attribute WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped ORDER BY attnum",
            CatalogKind.Indexes => "SELECT c.oid, c.relname FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid WHERE i.indrelid=@oid ORDER BY c.relname",
            _ => "SELECT c.oid, c.relname, c.relkind::text, pg_catalog.pg_get_userbyid(c.relowner) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relkind::text=ANY(@kinds) ORDER BY c.relname"
        };
        await using var connection = new NpgsqlConnection(ConnectionFor(parent.Database));
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        if (sql.Contains("@schema")) command.Parameters.AddWithValue("schema", parent.Schema ?? "public");
        if (sql.Contains("@oid")) command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, parent.Oid);
        if (sql.Contains("@kind") && !sql.Contains("@kinds")) command.Parameters.AddWithValue("kind", parent.Kind switch { CatalogKind.Procedures => "p", CatalogKind.Enums => "e", CatalogKind.Domains => "d", _ => "f" });
        if (sql.Contains("@kinds")) command.Parameters.AddWithValue("kinds", parent.Kind switch
        {
            CatalogKind.Tables => new[] { "r", "p", "f" }, CatalogKind.Views => ["v"],
            CatalogKind.MaterializedViews => ["m"], CatalogKind.Sequences => ["S"],
            _ => throw new ArgumentOutOfRangeException(nameof(parent))
        });
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var entries = new List<CatalogEntry>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            uint oid = reader.GetFieldValue<uint>(0);
            string name = reader.GetString(1);
            CatalogKind kind = parent.Kind switch
            {
                CatalogKind.Databases => CatalogKind.Database, CatalogKind.Schemas => CatalogKind.Schema,
                CatalogKind.Roles => CatalogKind.Role, CatalogKind.Columns => CatalogKind.Column,
                CatalogKind.Indexes => CatalogKind.Index, CatalogKind.Functions or CatalogKind.Procedures => CatalogKind.Routine,
                CatalogKind.Enums or CatalogKind.Domains => CatalogKind.DataType,
                CatalogKind.Keys => CatalogKind.Key, CatalogKind.Constraints => CatalogKind.Constraint, CatalogKind.Triggers => CatalogKind.Trigger,
                _ => CatalogKind.Relation
            };
            entries.Add(new(kind, name, kind == CatalogKind.Database ? reader.GetString(2) : parent.Database,
                kind == CatalogKind.Schema ? name : parent.Schema, oid) { IsOffline = kind == CatalogKind.Database && reader.GetBoolean(3), RelationType = kind == CatalogKind.Relation && reader.FieldCount > 2 ? reader.GetString(2) : null, DataTypeKind = kind == CatalogKind.DataType ? reader.GetString(2) : null, RoutineKind = kind == CatalogKind.Routine ? (parent.Kind == CatalogKind.Procedures ? "p" : "f") : null, RoleName = kind == CatalogKind.Role ? reader.GetString(2) : null, Owner = kind == CatalogKind.Relation ? reader.GetString(3) : kind == CatalogKind.Routine ? reader.GetString(2) : null });
        }
        return entries;
    }

    private async Task<IReadOnlyList<CatalogEntry>> DiagramsAsync(CatalogEntry folder, CancellationToken token)
    {
        var diagrams = await new DiagramService(ConnectionFor(folder.Database)).ListAsync(token).ConfigureAwait(false);
        return diagrams.Select(d => new CatalogEntry(CatalogKind.DatabaseDiagram, d.Name, folder.Database, null, (uint)d.Id) { Owner = d.Owner }).ToArray();
    }
}
