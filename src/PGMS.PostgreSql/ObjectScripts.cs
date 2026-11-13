using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum ScriptVerb { Create, CreateOrReplace, Drop, DropAndCreate, Select, Insert, Update, Delete, Execute }
public enum ScriptDestination { NewQuery, File, Clipboard }
public sealed class ObjectScriptException(string message) : Exception(message);

/// <summary>Script … as in Object Explorer (M1054, PGMS-026). SSMS 22.11 observed: CREATE, ALTER, CREATE OR ALTER, DROP, DROP And CREATE,
/// SELECT, INSERT, UPDATE, DELETE, EXECUTE, unavailable verbs in gray. Generating a script only reads the catalog; nothing here runs it.</summary>
public static class ObjectScripts
{
    private static readonly ScriptVerb[] Definition = [ScriptVerb.Create, ScriptVerb.Drop, ScriptVerb.DropAndCreate];

    /// <summary>The noun in the menu (Script <c>Table</c> as), or null when the node has no script.</summary>
    public static string? Noun(CatalogEntry entry) => entry.Kind switch
    {
        CatalogKind.Database => entry.IsOffline ? null : "Database",
        CatalogKind.Schema => "Schema",
        CatalogKind.Index => "Index",
        CatalogKind.Key => "Key",
        CatalogKind.Constraint => "Constraint",
        CatalogKind.Trigger => "Trigger",
        CatalogKind.Role when entry.RoleName?.StartsWith("pg_", StringComparison.Ordinal) != true => "Role",
        CatalogKind.Routine => entry.RoutineKind == "p" ? "Procedure" : "Function",
        CatalogKind.DataType => entry.DataTypeKind switch { "e" => "Enum", "d" => "Domain", _ => null },
        CatalogKind.Relation => entry.RelationType switch { "r" or "p" => "Table", "v" => "View", "m" => "Materialized View", "S" => "Sequence", _ => null },
        _ => null
    };

    public static IReadOnlyList<ScriptVerb> Verbs(CatalogEntry entry) => Noun(entry) switch
    {
        null => [],
        "Table" => [.. Definition, ScriptVerb.Select, ScriptVerb.Insert, ScriptVerb.Update, ScriptVerb.Delete],
        "View" => [ScriptVerb.Create, ScriptVerb.CreateOrReplace, ScriptVerb.Drop, ScriptVerb.DropAndCreate, ScriptVerb.Select, ScriptVerb.Insert, ScriptVerb.Update, ScriptVerb.Delete],
        "Materialized View" => [.. Definition, ScriptVerb.Select],
        "Function" or "Procedure" => [ScriptVerb.Create, ScriptVerb.CreateOrReplace, ScriptVerb.Drop, ScriptVerb.DropAndCreate, ScriptVerb.Execute],
        _ => Definition
    };

    public static string FileName(CatalogEntry entry)
    {
        string name = entry.RoleName ?? entry.Name;
        int arguments = name.IndexOf('(', StringComparison.Ordinal);
        if (entry.Kind == CatalogKind.Routine && arguments > 0) name = name[..arguments];
        string full = entry.Schema is { } schema && entry.Kind != CatalogKind.Schema ? schema + "." + name : name;
        foreach (char invalid in Path.GetInvalidFileNameChars()) full = full.Replace(invalid, '_');
        return full + ".sql";
    }
}

/// <summary>Builds the scripts from the catalog in one read-only snapshot, with an empty search_path so that every name the server prints is qualified.
/// Passwords are never read. Owners and privileges are left out, as in the SSMS default.</summary>
public sealed class ObjectScriptService(string connectionString)
{
    private const string NL = "\r\n";
    private static string Id(string? name) => SqlIdentifier.Quote(name ?? throw new ObjectScriptException("The catalog returned an object without a name."));
    private static string Full(string? schema, string? name) => Id(schema) + "." + Id(name);
    private static string Text(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    // Object names may hold anything, including the end of a comment.
    private static string InComment(string value) => Regex.Replace(value, @"[\u0000-\u001f]", " ").Replace("*/", "* /", StringComparison.Ordinal);
    private static string Header(string noun, string name) => $"/****** Object:  {noun} {InComment(name)}    Script Date: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ******/{NL}";

    private sealed class Catalog(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        public async Task<List<object?[]>> Rows(string sql, uint oid)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        public async Task<object?[]> One(string sql, uint oid) => (await Rows(sql, oid).ConfigureAwait(false)) is [var row, ..] ? row : throw new ObjectScriptException("The object no longer exists. Refresh Object Explorer.");
    }

    private sealed record Target(string Noun, string Name, string DropSql);

    public async Task<string> ScriptAsync(CatalogEntry entry, ScriptVerb verb, CancellationToken token = default)
    {
        if (!ObjectScripts.Verbs(entry).Contains(verb)) throw new ObjectScriptException("This script is not available for the selected object.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog", connection, transaction)) await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var catalog = new Catalog(connection, transaction, token);
        var target = await IdentifyAsync(catalog, entry).ConfigureAwait(false);
        string prefix = entry.Kind is CatalogKind.Database or CatalogKind.Role || entry.Database is null ? "" : $"-- Database: {InComment(entry.Database)}{NL}{NL}";
        string Stamped(string sql) => Header(target.Noun, target.Name) + sql;
        return prefix + verb switch
        {
            ScriptVerb.Create => Stamped(await CreateAsync(catalog, entry, false).ConfigureAwait(false)),
            ScriptVerb.CreateOrReplace => Stamped(await CreateAsync(catalog, entry, true).ConfigureAwait(false)),
            ScriptVerb.Drop => Stamped(target.DropSql + NL),
            ScriptVerb.DropAndCreate => Stamped(target.DropSql + NL) + NL + Stamped(await CreateAsync(catalog, entry, false).ConfigureAwait(false)),
            ScriptVerb.Execute => await ExecuteAsync(catalog, entry, target).ConfigureAwait(false),
            _ => await DataAsync(catalog, entry, verb, target).ConfigureAwait(false)
        };
    }

