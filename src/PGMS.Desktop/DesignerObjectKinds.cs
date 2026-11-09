using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Foreign Key Relationships, Indexes/Keys and Check Constraints of one table, shared by the Table Designer and the database diagram
/// (both open the same dialogs in SSMS 22.11). The lists are the caller's; Touch reports every change.</summary>
internal static class DesignerObjectKinds
{
    private static readonly string[] Actions = ["No Action", "Restrict", "Cascade", "Set Null", "Set Default"];
    private static readonly string[] YesNo = ["Yes", "No"];

    private static string Yes(bool value) => value ? "Yes" : "No";
    internal static string ActionLabel(ReferentialAction action) => action switch { ReferentialAction.Restrict => "Restrict", ReferentialAction.Cascade => "Cascade", ReferentialAction.SetNull => "Set Null", ReferentialAction.SetDefault => "Set Default", _ => "No Action" };
    internal static ReferentialAction ActionValue(string text) => text switch { "Restrict" => ReferentialAction.Restrict, "Cascade" => ReferentialAction.Cascade, "Set Null" => ReferentialAction.SetNull, "Set Default" => ReferentialAction.SetDefault, _ => ReferentialAction.NoAction };
    private static string ReferencedName(DesignerForeignKeyRow key) => key.ReferencedTable.Length == 0 ? "" : key.ReferencedSchema + "." + key.ReferencedTable;
    private static void SetReferenced(DesignerForeignKeyRow key, string text, string schema)
    {
        var parts = text.Trim().Split('.', 2);
        (key.ReferencedSchema, key.ReferencedTable) = parts.Length == 2 ? (parts[0].Trim('"'), parts[1].Trim('"')) : (schema, parts[0].Trim('"'));
    }

