using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PGMS.Desktop;

/// <summary>A template parameter written as in SSMS: &lt;name, type, value&gt; (the type may be empty).</summary>
public sealed record SqlTemplateParameter(string Name, string Type, string Value);

/// <summary>Template parameters of Query ▸ Specify Values for Template Parameters (PGMS-065, M1083).</summary>
public static partial class SqlTemplateParameters
{
    [GeneratedRegex(@"<(?<name>[^<>,\r\n]+),(?<type>[^<>,\r\n]*),(?<value>[^<>\r\n]*)>")]
    private static partial Regex ParameterPattern();

    /// <summary>Parameters in the order they first appear; a name repeated later keeps its first type and value.</summary>
    public static IReadOnlyList<SqlTemplateParameter> Parse(string text)
    {
        var found = new List<SqlTemplateParameter>();
        foreach (Match m in ParameterPattern().Matches(text))
        {
            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0 || found.Any(p => p.Name == name)) continue;
            found.Add(new SqlTemplateParameter(name, m.Groups["type"].Value.Trim(), m.Groups["value"].Value.Trim()));
        }
        return found;
    }

    /// <summary>Every occurrence of a parameter in <paramref name="values"/>, as (offset, length, replacement), from the last to the first so they can be applied in order.</summary>
    public static IReadOnlyList<(int Offset, int Length, string Text)> Replacements(string text, IReadOnlyDictionary<string, string> values)
    {
        var edits = new List<(int, int, string)>();
        foreach (Match m in ParameterPattern().Matches(text))
            if (values.TryGetValue(m.Groups["name"].Value.Trim(), out var value)) edits.Add((m.Index, m.Length, value));
        edits.Reverse();
        return edits;
    }

    /// <summary>Changes the value shown for some parameters, keeping them as parameters (New Function/Procedure/Trigger fill in the schema and table of the node; PGMS-061).</summary>
    public static string WithDefaults(string text, IReadOnlyDictionary<string, string> defaults) =>
        ParameterPattern().Replace(text, m => defaults.TryGetValue(m.Groups["name"].Value.Trim(), out var value) && !value.Contains('>') && !value.Contains('<') && !value.Contains('\n')
            ? $"<{m.Groups["name"].Value},{m.Groups["type"].Value}, {value}>" : m.Value);

    public static string Replace(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (offset, length, value) in Replacements(text, values)) text = text.Remove(offset, length).Insert(offset, value);
        return text;
    }
}

/// <summary>A template of Template Explorer: built in (Path null) or a .sql file of the user's Templates folder.</summary>
public sealed record SqlTemplate(string Folder, string Name, string Text, string? Path = null);

public sealed record SqlSnippetLiteral(string Id, string ToolTip, string Default);

/// <summary>A code snippet in the Visual Studio format that SSMS uses (.snippet). Only $selected$, $end$ and declared literals are replaced; any other $…$ (PostgreSQL dollar quoting) stays as text.</summary>
public sealed record SqlSnippet(string Folder, string Title, string Description, bool IsExpansion, bool IsSurroundsWith, IReadOnlyList<SqlSnippetLiteral> Literals, string Code, string Delimiter = "$", string? SourceFile = null)
{
    public abstract record Part;
    public sealed record TextPart(string Text) : Part;
    public sealed record LiteralPart(SqlSnippetLiteral Literal) : Part;
    public sealed record SelectedPart : Part;
    public sealed record EndPart : Part;

    /// <summary>The code split into text, literals, $selected$ and $end$.</summary>
    public IReadOnlyList<Part> Parts()
    {
        var parts = new List<Part>();
        var text = new System.Text.StringBuilder();
        string d = Delimiter.Length == 0 ? "$" : Delimiter;
        int i = 0;
        while (i < Code.Length)
        {
            if (string.CompareOrdinal(Code, i, d, 0, d.Length) == 0)
            {
                int close = Code.IndexOf(d, i + d.Length, StringComparison.Ordinal);
                if (close > i)
                {
                    string id = Code[(i + d.Length)..close];
                    Part? token = id == "selected" ? new SelectedPart() : id == "end" ? new EndPart() : Literals.FirstOrDefault(l => l.Id == id) is { } literal ? new LiteralPart(literal) : null;
                    if (token is not null)
                    {
                        if (text.Length > 0) { parts.Add(new TextPart(text.ToString())); text.Clear(); }
                        parts.Add(token); i = close + d.Length; continue;
                    }
                }
            }
            text.Append(Code[i]); i++;
        }
        if (text.Length > 0) parts.Add(new TextPart(text.ToString()));
        return parts;
    }

