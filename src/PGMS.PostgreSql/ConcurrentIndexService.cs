using System.Data;
using System.Transactions;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum ConcurrentIndexObservationKind { Absent, Valid, Invalid, NameOccupied, TargetChanged }
public sealed record ConcurrentIndexObservation(ConcurrentIndexObservationKind Kind, uint IndexOid = 0, bool Ready = false, bool Live = false, string? Definition = null);
public sealed record ConcurrentIndexReceipt(Guid OperationId, uint DatabaseOid, uint NamespaceOid, uint TableOid,
    string Schema, string Table, string Index, string Sql, DateTimeOffset RecordedAt)
{
    public uint OwnerOid { get; init; }
    public string Operation { get; init; } = "Create";
    public uint OriginalIndexOid { get; init; }
    public int WorkerPid { get; init; }
    public DateTime WorkerStarted { get; init; }
    public string ColumnSignature { get; init; } = "";
    internal static string Signature(ExpressionIndexTarget target) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(target.Columns)));
}

/// <summary>The caller must persist the receipt durably before returning and refuse an unresolved prior receipt.</summary>
public interface IConcurrentIndexJournal
{
    Task RecordAsync(ConcurrentIndexReceipt receipt, CancellationToken token);
}

public sealed class ConcurrentIndexUncertainException(ConcurrentIndexReceipt receipt, Exception inner)
    : Exception("The concurrent index command may have left an index. Inspect the recorded target before any retry; no cleanup or replay was attempted.", inner)
{
    public ConcurrentIndexReceipt Receipt { get; } = receipt;
}

public sealed record ConcurrentIndexResult(ConcurrentIndexReceipt Receipt, ConcurrentIndexObservation Observation);