    /// <summary>Name not used by another key, index or constraint of the table: name, name1, name2…</summary>
    internal static string UniqueName(string name, IEnumerable<DesignerIndexRow> indexes, IEnumerable<DesignerForeignKeyRow> keys, IEnumerable<DesignerCheckRow> checks)
    {
        var used = indexes.Select(i => i.Name).Concat(keys.Select(f => f.Name)).Concat(checks.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        string candidate = name;
        for (int i = 1; used.Contains(candidate); i++) candidate = name + i;
        return candidate;
    }

    /// <summary>The Foreign Key Relationships properties of one relationship.</summary>
    internal static IReadOnlyList<DesignerProperty> RelationshipProperties(DesignerForeignKeyRow key, string schema)
    {
        bool Open() => key.FixedDefinition is null;
        const string general = "(General)", identity = "Identity", designer = "Table Designer";
        return [
            new(general, "Check Existing Data On Creation", "Indicates whether existing rows are validated. No creates the key as NOT VALID.", () => Yes(key.CheckExisting), v => key.CheckExisting = v == "Yes", YesNo) { IsEditable = Open },
            new(general, "Foreign Key Columns", "Indicates the columns of this table, separated by commas.", () => DesignerNames.Join(key.Columns), v => key.Columns = DesignerNames.Split(v)) { IsEditable = Open },
            new(general, "Primary/Unique Key Base Table", "Indicates the referenced table as schema.table.", () => ReferencedName(key), v => SetReferenced(key, v, schema)) { IsEditable = Open },
            new(general, "Primary/Unique Key Columns", "Indicates the referenced columns, separated by commas, in the same order.", () => DesignerNames.Join(key.ReferencedColumns), v => key.ReferencedColumns = DesignerNames.Split(v)) { IsEditable = Open },
            new(identity, "(Name)", "Indicates the name of the constraint.", () => key.Name, v => key.Name = v.Trim()),
            new(identity, "Description", "Indicates the comment stored on the constraint.", () => key.Description, v => key.Description = v),
            new(designer, "Delete Rule", "Indicates what happens to this row when the referenced row is deleted.", () => ActionLabel(key.OnDelete), v => key.OnDelete = ActionValue(v), Actions) { IsEditable = Open },
            new(designer, "Update Rule", "Indicates what happens to this row when the referenced key changes.", () => ActionLabel(key.OnUpdate), v => key.OnUpdate = ActionValue(v), Actions) { IsEditable = Open },
        ];
    }
    internal static string RelationshipHelp(DesignerForeignKeyRow key) => key is { FixedDefinition: { } definition } ? "This relationship uses options the designer does not edit: " + definition
        : key.OriginalName is null ? "Editing properties for new relationship. The columns and the referenced table need to be filled in before saving." : "Editing properties for existing relationship.";

    internal static DesignerObjectKind Relationships(List<DesignerForeignKeyRow> keys, string schema, Func<string> newName, Action touch) =>
        new("Foreign Key Relationships", "Selected Relationship:", "Use the add button to create a new relationship.",
            item => RelationshipHelp((DesignerForeignKeyRow)item),
            () => { var row = new DesignerForeignKeyRow { Name = newName(), ReferencedSchema = schema }; keys.Add(row); touch(); return row; },
            item => { keys.Remove((DesignerForeignKeyRow)item); touch(); }, _ => true,
            item => RelationshipProperties((DesignerForeignKeyRow)item, schema), () => keys.Cast<object>().ToArray());

    internal static DesignerObjectKind Indexes(List<DesignerIndexRow> indexes, Func<string> newName, Func<string?> firstColumn, Action touch, Action keysChanged) =>
        new("Indexes/Keys", "Selected Primary/Unique Key or Index:", "Use the add button to create a new unique key or index.",
            item => ((DesignerIndexRow)item) is { FixedDefinition: { } definition } ? "This index uses options the designer does not edit: " + definition
                : ((DesignerIndexRow)item).OriginalName is null ? "Editing properties for new unique key or index." : "Editing properties for existing primary/unique key or index.",
            () =>
            {
                string? first = firstColumn();
                var row = new DesignerIndexRow { Name = newName(), Columns = string.IsNullOrWhiteSpace(first) ? [] : [new(first)] };
                indexes.Add(row); touch(); return row;
            },
            item => { indexes.Remove((DesignerIndexRow)item); keysChanged(); touch(); }, _ => true,
            item =>
            {
                var index = (DesignerIndexRow)item; bool Open() => index.FixedDefinition is null; bool Choice() => Open() && index.Kind != DesignIndexKind.PrimaryKey;
                const string general = "(General)", identity = "Identity";
                return [
                    new(general, "Columns", "Indicates the columns, separated by commas; add DESC for descending order in an index.", () => index.ColumnsText, v => { index.ColumnsText = v; keysChanged(); }) { IsEditable = Open },
                    new(general, "Is Unique", "Indicates whether the index refuses duplicate values.", () => Yes(index.Kind != DesignIndexKind.Index || index.Unique), v => index.Unique = v == "Yes", YesNo) { IsEditable = () => Open() && index.Kind == DesignIndexKind.Index },
                    new(general, "Type", "Indicates whether this is a unique key (a constraint) or an index.", () => index.Kind switch { DesignIndexKind.PrimaryKey => "Primary Key", DesignIndexKind.UniqueKey => "Unique Key", _ => "Index" }, v => index.Kind = v == "Unique Key" ? DesignIndexKind.UniqueKey : DesignIndexKind.Index, index.Kind == DesignIndexKind.PrimaryKey ? ["Primary Key"] : ["Unique Key", "Index"]) { IsEditable = Choice },
                    new(identity, "(Name)", "Indicates the name of the key or index.", () => index.Name, v => index.Name = v.Trim()),
                    new(identity, "Description", "Indicates the comment stored on the key or index.", () => index.Description, v => index.Description = v),
                ];
            }, () => indexes.Cast<object>().ToArray());

    internal static DesignerObjectKind Checks(List<DesignerCheckRow> checks, Func<string> newName, Action touch) =>
        new("Check Constraints", "Selected Check Constraint:", "Use the add button to create a new check constraint.",
            item => ((DesignerCheckRow)item) is { FixedDefinition: { } definition } ? "This constraint uses options the designer does not edit: " + definition
                : ((DesignerCheckRow)item).OriginalName is null ? "Editing properties for new check constraint. The 'Expression' property needs to be filled in before saving." : "Editing properties for existing check constraint.",
            () => { var row = new DesignerCheckRow { Name = newName() }; checks.Add(row); touch(); return row; },
            item => { checks.Remove((DesignerCheckRow)item); touch(); }, _ => true,
            item =>
            {
                var check = (DesignerCheckRow)item; bool Open() => check.FixedDefinition is null;
                const string general = "(General)", identity = "Identity", designer = "Table Designer";
                return [
                    new(general, "Expression", "Indicates the SQL expression each row must satisfy.", () => check.Expression, v => check.Expression = v) { IsEditable = Open },
                    new(identity, "(Name)", "Indicates the name of the constraint.", () => check.Name, v => check.Name = v.Trim()),
                    new(identity, "Description", "Indicates the comment stored on the constraint.", () => check.Description, v => check.Description = v),
                    new(designer, "Check Existing Data On Creation", "Indicates whether existing rows are validated. No creates the constraint as NOT VALID.", () => Yes(check.CheckExisting), v => check.CheckExisting = v == "Yes", YesNo) { IsEditable = Open },
                ];
            }, () => checks.Cast<object>().ToArray());
}