    /// <summary>The text the snippet inserts with every literal at its default; the caret offset is where $end$ falls (or the end).</summary>
    public (string Text, int Caret) Expand(string selected = "")
    {
        var builder = new System.Text.StringBuilder(); int caret = -1;
        foreach (var part in Parts())
            switch (part)
            {
                case TextPart t: builder.Append(t.Text); break;
                case LiteralPart l: builder.Append(l.Literal.Default); break;
                case SelectedPart: builder.Append(selected); break;
                case EndPart: caret = builder.Length; break;
            }
        return (builder.ToString(), caret < 0 ? builder.Length : caret);
    }

    /// <summary>Reads every CodeSnippet of a .snippet file; invalid XML or a missing Code element gives an empty list.</summary>
    public static IReadOnlyList<SqlSnippet> Load(string xml, string folder)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException) { return []; }
        var result = new List<SqlSnippet>();
        foreach (var snippet in doc.Descendants().Where(e => e.Name.LocalName == "CodeSnippet"))
        {
            XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            var header = Child(snippet, "Header"); var body = Child(snippet, "Snippet"); var code = Child(body, "Code");
            string title = Child(header, "Title")?.Value.Trim() ?? "";
            if (code is null || title.Length == 0) continue;
            var types = Child(header, "SnippetTypes")?.Elements().Select(e => e.Value.Trim()).ToArray() ?? [];
            var literals = (Child(body, "Declarations")?.Elements().Where(e => e.Name.LocalName is "Literal" or "Object") ?? [])
                .Select(l => new SqlSnippetLiteral(Child(l, "ID")?.Value.Trim() ?? "", Child(l, "ToolTip")?.Value.Trim() ?? "", Child(l, "Default")?.Value ?? ""))
                .Where(l => l.Id.Length > 0).ToArray();
            bool surround = types.Contains("SurroundsWith"), expansion = types.Length == 0 || types.Contains("Expansion");
            result.Add(new SqlSnippet(folder, title, Child(header, "Description")?.Value.Trim() ?? "", expansion, surround, literals, code.Value.Trim('\r', '\n'), code.Attribute("Delimiter")?.Value ?? "$"));
        }
        return result;
    }
}

/// <summary>Built-in PostgreSQL templates and snippets plus the user's own: .sql files under &lt;state&gt;\Templates\&lt;folder&gt; and .snippet files under &lt;state&gt;\Snippets\&lt;folder&gt;.</summary>
public static class SqlTemplateCatalog
{
    public static string StateDirectory => Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS");
    public static string UserTemplatesDirectory => Path.Combine(StateDirectory, "Templates");
    public static string UserSnippetsDirectory => Path.Combine(StateDirectory, "Snippets");
    /// <summary>Folder of the user's files that sit directly in Templates or Snippets.</summary>
    public const string UserRootFolder = "Custom";
    private const long MaxUserFileBytes = 1024 * 1024;