    private static async Task<Target> IdentifyAsync(Catalog catalog, CatalogEntry entry)
    {
        switch (entry.Kind)
        {
            case CatalogKind.Database:
            {
                var r = await catalog.One("SELECT datname FROM pg_database WHERE oid=@oid", entry.Oid).ConfigureAwait(false);
                return new("Database", Id((string?)r[0]), $"DROP DATABASE IF EXISTS {Id((string?)r[0])};");
            }
            case CatalogKind.Schema:
            {
                var r = await catalog.One("SELECT nspname FROM pg_namespace WHERE oid=@oid", entry.Oid).ConfigureAwait(false);
                return new("Schema", Id((string?)r[0]), $"DROP SCHEMA IF EXISTS {Id((string?)r[0])};");
            }
            case CatalogKind.Role:
            {
                var r = await catalog.One("SELECT rolname FROM pg_roles WHERE oid=@oid", entry.Oid).ConfigureAwait(false);
                return new("Role", Id((string?)r[0]), $"DROP ROLE IF EXISTS {Id((string?)r[0])};");
            }
            case CatalogKind.Relation:
            {
                var r = await catalog.One("SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=@oid", entry.Oid).ConfigureAwait(false);
                string noun = ObjectScripts.Noun(entry)!, name = Full((string?)r[0], (string?)r[1]);
                return new(noun, name, $"DROP {noun.ToUpperInvariant()} IF EXISTS {name};");
            }
            case CatalogKind.Index:
            {
                var r = await catalog.One("""
                    SELECT n.nspname, c.relname, tn.nspname, t.relname, con.conname
                    FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    JOIN pg_class t ON t.oid=i.indrelid JOIN pg_namespace tn ON tn.oid=t.relnamespace
                    LEFT JOIN pg_constraint con ON con.conindid=i.indexrelid AND con.conrelid=i.indrelid AND con.contype IN ('p','u','x')
                    WHERE i.indexrelid=@oid
                    """, entry.Oid).ConfigureAwait(false);
                string name = Full((string?)r[0], (string?)r[1]);
                return new("Index", name, r[4] is string constraint ? $"ALTER TABLE IF EXISTS {Full((string?)r[2], (string?)r[3])} DROP CONSTRAINT IF EXISTS {Id(constraint)};" : $"DROP INDEX IF EXISTS {name};");
            }
            case CatalogKind.Routine:
            {
                var r = await catalog.One("SELECT n.nspname, p.proname, p.prokind::text, pg_get_function_identity_arguments(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=@oid", entry.Oid).ConfigureAwait(false);
                string noun = (string?)r[2] == "p" ? "Procedure" : "Function", name = $"{Full((string?)r[0], (string?)r[1])}({r[3]})";
                return new(noun, name, $"DROP {noun.ToUpperInvariant()} IF EXISTS {name};");
            }
            case CatalogKind.Key or CatalogKind.Constraint:
            {
                var r = await catalog.One("SELECT n.nspname, t.relname, c.conname FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE c.oid=@oid", entry.Oid).ConfigureAwait(false);
                string table = Full((string?)r[0], (string?)r[1]), name = Id((string?)r[2]);
                return new(ObjectScripts.Noun(entry)!, $"{name} ON {table}", $"ALTER TABLE IF EXISTS {table} DROP CONSTRAINT IF EXISTS {name};");
            }
            case CatalogKind.Trigger:
            {
                var r = await catalog.One("SELECT n.nspname, t.relname, g.tgname FROM pg_trigger g JOIN pg_class t ON t.oid=g.tgrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE g.oid=@oid", entry.Oid).ConfigureAwait(false);
                string table = Full((string?)r[0], (string?)r[1]), name = Id((string?)r[2]);
                return new("Trigger", $"{name} ON {table}", $"DROP TRIGGER IF EXISTS {name} ON {table};");
            }
            case CatalogKind.DataType:
            {
                var r = await catalog.One("SELECT n.nspname, t.typname, t.typtype::text FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE t.oid=@oid", entry.Oid).ConfigureAwait(false);
                bool domain = (string?)r[2] == "d"; string name = Full((string?)r[0], (string?)r[1]);
                return new(domain ? "Domain" : "Enum", name, $"DROP {(domain ? "DOMAIN" : "TYPE")} IF EXISTS {name};");
            }
            default: throw new ObjectScriptException("This script is not available for the selected object.");
        }
    }

    private static Task<string> CreateAsync(Catalog catalog, CatalogEntry entry, bool replace) => entry.Kind switch
    {
        CatalogKind.Database => DatabaseAsync(catalog, entry.Oid),
        CatalogKind.Schema => SchemaAsync(catalog, entry.Oid),
        CatalogKind.Role => RoleAsync(catalog, entry.Oid),
        CatalogKind.Index => IndexAsync(catalog, entry.Oid),
        CatalogKind.Key or CatalogKind.Constraint => ConstraintAsync(catalog, entry.Oid),
        CatalogKind.Trigger => TriggerAsync(catalog, entry.Oid),
        CatalogKind.Routine => RoutineAsync(catalog, entry.Oid, replace),
        CatalogKind.DataType => entry.DataTypeKind == "e" ? EnumAsync(catalog, entry.Oid) : DomainAsync(catalog, entry.Oid),
        _ => entry.RelationType switch
        {
            "S" => SequenceScriptAsync(catalog, entry.Oid),
            "v" or "m" => ViewAsync(catalog, entry.Oid, replace),
            _ => TableAsync(catalog, entry.Oid)
        }
    };

