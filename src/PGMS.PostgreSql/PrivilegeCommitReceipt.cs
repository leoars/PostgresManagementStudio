using Npgsql;

namespace PGMS.PostgreSql;

public enum PrivilegeCommitStatus { Unknown, InProgress, Committed, Aborted }

public interface IPrivilegeCommitJournal
{
    // Must complete durable recording before COMMIT can be sent. Throwing here
    // prevents COMMIT; a caller must retain/check any successfully written record.
    Task RecordAsync(PrivilegeCommitReceipt receipt, CancellationToken token);
}

public sealed class PrivilegeCommitReceipt
{
    internal PrivilegeCommitReceipt(Guid context, string transactionId, uint databaseOid)
    { Context=context; TransactionId=transactionId; DatabaseOid=databaseOid; }
    internal Guid Context { get; }
    public string TransactionId { get; }
    public uint DatabaseOid { get; }
}

public sealed class PrivilegeCommitUncertainException : InvalidOperationException
{
    internal PrivilegeCommitUncertainException(PrivilegeCommitReceipt receipt, Exception inner)
        : base("Permission commit acknowledgement was lost. The result is unknown. Check this transaction before applying again.", inner) => Receipt=receipt;
    public PrivilegeCommitReceipt Receipt { get; }
}

internal static class PrivilegeCommitReconciliation
{
    internal static async Task<PrivilegeCommitStatus> ReadAsync(string connectionString, PrivilegeCommitReceipt receipt, CancellationToken token)
    {
        // xid8 is local to one cluster. No inference across multi-host routing,
        // changed profiles, failover or transaction-status retention boundaries.
        var settings=new NpgsqlConnectionStringBuilder(connectionString){Pooling=false};
        if(settings.Host?.Contains(',',StringComparison.Ordinal)==true)return PrivilegeCommitStatus.Unknown;
        await using var connection=new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command=new NpgsqlCommand("SELECT CASE WHEN (SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())=@database THEN pg_catalog.pg_xact_status(@xid::xid8) ELSE NULL END",connection){CommandTimeout=10};
        command.Parameters.AddWithValue("database",NpgsqlTypes.NpgsqlDbType.Oid,receipt.DatabaseOid);
        command.Parameters.AddWithValue("xid",receipt.TransactionId);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) switch
        {
            "committed"=>PrivilegeCommitStatus.Committed,
            "aborted"=>PrivilegeCommitStatus.Aborted,
            "in progress"=>PrivilegeCommitStatus.InProgress,
            _=>PrivilegeCommitStatus.Unknown
        };
    }
}
