namespace PGMS.PostgreSql;

public enum SchemaOperation { Rename, Drop }
public sealed record SchemaEdit(string Name, SchemaOperation Operation, string? NewName = null) : IAdministrativeEdit
{
    private static string QuoteUserSchema(string name)
    {
        string quoted = SqlIdentifier.Quote(name);
        if (name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) || name.Equals("information_schema", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("System schemas cannot be managed by this dialog.");
        return quoted;
    }
    public string ToSql()
    {
        string name = QuoteUserSchema(Name);
        return Operation switch
        {
            SchemaOperation.Rename when Name == NewName => throw new ArgumentException("Choose a different new schema name."),
            SchemaOperation.Rename => $"ALTER SCHEMA {name} RENAME TO {QuoteUserSchema(NewName ?? "")};",
            SchemaOperation.Drop => $"DROP SCHEMA {name} RESTRICT;",
            _ => throw new ArgumentException("Unsupported schema operation.")
        };
    }
}