    private static async Task<string> DatabaseAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("""
            SELECT d.datname, pg_get_userbyid(d.datdba), pg_encoding_to_char(d.encoding), d.datcollate, d.datctype, d.datlocprovider::text,
                COALESCE(to_jsonb(d)->>'datlocale', to_jsonb(d)->>'daticulocale'), t.spcname, d.datconnlimit::int, shobj_description(d.oid,'pg_database')
            FROM pg_database d JOIN pg_tablespace t ON t.oid=d.dattablespace WHERE d.oid=@oid
            """, oid).ConfigureAwait(false);
        string name = Id((string?)r[0]);
        var sql = new StringBuilder($"CREATE DATABASE {name}{NL}    WITH OWNER = {Id((string?)r[1])}{NL}    TEMPLATE = template0{NL}    ENCODING = {Text((string)r[2]!)}{NL}");
        sql.Append("    LC_COLLATE = ").Append(Text((string)r[3]!)).Append(NL).Append("    LC_CTYPE = ").Append(Text((string)r[4]!)).Append(NL);
        switch ((string?)r[5])
        {
            case "i": sql.Append("    LOCALE_PROVIDER = icu").Append(NL).Append("    ICU_LOCALE = ").Append(Text((string?)r[6] ?? "")).Append(NL); break;
            case "b": sql.Append("    LOCALE_PROVIDER = builtin").Append(NL).Append("    BUILTIN_LOCALE = ").Append(Text((string?)r[6] ?? "")).Append(NL); break;
            default: sql.Append("    LOCALE_PROVIDER = libc").Append(NL); break;
        }
        sql.Append("    TABLESPACE = ").Append(Id((string?)r[7])).Append(NL).Append("    CONNECTION LIMIT = ").Append(((int)r[8]!).ToString(CultureInfo.InvariantCulture)).Append(';').Append(NL);
        if (r[9] is string comment) sql.Append($"COMMENT ON DATABASE {name} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> SchemaAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("SELECT nspname, pg_get_userbyid(nspowner), obj_description(oid,'pg_namespace') FROM pg_namespace WHERE oid=@oid", oid).ConfigureAwait(false);
        string name = Id((string?)r[0]), sql = $"CREATE SCHEMA {name} AUTHORIZATION {Id((string?)r[1])};{NL}";
        return r[2] is string comment ? sql + $"COMMENT ON SCHEMA {name} IS {Text(comment)};{NL}" : sql;
    }

    private static readonly HashSet<string> ListSettings = new(StringComparer.OrdinalIgnoreCase) { "search_path", "temp_tablespaces", "session_preload_libraries", "local_preload_libraries", "shared_preload_libraries", "DateStyle" };

