using Npgsql;
using System.Data;

namespace PGMS.PostgreSql;

public sealed class DefaultPrivilegeService(string connectionString, IPrivilegeCommitJournal? journal = null)
{
    private readonly Guid context = Guid.NewGuid();
    private sealed record Entry(bool Schema, uint Grantor, uint Recipient, string Privilege, bool Grantable);
    private static HashSet<Entry> Normalize(DefaultPrivilegeSnapshot snapshot) => snapshot.Entries
        .Select(e => new Entry(e.Origin == DefaultPrivilegeOrigin.Schema, e.GrantorOid, e.RecipientOid, e.Privilege, e.Grantable)).ToHashSet();
    private static InvalidOperationException Conflict() => new("Default privilege identity or policy changed. Nothing was committed. Inspect again.");

    public PrivilegeCommitReceipt ResumeCommitCheck(string transactionId, uint databaseOid)
    {
        if (!ulong.TryParse(transactionId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var xid) || xid == 0 || databaseOid == 0)
            throw new ArgumentException("Invalid pending default privilege transaction identity.");
        return new(context, transactionId, databaseOid);
    }
    public Task<PrivilegeCommitStatus> ReconcileCommitAsync(PrivilegeCommitReceipt receipt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Context != context) throw new ArgumentException("Check the transaction with the original service and unchanged cluster configuration.");
        return PrivilegeCommitReconciliation.ReadAsync(connectionString, receipt, token);
    }

    public async Task<bool> ApplyInspectedAsync(DefaultPrivilegeRequest request, DefaultPrivilegeSnapshot expected,
        PrivilegeRecipientIdentity recipient, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(recipient);
        string sql = request.ToSql();
        if (expected.Identity.Target != request.Target || expected.Identity.Creator != request.Creator || expected.Identity.Schema != request.Schema)
            throw Conflict();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        // A stable snapshot is essential: deleting the policy row cannot be proved
        // with xmin. Concurrent outside changes must not masquerade as our delta.
        // PostgreSQL rejects conflicting catalog updates; never retry automatically.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        var before = await DefaultPrivilegeCatalog.ReadAsync(connection, transaction, request.Target, request.Creator, request.Schema, token).ConfigureAwait(false);
        if (before.Identity != expected.Identity || before.GlobalPolicyOid != expected.GlobalPolicyOid || before.SchemaPolicyOid != expected.SchemaPolicyOid || !Normalize(before).SetEquals(Normalize(expected)))
            throw Conflict();
        var currentRecipient = await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, request.Role, request.RecipientIsPublic, token).ConfigureAwait(false);
        if (currentRecipient != recipient) throw PrivilegeRecipientIdentity.Conflict();

        var original = Normalize(before); var planned = new HashSet<Entry>(original);
        bool local = request.Schema is not null;
        var key = new Entry(local, before.Identity.CreatorOid, recipient.Oid, request.Privilege, false);
        var prior = planned.SingleOrDefault(e => e.Schema == local && e.Grantor == key.Grantor && e.Recipient == key.Recipient && e.Privilege == key.Privilege);
        if (prior is not null) planned.Remove(prior);
        if (!request.Revoke) planned.Add(key with { Grantable = request.GrantOption || prior?.Grantable == true });
        else if (request.GrantOption && prior is not null) planned.Add(key);

        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 })
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var after = await DefaultPrivilegeCatalog.ReadAsync(connection, transaction, request.Target, request.Creator, request.Schema, token).ConfigureAwait(false);
        var actual = Normalize(after);
        if (after.Identity != before.Identity || !actual.SetEquals(planned)) throw Conflict();
        if (actual.SetEquals(original))
        {
            // No observable intended write cannot prove the resolved identities.
            await transaction.RollbackAsync(token).ConfigureAwait(false); return false;
        }
        token.ThrowIfCancellationRequested();
        string xid;
        await using (var command = new NpgsqlCommand("SELECT pg_catalog.pg_current_xact_id()::text", connection, transaction))
            xid = (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        var receipt = new PrivilegeCommitReceipt(context, xid, before.Identity.DatabaseOid);
        if (journal is not null) await journal.RecordAsync(receipt, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { await transaction.CommitAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or OperationCanceledException)
        { throw new PrivilegeCommitUncertainException(receipt, ex); }
        return true;
    }
}
