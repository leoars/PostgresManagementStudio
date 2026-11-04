namespace PGMS.PostgreSql;

public enum PartitionStrategy { Range, List, Hash }

/// <summary>A column-only partition key. Captures names without retaining mutable caller collections.</summary>
public sealed class TablePartitioning
{
    public PartitionStrategy Strategy { get; }
    public IReadOnlyList<string> Columns { get; }

    public TablePartitioning(PartitionStrategy strategy, IEnumerable<string> columns)
    {
        if (!Enum.IsDefined(strategy)) throw new ArgumentException("Select RANGE, LIST or HASH.", nameof(strategy));
        ArgumentNullException.ThrowIfNull(columns);
        var names = columns.Take(33).ToArray();
        if (names.Length is < 1 or > 32 || strategy == PartitionStrategy.List && names.Length != 1)
            throw new ArgumentException("LIST requires one key column; RANGE and HASH require 1 to 32.", nameof(columns));
        foreach (string name in names) _ = SqlIdentifier.Quote(name);
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Partition key columns must be distinct.", nameof(columns));
        Strategy = strategy;
        Columns = Array.AsReadOnly(names);
    }

    public string ToSql(IReadOnlyList<TableColumn> declaredColumns)
    {
        ArgumentNullException.ThrowIfNull(declaredColumns);
        var names = declaredColumns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (Columns.Any(c => !names.Contains(c)))
            throw new ArgumentException("Every partition key must exactly match a declared column.", nameof(declaredColumns));
        return KeySql();
    }

    /// <summary>Renders the clause without checking declared columns; the server resolves names against an existing parent.</summary>
    public string KeySql() => "PARTITION BY " + Strategy.ToString().ToUpperInvariant() + " (" +
        string.Join(", ", Columns.Select(SqlIdentifier.Quote)) + ")";
}
