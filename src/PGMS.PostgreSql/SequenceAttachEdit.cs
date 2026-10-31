using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceAttachEdit(SequenceSnapshot Expected, SequenceOwnerTarget Target) : IAdministrativeEdit
{
    private SequenceParameterEdit Guard => new(Expected.Schema, Expected.Name, SequenceOperation.SetStart) { Expected=Expected, Value=Expected.Start };
    public string ToSql() => new SequenceOwnershipProposal(Expected, Target).ToSql();
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token=default)
    {
        // Lock destination before sequence, matching table-drop dependency traversal.
        await using(var command=new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(Target.Schema,Target.Table)} IN ACCESS SHARE MODE",connection,transaction) { CommandTimeout=30 })
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var current=await SequenceOwnerTargetService.ReadAsync(connection,transaction,Target.Schema,Target.Table,Target.Column,token).ConfigureAwait(false);
        if(current!=Target) throw new InvalidOperationException("Destination identity or definition changed. Inspect the destination again.");
        await Guard.ValidateAsync(connection,transaction,token).ConfigureAwait(false);
        if(await ReadDependencyAsync(connection,transaction,Expected.Oid,token).ConfigureAwait(false)!=Expected.Dependency)
            throw new InvalidOperationException("Sequence association changed. Inspect the sequence again.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token=default)
    {
        await Guard.VerifyAppliedAsync(connection,transaction,token).ConfigureAwait(false);
        if(await ReadDependencyAsync(connection,transaction,Expected.Oid,token).ConfigureAwait(false)!=new SequenceDependency(Target.TableOid,Target.ColumnNumber,"a"))
            throw new InvalidOperationException("Sequence association did not match the destination. The change was not committed.");
    }
    internal static async Task<SequenceDependency?> ReadDependencyAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,uint oid,CancellationToken token)
    {
        await using var command=new NpgsqlCommand("""
            SELECT refobjid, refobjsubid, deptype::text FROM pg_catalog.pg_depend
            WHERE classid='pg_catalog.pg_class'::regclass AND refclassid='pg_catalog.pg_class'::regclass
              AND objid=@oid AND objsubid=0 AND deptype IN ('a','i')
            """,connection,transaction);
        command.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);
        await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new(reader.GetFieldValue<uint>(0),reader.GetInt32(1),reader.GetString(2)) : null;
    }
}
