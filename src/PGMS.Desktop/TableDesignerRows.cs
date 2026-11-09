using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Editable column in the Table Designer grid. Position keeps the attnum, so renaming stays a rename.</summary>
public sealed class DesignerColumnRow : INotifyPropertyChanged
{
    private string name = "", dataType = "text", defaultValue = "", generated = "", collation = "", description = "";
    private bool allowNulls = true, isPrimaryKey, isCurrent;
    private ColumnIdentity identity;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<DesignerColumnRow, string, string>? Renamed;
    public short? Position { get; init; }
    /// <summary>A generated expression can only be typed on a column that does not exist yet.</summary>
    public bool CanSetGenerated => Position is null;

    public DesignerColumnRow() { }
    public DesignerColumnRow(DesignColumn column)
    {
        Position = column.Position; name = column.Name; dataType = column.Type; allowNulls = column.Nullable; defaultValue = column.Default ?? ""; identity = column.Identity;
        generated = column.Generated ?? ""; collation = column.Collation ?? ""; description = column.Comment ?? "";
    }

    public string Name { get => name; set { string old = name; if (Set(ref name, value)) Renamed?.Invoke(this, old, value); } }
    public string DataType { get => dataType; set => Set(ref dataType, value.Trim()); }
    public bool AllowNulls { get => allowNulls; set => Set(ref allowNulls, value); }
    public string DefaultValue { get => defaultValue; set => Set(ref defaultValue, value); }
    public ColumnIdentity Identity { get => identity; set { if (Set(ref identity, value) && value != ColumnIdentity.None) AllowNulls = false; OnChanged(nameof(CanAllowNulls)); } }
    public string Generated { get => generated; set => Set(ref generated, value); }
    public string Collation { get => collation; set => Set(ref collation, value.Trim()); }
    public string Description { get => description; set => Set(ref description, value); }
    public bool IsPrimaryKey { get => isPrimaryKey; set { if (Set(ref isPrimaryKey, value) && value) AllowNulls = false; OnChanged(nameof(CanAllowNulls)); } }
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
    public bool CanAllowNulls => !isPrimaryKey && identity == ColumnIdentity.None;

    public DesignColumn ToDesign() => new(Position, name.Trim(), dataType, allowNulls)
    {
        Default = Optional(defaultValue), Identity = identity, Generated = Optional(generated), Collation = Optional(collation), Comment = description.Length == 0 ? null : description
    };
    internal static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnChanged(property!); return true;
    }
    private void OnChanged(string property) => PropertyChanged?.Invoke(this, new(property));
}

/// <summary>Length, precision and scale read from and written to the type text, like the Length row of the SSMS Column Properties.</summary>
public static partial class DesignerTypeText
{
    [GeneratedRegex(@"^(?<base>[^()]*[^()\s])\s*(\(\s*(?<a>\d+)(\s*,\s*(?<b>-?\d+))?\s*\))?(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
    private static readonly string[] Sized = ["character varying", "varchar", "character", "char", "bit", "bit varying"];
    private static readonly string[] Decimal = ["numeric", "decimal"];

    private static (string Base, string A, string B, string Tail) Parts(string type)
    {
        var match = Shape().Match(type.Trim());
        return match.Success ? (match.Groups["base"].Value, match.Groups["a"].Value, match.Groups["b"].Value, match.Groups["tail"].Value) : (type, "", "", "");
    }
    public static bool HasLength(string type) => Sized.Contains(Parts(type).Base, StringComparer.OrdinalIgnoreCase);
    public static bool HasPrecision(string type) => Decimal.Contains(Parts(type).Base, StringComparer.OrdinalIgnoreCase);
    public static string Length(string type) => Parts(type).A;
    public static string Scale(string type) => Parts(type).B;
    public static string WithLength(string type, string value)
    {
        var parts = Parts(type); string size = value.Trim();
        return parts.Base + (size.Length == 0 ? "" : $"({size})") + parts.Tail;
    }
    public static string WithScale(string type, string value)
    {
        var parts = Parts(type); string scale = value.Trim();
        return parts.A.Length == 0 ? type : parts.Base + (scale.Length == 0 ? $"({parts.A})" : $"({parts.A},{scale})") + parts.Tail;
    }
}

public sealed class DesignerIndexRow
{
    public string? OriginalName { get; init; }
    public string Name { get; set; } = "";
    public DesignIndexKind Kind { get; set; } = DesignIndexKind.Index;
    public List<DesignIndexColumn> Columns { get; set; } = [];
    public bool Unique { get; set; }
    public bool ConstraintBacked { get; init; }
    public string Description { get; set; } = "";
    public string? FixedDefinition { get; init; }
    public string DisplayName => Name + (OriginalName is null ? "*" : "");
    public string ColumnsText
    {
        get => string.Join(", ", Columns.Select(c => c.Name + (c.Descending ? " (DESC)" : " (ASC)")));
        set => Columns = DesignerNames.Split(value).Select(Parse).ToList();
    }
    private static DesignIndexColumn Parse(string text)
    {
        string name = text.Trim(); bool descending = false;
        foreach (var (suffix, value) in new[] { (" (DESC)", true), (" DESC", true), (" (ASC)", false), (" ASC", false) })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length].Trim(); descending = value; break; }
        return new(name, descending);
    }
    public DesignerIndexRow() { }
    public DesignerIndexRow(DesignIndex index)
    {
        OriginalName = index.OriginalName; Name = index.Name; Kind = index.Kind; Columns = index.Columns.ToList(); Unique = index.Unique;
        ConstraintBacked = index.ConstraintBacked; Description = index.Comment ?? ""; FixedDefinition = index.FixedDefinition;
    }
    // Keys are unique by nature, so the flag that PostgreSQL reports for them follows the kind.
    public DesignIndex ToDesign() => new(OriginalName, Name.Trim(), Kind, Columns.ToArray())
    {
        Unique = Kind != DesignIndexKind.Index || Unique, ConstraintBacked = ConstraintBacked || Kind != DesignIndexKind.Index, Comment = Description.Length == 0 ? null : Description, FixedDefinition = FixedDefinition
    };
}

