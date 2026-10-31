using Npgsql;

namespace PGMS.PostgreSql;

public enum PrivilegeTarget { Database, Schema, Table, Sequence, Routine }
public sealed record PrivilegeRequest(PrivilegeTarget Target, string Name, string Role, string Privilege, bool Revoke = false, string Schema = "public", bool RecipientIsPublic = false, bool GrantOption = false, string? Column = null)
{
    public RoutinePrivilegeSelection? Routine { get; init; }
    public PrivilegeGrantorIdentity? Grantor { get; init; }
    public bool Cascade { get; init; }
    public static string[] Allowed(PrivilegeTarget target) => target switch
    {
        PrivilegeTarget.Database => ["CONNECT", "CREATE", "TEMPORARY"],
        PrivilegeTarget.Schema => ["USAGE", "CREATE"],
        PrivilegeTarget.Table => ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"],
        PrivilegeTarget.Sequence => ["USAGE", "SELECT", "UPDATE"],
        PrivilegeTarget.Routine => ["EXECUTE"],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };
    public static string[] AllowedForColumn => ["SELECT", "INSERT", "UPDATE", "REFERENCES"];
    public string ToSql()
    {
        string command=ToCommandSql();
        return Grantor is null?command:$"BEGIN;\nSET LOCAL ROLE {SqlIdentifier.Quote(Grantor.Name)};\n{command}\nCOMMIT;";
    }
    internal string ToCommandSql()
    {
        Grantor?.Validate();
        if (Cascade && (!Revoke || Grantor is null)) throw new ArgumentException("CASCADE requires a revoke operation and an explicit grantor.");
        if (Column is not null && Target != PrivilegeTarget.Table) throw new ArgumentException("Column privileges require a table target.");
        var allowed = Column is null ? Allowed(Target) : AllowedForColumn;
        if (!allowed.Contains(Privilege, StringComparer.Ordinal)) throw new ArgumentException("Select a privilege supported by this object type.");
        if (RecipientIsPublic && GrantOption) throw new ArgumentException("PUBLIC cannot receive grant options.");
        if ((Target == PrivilegeTarget.Routine) != (Routine is not null)) throw new ArgumentException("Select an exact routine overload from the catalog.");
        if (Routine is not null && (Routine.Name != Name || Routine.Schema != Schema)) throw new ArgumentException("Routine selection does not match the requested object.");
        string name = Routine?.QualifiedSignature ?? SqlIdentifier.Quote(Name);
        if (Target is PrivilegeTarget.Table or PrivilegeTarget.Sequence) name = SqlIdentifier.Quote(Schema) + "." + name;
        string privilege = Column is null ? Privilege : $"{Privilege} ({SqlIdentifier.Quote(Column)})";
        string recipient = RecipientIsPublic ? "PUBLIC" : SqlIdentifier.Quote(Role);
        string action = Revoke ? GrantOption ? "REVOKE GRANT OPTION FOR" : "REVOKE" : "GRANT";
        string grantor=Grantor is null?"":" GRANTED BY "+SqlIdentifier.Quote(Grantor.Name);
        return $"{action} {privilege} ON {Target.ToString().ToUpperInvariant()} {name} {(Revoke ? "FROM" : "TO")} {recipient}{(!Revoke && GrantOption ? " WITH GRANT OPTION" : "")}{grantor}{(Revoke ? Cascade ? " CASCADE" : " RESTRICT" : "")};";
    }
}

public sealed class PrivilegeService(string connectionString, IPrivilegeCommitJournal? journal = null)
{
    private readonly Guid commitContext=Guid.NewGuid();

    public PrivilegeCommitReceipt ResumeCommitCheck(string transactionId,uint databaseOid)
    {
        if(!ulong.TryParse(transactionId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var xid) || xid==0 || databaseOid==0)
            throw new ArgumentException("Invalid pending permission transaction identity.");
        // Import only a trusted journal bound to the unchanged connection profile.
        // The returned receipt permits status inspection, never replay of SQL.
        return new(commitContext,transactionId,databaseOid);
    }