    public static IReadOnlyList<SqlTemplate> Templates(string? userDirectory = null)
    {
        var all = new List<SqlTemplate>(BuiltInTemplates);
        foreach (var (folder, file) in UserFiles(userDirectory ?? UserTemplatesDirectory, "*.sql"))
            try { all.Add(new SqlTemplate(folder, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return all;
    }

    public static IReadOnlyList<SqlSnippet> Snippets(string? userDirectory = null)
    {
        var all = new List<SqlSnippet>(BuiltInSnippets);
        foreach (var (folder, file) in UserFiles(userDirectory ?? UserSnippetsDirectory, "*.snippet"))
            try { all.AddRange(SqlSnippet.Load(File.ReadAllText(file), folder).Select(s => s with { SourceFile = file })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return all;
    }

    private static IEnumerable<(string Folder, string File)> UserFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        string[] files;
        try { files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            try { if (new FileInfo(file).Length > MaxUserFileBytes) continue; } catch (IOException) { continue; }
            string relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
            yield return (relative == "." ? UserRootFolder : relative.Replace(Path.DirectorySeparatorChar, '/'), file);
        }
    }

    private static SqlTemplate T(string folder, string name, string text) => new(folder, name, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    public static IReadOnlyList<SqlTemplate> BuiltInTemplates { get; } =
    [
        T("Database", "Create Database", """
            -- Create database template
            CREATE DATABASE <database_name, name, sample_database>
                WITH OWNER = <owner, role, postgres>
                     ENCODING = 'UTF8'
                     TEMPLATE = template0;
            """),
        T("Database", "Drop Database", """
            -- Drop database template (no session may be connected to it)
            DROP DATABASE IF EXISTS <database_name, name, sample_database>;
            """),
        T("Schema", "Create Schema", """
            -- Create schema template
            CREATE SCHEMA IF NOT EXISTS <schema_name, name, sample_schema>
                AUTHORIZATION <owner, role, CURRENT_USER>;
            """),
        T("Schema", "Drop Schema", """
            -- Drop schema template (RESTRICT refuses a schema that still has objects)
            DROP SCHEMA IF EXISTS <schema_name, name, sample_schema> RESTRICT;
            """),
        T("Table", "Create Table", """
            -- Create table template
            CREATE TABLE <schema_name, name, public>.<table_name, name, sample_table>
            (
                <column1_name, name, id> <column1_datatype, type, integer> GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                <column2_name, name, name> <column2_datatype, type, text> NOT NULL,
                <column3_name, name, created_at> <column3_datatype, type, timestamptz> NOT NULL DEFAULT now()
            );
            """),
        T("Table", "Add Column", """
            -- Add column template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                ADD COLUMN <new_column_name, name, column3> <new_column_datatype, type, text> <new_column_nullability, , NULL>;
            """),
        T("Table", "Drop Column", """
            -- Drop column template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                DROP COLUMN IF EXISTS <column_name, name, column3>;
            """),
        T("Table", "Add Primary Key", """
            -- Add primary key template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                ADD CONSTRAINT <constraint_name, name, sample_table_pkey> PRIMARY KEY (<columns, , id>);
            """),
        T("Table", "Add Foreign Key", """
            -- Add foreign key template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                ADD CONSTRAINT <constraint_name, name, sample_table_fkey>
                FOREIGN KEY (<columns, , parent_id>)
                REFERENCES <referenced_schema, name, public>.<referenced_table, name, parent_table> (<referenced_columns, , id>)
                ON DELETE <on_delete, , NO ACTION>;
            """),
        T("Table", "Add Check Constraint", """
            -- Add check constraint template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                ADD CONSTRAINT <constraint_name, name, sample_table_check> CHECK (<column_name, name, column1> > <minimum, , 0>);
            """),
        T("Table", "Rename Table", """
            -- Rename table template
            ALTER TABLE <schema_name, name, public>.<table_name, name, sample_table>
                RENAME TO <new_table_name, name, sample_table2>;
            """),
        T("Table", "Drop Table", """
            -- Drop table template
            DROP TABLE IF EXISTS <schema_name, name, public>.<table_name, name, sample_table>;
            """),
        T("Index", "Create Index", """
            -- Create index template
            CREATE INDEX <index_name, name, sample_table_column1_idx>
                ON <schema_name, name, public>.<table_name, name, sample_table> USING <method, , btree> (<columns, , column1>);
            """),
        T("Index", "Create Unique Index", """
            -- Create unique index template
            CREATE UNIQUE INDEX <index_name, name, sample_table_column1_key>
                ON <schema_name, name, public>.<table_name, name, sample_table> (<columns, , column1>);
            """),
        T("Index", "Create Index Concurrently", """
            -- Create index without blocking writes; cannot run inside a transaction block
            CREATE INDEX CONCURRENTLY <index_name, name, sample_table_column1_idx>
                ON <schema_name, name, public>.<table_name, name, sample_table> (<columns, , column1>);
            """),
        T("Index", "Drop Index", """
            -- Drop index template
            DROP INDEX IF EXISTS <schema_name, name, public>.<index_name, name, sample_table_column1_idx>;
            """),
        T("View", "Create View", """
            -- Create view template
            CREATE OR REPLACE VIEW <schema_name, name, public>.<view_name, name, sample_view> AS
            SELECT <columns, , *>
            FROM <source_schema, name, public>.<source_table, name, sample_table>;
            """),
        T("View", "Create Materialized View", """
            -- Create materialized view template
            CREATE MATERIALIZED VIEW <schema_name, name, public>.<view_name, name, sample_matview> AS
            SELECT <columns, , *>
            FROM <source_schema, name, public>.<source_table, name, sample_table>
            WITH DATA;
            """),
        T("View", "Refresh Materialized View", """
            -- Refresh materialized view template
            REFRESH MATERIALIZED VIEW <schema_name, name, public>.<view_name, name, sample_matview>;
            """),
        T("Function", "Create SQL Function", """
            -- Create SQL function template
            CREATE OR REPLACE FUNCTION <schema_name, name, public>.<function_name, name, sample_function>(<parameter_name, name, p_value> <parameter_type, type, integer>)
            RETURNS <return_type, type, integer>
            LANGUAGE sql
            <volatility, , STABLE>
            AS $function$
                SELECT <parameter_name, name, p_value> * 2;
            $function$;
            """),
        T("Function", "Create PL/pgSQL Function", """
            -- Create PL/pgSQL function template
            CREATE OR REPLACE FUNCTION <schema_name, name, public>.<function_name, name, sample_function>(<parameter_name, name, p_value> <parameter_type, type, integer>)
            RETURNS <return_type, type, integer>
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                result <return_type, type, integer>;
            BEGIN
                result := <parameter_name, name, p_value> * 2;
                RETURN result;
            END;
            $function$;
            """),
        T("Function", "Drop Function", """
            -- Drop function template
            DROP FUNCTION IF EXISTS <schema_name, name, public>.<function_name, name, sample_function>(<parameter_types, , integer>);
            """),
        T("Procedure", "Create Procedure", """
            -- Create procedure template (run it with CALL)
            CREATE OR REPLACE PROCEDURE <schema_name, name, public>.<procedure_name, name, sample_procedure>(<parameter_name, name, p_value> <parameter_type, type, integer>)
            LANGUAGE plpgsql
            AS $procedure$
            BEGIN
                RAISE NOTICE 'value: %', <parameter_name, name, p_value>;
            END;
            $procedure$;

            CALL <schema_name, name, public>.<procedure_name, name, sample_procedure>(<test_value, , 1>);
            """),
        T("Trigger", "Create Trigger", """
            -- Create trigger template: the trigger function, then the trigger
            CREATE OR REPLACE FUNCTION <schema_name, name, public>.<function_name, name, sample_table_trigger>()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $trigger$
            BEGIN
                NEW.<column_name, name, updated_at> := now();
                RETURN NEW;
            END;
            $trigger$;

            CREATE TRIGGER <trigger_name, name, sample_table_set_updated_at>
                <timing, , BEFORE> <events, , UPDATE> ON <schema_name, name, public>.<table_name, name, sample_table>
                FOR EACH ROW
                EXECUTE FUNCTION <schema_name, name, public>.<function_name, name, sample_table_trigger>();
            """),
        T("Trigger", "Drop Trigger", """
            -- Drop trigger template
            DROP TRIGGER IF EXISTS <trigger_name, name, sample_table_set_updated_at> ON <schema_name, name, public>.<table_name, name, sample_table>;
            """),
        T("Sequence", "Create Sequence", """
            -- Create sequence template
            CREATE SEQUENCE <schema_name, name, public>.<sequence_name, name, sample_sequence>
                AS <datatype, type, bigint>
                START WITH <start, , 1>
                INCREMENT BY <increment, , 1>;
            """),
        T("Role", "Create Login Role", """
            -- Create a role that can log in; the password is sent to the server as written
            CREATE ROLE <role_name, name, sample_login> WITH LOGIN PASSWORD '<password, , change_me>';
            """),
        T("Role", "Create Group Role", """
            -- Create a group role and add a member
            CREATE ROLE <role_name, name, sample_group> NOLOGIN;
            GRANT <role_name, name, sample_group> TO <member_role, name, sample_login>;
            """),
        T("Role", "Grant Table Privileges", """
            -- Grant privileges on every table of a schema
            GRANT <privileges, , SELECT> ON ALL TABLES IN SCHEMA <schema_name, name, public> TO <role_name, name, sample_group>;
            """),
        T("Type", "Create Enum", """
            -- Create enum type template
            CREATE TYPE <schema_name, name, public>.<type_name, name, sample_status> AS ENUM (<labels, , 'new', 'active', 'closed'>);
            """),
        T("Type", "Create Composite Type", """
            -- Create composite type template
            CREATE TYPE <schema_name, name, public>.<type_name, name, sample_pair> AS
            (
                <field1_name, name, first_value> <field1_type, type, integer>,
                <field2_name, name, second_value> <field2_type, type, text>
            );
            """),
        T("Type", "Create Domain", """
            -- Create domain template
            CREATE DOMAIN <schema_name, name, public>.<domain_name, name, positive_integer> AS <base_type, type, integer>
                CHECK (VALUE > <minimum, , 0>);
            """),
        T("Extension", "Create Extension", """
            -- Create extension template (the extension must be installed on the server)
            CREATE EXTENSION IF NOT EXISTS <extension_name, name, pg_stat_statements> WITH SCHEMA <schema_name, name, public>;
            """),
        T("Recursive Queries", "Recursive CTE", """
            -- Recursive query template: walks a parent/child hierarchy
            WITH RECURSIVE tree AS
            (
                SELECT <id_column, name, id>, <parent_column, name, parent_id>, 1 AS depth
                FROM <schema_name, name, public>.<table_name, name, sample_tree>
                WHERE <parent_column, name, parent_id> IS NULL
                UNION ALL
                SELECT child.<id_column, name, id>, child.<parent_column, name, parent_id>, tree.depth + 1
                FROM <schema_name, name, public>.<table_name, name, sample_tree> AS child
                JOIN tree ON child.<parent_column, name, parent_id> = tree.<id_column, name, id>
            )
            SELECT * FROM tree ORDER BY depth;
            """),
        T("Maintenance", "Vacuum Analyze", """
            -- Vacuum and analyze a table (cannot run inside a transaction block)
            VACUUM (ANALYZE, VERBOSE) <schema_name, name, public>.<table_name, name, sample_table>;
            """),
        T("Maintenance", "Reindex Table", """
            -- Rebuild the indexes of a table without blocking writes (PostgreSQL 12 or later)
            REINDEX TABLE CONCURRENTLY <schema_name, name, public>.<table_name, name, sample_table>;
            """),
    ];

    private static SqlSnippet S(string folder, string title, string description, string code, bool surround = false, params SqlSnippetLiteral[] literals) =>
        new(folder, title, description, !surround, surround, literals, code.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    private static SqlSnippetLiteral L(string id, string tip, string value) => new(id, tip, value);

    public static IReadOnlyList<SqlSnippet> BuiltInSnippets { get; } =
    [
        S("Function", "Create SQL Function", "Creates a SQL function.", """
            CREATE OR REPLACE FUNCTION $SchemaName$.$FunctionName$($Parameter$ $ParameterType$)
            RETURNS $ReturnType$
            LANGUAGE sql
            AS $function$
                SELECT $Parameter$;$end$
            $function$;
            """, false, L("SchemaName", "Name of the schema", "public"), L("FunctionName", "Name of the function", "sample_function"), L("Parameter", "Name of the parameter", "p_value"), L("ParameterType", "Data type of the parameter", "integer"), L("ReturnType", "Data type returned", "integer")),
        S("Function", "Drop Function", "Drops a function.", "DROP FUNCTION IF EXISTS $SchemaName$.$FunctionName$($ParameterTypes$);$end$", false, L("SchemaName", "Name of the schema", "public"), L("FunctionName", "Name of the function", "sample_function"), L("ParameterTypes", "Data types of the parameters", "integer")),
        S("Index", "Create Index", "Creates an index.", "CREATE INDEX $IndexName$ ON $SchemaName$.$TableName$ ($Columns$);$end$", false, L("IndexName", "Name of the index", "sample_table_column1_idx"), L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table"), L("Columns", "Indexed columns", "column1")),
        S("Index", "Create Unique Index", "Creates a unique index.", "CREATE UNIQUE INDEX $IndexName$ ON $SchemaName$.$TableName$ ($Columns$);$end$", false, L("IndexName", "Name of the index", "sample_table_column1_key"), L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table"), L("Columns", "Indexed columns", "column1")),
        S("Index", "Drop Index", "Drops an index.", "DROP INDEX IF EXISTS $SchemaName$.$IndexName$;$end$", false, L("SchemaName", "Name of the schema", "public"), L("IndexName", "Name of the index", "sample_table_column1_idx")),
        S("Role", "Create Login Role", "Creates a role that can log in.", "CREATE ROLE $RoleName$ WITH LOGIN PASSWORD '$Password$';$end$", false, L("RoleName", "Name of the role", "sample_login"), L("Password", "Password of the role", "change_me")),
        S("Role", "Create Group Role", "Creates a role that cannot log in.", "CREATE ROLE $RoleName$ NOLOGIN;$end$", false, L("RoleName", "Name of the role", "sample_group")),
        S("Role", "Drop Role", "Drops a role.", "DROP ROLE IF EXISTS $RoleName$;$end$", false, L("RoleName", "Name of the role", "sample_login")),
        S("Schema", "Create Schema", "Creates a schema.", "CREATE SCHEMA IF NOT EXISTS $SchemaName$ AUTHORIZATION $Owner$;$end$", false, L("SchemaName", "Name of the schema", "sample_schema"), L("Owner", "Owner of the schema", "CURRENT_USER")),
        S("Schema", "Drop Schema", "Drops a schema.", "DROP SCHEMA IF EXISTS $SchemaName$ RESTRICT;$end$", false, L("SchemaName", "Name of the schema", "sample_schema")),
        S("Table", "Create Table", "Creates a table.", """
            CREATE TABLE $SchemaName$.$TableName$
            (
                $Column1$ $Datatype1$,
                $Column2$ $Datatype2$
            );$end$
            """, false, L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table"), L("Column1", "Name of the column", "column_1"), L("Datatype1", "Data type of the column", "integer NOT NULL"), L("Column2", "Name of the column", "column_2"), L("Datatype2", "Data type of the column", "text NULL")),
        S("Table", "Alter Table", "Adds a column to a table.", "ALTER TABLE $SchemaName$.$TableName$ ADD COLUMN $Column$ $Datatype$;$end$", false, L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table"), L("Column", "Name of the column", "column_3"), L("Datatype", "Data type of the column", "text NULL")),
        S("Table", "Drop Table", "Drops a table.", "DROP TABLE IF EXISTS $SchemaName$.$TableName$;$end$", false, L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table")),
        S("Trigger", "Create Trigger", "Creates a row trigger that runs a trigger function.", """
            CREATE TRIGGER $TriggerName$
                $Timing$ $Events$ ON $SchemaName$.$TableName$
                FOR EACH ROW
                EXECUTE FUNCTION $FunctionName$();$end$
            """, false, L("TriggerName", "Name of the trigger", "sample_trigger"), L("Timing", "BEFORE, AFTER or INSTEAD OF", "BEFORE"), L("Events", "INSERT, UPDATE, DELETE (OR combined)", "UPDATE"), L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table"), L("FunctionName", "Trigger function", "sample_trigger_function")),
        S("Trigger", "Drop Trigger", "Drops a trigger.", "DROP TRIGGER IF EXISTS $TriggerName$ ON $SchemaName$.$TableName$;$end$", false, L("TriggerName", "Name of the trigger", "sample_trigger"), L("SchemaName", "Name of the schema", "public"), L("TableName", "Name of the table", "sample_table")),
        S("View", "Create View", "Creates a view.", """
            CREATE OR REPLACE VIEW $SchemaName$.$ViewName$ AS
            SELECT $Columns$
            FROM $SourceTable$;$end$
            """, false, L("SchemaName", "Name of the schema", "public"), L("ViewName", "Name of the view", "sample_view"), L("Columns", "Columns of the view", "*"), L("SourceTable", "Table read by the view", "public.sample_table")),
        S("View", "Drop View", "Drops a view.", "DROP VIEW IF EXISTS $SchemaName$.$ViewName$;$end$", false, L("SchemaName", "Name of the schema", "public"), L("ViewName", "Name of the view", "sample_view")),
        S("Control Flow", "Transaction", "Code Snippet for a transaction block.", """
            BEGIN;

            $selected$$end$

            COMMIT;
            """, true),
        S("Control Flow", "DO Block", "Code Snippet for an anonymous PL/pgSQL block.", """
            DO $do$
            BEGIN
                $selected$$end$
            END
            $do$;
            """, true),
        S("Control Flow", "If", "Code Snippet for an IF construct inside PL/pgSQL.", """
            IF $Condition$ THEN
                $selected$$end$
            END IF;
            """, true, L("Condition", "Condition to evaluate", "condition")),
        S("Control Flow", "While", "Code Snippet for a WHILE loop inside PL/pgSQL.", """
            WHILE $Condition$ LOOP
                $selected$$end$
            END LOOP;
            """, true, L("Condition", "Condition to evaluate", "condition")),
        S("Control Flow", "Begin Exception End", "Code Snippet for a PL/pgSQL block that handles errors.", """
            BEGIN
                $selected$$end$
            EXCEPTION
                WHEN $Condition$ THEN
                    RAISE NOTICE '%', SQLERRM;
            END;
            """, true, L("Condition", "Error condition", "others")),
    ];
}
