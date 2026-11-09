using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum DesignIndexKind { PrimaryKey, UniqueKey, Index }

/// <summary>One column in the Table Designer. Position is the attnum of an existing column and null for a new one, so a rename is not mistaken for drop and add.</summary>
public sealed record DesignColumn(short? Position, string Name, string Type, bool Nullable)
{
    public string? Default { get; init; }
    public ColumnIdentity Identity { get; init; }
    public string? Generated { get; init; }
    public string? Collation { get; init; }
    public string? Comment { get; init; }
}
public sealed record DesignIndexColumn(string Name, bool Descending = false);

/// <summary>Primary key, unique key or index. OriginalName is null when new. FixedDefinition marks what the designer shows but does not edit (expressions, partial, INCLUDE, other methods, exclusion).</summary>
public sealed record DesignIndex(string? OriginalName, string Name, DesignIndexKind Kind, IReadOnlyList<DesignIndexColumn> Columns)
{
    public bool Unique { get; init; }
    public bool ConstraintBacked { get; init; }
    public string? Comment { get; init; }
    public string? FixedDefinition { get; init; }
}
public sealed record DesignForeignKey(string? OriginalName, string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns)
{
    public ReferentialAction OnUpdate { get; init; }
    public ReferentialAction OnDelete { get; init; }
    public bool CheckExisting { get; init; } = true;
    public string? Comment { get; init; }
    public string? FixedDefinition { get; init; }
}
public sealed record DesignCheck(string? OriginalName, string Name, string Expression)
{
    public bool CheckExisting { get; init; } = true;
    public string? Comment { get; init; }
    public string? FixedDefinition { get; init; }
}

public sealed record TableDesign(uint Oid, string Schema, string Name, IReadOnlyList<DesignColumn> Columns)
{
    public IReadOnlyList<DesignIndex> Indexes { get; init; } = [];
    public IReadOnlyList<DesignForeignKey> ForeignKeys { get; init; } = [];
    public IReadOnlyList<DesignCheck> Checks { get; init; } = [];
    public string? Comment { get; init; }
    public string Owner { get; init; } = "";
    public double EstimatedRows { get; init; }
    /// <summary>Null when the designer can change the table; otherwise why it opens read-only.</summary>
    public string? ReadOnlyReason { get; init; }
    [JsonIgnore] public string Fingerprint => JsonSerializer.Serialize(this with { EstimatedRows = 0 });
}

public sealed record TableDesignChange(IReadOnlyList<string> Statements, IReadOnlyList<string> Warnings)
{
    public string Sql => string.Join("\n", Statements);
}