/// <summary>Standalone commands use nominal resolution. Inspection does not lock schema names against concurrent DDL.</summary>
public sealed class ConcurrentIndexService(string connectionString, IConcurrentIndexJournal journal)
{
    public async Task<ConcurrentIndexResult> CreateAsync(ExpressionIndexEdit proposal, bool acceptNominalResolution, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!acceptNominalResolution) throw new InvalidOperationException("Review that concurrent schema/name changes cannot be excluded by this standalone command.");
        if (Transaction.Current is not null) throw new InvalidOperationException("Concurrent index creation cannot run in an ambient transaction.");
        token.ThrowIfCancellationRequested();
        var preflight = await new ConcurrentIndexInspector(connectionString).InspectAsync(proposal, token).ConfigureAwait(false);
        if (!preflight.EligibleAtInspection) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Blockers));
        var target = proposal.Target;
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false, Multiplexing = false, Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        DateTime workerStarted = await ReadWorkerStart(connection, token).ConfigureAwait(false);
        var receipt = new ConcurrentIndexReceipt(Guid.NewGuid(), target.DatabaseOid, target.NamespaceOid, target.Oid,
            target.Schema, target.Name, proposal.Name, preflight.Sql, DateTimeOffset.UtcNow)
        { OwnerOid = target.OwnerOid, ColumnSignature = ConcurrentIndexReceipt.Signature(target), WorkerPid = connection.ProcessID, WorkerStarted = workerStarted };
        // An unresolved record also blocks a subsequent attempt if process death occurs before submission.
        await journal.RecordAsync(receipt, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The durable journal can be slow. Refresh all preconditions once more before submission.
        var refreshed = await new ConcurrentIndexInspector(settings.ConnectionString).InspectAsync(proposal, token).ConfigureAwait(false);
        if (!refreshed.EligibleAtInspection) throw new InvalidOperationException("The recorded operation was not submitted: " + string.Join(Environment.NewLine, refreshed.Blockers));
        token.ThrowIfCancellationRequested();
        try
        {
            await using var command = new NpgsqlCommand(preflight.Sql, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            // Cancellation after server acknowledgement must not suppress inspection. It is bounded separately.
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var observed = await InspectAsync(target, proposal.Name, verification.Token).ConfigureAwait(false);
            if (observed.Kind != ConcurrentIndexObservationKind.Valid)
                throw new InvalidOperationException("The acknowledged index could not be verified as valid on the inspected target.");
            await using var verifyTransaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, verification.Token).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, verifyTransaction))
                await readOnly.ExecuteNonQueryAsync(verification.Token).ConfigureAwait(false);
            await proposal.VerifyAppliedAsync(connection, verifyTransaction, verification.Token).ConfigureAwait(false);
            return new(receipt, observed);
        }
        catch (Exception error)
        {
            // Even a server error can follow earlier committed phases. Never claim rollback.
            throw new ConcurrentIndexUncertainException(receipt, error);
        }
    }

    /// <summary>Reopens an observation using persisted identity, never executes the receipt's SQL.</summary>
    public async Task<ConcurrentIndexObservation> InspectReceiptAsync(ConcurrentIndexReceipt receipt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.OperationId == Guid.Empty || receipt.DatabaseOid == 0 || receipt.NamespaceOid == 0 ||
            receipt.TableOid == 0 || receipt.OwnerOid == 0 || receipt.ColumnSignature is null || receipt.ColumnSignature.Length != 64)
            throw new ArgumentException("The pending index receipt has no complete inspected identity.");
        ExpressionIndexTarget target;
        try { target = await new ExpressionIndexCatalog(connectionString).InspectAsync(receipt.Schema, receipt.Table, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return new(ConcurrentIndexObservationKind.TargetChanged); }
        if (target.DatabaseOid != receipt.DatabaseOid || target.NamespaceOid != receipt.NamespaceOid || target.Oid != receipt.TableOid ||
            target.OwnerOid != receipt.OwnerOid || ConcurrentIndexReceipt.Signature(target) != receipt.ColumnSignature)
            return new(ConcurrentIndexObservationKind.TargetChanged);
        return await InspectAsync(target, receipt.Index, token).ConfigureAwait(false);
    }

    internal static async Task<DateTime> ReadWorkerStart(NpgsqlConnection connection,CancellationToken token)
    {
        await using var command=new NpgsqlCommand("SELECT backend_start FROM pg_catalog.pg_stat_activity WHERE pid=pg_catalog.pg_backend_pid()",connection){CommandTimeout=15};
        return (DateTime)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    /// <summary>Null means a legacy receipt cannot identify its worker. Absence is not proof of DDL success.</summary>
    public async Task<bool?> IsWorkerActiveAsync(ConcurrentIndexReceipt receipt,CancellationToken token=default)
    {
        if(receipt.WorkerPid<=0 || receipt.WorkerStarted==default)return null;
        await using var connection=new NpgsqlConnection(connectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datid=@database AND pid=@pid AND backend_start=@started)",connection){CommandTimeout=15};
        command.Parameters.AddWithValue("database",NpgsqlDbType.Oid,receipt.DatabaseOid);
        command.Parameters.AddWithValue("pid",receipt.WorkerPid);command.Parameters.AddWithValue("started",NpgsqlDbType.TimestampTz,receipt.WorkerStarted);
        return (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    public async Task<ConcurrentIndexObservation> InspectAsync(ExpressionIndexTarget expected, string index, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); SqlIdentifier.Quote(index);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        ExpressionIndexTarget actual;
        try { actual = await ExpressionIndexCatalog.Read(connection, transaction, expected.Schema, expected.Name, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return new(ConcurrentIndexObservationKind.TargetChanged); }
        if (!expected.Matches(actual)) return new(ConcurrentIndexObservationKind.TargetChanged);
        await using var read = new NpgsqlCommand("""
            SELECT c.oid,c.relkind::text,i.indrelid,i.indisvalid,i.indisready,i.indislive,
                   CASE WHEN c.relkind='i' THEN pg_catalog.pg_get_indexdef(c.oid) ELSE NULL END
            FROM pg_catalog.pg_class c LEFT JOIN pg_catalog.pg_index i ON i.indexrelid=c.oid
            WHERE c.relnamespace=@namespace AND c.relname=@name
            """, connection, transaction) { CommandTimeout = 15 };
        read.Parameters.AddWithValue("namespace", NpgsqlDbType.Oid, expected.NamespaceOid);
        read.Parameters.AddWithValue("name", index);
        await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return new(ConcurrentIndexObservationKind.Absent);
        uint oid = reader.GetFieldValue<uint>(0);
        if (reader.GetString(1) != "i" || reader.IsDBNull(2) || reader.GetFieldValue<uint>(2) != expected.Oid)
            return new(ConcurrentIndexObservationKind.NameOccupied, oid);
        bool valid = reader.GetBoolean(3), ready = reader.GetBoolean(4), live = reader.GetBoolean(5);
        return new(valid && ready && live ? ConcurrentIndexObservationKind.Valid : ConcurrentIndexObservationKind.Invalid,
            oid, ready, live, reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
