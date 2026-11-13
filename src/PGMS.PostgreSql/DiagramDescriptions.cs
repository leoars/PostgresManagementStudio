using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

/// <summary>One description kept in the saved diagram (PGMS-045): the COMMENT ON of a table, a column or a foreign key. The catalog is the source of truth;
/// this copy lets tools and AI agents read the diagram without a connection. Applied false marks a description the user could not apply (only the owner
/// of a table can comment on it and its columns and constraints): Text is the proposed description and DatabaseText what the catalog had at the save.</summary>
public sealed record DiagramDescription(string Object, string? Name, string Text)
{
    public const string TableObject = "table", ColumnObject = "column", ForeignKeyObject = "foreignKey";
    public bool Applied { get; init; } = true;
    public string? DatabaseText { get; init; }

    public bool Same(string obj, string? name) => Object == obj && Name == name;
}

/// <summary>Descriptions of the tables of a diagram: reading them from the catalog, the limit of the diagram, and the change of a table that is only descriptions.</summary>
public static class DiagramDescriptions
{
    /// <summary>The limit of one description edited in a diagram (decisão do usuário, 11/11/2026): 4 KiB of UTF-8.</summary>
    public const int MaxBytes = 4096;

    public static string? LimitError(string? text) =>
        text is null ? null : text.Contains('\0') ? "A description cannot contain the NUL character."
        : Encoding.UTF8.GetByteCount(text) > MaxBytes ? $"A description can have at most {MaxBytes} bytes of UTF-8 ({Encoding.UTF8.GetByteCount(text)} now)." : null;

    private static int Order(string obj) => obj switch { DiagramDescription.TableObject => 0, DiagramDescription.ColumnObject => 1, _ => 2 };
    /// <summary>The stable order of the saved copy: the table, then columns, then foreign keys, each by name.</summary>
    public static IReadOnlyList<DiagramDescription> Sorted(IEnumerable<DiagramDescription> items) =>
        items.OrderBy(d => Order(d.Object)).ThenBy(d => d.Name ?? "", StringComparer.Ordinal).ToArray();

    /// <summary>The descriptions a table has, as read with it.</summary>
    public static IReadOnlyList<DiagramDescription> Of(TableDesign design) => Sorted(
        (design.Comment is { } table ? [new DiagramDescription(DiagramDescription.TableObject, null, table)] : Array.Empty<DiagramDescription>())
        .Concat(design.Columns.Where(c => c.Comment is not null).Select(c => new DiagramDescription(DiagramDescription.ColumnObject, c.Name, c.Comment!)))
        .Concat(design.ForeignKeys.Where(k => k.Comment is not null).Select(k => new DiagramDescription(DiagramDescription.ForeignKeyObject, k.Name, k.Comment!))));

    /// <summary>The description of one object of a design; null when it has none or the object is not there.</summary>
    public static string? Text(TableDesign design, string obj, string? name) => obj switch
    {
        DiagramDescription.TableObject => design.Comment,
        DiagramDescription.ColumnObject => design.Columns.FirstOrDefault(c => c.Name == name)?.Comment,
        _ => design.ForeignKeys.FirstOrDefault(k => k.Name == name)?.Comment
    };

    /// <summary>The design with the descriptions of the original wherever the object existed before, so what is left to compare is structure.</summary>
    public static TableDesign WithoutDescriptionChanges(TableDesign original, TableDesign current)
    {
        var columns = original.Columns.Where(c => c.Position is not null).ToDictionary(c => c.Position!.Value);
        return current with
        {
            Comment = original.Comment,
            Columns = current.Columns.Select(c => c.Position is { } p && columns.TryGetValue(p, out var before) ? c with { Comment = before.Comment } : c).ToArray(),
            ForeignKeys = current.ForeignKeys.Select(k => k.OriginalName is { } was && original.ForeignKeys.FirstOrDefault(o => o.Name == was) is { } before ? k with { Comment = before.Comment } : k).ToArray()
        };
    }

