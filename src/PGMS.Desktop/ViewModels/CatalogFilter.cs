namespace PGMS.Desktop.ViewModels;

/// <summary>SSMS Filter Settings operators for text properties (ObjectExplorer.dll resources: Equals, Contains, Does not contain, Does not equal; PGMS-054, M1087).</summary>
public enum CatalogFilterOperator { Contains, Equals, DoesNotContain, DoesNotEqual }

/// <summary>Criteria of the Filter Settings window. An empty value does not restrict; comparison is literal and ignores case.</summary>
public sealed record CatalogFilter(string Name = "", CatalogFilterOperator NameOperator = CatalogFilterOperator.Contains, string Owner = "", CatalogFilterOperator OwnerOperator = CatalogFilterOperator.Equals)
{
    public static readonly CatalogFilter None = new();
    public bool IsActive => Name.Length > 0 || Owner.Length > 0;
    public bool Matches(string name, string? owner) => Test(name, Name, NameOperator) && Test(owner ?? "", Owner, OwnerOperator);
    private static bool Test(string actual, string value, CatalogFilterOperator op) => value.Length == 0 || op switch
    {
        CatalogFilterOperator.Equals => actual.Equals(value, StringComparison.OrdinalIgnoreCase),
        CatalogFilterOperator.DoesNotContain => !actual.Contains(value, StringComparison.OrdinalIgnoreCase),
        CatalogFilterOperator.DoesNotEqual => !actual.Equals(value, StringComparison.OrdinalIgnoreCase),
        _ => actual.Contains(value, StringComparison.OrdinalIgnoreCase)
    };
}
