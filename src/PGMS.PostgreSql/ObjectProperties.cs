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
            CatalogKind.Relation when target.RelationType == "f" => ForeignTableSql,
            CatalogKind.Relation => ViewSql,
            CatalogKind.Routine => RoutineSql,
            CatalogKind.DataType => CompositeTypeSql,
            CatalogKind.ForeignServer => ForeignServerSql,
            CatalogKind.Publication => PublicationSql,
            CatalogKind.Subscription => SubscriptionSql,
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
        if (target.Kind is CatalogKind.Relation or CatalogKind.Routine or CatalogKind.DataType)
            command.Parameters.AddWithValue("schema", target.Schema ?? "");
        if (target.Kind == CatalogKind.Relation && target.RelationType != "f") command.Parameters.AddWithValue("kind", target.RelationType!);
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

    // PGMS-063 (M1099). Options of servers and user mappings can hold passwords: only option names are shown, never their values,
    // and subconninfo is never read.
    private const string CompositeTypeSql = """
        SELECT current_database() AS "Database", n.nspname AS "Schema", t.typname AS "Name", t.oid AS "OID",
            pg_catalog.pg_get_userbyid(t.typowner) AS "Owner", 'Composite type' AS "Kind",
            (SELECT count(*) FROM pg_catalog.pg_attribute a WHERE a.attrelid=t.typrelid AND a.attnum>0 AND NOT a.attisdropped) AS "Attributes",
            pg_catalog.obj_description(t.oid, 'pg_type') AS "Comment",
            'CREATE TYPE ' || pg_catalog.quote_ident(n.nspname) || '.' || pg_catalog.quote_ident(t.typname) || ' AS (' || E'\n' ||
                (SELECT pg_catalog.string_agg('    ' || pg_catalog.quote_ident(a.attname) || ' ' || pg_catalog.format_type(a.atttypid, a.atttypmod)
                    || CASE WHEN a.attcollation <> 0 AND a.attcollation <> ty.typcollation THEN ' COLLATE ' || pg_catalog.quote_ident(co.collname) ELSE '' END, E',\n' ORDER BY a.attnum)
                 FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_type ty ON ty.oid=a.atttypid LEFT JOIN pg_catalog.pg_collation co ON co.oid=a.attcollation
                 WHERE a.attrelid=t.typrelid AND a.attnum>0 AND NOT a.attisdropped) || E'\n);' AS "Definition"
        FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace JOIN pg_catalog.pg_class c ON c.oid=t.typrelid
        WHERE t.oid=@oid AND t.typname=@name AND n.nspname=@schema AND t.typtype='c' AND c.relkind='c' AND current_database()=@database
        """;

    private const string ForeignTableSql = """
        SELECT current_database() AS "Database", n.nspname AS "Schema", c.relname AS "Name", c.oid AS "OID",
            pg_catalog.pg_get_userbyid(c.relowner) AS "Owner", 'Foreign table' AS "Kind",
            s.srvname AS "Foreign server", w.fdwname AS "Foreign-data wrapper",
            pg_catalog.array_to_string(ft.ftoptions, ', ') AS "Options",
            pg_catalog.obj_description(c.oid, 'pg_class') AS "Comment"
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        JOIN pg_catalog.pg_foreign_table ft ON ft.ftrelid=c.oid JOIN pg_catalog.pg_foreign_server s ON s.oid=ft.ftserver
        JOIN pg_catalog.pg_foreign_data_wrapper w ON w.oid=s.srvfdw
        WHERE c.oid=@oid AND c.relname=@name AND n.nspname=@schema AND c.relkind='f' AND current_database()=@database
        """;

    private const string ForeignServerSql = """
        SELECT current_database() AS "Database", s.srvname AS "Name", s.oid AS "OID",
            pg_catalog.pg_get_userbyid(s.srvowner) AS "Owner", w.fdwname AS "Foreign-data wrapper",
            s.srvtype AS "Type", s.srvversion AS "Version",
            (SELECT pg_catalog.string_agg(pg_catalog.split_part(o, '=', 1), ', ') FROM pg_catalog.unnest(s.srvoptions) o) AS "Option names",
            (SELECT pg_catalog.string_agg(m.usename::text, ', ' ORDER BY m.usename) FROM pg_catalog.pg_user_mappings m WHERE m.srvid=s.oid) AS "User mappings",
            (SELECT count(*) FROM pg_catalog.pg_foreign_table ft WHERE ft.ftserver=s.oid) AS "Foreign tables",
            pg_catalog.has_server_privilege(s.oid, 'USAGE') AS "Current role USAGE",
            pg_catalog.obj_description(s.oid, 'pg_foreign_server') AS "Comment"
        FROM pg_catalog.pg_foreign_server s JOIN pg_catalog.pg_foreign_data_wrapper w ON w.oid=s.srvfdw
        WHERE s.oid=@oid AND s.srvname=@name AND current_database()=@database
        """;

    // pubtruncate (PostgreSQL 11) and pubviaroot (13) are read through to_jsonb so the query works on every supported version.
    private const string PublicationSql = """
        SELECT current_database() AS "Database", p.pubname AS "Name", p.oid AS "OID",
            pg_catalog.pg_get_userbyid(p.pubowner) AS "Owner", p.puballtables AS "All tables",
            p.pubinsert AS "Publish INSERT", p.pubupdate AS "Publish UPDATE", p.pubdelete AS "Publish DELETE",
            pg_catalog.to_jsonb(p) ->> 'pubtruncate' AS "Publish TRUNCATE",
            pg_catalog.to_jsonb(p) ->> 'pubviaroot' AS "Publish via partition root",
            (SELECT count(*) FROM pg_catalog.pg_publication_tables pt WHERE pt.pubname=p.pubname) AS "Tables",
            pg_catalog.obj_description(p.oid, 'pg_publication') AS "Comment",
            (SELECT pg_catalog.left(pg_catalog.string_agg(pg_catalog.quote_ident(pt.schemaname) || '.' || pg_catalog.quote_ident(pt.tablename), E'\n' ORDER BY pt.schemaname, pt.tablename), 262145)
             FROM pg_catalog.pg_publication_tables pt WHERE pt.pubname=p.pubname) AS "Definition"
        FROM pg_catalog.pg_publication p
        WHERE p.oid=@oid AND p.pubname=@name AND current_database()=@database
        """;

    private const string SubscriptionSql = """
        SELECT current_database() AS "Database", s.subname AS "Name", s.oid AS "OID",
            pg_catalog.pg_get_userbyid(s.subowner) AS "Owner", s.subenabled AS "Enabled",
            pg_catalog.array_to_string(s.subpublications, ', ') AS "Publications",
            s.subslotname AS "Slot name", s.subsynccommit AS "Synchronous commit",
            pg_catalog.shobj_description(s.oid, 'pg_subscription') AS "Comment"
        FROM pg_catalog.pg_subscription s JOIN pg_catalog.pg_database d ON d.oid=s.subdbid
        WHERE s.oid=@oid AND s.subname=@name AND d.datname=pg_catalog.current_database() AND current_database()=@database
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