    /// <summary>Descriptions changed on objects that existed before; Text is the new description ("" when it is removed).</summary>
    public static IReadOnlyList<DiagramDescription> Changed(TableDesign original, TableDesign current)
    {
        var result = new List<DiagramDescription>();
        if (current.Comment != original.Comment) result.Add(new(DiagramDescription.TableObject, null, current.Comment ?? "") { DatabaseText = original.Comment });
        var columns = original.Columns.Where(c => c.Position is not null).ToDictionary(c => c.Position!.Value);
        foreach (var column in current.Columns)
            if (column.Position is { } p && columns.TryGetValue(p, out var before) && before.Comment != column.Comment)
                result.Add(new(DiagramDescription.ColumnObject, column.Name, column.Comment ?? "") { DatabaseText = before.Comment });
        foreach (var key in current.ForeignKeys)
            if (key.OriginalName is { } was && original.ForeignKeys.FirstOrDefault(o => o.Name == was) is { } before && before.Comment != key.Comment)
                result.Add(new(DiagramDescription.ForeignKeyObject, key.Name, key.Comment ?? "") { DatabaseText = before.Comment });
        return result;
    }

    /// <summary>Refuses a description edited in the diagram beyond the limit; descriptions read from the catalog as they are stay as they are.</summary>
    public static void CheckLimits(TableDesign? original, TableDesign current)
    {
        string where = current.Schema + "." + current.Name;
        void Check(string? text, string what) { if (LimitError(text) is { } error) throw new ArgumentException($"{what}: {error}"); }
        if (current.Comment != original?.Comment) Check(current.Comment, where);
        var columns = original?.Columns.Where(c => c.Position is not null).ToDictionary(c => c.Position!.Value) ?? [];
        foreach (var column in current.Columns)
            if (column.Position is not { } p || !columns.TryGetValue(p, out var before) || before.Comment != column.Comment) Check(column.Comment, $"{where}.{column.Name}");
        foreach (var key in current.ForeignKeys)
            if (key.OriginalName is not { } was || original?.ForeignKeys.FirstOrDefault(o => o.Name == was) is not { } before || before.Comment != key.Comment) Check(key.Comment, $"{where}, {key.Name}");
    }

    /// <summary>The COMMENT ON statement that sets one description of a table ("" or null removes it).</summary>
    public static string CommentSql(string schema, string table, DiagramDescription item)
    {
        string relation = ObjectSql.Relation(schema, table), text = Literal(item.Text.Length == 0 ? null : item.Text);
        return item.Object switch
        {
            DiagramDescription.TableObject => $"COMMENT ON TABLE {relation} IS {text};",
            DiagramDescription.ColumnObject => $"COMMENT ON COLUMN {relation}.{SqlIdentifier.Quote(item.Name!)} IS {text};",
            _ => $"COMMENT ON CONSTRAINT {SqlIdentifier.Quote(item.Name!)} ON {relation} IS {text};"
        };
    }
    private static string Literal(string? value) => value is null ? "NULL" : "E'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";

    private const string ReadSql = """
        SELECT c.oid, 0, NULL::text, pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c
        WHERE c.oid = ANY(@oids) AND pg_catalog.obj_description(c.oid, 'pg_class') IS NOT NULL
        UNION ALL
        SELECT a.attrelid, 1, a.attname::text, pg_catalog.col_description(a.attrelid, a.attnum) FROM pg_catalog.pg_attribute a
        WHERE a.attrelid = ANY(@oids) AND a.attnum > 0 AND NOT a.attisdropped AND pg_catalog.col_description(a.attrelid, a.attnum) IS NOT NULL
        UNION ALL
        SELECT k.conrelid, 2, k.conname::text, pg_catalog.obj_description(k.oid, 'pg_constraint') FROM pg_catalog.pg_constraint k
        WHERE k.conrelid = ANY(@oids) AND k.contype = 'f' AND pg_catalog.obj_description(k.oid, 'pg_constraint') IS NOT NULL
        """;

