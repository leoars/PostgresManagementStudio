using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record ObjectProperty(string Name, string Value);
public sealed record ObjectProperties(CatalogEntry Target, IReadOnlyList<ObjectProperty> Values)
{
    public string? Definition { get; init; }
    public bool DefinitionTruncated { get; init; }
}

// Inspects an Explorer identity. No user SQL or object bodies are executed.
public sealed class ObjectPropertiesService(string connectionString)
{
    public async Task<ObjectProperties> InspectAsync(CatalogEntry target, CancellationToken token = default)
    {
        if (!target.CanInspectObjectProperties || target.Oid == 0)
            throw new ArgumentException("Select a supported object from Object Explorer and refresh its folder first.");
        string sql = target.Kind switch
        {
            CatalogKind.Database => DatabaseSql,
            CatalogKind.Schema => SchemaSql,
            CatalogKind.Relation => ViewSql,
            CatalogKind.Routine => RoutineSql,
            _ => throw new ArgumentException("This object kind is not supported.")
        };
        if (target.Kind != CatalogKind.Database && string.IsNullOrEmpty(target.Database))
            throw new ArgumentException("The Explorer database identity is required.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, target.Oid);
        command.Parameters.AddWithValue("name", target.Name);
        if (target.Kind != CatalogKind.Database) command.Parameters.AddWithValue("database", target.Database!);
        if (target.Kind is CatalogKind.Relation or CatalogKind.Routine)
            command.Parameters.AddWithValue("schema", target.Schema ?? "");
        if (target.Kind == CatalogKind.Relation) command.Parameters.AddWithValue("kind", target.RelationType!);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Object identity no longer matches the catalog. Refresh its Explorer folder and open Properties again.");
        var values = new List<ObjectProperty>();
        string? definition = null;
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == "Definition") definition = reader.IsDBNull(i) ? null : reader.GetString(i);
            else values.Add(new(reader.GetName(i), reader.IsDBNull(i) ? "(none)" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? ""));
        }
        const int definitionLimit = 262144;
        bool truncated = definition?.Length > definitionLimit;
        if (truncated)
        {
            int length = char.IsHighSurrogate(definition![definitionLimit - 1]) ? definitionLimit - 1 : definitionLimit;
            definition = definition[..length];
        }
        return new(target, values) { Definition = definition, DefinitionTruncated = truncated };
    }

    private const string RoutineSql = """
        SELECT current_database() AS "Database", n.nspname AS "Schema", p.proname AS "Name", p.oid AS "OID",
            pg_catalog.pg_get_function_identity_arguments(p.oid) AS "Identity arguments",
            CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END AS "Kind",
            pg_catalog.pg_get_userbyid(p.proowner) AS "Owner", l.lanname AS "Language",
            pg_catalog.pg_get_function_result(p.oid) AS "Returns",
            CASE p.provolatile WHEN 'i' THEN 'Immutable' WHEN 's' THEN 'Stable' ELSE 'Volatile' END AS "Volatility",
            p.prosecdef AS "Security definer", p.proisstrict AS "Strict (null input)",
            CASE p.proparallel WHEN 's' THEN 'Safe' WHEN 'r' THEN 'Restricted' ELSE 'Unsafe' END AS "Parallel",
            pg_catalog.has_function_privilege(p.oid, 'EXECUTE') AS "Current role EXECUTE",
            pg_catalog.array_to_string(p.proconfig, ', ') AS "Configuration",
            pg_catalog.obj_description(p.oid, 'pg_proc') AS "Comment",
            pg_catalog.left(pg_catalog.pg_get_functiondef(p.oid), 262145) AS "Definition"
        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
        JOIN pg_catalog.pg_language l ON l.oid=p.prolang
        WHERE p.oid=@oid AND n.nspname=@schema AND p.prokind IN ('f','p')
            AND p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')'=@name
            AND current_database()=@database
        """;

    private const string ViewSql = """
        SELECT current_database() AS "Database", n.nspname AS "Schema", c.relname AS "Name", c.oid AS "OID",
            pg_catalog.pg_get_userbyid(c.relowner) AS "Owner",
            CASE c.relkind WHEN 'v' THEN 'View' ELSE 'Materialized view' END AS "Kind",
            CASE WHEN c.relkind='m' THEN c.relispopulated::text ELSE NULL END AS "Materialized data populated",
            pg_catalog.array_to_string(c.reloptions, ', ') AS "Options",
            pg_catalog.obj_description(c.oid, 'pg_class') AS "Comment",
            pg_catalog.left(pg_catalog.pg_get_viewdef(c.oid, true), 262145) AS "Definition"
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE c.oid=@oid AND c.relname=@name AND n.nspname=@schema
            AND c.relkind::text=@kind AND c.relkind IN ('v','m') AND current_database()=@database
        """;

    private const string SchemaSql = """
        SELECT current_database() AS "Database", n.nspname AS "Schema", n.oid AS "OID",
            pg_catalog.pg_get_userbyid(n.nspowner) AS "Owner",
            current_user AS "Connected role",
            pg_catalog.has_schema_privilege(n.oid, 'USAGE') AS "Current role USAGE",
            pg_catalog.has_schema_privilege(n.oid, 'CREATE') AS "Current role CREATE",
            pg_catalog.obj_description(n.oid, 'pg_namespace') AS "Comment"
        FROM pg_catalog.pg_namespace n
        WHERE n.oid=@oid AND n.nspname=@name AND current_database()=@database
        """;

    private const string DatabaseSql = """
        SELECT d.datname AS "Database", d.oid AS "OID",
            pg_catalog.pg_get_userbyid(d.datdba) AS "Owner",
            pg_catalog.pg_encoding_to_char(d.encoding) AS "Encoding",
            d.datcollate AS "Collation", d.datctype AS "Character classification",
            t.spcname AS "Default tablespace", d.datallowconn AS "Allows connections",
            d.datistemplate AS "Template", d.datconnlimit AS "Connection limit (-1 = unlimited)",
            pg_catalog.shobj_description(d.oid, 'pg_database') AS "Comment"
        FROM pg_catalog.pg_database d JOIN pg_catalog.pg_tablespace t ON t.oid=d.dattablespace
        WHERE d.oid=@oid AND d.datname=@name
        """;
}