    public Task<PrivilegeCommitStatus> ReconcileCommitAsync(PrivilegeCommitReceipt receipt,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(receipt);
        if(receipt.Context!=commitContext)throw new ArgumentException("Check the transaction using the original permission operation and unchanged cluster configuration.");
        return PrivilegeCommitReconciliation.ReadAsync(connectionString,receipt,token);
    }
    public async Task ApplyAsync(PrivilegeRequest request, CancellationToken token = default)
        => _ = await ApplyCoreAsync(request, null, null, token).ConfigureAwait(false);

    public Task<bool> ApplyCurrentAsync(PrivilegeRequest request, CancellationToken token = default)
        => ApplyCoreAsync(request, null, null, token);

    // True means the command was committed; false is a rolled-back empty-column
    // no-op observation. A target mismatch always throws and cannot commit.
    public Task<bool> ApplyInspectedAsync(PrivilegeRequest request, PrivilegeTargetIdentity identity, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ApplyCoreAsync(request, identity, null, token);
    }

    public Task<bool> ApplyInspectedAsync(PrivilegeRequest request, PrivilegeTargetIdentity identity, PrivilegeRecipientIdentity recipient, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(recipient);
        return ApplyCoreAsync(request, identity, recipient, token);
    }

    public Task<bool> ApplyReviewedRevokeAsync(PrivilegeRevokePreview preview, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return ApplyCoreAsync(preview.Request with { Cascade = true }, preview.Target, preview.Recipient, token, preview);
    }

