using Npgsql;

namespace PGMS.PostgreSql;

public enum IndexSortOrder { Default, Ascending, Descending }
public enum IndexNullPlacement { Default, First, Last }
public enum IndexMethod { BTree, Hash }
public enum RelationOperation { CreateIndex, DropIndex }
public sealed record RelationEdit(RelationOperation Operation, string Schema, string Table, string Name, IReadOnlyList<string> Columns, bool Unique = false) : IAdministrativeEdit
{
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];
    public bool NullsNotDistinct { get; init; }
    public int? FillFactor { get; init; }
    public IndexMethod Method { get; init; } = IndexMethod.BTree;
    public IndexSortOrder SortOrder { get; init; }
    public IndexNullPlacement NullPlacement { get; init; }
    public IndexIdentity? ExpectedIdentity { get; init; }
    public Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default) =>
        Operation == RelationOperation.DropIndex && ExpectedIdentity is not null
            ? ExpectedIdentity.ValidateAsync(connection, transaction, Schema, Table, Name, token) : Task.CompletedTask;
    public Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default) =>
        Operation == RelationOperation.DropIndex && ExpectedIdentity is not null
            ? ExpectedIdentity.VerifyDroppedAsync(connection, transaction, token) : Task.CompletedTask;
    public static string[] ParseColumns(string text) => text.Replace("\r\n", "\n").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    public string ToSql()
    {
        string name = SqlIdentifier.Quote(Name);
        if (Operation == RelationOperation.DropIndex) return $"DROP INDEX {ObjectSql.Relation(Schema, Name)} RESTRICT;";
        if (Operation != RelationOperation.CreateIndex) throw new ArgumentException("Unsupported operation.");
        string table = ObjectSql.Relation(Schema, Table);
        if (Columns.Count is < 1 or > 32 || Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Count)
            throw new ArgumentException("Enter 1 to 32 distinct column names, one exact name per line.");
        if (Columns.Count + IncludedColumns.Count > 32 || IncludedColumns.Distinct(StringComparer.Ordinal).Count() != IncludedColumns.Count || Columns.Intersect(IncludedColumns, StringComparer.Ordinal).Any())
            throw new ArgumentException("Use distinct key and INCLUDE columns, at most 32 columns in total.");
        if (NullsNotDistinct && !Unique) throw new ArgumentException("NULLS NOT DISTINCT requires a unique index.");
        if (FillFactor is < 10 or > 100) throw new ArgumentException("Fillfactor must be between 10 and 100, or blank for the server default.");
        string method = Method switch { IndexMethod.BTree => "btree", IndexMethod.Hash => "hash", _ => throw new ArgumentException("Unsupported index method.") };
        if (Method == IndexMethod.Hash && (Columns.Count != 1 || Unique || IncludedColumns.Count != 0))
            throw new ArgumentException("Hash requires exactly one key column, no UNIQUE and no INCLUDE columns. Clear these options or choose BTree.");
        string order = SortOrder switch { IndexSortOrder.Default => "", IndexSortOrder.Ascending => " ASC", IndexSortOrder.Descending => " DESC", _ => throw new ArgumentException("Unsupported key order.") };
        string nulls = NullPlacement switch { IndexNullPlacement.Default => "", IndexNullPlacement.First => " NULLS FIRST", IndexNullPlacement.Last => " NULLS LAST", _ => throw new ArgumentException("Unsupported NULL placement.") };
        if (Method != IndexMethod.BTree && (SortOrder != IndexSortOrder.Default || NullPlacement != IndexNullPlacement.Default))
            throw new ArgumentException("Key ordering requires BTree. Choose Default for both ordering fields to use Hash.");
        string include = IncludedColumns.Count == 0 ? "" : " INCLUDE (" + string.Join(", ", IncludedColumns.Select(SqlIdentifier.Quote)) + ")";
        return $"CREATE {(Unique ? "UNIQUE " : "")}INDEX {name} ON {table} USING {method} (" + string.Join(", ", Columns.Select(column => SqlIdentifier.Quote(column) + order + nulls)) + ")" + include + (NullsNotDistinct ? " NULLS NOT DISTINCT" : "") + (FillFactor is { } fill ? $" WITH (fillfactor = {fill.ToString(System.Globalization.CultureInfo.InvariantCulture)})" : "") + ";";
    }
}