public sealed class DesignerForeignKeyRow
{
    public string? OriginalName { get; init; }
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public string ReferencedSchema { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public List<string> ReferencedColumns { get; set; } = [];
    public ReferentialAction OnUpdate { get; set; }
    public ReferentialAction OnDelete { get; set; }
    public bool CheckExisting { get; set; } = true;
    public string Description { get; set; } = "";
    public string? FixedDefinition { get; init; }
    public string DisplayName => Name + (OriginalName is null ? "*" : "");
    public DesignerForeignKeyRow() { }
    public DesignerForeignKeyRow(DesignForeignKey key)
    {
        OriginalName = key.OriginalName; Name = key.Name; Columns = key.Columns.ToList(); ReferencedSchema = key.ReferencedSchema; ReferencedTable = key.ReferencedTable; ReferencedColumns = key.ReferencedColumns.ToList();
        OnUpdate = key.OnUpdate; OnDelete = key.OnDelete; CheckExisting = key.CheckExisting; Description = key.Comment ?? ""; FixedDefinition = key.FixedDefinition;
    }
    public DesignForeignKey ToDesign() => new(OriginalName, Name.Trim(), Columns.ToArray(), ReferencedSchema, ReferencedTable, ReferencedColumns.ToArray())
    { OnUpdate = OnUpdate, OnDelete = OnDelete, CheckExisting = CheckExisting, Comment = Description.Length == 0 ? null : Description, FixedDefinition = FixedDefinition };
}

public sealed class DesignerCheckRow
{
    public string? OriginalName { get; init; }
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
    public bool CheckExisting { get; set; } = true;
    public string Description { get; set; } = "";
    public string? FixedDefinition { get; init; }
    public string DisplayName => Name + (OriginalName is null ? "*" : "");
    public DesignerCheckRow() { }
    public DesignerCheckRow(DesignCheck check)
    {
        OriginalName = check.OriginalName; Name = check.Name; Expression = check.Expression; CheckExisting = check.CheckExisting; Description = check.Comment ?? ""; FixedDefinition = check.FixedDefinition;
    }
    public DesignCheck ToDesign() => new(OriginalName, Name.Trim(), Expression.Trim()) { CheckExisting = CheckExisting, Comment = Description.Length == 0 ? null : Description, FixedDefinition = FixedDefinition };
}

public static class DesignerNames
{
    /// <summary>Comma-separated names; a double-quoted name may contain commas.</summary>
    public static List<string> Split(string text)
    {
        var names = new List<string>(); var current = new System.Text.StringBuilder(); bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { names.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        names.Add(current.ToString().Trim());
        return names.Where(n => n.Length > 0).ToList();
    }
    public static string Join(IEnumerable<string> names) => string.Join(", ", names.Select(n => n.Contains(',') ? "\"" + n + "\"" : n));
}