    public async Task<PrivilegeRecipientIdentity?> InspectRecipientAsync(string name, bool isPublic = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        return await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, name, isPublic, token).ConfigureAwait(false);
    }

    public async Task<PrivilegeTargetIdentity> InspectTargetAsync(PrivilegeTarget target, string name, string schema = "public", string? column = null, CancellationToken token = default, RoutinePrivilegeSelection? routine = null)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var observed = await PrivilegeTargetIdentity.ReadAsync(connection, transaction, target, name, schema, column, token, routine).ConfigureAwait(false);
        return observed.Identity;
    }

    private async Task<bool> ApplyCoreAsync(PrivilegeRequest request, PrivilegeTargetIdentity? expected, PrivilegeRecipientIdentity? recipient, CancellationToken token, PrivilegeRevokePreview? preview = null)
    {
        token.ThrowIfCancellationRequested();
        string sql = request.ToCommandSql();
        if (request.Cascade && preview is null) throw new ArgumentException("Review dependencies before applying CASCADE.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        if(request.Grantor is { } selectedGrantor)await selectedGrantor.BindAsync(connection,transaction,token).ConfigureAwait(false);
        // Capture once for name-based API callers. Inspected callers carry the older
        // identity; checking only before the command would leave a DDL race.
        if(expected is null)
        {
            try { expected=(await PrivilegeTargetIdentity.ReadAsync(connection,transaction,request.Target,request.Name,request.Schema,request.Column,token,request.Routine).ConfigureAwait(false)).Identity; }
            catch(PrivilegeTargetMissingException)
            {
                if (request.Target == PrivilegeTarget.Routine) throw PrivilegeTargetIdentity.Conflict();
                // Preserve native SQLSTATE (e.g.42703 for a missing column). If
                // concurrent DDL makes the command succeed, still never commit it:
                // no identity was captured for this invocation.
                await using var validation=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=30};
                await validation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                throw PrivilegeTargetIdentity.Conflict();
            }
        }
        expected.ValidateRequest(request);
        recipient ??= await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, request.Role, request.RecipientIsPublic, token).ConfigureAwait(false);
        if (recipient is null)
        {
            // Retain native 42704, but never commit if a concurrent CREATE makes it succeed.
            await using var validation = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
            await validation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            throw PrivilegeRecipientIdentity.Conflict();
        }
        if (recipient.IsPublic != request.RecipientIsPublic || (recipient.IsPublic ? recipient.Oid != 0 : recipient.Oid == 0 || recipient.Name != request.Role))
            throw PrivilegeRecipientIdentity.Conflict();
        var aclBefore = await PrivilegeRecipientIdentity.ReadAclAsync(connection, transaction, expected, token).ConfigureAwait(false);
        if (preview is not null)
        {
            var current = await PrivilegeTargetIdentity.ReadAsync(connection, transaction, request.Target, request.Name, request.Schema, request.Column, token, request.Routine).ConfigureAwait(false);
            if (current.Identity != expected || !aclBefore.SetEquals(preview.Acl)) throw PrivilegeTargetIdentity.Conflict();
            if (await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, request.Role, request.RecipientIsPublic, token).ConfigureAwait(false) != recipient) throw PrivilegeRecipientIdentity.Conflict();
            await preview.VerifyNamesAsync(connection, transaction, token).ConfigureAwait(false);
        }
        // PostgreSQL can report insufficient grant authority as a warning and no-op.
        string? warning = null;
        connection.Notice += (_, e) => warning = e.Notice.MessageText;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (warning is not null) throw new InvalidOperationException("PostgreSQL did not confirm a clean operation: " + warning);
        var after = await PrivilegeTargetIdentity.ReadAsync(connection, transaction, request.Target, request.Name, request.Schema, request.Column, token, request.Routine).ConfigureAwait(false);
        if (after.Identity != expected) throw PrivilegeTargetIdentity.Conflict();
        var recipientAfter = await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, request.Role, request.RecipientIsPublic, token).ConfigureAwait(false);
        var aclAfter = await PrivilegeRecipientIdentity.ReadAclAsync(connection, transaction, expected, token).ConfigureAwait(false);
        if(request.Grantor is { } grantor)
        {
            await grantor.VerifyAsync(connection,transaction,true,token).ConfigureAwait(false);
            short column=expected.ColumnNumber??0;
            bool Intended(PrivilegeRecipientIdentity.AclEntry e)=>e.Grantor==grantor.Oid && e.Grantee==recipient.Oid && e.ColumnNumber==column && e.Privilege==request.Privilege;
            if(preview is null && !aclBefore.Where(e=>!Intended(e)).ToHashSet().SetEquals(aclAfter.Where(e=>!Intended(e))))throw PrivilegeGrantorIdentity.Conflict();
            // A selected role can inherit grant authority from another role. Only
            // the explicitly selected ACL origin may change in this operation.
        }
        if (recipientAfter != recipient || (preview is null && !aclBefore.Where(e=>e.Grantee!=recipient.Oid).ToHashSet().SetEquals(aclAfter.Where(e=>e.Grantee!=recipient.Oid))))
            throw PrivilegeRecipientIdentity.Conflict();
        if (preview is not null)
        {
            await preview.VerifyNamesAsync(connection, transaction, token).ConfigureAwait(false);
            preview.VerifyDelta(aclBefore, aclAfter);
        }
        if (aclBefore.SetEquals(aclAfter))
        {
            // A native idempotent command cannot prove which recipient name was
            // resolved. No ACL delta: roll back and report no committed change.
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return false;
        }
        if (!after.Written)
        {
            // PG can omit the tuple update for REVOKE on a NULL column ACL.
            // Roll back even this case: never commit a write to a different object
            // whose name may have temporarily occupied the inspected name.
            if (request.Column is not null && request.Revoke && after.EmptyColumnAcl)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return false;
            }
            throw PrivilegeTargetIdentity.Conflict();
        }
        // Reduce the cancellation window before COMMIT. A lost commit acknowledgement
        // still requires inspection; cancellation cannot prove the server rolled back.
        token.ThrowIfCancellationRequested();
        string transactionId;
        await using(var xid=new NpgsqlCommand("SELECT pg_catalog.pg_current_xact_id()::text",connection,transaction){CommandTimeout=10})
            transactionId=(string)(await xid.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        var receipt=new PrivilegeCommitReceipt(commitContext,transactionId,expected.DatabaseOid);
        token.ThrowIfCancellationRequested();
        if(journal is not null)await journal.RecordAsync(receipt,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { await transaction.CommitAsync(token).ConfigureAwait(false); }
        catch(Exception ex) when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException)
        { throw new PrivilegeCommitUncertainException(receipt,ex); }
        return true;
    }
}
