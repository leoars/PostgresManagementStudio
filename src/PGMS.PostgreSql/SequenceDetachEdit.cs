using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceDetachEdit(string Schema, string Name) : IAdministrativeEdit
{
    public SequenceSnapshot? Expected { get; init; }
    private SequenceParameterEdit Guard => new(Schema, Name, SequenceOperation.SetStart)
        { Expected = Expected, Value = Expected?.Start ?? 0 };
    public string ToSql()
    {
        if (Expected is null || Expected.Schema != Schema || Expected.Name != Name)
            throw new ArgumentException("Inspect this sequence before reviewing its association.");
        if (Expected.Dependency is null) throw new ArgumentException("This sequence has no inspected OWNED BY association to remove.");
        if (Expected.Dependency.Kind != "a") throw new ArgumentException("Identity sequences cannot be detached here. Manage the identity column instead.");
        return $"ALTER SEQUENCE {ObjectSql.Relation(Schema, Name)} OWNED BY NONE;";
    }
    private async Task<SequenceDependency?> ReadDependencyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT refobjid, refobjsubid, deptype::text FROM pg_catalog.pg_depend
            WHERE classid='pg_catalog.pg_class'::regclass AND refclassid='pg_catalog.pg_class'::regclass
                AND objid=@oid AND objsubid=0 AND deptype IN ('a','i')
            """, connection, transaction);
        command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, Expected!.Oid);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return new(reader.GetFieldValue<uint>(0), reader.GetInt32(1), reader.GetString(2));
    }
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await Guard.ValidateAsync(connection, transaction, token).ConfigureAwait(false);
        if (await ReadDependencyAsync(connection, transaction, token).ConfigureAwait(false) != Expected!.Dependency)
            throw new InvalidOperationException("Sequence association changed. Inspect again before detaching it.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await Guard.VerifyAppliedAsync(connection, transaction, token).ConfigureAwait(false);
        if (await ReadDependencyAsync(connection, transaction, token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Sequence association was not removed. The change was not committed.");
    }
}