/// <summary>Compares the table as read with the table as designed and writes the ALTER statements, in an order that lets PostgreSQL run them in one transaction.</summary>
public static class TableDesignScript
{
    private static readonly Regex TypeShape = new("""^[\p{L}_"][\p{L}\p{N}_$." ]*(\(\s*\d+(\s*,\s*-?\d+)?\s*\))?(\s+with(out)?\s+time\s+zone)?(\s*\[\s*\d*\s*\])*$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] IdentityTypes = ["smallint", "integer", "bigint", "int2", "int4", "int8"];

    public static TableDesignChange Build(TableDesign? original, TableDesign current)
    {
        Validate(current);
        var statements = new List<string>();
        var warnings = new List<string>();
        if (original is null) { Create(current, statements); return new(statements, warnings); }

        string table = ObjectSql.Relation(original.Schema, original.Name);
        var kept = current.Columns.Where(c => c.Position is not null).ToDictionary(c => c.Position!.Value);
        var renames = original.Columns.Where(c => kept.ContainsKey(c.Position!.Value)).ToDictionary(c => c.Name, c => kept[c.Position!.Value].Name, StringComparer.Ordinal);
        var dropped = original.Columns.Where(c => !kept.ContainsKey(c.Position!.Value)).ToArray();
        string Mapped(string column) => renames.GetValueOrDefault(column, column);

        // Definitions compared under the designer's renames: renaming a column alone must not rebuild its keys.
        bool SameIndex(DesignIndex before, DesignIndex after) => before.FixedDefinition is not null || before.Kind == after.Kind && before.Unique == after.Unique && before.Columns.Select(c => c with { Name = Mapped(c.Name) }).SequenceEqual(after.Columns);
        bool SameForeignKey(DesignForeignKey before, DesignForeignKey after) => before.FixedDefinition is not null || before.Columns.Select(Mapped).SequenceEqual(after.Columns) && before.ReferencedSchema == after.ReferencedSchema && before.ReferencedTable == after.ReferencedTable
            && before.ReferencedColumns.Select(c => before.ReferencedSchema == original.Schema && before.ReferencedTable == original.Name ? Mapped(c) : c).SequenceEqual(after.ReferencedColumns)
            && before.OnUpdate == after.OnUpdate && before.OnDelete == after.OnDelete && before.CheckExisting == after.CheckExisting;
        static bool SameCheck(DesignCheck before, DesignCheck after) => before.FixedDefinition is not null || before.Expression == after.Expression && before.CheckExisting == after.CheckExisting;

        var foreignKeys = Pair(original.ForeignKeys, current.ForeignKeys, f => f.Name, f => f.OriginalName, SameForeignKey);
        var checks = Pair(original.Checks, current.Checks, c => c.Name, c => c.OriginalName, SameCheck);
        var indexes = Pair(original.Indexes, current.Indexes, i => i.Name, i => i.OriginalName, SameIndex);

        foreach (var key in foreignKeys.Removed) statements.Add($"ALTER TABLE {table} DROP CONSTRAINT {SqlIdentifier.Quote(key.Name)} RESTRICT;");
        foreach (var check in checks.Removed) statements.Add($"ALTER TABLE {table} DROP CONSTRAINT {SqlIdentifier.Quote(check.Name)} RESTRICT;");
        foreach (var index in indexes.Removed.OrderBy(i => i.Kind == DesignIndexKind.PrimaryKey))
            statements.Add(index.ConstraintBacked ? $"ALTER TABLE {table} DROP CONSTRAINT {SqlIdentifier.Quote(index.Name)} RESTRICT;" : $"DROP INDEX {ObjectSql.Relation(original.Schema, index.Name)} RESTRICT;");

        foreach (var column in dropped)
        {
            statements.Add($"ALTER TABLE {table} DROP COLUMN {SqlIdentifier.Quote(column.Name)} RESTRICT;");
            warnings.Add($"Dropping column {column.Name} deletes its data.");
        }
        foreach (var (from, to) in renames.Where(r => r.Key != r.Value))
        {
            if (renames.ContainsKey(to))
                throw new ArgumentException($"Column {from} cannot take the name {to} while another column still has it. Save the renames separately.");
            statements.Add($"ALTER TABLE {table} RENAME COLUMN {SqlIdentifier.Quote(from)} TO {SqlIdentifier.Quote(to)};");
        }
        foreach (var before in original.Columns.Where(c => kept.ContainsKey(c.Position!.Value)))
            AlterColumn(table, before, kept[before.Position!.Value], statements, warnings);
        foreach (var column in current.Columns.Where(c => c.Position is null))
            statements.Add($"ALTER TABLE {table} ADD COLUMN {ColumnDefinition(column)};");

        foreach (var (before, after) in indexes.Renamed)
            statements.Add(before.ConstraintBacked ? $"ALTER TABLE {table} RENAME CONSTRAINT {SqlIdentifier.Quote(before.Name)} TO {SqlIdentifier.Quote(after.Name)};"
                : $"ALTER INDEX {ObjectSql.Relation(original.Schema, before.Name)} RENAME TO {SqlIdentifier.Quote(after.Name)};");
        foreach (var (before, after) in foreignKeys.Renamed) statements.Add($"ALTER TABLE {table} RENAME CONSTRAINT {SqlIdentifier.Quote(before.Name)} TO {SqlIdentifier.Quote(after.Name)};");
        foreach (var (before, after) in checks.Renamed) statements.Add($"ALTER TABLE {table} RENAME CONSTRAINT {SqlIdentifier.Quote(before.Name)} TO {SqlIdentifier.Quote(after.Name)};");
        AddObjects(table, indexes.Added, checks.Added, foreignKeys.Added, statements);

        if (current.Name != original.Name)
        {
            statements.Add($"ALTER TABLE {table} RENAME TO {SqlIdentifier.Quote(current.Name)};");
            table = ObjectSql.Relation(original.Schema, current.Name);
        }
        if (current.Comment != original.Comment) statements.Add($"COMMENT ON TABLE {table} IS {Text(current.Comment)};");
        foreach (var before in original.Columns.Where(c => kept.ContainsKey(c.Position!.Value)))
            if (kept[before.Position!.Value] is var after && after.Comment != before.Comment) statements.Add(ColumnComment(table, after));
        foreach (var column in current.Columns.Where(c => c.Position is null && c.Comment is not null)) statements.Add(ColumnComment(table, column));
        var rebuilt = indexes.Added.Select(i => i.Name).Concat(foreignKeys.Added.Select(f => f.Name)).Concat(checks.Added.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        Comments(table, original.Schema, current, original, rebuilt, statements);
        return new(statements, warnings);
    }

    private sealed record Paired<T>(IReadOnlyList<T> Removed, IReadOnlyList<T> Added, IReadOnlyList<(T Before, T After)> Renamed);

    // An item that only changed name is renamed; an item that changed definition is dropped and added again, which is the only way PostgreSQL offers.
    private static Paired<T> Pair<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> name, Func<T, string?> originalName, Func<T, T, bool> same)
    {
        var removed = new List<T>(); var added = after.Where(a => originalName(a) is null).ToList(); var renamed = new List<(T, T)>();
        foreach (var item in before)
        {
            var match = after.FirstOrDefault(a => originalName(a) == name(item));
            if (match is null) removed.Add(item);
            else if (!same(item, match)) { removed.Add(item); added.Add(match); }
            else if (name(match) != name(item)) renamed.Add((item, match));
        }
        return new(removed, added, renamed);
    }

    private static void Create(TableDesign design, List<string> statements)
    {
        string table = ObjectSql.Relation(design.Schema, design.Name);
        statements.Add($"CREATE TABLE {table} (\n    " + string.Join(",\n    ", design.Columns.Select(ColumnDefinition)) + "\n);");
        AddObjects(table, design.Indexes, design.Checks, design.ForeignKeys, statements);
        if (design.Comment is not null) statements.Add($"COMMENT ON TABLE {table} IS {Text(design.Comment)};");
        foreach (var column in design.Columns.Where(c => c.Comment is not null)) statements.Add(ColumnComment(table, column));
        Comments(table, design.Schema, design, null, [], statements);
    }

    private static void AddObjects(string table, IEnumerable<DesignIndex> indexes, IEnumerable<DesignCheck> checks, IEnumerable<DesignForeignKey> foreignKeys, List<string> statements)
    {
        foreach (var index in indexes.OrderBy(i => i.Kind))
        {
            string columns = string.Join(", ", index.Columns.Select(c => SqlIdentifier.Quote(c.Name) + (c.Descending ? " DESC" : "")));
            string plain = string.Join(", ", index.Columns.Select(c => SqlIdentifier.Quote(c.Name)));
            statements.Add(index.Kind switch
            {
                DesignIndexKind.PrimaryKey => $"ALTER TABLE {table} ADD CONSTRAINT {SqlIdentifier.Quote(index.Name)} PRIMARY KEY ({plain});",
                DesignIndexKind.UniqueKey => $"ALTER TABLE {table} ADD CONSTRAINT {SqlIdentifier.Quote(index.Name)} UNIQUE ({plain});",
                _ => $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {SqlIdentifier.Quote(index.Name)} ON {table} ({columns});"
            });
        }
        foreach (var check in checks)
            statements.Add($"ALTER TABLE {table} ADD CONSTRAINT {SqlIdentifier.Quote(check.Name)} CHECK ({check.Expression.Trim()}){(check.CheckExisting ? "" : " NOT VALID")};");
        foreach (var key in foreignKeys)
        {
            string columns = string.Join(", ", key.Columns.Select(SqlIdentifier.Quote)), referenced = string.Join(", ", key.ReferencedColumns.Select(SqlIdentifier.Quote));
            statements.Add($"ALTER TABLE {table} ADD CONSTRAINT {SqlIdentifier.Quote(key.Name)} FOREIGN KEY ({columns}) REFERENCES {ObjectSql.Relation(key.ReferencedSchema, key.ReferencedTable)} ({referenced})"
                + $" ON UPDATE {Action(key.OnUpdate)} ON DELETE {Action(key.OnDelete)}{(key.CheckExisting ? "" : " NOT VALID")};");
        }
    }

    /// <summary>The statements that add the foreign keys of a table, with their descriptions.</summary>
    public static IReadOnlyList<string> AddForeignKeys(TableDesign design)
    {
        string table = ObjectSql.Relation(design.Schema, design.Name);
        var statements = new List<string>();
        AddObjects(table, [], [], design.ForeignKeys, statements);
        foreach (var key in design.ForeignKeys.Where(k => k.Comment is not null)) statements.Add($"COMMENT ON CONSTRAINT {SqlIdentifier.Quote(key.Name)} ON {table} IS {Text(key.Comment)};");
        return statements;
    }

    // A rebuilt object loses its comment with the DROP, so it is compared against no comment at all.
    private static void Comments(string table, string schema, TableDesign current, TableDesign? original, HashSet<string> rebuilt, List<string> statements)
    {
        foreach (var index in current.Indexes)
        {
            string? before = rebuilt.Contains(index.Name) ? null : original?.Indexes.FirstOrDefault(i => i.Name == index.OriginalName)?.Comment;
            if (index.Comment == before) continue;
            statements.Add(index.Kind == DesignIndexKind.Index ? $"COMMENT ON INDEX {ObjectSql.Relation(schema, index.Name)} IS {Text(index.Comment)};"
                : $"COMMENT ON CONSTRAINT {SqlIdentifier.Quote(index.Name)} ON {table} IS {Text(index.Comment)};");
        }
        foreach (var key in current.ForeignKeys)
            if (key.Comment != (rebuilt.Contains(key.Name) ? null : original?.ForeignKeys.FirstOrDefault(f => f.Name == key.OriginalName)?.Comment)) statements.Add($"COMMENT ON CONSTRAINT {SqlIdentifier.Quote(key.Name)} ON {table} IS {Text(key.Comment)};");
        foreach (var check in current.Checks)
            if (check.Comment != (rebuilt.Contains(check.Name) ? null : original?.Checks.FirstOrDefault(c => c.Name == check.OriginalName)?.Comment)) statements.Add($"COMMENT ON CONSTRAINT {SqlIdentifier.Quote(check.Name)} ON {table} IS {Text(check.Comment)};");
    }

    private static void AlterColumn(string table, DesignColumn before, DesignColumn after, List<string> statements, List<string> warnings)
    {
        string alter = $"ALTER TABLE {table} ALTER COLUMN {SqlIdentifier.Quote(after.Name)}";
        bool typeChanged = before.Type != after.Type, collationChanged = before.Collation != after.Collation;
        bool defaultChanged = before.Default != after.Default;
        if (after.Generated is not null && after.Generated != before.Generated)
            throw new ArgumentException($"Column {after.Name}: a generated expression can only be set on a new column.");
        // The default goes first because PostgreSQL would otherwise try to cast it to the new type.
        if (before.Default is not null && (defaultChanged || typeChanged)) statements.Add($"{alter} DROP DEFAULT;");
        if (before.Identity != ColumnIdentity.None && after.Identity == ColumnIdentity.None) statements.Add($"{alter} DROP IDENTITY;");
        if (before.Generated is not null && after.Generated is null) statements.Add($"{alter} DROP EXPRESSION;");
        if (typeChanged || collationChanged)
        {
            string collation = collationChanged ? " COLLATE " + (after.Collation ?? "pg_catalog.\"default\"") : "";
            string column = SqlIdentifier.Quote(after.Name);
            statements.Add($"{alter} TYPE {after.Type}{collation}" + (typeChanged ? $" USING {column}::{after.Type};" : ";"));
            if (typeChanged) warnings.Add($"Changing the type of column {after.Name} converts stored values and may rewrite the table.");
        }
        if (before.Nullable != after.Nullable) statements.Add($"{alter} {(after.Nullable ? "DROP" : "SET")} NOT NULL;");
        if (after.Default is not null && (defaultChanged || typeChanged)) statements.Add($"{alter} SET DEFAULT {after.Default.Trim()};");
        if (before.Identity == ColumnIdentity.None && after.Identity != ColumnIdentity.None) statements.Add($"{alter} ADD GENERATED {Generation(after.Identity)} AS IDENTITY;");
        else if (before.Identity != after.Identity && after.Identity != ColumnIdentity.None) statements.Add($"{alter} SET GENERATED {Generation(after.Identity)};");
    }

    private static string ColumnDefinition(DesignColumn column)
    {
        string sql = SqlIdentifier.Quote(column.Name) + " " + column.Type;
        if (column.Collation is not null) sql += " COLLATE " + column.Collation;
        if (column.Generated is not null) sql += $" GENERATED ALWAYS AS ({column.Generated.Trim()}) STORED";
        if (column.Identity != ColumnIdentity.None) sql += $" GENERATED {Generation(column.Identity)} AS IDENTITY";
        if (column.Default is not null) sql += " DEFAULT " + column.Default.Trim();
        return sql + (column.Nullable ? " NULL" : " NOT NULL");
    }
    private static string ColumnComment(string table, DesignColumn column) => $"COMMENT ON COLUMN {table}.{SqlIdentifier.Quote(column.Name)} IS {Text(column.Comment)};";
    private static string Generation(ColumnIdentity identity) => identity == ColumnIdentity.Always ? "ALWAYS" : "BY DEFAULT";
    private static string Action(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION", ReferentialAction.Restrict => "RESTRICT", ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL", ReferentialAction.SetDefault => "SET DEFAULT", _ => throw new ArgumentException("Unsupported referential action.")
    };
    private static string Text(string? value)
    {
        if (value is null) return "NULL";
        if (value.Contains('\0') || value.Length > 10000) throw new ArgumentException("Descriptions must contain no NUL and at most 10000 characters.");
        return "E'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";
    }
    private static void Expression(string? value, string label)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Contains(';') || value.Contains('\0')))
            throw new ArgumentException($"{label}: enter one expression without a statement terminator.");
    }

    private static void Validate(TableDesign design)
    {
        ObjectSql.Relation(design.Schema, design.Name);
        if (design.Columns.Count is < 1 or > 1600) throw new ArgumentException("A table needs at least one column.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in design.Columns)
        {
            SqlIdentifier.Quote(column.Name);
            if (!names.Add(column.Name)) throw new ArgumentException($"Column name {column.Name} is used more than once.");
            if (!TypeShape.IsMatch(column.Type.Trim()) || column.Type.Contains("--", StringComparison.Ordinal)) throw new ArgumentException($"Column {column.Name}: enter a data type such as integer, text or numeric(18,2).");
            Expression(column.Default, $"Column {column.Name} default");
            Expression(column.Generated, $"Column {column.Name} generated expression");
            if (column.Collation is { } collation && !Regex.IsMatch(collation, """^("[^"]+"|[\p{L}_][\p{L}\p{N}_$]*)(\.("[^"]+"|[\p{L}_][\p{L}\p{N}_$]*))?$""", RegexOptions.CultureInvariant)) throw new ArgumentException($"Column {column.Name}: enter a collation name.");
            if (column.Identity != ColumnIdentity.None)
            {
                if (!IdentityTypes.Contains(column.Type.Trim(), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Column {column.Name}: identity requires smallint, integer or bigint.");
                if (column.Nullable) throw new ArgumentException($"Column {column.Name}: an identity column cannot allow nulls.");
                if (column.Default is not null || column.Generated is not null) throw new ArgumentException($"Column {column.Name}: an identity column cannot have a default or generated expression.");
            }
            if (column.Generated is not null && column.Default is not null) throw new ArgumentException($"Column {column.Name}: a generated column cannot have a default.");
        }
        void Known(IEnumerable<string> columns, string owner)
        {
            foreach (var column in columns) if (!names.Contains(column)) throw new ArgumentException($"{owner} uses column {column}, which is not in the table.");
        }
        if (design.Indexes.Count(i => i.Kind == DesignIndexKind.PrimaryKey) > 1) throw new ArgumentException("A table can have only one primary key.");
        var objects = new HashSet<string>(StringComparer.Ordinal);
        void Unique(string name) { SqlIdentifier.Quote(name); if (!objects.Add(name)) throw new ArgumentException($"The name {name} is used by more than one key, index or constraint."); }
        foreach (var index in design.Indexes)
        {
            Unique(index.Name);
            if (index.FixedDefinition is not null) continue;
            if (index.Columns.Count == 0) throw new ArgumentException($"{index.Name}: choose at least one column.");
            if (index.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != index.Columns.Count) throw new ArgumentException($"{index.Name}: a column can appear only once.");
            Known(index.Columns.Select(c => c.Name), index.Name);
            if (index.Kind != DesignIndexKind.Index && index.Columns.Any(c => c.Descending)) throw new ArgumentException($"{index.Name}: PostgreSQL keys cannot set a sort order; use an index.");
        }
        foreach (var key in design.ForeignKeys)
        {
            Unique(key.Name);
            if (key.FixedDefinition is not null) continue;
            if (key.Columns.Count == 0 || key.Columns.Count != key.ReferencedColumns.Count) throw new ArgumentException($"{key.Name}: choose the same number of columns on both sides.");
            ObjectSql.Relation(key.ReferencedSchema, key.ReferencedTable);
            foreach (var column in key.ReferencedColumns) SqlIdentifier.Quote(column);
            Known(key.Columns, key.Name);
        }
        foreach (var check in design.Checks)
        {
            Unique(check.Name);
            if (check.FixedDefinition is null) { if (check.Expression.Length == 0) throw new ArgumentException($"{check.Name}: enter the expression."); Expression(check.Expression, check.Name); }
        }
    }
}

/// <summary>Applies a designer change in one transaction, refusing it when the table no longer matches what the designer read.</summary>
public sealed class TableDesignEdit(TableDesign? original, TableDesign current) : IAdministrativeEdit
{
    public int CommandTimeoutSeconds => 0; // Type changes can rewrite large tables; Cancel stops them.
    public string ToSql() => TableDesignScript.Build(original, current).Sql;
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (original is null) return;
        await using (var guard = new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(original.Schema, original.Name)} IN ACCESS EXCLUSIVE MODE", connection, transaction) { CommandTimeout = 30 })
            await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var observed = await TableDesignService.ReadAsync(connection, transaction, original.Schema, original.Name, token).ConfigureAwait(false);
        if (observed.Fingerprint != original.Fingerprint)
            throw new InvalidOperationException("The table changed since the designer read it. Refresh the designer before saving.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        var observed = await TableDesignService.ReadAsync(connection, transaction, current.Schema, current.Name, token).ConfigureAwait(false);
        if (original is not null && observed.Oid != original.Oid) throw new InvalidOperationException("The saved table is not the table the designer read.");
        if (!observed.Columns.Select(c => (c.Name, c.Nullable)).OrderBy(c => c.Name, StringComparer.Ordinal).SequenceEqual(current.Columns.Select(c => (c.Name, c.Nullable)).OrderBy(c => c.Name, StringComparer.Ordinal)))
            throw new InvalidOperationException("The saved columns do not match the design.");
        static string[] Key(TableDesign d) => d.Indexes.Where(i => i.Kind == DesignIndexKind.PrimaryKey).SelectMany(i => i.Columns).Select(c => c.Name).ToArray();
        if (!Key(observed).SequenceEqual(Key(current))) throw new InvalidOperationException("The saved primary key does not match the design.");
    }
}

public sealed class TableDesignService(string connectionString)
{
    public async Task<TableDesign> InspectAsync(string schema, string table, CancellationToken token = default)
    {
        ObjectSql.Relation(schema, table);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var design = await ReadAsync(connection, transaction, schema, table, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return design;
    }

    internal static async Task<TableDesign> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table, CancellationToken token)
    {
        const string tableSql = """
            SELECT c.oid, pg_catalog.pg_get_userbyid(c.relowner), c.reltuples::double precision, pg_catalog.obj_description(c.oid, 'pg_class'),
                c.relkind::text, c.relpersistence::text, c.relispartition,
                EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema AND c.relname=@table AND c.relkind IN ('r','p')
            """;
        uint oid; string owner, kind, persistence; double rows; string? comment; bool partition, inherited;
        await using (var command = new NpgsqlCommand(tableSql, connection, transaction) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Table was not found. Refresh Object Explorer and try again.");
            oid = reader.GetFieldValue<uint>(0); owner = reader.GetString(1); rows = reader.GetDouble(2); comment = reader.IsDBNull(3) ? null : reader.GetString(3);
            kind = reader.GetString(4); persistence = reader.GetString(5); partition = reader.GetBoolean(6); inherited = reader.GetBoolean(7);
        }
        const string columnSql = """
            SELECT a.attnum, a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod), NOT a.attnotnull,
                CASE WHEN a.attgenerated='' THEN pg_catalog.pg_get_expr(d.adbin,d.adrelid) END,
                a.attidentity::text,
                CASE WHEN a.attgenerated<>'' THEN pg_catalog.pg_get_expr(d.adbin,d.adrelid) END,
                CASE WHEN a.attcollation<>t.typcollation THEN pg_catalog.quote_ident(cn.nspname)||'.'||pg_catalog.quote_ident(co.collname) END,
                pg_catalog.col_description(a.attrelid,a.attnum)
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            LEFT JOIN pg_catalog.pg_collation co ON co.oid=a.attcollation
            LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid=co.collnamespace
            WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """;
        var columns = new List<DesignColumn>();
        await using (var command = new NpgsqlCommand(columnSql, connection, transaction) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                columns.Add(new(reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
                {
                    Default = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Identity = reader.GetString(5) switch { "a" => ColumnIdentity.Always, "d" => ColumnIdentity.ByDefault, _ => ColumnIdentity.None },
                    Generated = reader.IsDBNull(6) ? null : reader.GetString(6), Collation = reader.IsDBNull(7) ? null : reader.GetString(7), Comment = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
        }
        const string indexSql = """
            SELECT r.relname, i.indisunique, con.contype::text, am.amname,
                i.indpred IS NOT NULL OR i.indexprs IS NOT NULL OR i.indnkeyatts<>i.indnatts OR NOT i.indisvalid,
                pg_catalog.pg_get_indexdef(i.indexrelid),
                COALESCE(pg_catalog.obj_description(con.oid,'pg_constraint'), pg_catalog.obj_description(i.indexrelid,'pg_class')),
                ARRAY(SELECT a.attname::text FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum,ord) JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum ORDER BY k.ord),
                ARRAY(SELECT (o.opt & 1)=1 FROM unnest(i.indoption::int2[]) WITH ORDINALITY o(opt,ord) ORDER BY o.ord)
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class r ON r.oid=i.indexrelid JOIN pg_catalog.pg_am am ON am.oid=r.relam
            LEFT JOIN pg_catalog.pg_constraint con ON con.conindid=i.indexrelid AND con.conrelid=i.indrelid AND con.contype IN ('p','u','x')
            WHERE i.indrelid=@oid ORDER BY con.contype NULLS LAST, r.relname
            """;
        var indexes = new List<DesignIndex>();
        await using (var command = new NpgsqlCommand(indexSql, connection, transaction) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string name = reader.GetString(0); string? type = reader.IsDBNull(2) ? null : reader.GetString(2);
                var names = reader.GetFieldValue<string[]>(7); var descending = reader.GetFieldValue<bool[]>(8);
                bool editable = !reader.GetBoolean(4) && reader.GetString(3) == "btree" && type != "x" && names.Length == descending.Length;
                indexes.Add(new(name, name, type switch { "p" => DesignIndexKind.PrimaryKey, "u" => DesignIndexKind.UniqueKey, _ => DesignIndexKind.Index }, names.Select((c, n) => new DesignIndexColumn(c, n < descending.Length && descending[n])).ToArray())
                {
                    Unique = reader.GetBoolean(1), ConstraintBacked = type is not null, Comment = reader.IsDBNull(6) ? null : reader.GetString(6), FixedDefinition = editable ? null : reader.GetString(5)
                });
            }
        }
        const string constraintSql = """
            SELECT c.conname, c.contype::text, c.convalidated, pg_catalog.pg_get_constraintdef(c.oid), pg_catalog.obj_description(c.oid,'pg_constraint'),
                ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k(attnum,ord) JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord),
                ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(attnum,ord) JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord),
                n.nspname, r.relname, c.confupdtype::text, c.confdeltype::text,
                c.condeferrable OR (c.contype='f' AND c.confmatchtype<>'s') OR (c.contype='c' AND c.connoinherit), pg_catalog.pg_get_expr(c.conbin,c.conrelid)
            FROM pg_catalog.pg_constraint c
            LEFT JOIN pg_catalog.pg_class r ON r.oid=c.confrelid LEFT JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            WHERE c.conrelid=@oid AND c.contype IN ('f','c') ORDER BY c.conname
            """;
        var foreignKeys = new List<DesignForeignKey>(); var checks = new List<DesignCheck>();
        await using (var command = new NpgsqlCommand(constraintSql, connection, transaction) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string name = reader.GetString(0); bool validated = reader.GetBoolean(2); string definition = reader.GetString(3); string? note = reader.IsDBNull(4) ? null : reader.GetString(4);
                bool special = reader.GetBoolean(11);
                if (reader.GetString(1) == "c") { checks.Add(new(name, name, reader.GetString(12)) { CheckExisting = validated, Comment = note, FixedDefinition = special ? definition : null }); continue; }
                foreignKeys.Add(new(name, name, reader.GetFieldValue<string[]>(5), reader.GetString(7), reader.GetString(8), reader.GetFieldValue<string[]>(6))
                {
                    OnUpdate = Action(reader.GetString(9)), OnDelete = Action(reader.GetString(10)), CheckExisting = validated, Comment = note,
                    // SET NULL (column list) has no form in the designer, so it stays fixed along with MATCH and DEFERRABLE.
                    FixedDefinition = special || definition.Contains("SET NULL (", StringComparison.Ordinal) || definition.Contains("SET DEFAULT (", StringComparison.Ordinal) ? definition : null
                });
            }
        }
        string? reason = kind == "p" ? "Partitioned tables open read-only in the designer. Use Partitions… and Table Operations…."
            : partition || inherited ? "Partitions and tables in an inheritance tree open read-only in the designer."
            : persistence == "t" ? "Temporary tables open read-only in the designer." : null;
        return new(oid, schema, table, columns) { Indexes = indexes, ForeignKeys = foreignKeys, Checks = checks, Comment = comment, Owner = owner, EstimatedRows = rows, ReadOnlyReason = reason };
    }

    private static ReferentialAction Action(string code) => code switch
    {
        "r" => ReferentialAction.Restrict, "c" => ReferentialAction.Cascade, "n" => ReferentialAction.SetNull, "d" => ReferentialAction.SetDefault, _ => ReferentialAction.NoAction
    };
}

/// <summary>Column edits made outside the designer grid, in Table Properties (pedido do usuário, 08/11/2026): a rename also renames the column
/// in the table's own keys, indexes and foreign keys, as PostgreSQL does with RENAME COLUMN, so the script renames instead of rebuilding them.</summary>
public static class TableDesignColumns
{
    /// <summary>Old name → new name of the columns kept, matched by position.</summary>
    public static IReadOnlyDictionary<string, string> Renames(TableDesign original, IReadOnlyList<DesignColumn> columns)
    {
        var byPosition = columns.Where(c => c.Position is not null).ToDictionary(c => c.Position!.Value);
        return original.Columns.Where(c => c.Position is { } p && byPosition.TryGetValue(p, out var now) && now.Name != c.Name)
            .ToDictionary(c => c.Name, c => byPosition[c.Position!.Value].Name, StringComparer.Ordinal);
    }

    public static TableDesign WithColumns(TableDesign design, IReadOnlyList<DesignColumn> columns)
    {
        var renames = Renames(design, columns);
        string Map(string column) => renames.GetValueOrDefault(column, column);
        bool Self(DesignForeignKey key) => key.ReferencedSchema == design.Schema && key.ReferencedTable == design.Name;
        return design with
        {
            Columns = columns.ToArray(),
            Indexes = design.Indexes.Select(i => i.FixedDefinition is not null ? i : i with { Columns = i.Columns.Select(c => c with { Name = Map(c.Name) }).ToArray() }).ToArray(),
            ForeignKeys = design.ForeignKeys.Select(k => k.FixedDefinition is not null ? k : k with { Columns = k.Columns.Select(Map).ToArray(), ReferencedColumns = Self(k) ? k.ReferencedColumns.Select(Map).ToArray() : k.ReferencedColumns }).ToArray()
        };
    }

    /// <summary>The foreign keys of design that point at another table whose columns are renamed, with the new names, as the catalog shows them after the rename.</summary>
    public static TableDesign WithReferencedRenames(TableDesign design, string schema, string table, IReadOnlyDictionary<string, string> renames) =>
        renames.Count == 0 || design.Schema == schema && design.Name == table ? design : design with
        {
            ForeignKeys = design.ForeignKeys.Select(k => k.ReferencedSchema == schema && k.ReferencedTable == table
                ? k with { ReferencedColumns = k.ReferencedColumns.Select(c => renames.GetValueOrDefault(c, c)).ToArray() } : k).ToArray()
        };
}