    private static async Task<string> RoleAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("""
            SELECT rolname, rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolcanlogin, rolreplication, rolbypassrls, rolconnlimit::int, rolvaliduntil::text, rolconfig, shobj_description(oid,'pg_authid')
            FROM pg_roles WHERE oid=@oid
            """, oid).ConfigureAwait(false);
        string name = Id((string?)r[0]);
        string Flag(int i, string word) => ((bool)r[i]! ? "" : "NO") + word;
        var sql = new StringBuilder();
        if ((bool)r[5]!) sql.Append("-- The password is not scripted. Set one with ALTER ROLE or PASSWORD '...' before using this login.").Append(NL);
        sql.Append($"CREATE ROLE {name} WITH {Flag(5, "LOGIN")} {Flag(1, "SUPERUSER")} {Flag(4, "CREATEDB")} {Flag(3, "CREATEROLE")} {Flag(2, "INHERIT")} {Flag(6, "REPLICATION")} {Flag(7, "BYPASSRLS")} CONNECTION LIMIT {((int)r[8]!).ToString(CultureInfo.InvariantCulture)}");
        if (r[9] is string until) sql.Append(" VALID UNTIL ").Append(Text(until));
        sql.Append(';').Append(NL);
        foreach (string setting in r[10] as string[] ?? [])
        {
            int equals = setting.IndexOf('=', StringComparison.Ordinal); string key = setting[..equals], value = setting[(equals + 1)..];
            sql.Append($"ALTER ROLE {name} SET {Id(key)} = {(ListSettings.Contains(key) ? value : Text(value))};{NL}");
        }
        foreach (var m in await catalog.Rows("""
            SELECT g.rolname, a.admin_option, a.inherit_option, a.set_option
            FROM pg_auth_members a JOIN pg_roles g ON g.oid=a.roleid WHERE a.member=@oid ORDER BY g.rolname
            """, oid).ConfigureAwait(false))
            sql.Append($"GRANT {Id((string?)m[0])} TO {name} WITH ADMIN {Bool(m[1])}, INHERIT {Bool(m[2])}, SET {Bool(m[3])};{NL}");
        if (r[11] is string comment) sql.Append($"COMMENT ON ROLE {name} IS {Text(comment)};{NL}");
        return sql.ToString();
    }
    private static string Bool(object? value) => (bool)value! ? "TRUE" : "FALSE";

    private const string IndexesSql = """
        SELECT r.relname, pg_get_indexdef(i.indexrelid), i.indisvalid, i.indisclustered, obj_description(i.indexrelid,'pg_class'), con.conname, n.nspname
        FROM pg_index i JOIN pg_class r ON r.oid=i.indexrelid JOIN pg_namespace n ON n.oid=r.relnamespace
        LEFT JOIN pg_constraint con ON con.conindid=i.indexrelid AND con.conrelid=i.indrelid AND con.contype IN ('p','u','x')
        WHERE i.indrelid=@oid AND NOT EXISTS (SELECT 1 FROM pg_inherits h WHERE h.inhrelid=i.indexrelid)
        ORDER BY r.relname
        """;

    private static async Task<string> IndexAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("""
            SELECT n.nspname, c.relname, pg_get_indexdef(i.indexrelid), i.indisvalid, obj_description(i.indexrelid,'pg_class'),
                con.conname, pg_get_constraintdef(con.oid), tn.nspname, t.relname, obj_description(con.oid,'pg_constraint')
            FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_class t ON t.oid=i.indrelid JOIN pg_namespace tn ON tn.oid=t.relnamespace
            LEFT JOIN pg_constraint con ON con.conindid=i.indexrelid AND con.conrelid=i.indrelid AND con.contype IN ('p','u','x')
            WHERE i.indexrelid=@oid
            """, oid).ConfigureAwait(false);
        var sql = new StringBuilder();
        if (r[3] is false) sql.Append("-- This index is not valid on the server: a CREATE INDEX CONCURRENTLY did not finish.").Append(NL);
        if (r[5] is string constraint)
        {
            string table = Full((string?)r[7], (string?)r[8]);
            sql.Append($"ALTER TABLE {table} ADD CONSTRAINT {Id(constraint)} {r[6]};{NL}");
            if (r[9] is string note) sql.Append($"COMMENT ON CONSTRAINT {Id(constraint)} ON {table} IS {Text(note)};{NL}");
            return sql.ToString();
        }
        sql.Append(r[2]).Append(';').Append(NL);
        if (r[4] is string comment) sql.Append($"COMMENT ON INDEX {Full((string?)r[0], (string?)r[1])} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> ConstraintAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("SELECT n.nspname, t.relname, c.conname, pg_get_constraintdef(c.oid), obj_description(c.oid,'pg_constraint') FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE c.oid=@oid", oid).ConfigureAwait(false);
        string table = Full((string?)r[0], (string?)r[1]), name = Id((string?)r[2]), sql = $"ALTER TABLE {table} ADD CONSTRAINT {name} {r[3]};{NL}";
        return r[4] is string comment ? sql + $"COMMENT ON CONSTRAINT {name} ON {table} IS {Text(comment)};{NL}" : sql;
    }

    private static async Task<string> TriggerAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("SELECT n.nspname, t.relname, g.tgname, pg_get_triggerdef(g.oid, true), g.tgenabled::text, obj_description(g.oid,'pg_trigger') FROM pg_trigger g JOIN pg_class t ON t.oid=g.tgrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE g.oid=@oid", oid).ConfigureAwait(false);
        string table = Full((string?)r[0], (string?)r[1]), name = Id((string?)r[2]);
        var sql = new StringBuilder().Append(r[3]).Append(';').Append(NL);
        string? state = (string?)r[4] switch { "D" => "DISABLE TRIGGER", "R" => "ENABLE REPLICA TRIGGER", "A" => "ENABLE ALWAYS TRIGGER", _ => null };
        if (state is not null) sql.Append($"ALTER TABLE {table} {state} {name};{NL}");
        if (r[5] is string comment) sql.Append($"COMMENT ON TRIGGER {name} ON {table} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> RoutineAsync(Catalog catalog, uint oid, bool replace)
    {
        var r = await catalog.One("""
            SELECT pg_get_functiondef(p.oid), p.prokind::text, n.nspname, p.proname, pg_get_function_identity_arguments(p.oid), obj_description(p.oid,'pg_proc')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=@oid
            """, oid).ConfigureAwait(false);
        string definition = ((string)r[0]!).TrimEnd();
        const string Replace = "CREATE OR REPLACE ";
        if (!replace && definition.StartsWith(Replace, StringComparison.Ordinal)) definition = "CREATE " + definition[Replace.Length..];
        var sql = new StringBuilder(definition).Append(';').Append(NL);
        if (r[5] is string comment) sql.Append($"COMMENT ON {((string?)r[1] == "p" ? "PROCEDURE" : "FUNCTION")} {Full((string?)r[2], (string?)r[3])}({r[4]}) IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> EnumAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("SELECT n.nspname, t.typname, obj_description(t.oid,'pg_type') FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE t.oid=@oid", oid).ConfigureAwait(false);
        var labels = await catalog.Rows("SELECT enumlabel FROM pg_enum WHERE enumtypid=@oid ORDER BY enumsortorder", oid).ConfigureAwait(false);
        string name = Full((string?)r[0], (string?)r[1]);
        var sql = new StringBuilder($"CREATE TYPE {name} AS ENUM ({NL}    {string.Join("," + NL + "    ", labels.Select(l => Text((string)l[0]!)))}{NL});{NL}");
        if (r[2] is string comment) sql.Append($"COMMENT ON TYPE {name} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> DomainAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("""
            SELECT n.nspname, t.typname, format_type(t.typbasetype, t.typtypmod), t.typnotnull, pg_get_expr(t.typdefaultbin, 0), obj_description(t.oid,'pg_type'),
                CASE WHEN t.typcollation<>0 AND t.typcollation<>b.typcollation THEN quote_ident(cn.nspname)||'.'||quote_ident(co.collname) END
            FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace JOIN pg_type b ON b.oid=t.typbasetype
            LEFT JOIN pg_collation co ON co.oid=t.typcollation LEFT JOIN pg_namespace cn ON cn.oid=co.collnamespace
            WHERE t.oid=@oid
            """, oid).ConfigureAwait(false);
        var constraints = await catalog.Rows("SELECT conname, pg_get_constraintdef(oid), contype::text, convalidated FROM pg_constraint WHERE contypid=@oid ORDER BY conname", oid).ConfigureAwait(false);
        string name = Full((string?)r[0], (string?)r[1]);
        var sql = new StringBuilder($"CREATE DOMAIN {name} AS {r[2]}");
        if (r[6] is string collation) sql.Append(" COLLATE ").Append(collation);
        if (r[4] is string fallback) sql.Append(" DEFAULT ").Append(fallback);
        // PostgreSQL 17 and later also catalog NOT NULL as a constraint row; earlier versions keep only typnotnull.
        if (r[3] is true && !constraints.Any(c => (string?)c[2] == "n")) sql.Append(" NOT NULL");
        foreach (var c in constraints.Where(c => c[3] is true)) sql.Append(NL).Append((string?)c[2] == "n" ? "    " : $"    CONSTRAINT {Id((string?)c[0])} ").Append(c[1]);
        sql.Append(';').Append(NL);
        foreach (var c in constraints.Where(c => c[3] is false)) sql.Append($"ALTER DOMAIN {name} ADD CONSTRAINT {Id((string?)c[0])} {c[1]};{NL}");
        if (r[5] is string comment) sql.Append($"COMMENT ON DOMAIN {name} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private const string SequenceOptionsSql = "SELECT format_type(seqtypid,NULL), seqincrement::text, seqmin::text, seqmax::text, seqstart::text, seqcache::text, seqcycle FROM pg_sequence WHERE seqrelid=@oid";
    private static string SequenceOptions(object?[] s) => $"AS {s[0]} START WITH {s[4]} INCREMENT BY {s[1]} MINVALUE {s[2]} MAXVALUE {s[3]} CACHE {s[5]}{(s[6] is true ? " CYCLE" : "")}";

    private static async Task<string> SequenceScriptAsync(Catalog catalog, uint oid)
    {
        var r = await catalog.One("""
            SELECT n.nspname, c.relname, obj_description(c.oid,'pg_class'),
                (SELECT d.deptype::text FROM pg_depend d WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype IN ('a','i') LIMIT 1)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=@oid
            """, oid).ConfigureAwait(false);
        var options = await catalog.One(SequenceOptionsSql, oid).ConfigureAwait(false);
        string name = Full((string?)r[0], (string?)r[1]);
        var sql = new StringBuilder();
        if ((string?)r[3] == "i") sql.Append("-- This sequence belongs to an identity column; PostgreSQL creates and drops it with the column.").Append(NL);
        sql.Append($"CREATE SEQUENCE {name} {SequenceOptions(options)};{NL}");
        var owner = await catalog.Rows("""
            SELECT tn.nspname, t.relname, a.attname FROM pg_depend d JOIN pg_class t ON t.oid=d.refobjid JOIN pg_namespace tn ON tn.oid=t.relnamespace
            JOIN pg_attribute a ON a.attrelid=d.refobjid AND a.attnum=d.refobjsubid
            WHERE d.classid='pg_class'::regclass AND d.objid=@oid AND d.refclassid='pg_class'::regclass AND d.deptype='a'
            """, oid).ConfigureAwait(false);
        if (owner is [var o, ..]) sql.Append($"ALTER SEQUENCE {name} OWNED BY {Full((string?)o[0], (string?)o[1])}.{Id((string?)o[2])};{NL}");
        if (r[2] is string comment) sql.Append($"COMMENT ON SEQUENCE {name} IS {Text(comment)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> ViewAsync(Catalog catalog, uint oid, bool replace)
    {
        var r = await catalog.One("""
            SELECT n.nspname, c.relname, c.relkind::text, c.reloptions, ts.spcname, c.relispopulated, pg_get_viewdef(c.oid, true), obj_description(c.oid,'pg_class')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_tablespace ts ON ts.oid=c.reltablespace WHERE c.oid=@oid
            """, oid).ConfigureAwait(false);
        string name = Full((string?)r[0], (string?)r[1]); bool materialized = (string?)r[2] == "m";
        string body = ((string)r[6]!).Trim().TrimEnd(';').TrimEnd();
        string options = r[3] is string[] { Length: > 0 } list ? $" WITH ({string.Join(", ", list)})" : "";
        var sql = new StringBuilder();
        if (materialized) sql.Append($"CREATE MATERIALIZED VIEW {name}{options}{(r[4] is string space ? " TABLESPACE " + Id(space) : "")} AS{NL}{body}{NL}WITH {(r[5] is true ? "DATA" : "NO DATA")};{NL}");
        else sql.Append($"CREATE {(replace ? "OR REPLACE " : "")}VIEW {name}{options} AS{NL}{body};{NL}");
        if (materialized) foreach (var i in await catalog.Rows(IndexesSql, oid).ConfigureAwait(false)) sql.Append(i[1]).Append(';').Append(NL);
        await AppendTriggersAndRulesAsync(catalog, sql, oid, name).ConfigureAwait(false);
        if (r[7] is string comment) sql.Append($"COMMENT ON {(materialized ? "MATERIALIZED VIEW" : "VIEW")} {name} IS {Text(comment)};{NL}");
        await AppendColumnCommentsAsync(catalog, sql, oid, name).ConfigureAwait(false);
        return sql.ToString();
    }

    private static async Task AppendColumnCommentsAsync(Catalog catalog, StringBuilder sql, uint oid, string name)
    {
        foreach (var c in await catalog.Rows("SELECT attname, col_description(attrelid, attnum) FROM pg_attribute WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped AND col_description(attrelid, attnum) IS NOT NULL ORDER BY attnum", oid).ConfigureAwait(false))
            sql.Append($"COMMENT ON COLUMN {name}.{Id((string?)c[0])} IS {Text((string)c[1]!)};{NL}");
    }

    private static async Task AppendTriggersAndRulesAsync(Catalog catalog, StringBuilder sql, uint oid, string name)
    {
        foreach (var t in await catalog.Rows("SELECT tgname, pg_get_triggerdef(oid, true), tgenabled::text, obj_description(oid,'pg_trigger') FROM pg_trigger WHERE tgrelid=@oid AND NOT tgisinternal AND tgparentid=0 ORDER BY tgname", oid).ConfigureAwait(false))
        {
            sql.Append(t[1]).Append(';').Append(NL);
            string trigger = Id((string?)t[0]);
            string? state = (string?)t[2] switch { "D" => "DISABLE TRIGGER", "R" => "ENABLE REPLICA TRIGGER", "A" => "ENABLE ALWAYS TRIGGER", _ => null };
            if (state is not null) sql.Append($"ALTER TABLE {name} {state} {trigger};{NL}");
            if (t[3] is string note) sql.Append($"COMMENT ON TRIGGER {trigger} ON {name} IS {Text(note)};{NL}");
        }
        foreach (var rule in await catalog.Rows("SELECT pg_get_ruledef(oid, true) FROM pg_rewrite WHERE ev_class=@oid AND rulename<>'_RETURN' ORDER BY rulename", oid).ConfigureAwait(false))
            sql.Append(((string)rule[0]!).TrimEnd().TrimEnd(';')).Append(';').Append(NL);
    }

    private static async Task<string> TableAsync(Catalog catalog, uint oid)
    {
        var t = await catalog.One("""
            SELECT n.nspname, c.relname, c.relkind::text, c.relpersistence::text, c.relispartition, c.relreplident::text, c.relrowsecurity, c.relforcerowsecurity,
                c.reloptions, ts.spcname, am.amname, CASE WHEN c.relkind='p' THEN pg_get_partkeydef(c.oid) END,
                CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound, c.oid) END,
                ARRAY(SELECT h.inhparent::regclass::text FROM pg_inherits h WHERE h.inhrelid=c.oid ORDER BY h.inhseqno),
                obj_description(c.oid,'pg_class')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_tablespace ts ON ts.oid=c.reltablespace LEFT JOIN pg_am am ON am.oid=c.relam
            WHERE c.oid=@oid
            """, oid).ConfigureAwait(false);
        string name = Full((string?)t[0], (string?)t[1]);
        var columns = await catalog.Rows("""
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.attidentity::text, a.attgenerated::text, pg_get_expr(d.adbin, d.adrelid),
                CASE WHEN a.attcollation<>0 AND a.attcollation<>ty.typcollation THEN quote_ident(cn.nspname)||'.'||quote_ident(co.collname) END,
                a.attislocal, a.attstorage::text, ty.typstorage::text, a.attcompression::text, COALESCE(a.attstattarget, -1)::int,
                (SELECT d2.objid FROM pg_depend d2 WHERE d2.classid='pg_class'::regclass AND d2.refclassid='pg_class'::regclass AND d2.refobjid=a.attrelid AND d2.refobjsubid=a.attnum AND d2.deptype='i' LIMIT 1)
            FROM pg_attribute a JOIN pg_type ty ON ty.oid=a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            LEFT JOIN pg_collation co ON co.oid=a.attcollation LEFT JOIN pg_namespace cn ON cn.oid=co.collnamespace
            WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """, oid).ConfigureAwait(false);
        var constraints = await catalog.Rows("""
            SELECT conname, contype::text, convalidated, pg_get_constraintdef(oid), obj_description(oid,'pg_constraint')
            FROM pg_constraint WHERE conrelid=@oid AND contype IN ('p','u','c','x','f') AND conparentid=0 AND conislocal
            ORDER BY CASE contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'c' THEN 2 WHEN 'x' THEN 3 ELSE 4 END, conname
            """, oid).ConfigureAwait(false);
        var owned = await catalog.Rows("""
            SELECT s.oid, a.attname FROM pg_depend d JOIN pg_class s ON s.oid=d.objid
            JOIN pg_attribute a ON a.attrelid=d.refobjid AND a.attnum=d.refobjsubid
            WHERE d.classid='pg_class'::regclass AND d.refclassid='pg_class'::regclass AND d.refobjid=@oid AND d.deptype='a' AND s.relkind='S' ORDER BY s.relname
            """, oid).ConfigureAwait(false);

        var sql = new StringBuilder();
        // A serial column's default names its sequence, so the sequence goes first; DROP TABLE removes it with the column.
        var ownedDetail = new List<(string Name, string Column)>();
        foreach (var s in owned)
        {
            var seq = await catalog.One("SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=@oid", (uint)s[0]!).ConfigureAwait(false);
            string seqName = Full((string?)seq[0], (string?)seq[1]);
            var options = await catalog.One(SequenceOptionsSql, (uint)s[0]!).ConfigureAwait(false);
            sql.Append($"CREATE SEQUENCE {seqName} {SequenceOptions(options)};{NL}");
            ownedDetail.Add((seqName, (string)s[1]!));
        }

        bool partition = t[4] is true;
        string kindWord = (string?)t[3] switch { "t" => "TEMPORARY ", "u" => "UNLOGGED ", _ => "" };
        sql.Append($"CREATE {kindWord}TABLE {name}");
        var inherits = (string[])t[13]!;
        var parts = new List<string>();
        if (!partition)
            foreach (var c in columns.Where(c => c[7] is true))
            {
                var line = new StringBuilder("    ").Append(Id((string?)c[0])).Append(' ').Append(c[1]);
                if ((string?)c[10] is { Length: > 0 } compression) line.Append(" COMPRESSION ").Append(compression switch { "p" => "pglz", "l" => "lz4", _ => compression });
                if (c[6] is string collation) line.Append(" COLLATE ").Append(collation);
                switch ((string?)c[4])
                {
                    case "s": line.Append($" GENERATED ALWAYS AS ({c[5]}) STORED"); break;
                    case "v": line.Append($" GENERATED ALWAYS AS ({c[5]}) VIRTUAL"); break;
                    default:
                        if ((string?)c[3] is "a" or "d")
                        {
                            var identity = await catalog.One(SequenceOptionsSql, (uint)c[12]!).ConfigureAwait(false);
                            line.Append($" GENERATED {((string?)c[3] == "a" ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY (START WITH {identity[4]} INCREMENT BY {identity[1]} MINVALUE {identity[2]} MAXVALUE {identity[3]} CACHE {identity[5]}{(identity[6] is true ? " CYCLE" : "")})");
                        }
                        else if (c[5] is string fallback) line.Append(" DEFAULT ").Append(fallback);
                        break;
                }
                if (c[2] is true) line.Append(" NOT NULL");
                parts.Add(line.ToString());
            }
        var inline = constraints.Where(c => (string?)c[1] != "f" && c[2] is true).ToList();
        foreach (var c in inline) parts.Add($"    CONSTRAINT {Id((string?)c[0])} {c[3]}");

        if (partition)
        {
            sql.Append(" PARTITION OF ").Append(inherits[0]);
            if (parts.Count > 0) sql.Append($" ({NL}{string.Join("," + NL, parts)}{NL})");
            sql.Append(' ').Append(t[12]);
        }
        else
        {
            sql.Append($" ({NL}{string.Join("," + NL, parts)}{NL})");
            if (inherits.Length > 0) sql.Append(NL).Append("INHERITS (").Append(string.Join(", ", inherits)).Append(')');
        }
        if (t[11] is string partitionKey) sql.Append(NL).Append("PARTITION BY ").Append(partitionKey);
        if ((string?)t[10] is { } method && method != "heap" && (string?)t[2] == "r") sql.Append(NL).Append("USING ").Append(Id(method));
        if (t[8] is string[] { Length: > 0 } reloptions) sql.Append(NL).Append("WITH (").Append(string.Join(", ", reloptions)).Append(')');
        if (t[9] is string tablespace) sql.Append(NL).Append("TABLESPACE ").Append(Id(tablespace));
        sql.Append(';').Append(NL);

        foreach (var c in columns.Where(c => !partition && c[7] is true))
        {
            if ((string?)c[8] != (string?)c[9]) sql.Append($"ALTER TABLE ONLY {name} ALTER COLUMN {Id((string?)c[0])} SET STORAGE {(string?)c[8] switch { "p" => "PLAIN", "e" => "EXTERNAL", "m" => "MAIN", _ => "EXTENDED" }};{NL}");
            if ((int)c[11]! >= 0) sql.Append($"ALTER TABLE ONLY {name} ALTER COLUMN {Id((string?)c[0])} SET STATISTICS {((int)c[11]!).ToString(CultureInfo.InvariantCulture)};{NL}");
        }
        foreach (var (seqName, column) in ownedDetail) sql.Append($"ALTER SEQUENCE {seqName} OWNED BY {name}.{Id(column)};{NL}");
        // Foreign keys go after the table, since the referenced table may come later; a CHECK written inside CREATE TABLE would lose NOT VALID.
        foreach (var c in constraints.Where(c => !inline.Contains(c))) sql.Append($"ALTER TABLE {name} ADD CONSTRAINT {Id((string?)c[0])} {c[3]};{NL}");

        var indexes = await catalog.Rows(IndexesSql, oid).ConfigureAwait(false);
        foreach (var i in indexes.Where(i => i[5] is null))
        {
            if (i[2] is false) sql.Append("-- This index is not valid on the server: a CREATE INDEX CONCURRENTLY did not finish.").Append(NL);
            sql.Append(i[1]).Append(';').Append(NL);
        }
        foreach (var i in indexes.Where(i => i[3] is true)) sql.Append($"ALTER TABLE ONLY {name} CLUSTER ON {Id((string?)i[0])};{NL}");
        switch ((string?)t[5])
        {
            case "n": sql.Append($"ALTER TABLE ONLY {name} REPLICA IDENTITY NOTHING;{NL}"); break;
            case "f": sql.Append($"ALTER TABLE ONLY {name} REPLICA IDENTITY FULL;{NL}"); break;
            case "i":
                foreach (var i in await catalog.Rows("SELECT r.relname FROM pg_index x JOIN pg_class r ON r.oid=x.indexrelid WHERE x.indrelid=@oid AND x.indisreplident", oid).ConfigureAwait(false))
                    sql.Append($"ALTER TABLE ONLY {name} REPLICA IDENTITY USING INDEX {Id((string?)i[0])};{NL}");
                break;
        }
        await AppendTriggersAndRulesAsync(catalog, sql, oid, name).ConfigureAwait(false);
        if (t[6] is true) sql.Append($"ALTER TABLE {name} ENABLE ROW LEVEL SECURITY;{NL}");
        if (t[7] is true) sql.Append($"ALTER TABLE {name} FORCE ROW LEVEL SECURITY;{NL}");
        foreach (var p in await catalog.Rows("""
            SELECT polname, polcmd::text, polpermissive, pg_get_expr(polqual, polrelid), pg_get_expr(polwithcheck, polrelid),
                CASE WHEN polroles='{0}' THEN 'PUBLIC' ELSE (SELECT string_agg(quote_ident(r.rolname), ', ' ORDER BY r.rolname) FROM pg_roles r WHERE r.oid=ANY(polroles)) END
            FROM pg_policy WHERE polrelid=@oid ORDER BY polname
            """, oid).ConfigureAwait(false))
        {
            string command = (string?)p[1] switch { "r" => "SELECT", "a" => "INSERT", "w" => "UPDATE", "d" => "DELETE", _ => "ALL" };
            sql.Append($"CREATE POLICY {Id((string?)p[0])} ON {name} AS {(p[2] is true ? "PERMISSIVE" : "RESTRICTIVE")} FOR {command} TO {p[5]}");
            if (p[3] is string qualifier) sql.Append($" USING ({qualifier})");
            if (p[4] is string check) sql.Append($" WITH CHECK ({check})");
            sql.Append(';').Append(NL);
        }
        if (t[14] is string comment) sql.Append($"COMMENT ON TABLE {name} IS {Text(comment)};{NL}");
        await AppendColumnCommentsAsync(catalog, sql, oid, name).ConfigureAwait(false);
        foreach (var c in constraints.Where(c => c[4] is string)) sql.Append($"COMMENT ON CONSTRAINT {Id((string?)c[0])} ON {name} IS {Text((string)c[4]!)};{NL}");
        foreach (var i in indexes.Where(i => i[5] is null && i[4] is string)) sql.Append($"COMMENT ON INDEX {Full((string?)i[6], (string?)i[0])} IS {Text((string)i[4]!)};{NL}");
        return sql.ToString();
    }

    private static async Task<string> DataAsync(Catalog catalog, CatalogEntry entry, ScriptVerb verb, Target target)
    {
        var columns = await catalog.Rows("SELECT attname, format_type(atttypid, atttypmod), attidentity::text, attgenerated::text FROM pg_attribute WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped ORDER BY attnum", entry.Oid).ConfigureAwait(false);
        string Name(object?[] c) => Id((string?)c[0]);
        static string Placeholder(object?[] c) => $"<{c[0]}, {c[1]},>";
        // Generated and GENERATED ALWAYS columns cannot be written, so SSMS-style templates leave them out.
        var writable = columns.Where(c => (string?)c[3] == "" && (string?)c[2] != "a").ToList();
        if (verb != ScriptVerb.Select && verb != ScriptVerb.Delete && writable.Count == 0) throw new ObjectScriptException("Every column of this object is generated, so there is nothing to write.");
        return verb switch
        {
            ScriptVerb.Select => $"SELECT {string.Join($"{NL}      ,", columns.Select(Name))}{NL}  FROM {target.Name};{NL}",
            ScriptVerb.Insert => $"INSERT INTO {target.Name}{NL}           ({string.Join($"{NL}           ,", writable.Select(Name))}){NL}     VALUES{NL}           ({string.Join($"{NL}           ,", writable.Select(Placeholder))});{NL}",
            ScriptVerb.Update => $"UPDATE {target.Name}{NL}   SET {string.Join($"{NL}      ,", writable.Select(c => $"{Name(c)} = {Placeholder(c)}"))}{NL} WHERE <Search Conditions,,>;{NL}",
            ScriptVerb.Delete => $"DELETE FROM {target.Name}{NL}      WHERE <Search Conditions,,>;{NL}",
            _ => throw new ObjectScriptException("This script is not available for the selected object.")
        };
    }

    private static async Task<string> ExecuteAsync(Catalog catalog, CatalogEntry entry, Target target)
    {
        var r = await catalog.One("SELECT p.prokind::text, p.proretset, n.nspname, p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=@oid", entry.Oid).ConfigureAwait(false);
        var arguments = await catalog.Rows("""
            SELECT a.name, format_type(a.type, NULL), COALESCE(a.mode, 'i')
            FROM pg_proc p, LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]), p.proargmodes::text[], p.proargnames) WITH ORDINALITY AS a(type, mode, name, position)
            WHERE p.oid=@oid ORDER BY a.position
            """, entry.Oid).ConfigureAwait(false);
        bool procedure = (string?)r[0] == "p"; string name = Full((string?)r[2], (string?)r[3]);
        var values = new List<string>();
        foreach (var a in arguments)
        {
            string mode = (string)a[2]!;
            if (mode == "t" || (mode == "o" && !procedure)) continue;
            values.Add(mode == "o" ? "NULL" : (mode == "v" ? "VARIADIC " : "") + $"<{a[0]}, {a[1]},>");
        }
        string list = values.Count == 0 ? "()" : $"({NL}    {string.Join($"{NL}   ,", values)})";
        return procedure ? $"CALL {name}{list};{NL}" : r[1] is true ? $"SELECT *{NL}  FROM {name}{list};{NL}" : $"SELECT {name}{list};{NL}";
    }
}