    /// <summary>The descriptions of tables, their columns and foreign keys, by table, in the stable order of the saved copy.</summary>
    public static async Task<IReadOnlyDictionary<uint, IReadOnlyList<DiagramDescription>>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<uint> oids, CancellationToken token = default)
    {
        var wanted = oids.Where(o => o != 0).Distinct().ToArray();
        var found = wanted.ToDictionary(o => o, _ => new List<DiagramDescription>());
        if (wanted.Length > 0)
        {
            await using var command = new NpgsqlCommand(ReadSql, connection, transaction) { CommandTimeout = 30 };
            command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, wanted);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                string obj = reader.GetInt32(1) switch { 0 => DiagramDescription.TableObject, 1 => DiagramDescription.ColumnObject, _ => DiagramDescription.ForeignKeyObject };
                found[reader.GetFieldValue<uint>(0)].Add(new(obj, reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            }
        }
        return found.ToDictionary(p => p.Key, p => Sorted(p.Value));
    }

    /// <summary>The copy a save writes: what the catalog has now and, for objects whose catalog text is not the proposed one, the descriptions not applied.
    /// A description not applied that the catalog now has (someone with the privilege ran the script) is simply applied.</summary>
    public static IReadOnlyList<DiagramDescription> Merge(IReadOnlyList<DiagramDescription> catalog, IEnumerable<DiagramDescription> unapplied)
    {
        var pending = unapplied.Where(u => !u.Applied).GroupBy(u => (u.Object, u.Name)).Select(g => g.Last())
            .Where(u => catalog.FirstOrDefault(c => c.Same(u.Object, u.Name))?.Text != (u.Text.Length == 0 ? null : u.Text)).ToArray();
        return Sorted(catalog.Where(c => !pending.Any(u => u.Same(c.Object, c.Name)))
            .Concat(pending.Select(u => u with { Applied = false, DatabaseText = catalog.FirstOrDefault(c => c.Same(u.Object, u.Name))?.Text })));
    }

    /// <summary>Tables among oids whose owner the current user is not (and is not a member of): PostgreSQL lets only the owner comment on a table and on its columns and constraints.</summary>
    public static async Task<IReadOnlySet<uint>> NotOwnedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<uint> oids, CancellationToken token = default)
    {
        var result = new HashSet<uint>();
        var wanted = oids.Where(o => o != 0).Distinct().ToArray();
        if (wanted.Length == 0) return result;
        await using var command = new NpgsqlCommand("SELECT c.oid FROM pg_catalog.pg_class c WHERE c.oid = ANY(@oids) AND NOT pg_catalog.pg_has_role(c.relowner, 'USAGE')", connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, wanted);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(reader.GetFieldValue<uint>(0));
        return result;
    }
}

/// <summary>A table of a diagram whose only change is descriptions: COMMENT ON without the ACCESS EXCLUSIVE lock and full comparison of a structural change.
/// Refused when the table, a column or a foreign key is no longer the one the diagram read (identity by OID, attnum and name), or when a description
/// it changes was changed in the database since then.</summary>
public sealed class DescriptionEdit(TableDesign original, TableDesign current) : IAdministrativeEdit
{
    public TableDesign Original => original;
    public string ToSql() => TableDesignScript.Build(original, current).Sql;

    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        string table = original.Schema + "." + original.Name;
        await using (var command = new NpgsqlCommand("SELECT c.oid, pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p')", connection, transaction) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("schema", original.Schema); command.Parameters.AddWithValue("name", original.Name);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetFieldValue<uint>(0) != original.Oid)
                throw new InvalidOperationException($"Table {table} changed since the diagram read it. Refresh the diagram before saving.");
            if (current.Comment != original.Comment && (reader.IsDBNull(1) ? null : reader.GetString(1)) != original.Comment)
                throw new InvalidOperationException($"The description of {table} changed in the database since the diagram read it. Refresh the diagram before saving.");
        }
        foreach (var item in DiagramDescriptions.Changed(original, current).Where(i => i.Object != DiagramDescription.TableObject))
        {
            bool column = item.Object == DiagramDescription.ColumnObject;
            // A column is the attnum the diagram read, still with its name; a foreign key is the constraint of that name on this table.
            var before = column ? original.Columns.First(c => c.Position == current.Columns.First(n => n.Name == item.Name).Position) : null;
            string sql = column
                ? "SELECT pg_catalog.col_description(a.attrelid, a.attnum) FROM pg_catalog.pg_attribute a WHERE a.attrelid=@oid AND a.attnum=@attnum AND a.attname=@name AND NOT a.attisdropped"
                : "SELECT pg_catalog.obj_description(k.oid, 'pg_constraint') FROM pg_catalog.pg_constraint k WHERE k.conrelid=@oid AND k.conname=@name AND k.contype='f'";
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
            command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, original.Oid); command.Parameters.AddWithValue("name", column ? before!.Name : item.Name!);
            if (column) command.Parameters.AddWithValue("attnum", before!.Position!.Value);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            string what = column ? $"Column {table}.{before!.Name}" : $"Foreign key {item.Name} of {table}";
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException($"{what} changed since the diagram read it. Refresh the diagram before saving.");
            if ((reader.IsDBNull(0) ? null : reader.GetString(0)) != item.DatabaseText)
                throw new InvalidOperationException($"The description of {what.ToLowerInvariant()[0]}{what[1..]} changed in the database since the diagram read it. Refresh the diagram before saving.");
        }
    }
}
