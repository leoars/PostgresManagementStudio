namespace PGMS.Desktop.ViewModels;

public enum CatalogFilterOperator { Contains, Equals, DoesNotContain }

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
        _ => actual.Contains(value, StringComparison.OrdinalIgnoreCase)
    };
}
