using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed class EffectivePrivilegeService(string connectionString)
{
    public const int MaximumColumns = 256;
    public const int MaximumAclEntries = 512;
    public async Task<EffectivePrivilegeSnapshot> InspectAsync(PrivilegeTarget target, string name, string schema = "public", CancellationToken token = default, string? column = null, string? recipient = null, bool recipientIsPublic = false, RoutinePrivilegeSelection? routine = null)
    {
        token.ThrowIfCancellationRequested();
        if (target == PrivilegeTarget.Routine && routine is null) throw new ArgumentException("Select an exact routine overload from the catalog.");
        _ = SqlIdentifier.Quote(name);
        if (target is PrivilegeTarget.Table or PrivilegeTarget.Sequence) _ = SqlIdentifier.Quote(schema);
        string metadata = target switch
        {
            PrivilegeTarget.Routine => "SELECT p.oid,p.proname,n.nspname,pg_catalog.pg_get_userbyid(p.proowner),pg_catalog.current_database(),current_user,pg_catalog.has_schema_privilege(n.oid,'USAGE'),NULL::boolean,NULL::boolean,NULL::boolean,session_user FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=@routineOid",
            PrivilegeTarget.Database => "SELECT oid,datname,NULL::text,pg_catalog.pg_get_userbyid(datdba),pg_catalog.current_database(),current_user,NULL::boolean,NULL::boolean,NULL::boolean,NULL::boolean,session_user FROM pg_catalog.pg_database WHERE datname=@name",
            PrivilegeTarget.Schema => "SELECT oid,nspname,NULL::text,pg_catalog.pg_get_userbyid(nspowner),pg_catalog.current_database(),current_user,NULL::boolean,NULL::boolean,NULL::boolean,NULL::boolean,session_user FROM pg_catalog.pg_namespace WHERE nspname=@name",
            PrivilegeTarget.Table => "SELECT c.oid,c.relname,n.nspname,pg_catalog.pg_get_userbyid(c.relowner),pg_catalog.current_database(),current_user,pg_catalog.has_schema_privilege(n.oid,'USAGE'),c.relrowsecurity,c.relforcerowsecurity,pg_catalog.row_security_active(c.oid),session_user FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p')",
            PrivilegeTarget.Sequence => "SELECT c.oid,c.relname,n.nspname,pg_catalog.pg_get_userbyid(c.relowner),pg_catalog.current_database(),current_user,pg_catalog.has_schema_privilege(n.oid,'USAGE'),NULL::boolean,NULL::boolean,NULL::boolean,session_user FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='S'",
            _ => throw new ArgumentException("Unsupported inspection target.", nameof(target))
        };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))
            await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        uint oid; string exactName, owner, database, role, sessionUser; string? exactSchema; bool? usage, enabled, forced, active;
        await using (var command = new NpgsqlCommand(metadata,connection,transaction) { CommandTimeout=15 })
        {
            command.Parameters.AddWithValue("routineOid",NpgsqlTypes.NpgsqlDbType.Oid,routine?.ObjectOid ?? 0u);
            command.Parameters.AddWithValue("name",name);
            command.Parameters.AddWithValue("schema",schema);
            await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Object not found or unsupported kind. Inspect the exact name in the connected database.");
            oid=row.GetFieldValue<uint>(0); exactName=row.GetString(1); exactSchema=row.IsDBNull(2)?null:row.GetString(2);
            owner=row.GetString(3); database=row.GetString(4); role=row.GetString(5);
            bool? Flag(int i) => row.IsDBNull(i)?null:row.GetBoolean(i);
            usage=Flag(6); enabled=Flag(7); forced=Flag(8); active=Flag(9); sessionUser=row.GetString(10);
        }
        string function = target switch { PrivilegeTarget.Routine => "has_function_privilege", PrivilegeTarget.Database => "has_database_privilege", PrivilegeTarget.Schema => "has_schema_privilege", PrivilegeTarget.Table => "has_table_privilege", PrivilegeTarget.Sequence => "has_sequence_privilege", _ => throw new ArgumentException("Unsupported target.") };
        var privileges = new List<EffectivePrivilege>();
        await using (var command = new NpgsqlCommand($"SELECT p,pg_catalog.{function}(@oid,p),pg_catalog.{function}(@oid,p || ' WITH GRANT OPTION') FROM pg_catalog.unnest(@privileges::text[]) AS p",connection,transaction) { CommandTimeout=15 })
        {
            command.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);
            command.Parameters.AddWithValue("privileges",PrivilegeRequest.Allowed(target));
            await using var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await rows.ReadAsync(token).ConfigureAwait(false)) privileges.Add(new(target.ToString(),null,null,rows.GetString(0),rows.GetBoolean(1),rows.GetBoolean(2)));
        }
        bool columnsTruncated=false;
        if (target == PrivilegeTarget.Table)
        {
            const string sql = "SELECT a.attnum,a.attname,p,pg_catalog.has_column_privilege(@oid,a.attnum,p),pg_catalog.has_column_privilege(@oid,a.attnum,p || ' WITH GRANT OPTION') FROM (SELECT attnum,attname FROM pg_catalog.pg_attribute WHERE attrelid=@oid AND attnum>0 AND NOT attisdropped ORDER BY attnum LIMIT @columnLimit) a CROSS JOIN pg_catalog.unnest(ARRAY['SELECT','INSERT','UPDATE','REFERENCES']) p ORDER BY a.attnum,p";
            await using var command=new NpgsqlCommand(sql,connection,transaction) { CommandTimeout=15 };
            command.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);
            command.Parameters.AddWithValue("columnLimit",MaximumColumns+1);
            await using var rows=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            int count=0; short prior=0;
            while(await rows.ReadAsync(token).ConfigureAwait(false))
            {
                short attnum=rows.GetInt16(0);
                if (attnum!=prior) {count++; prior=attnum;}
                if (count>MaximumColumns) {columnsTruncated=true; continue;}
                privileges.Add(new("Column",rows.GetString(1),attnum,rows.GetString(2),rows.GetBoolean(3),rows.GetBoolean(4)));
            }
        }
        var acl=await PrivilegeAclReader.ReadAsync(connection,transaction,target,oid,token).ConfigureAwait(false);
        var identity=await PrivilegeTargetIdentity.ReadAsync(connection,transaction,target,name,schema,column,token,routine).ConfigureAwait(false);
        if(identity.Identity.ObjectOid!=oid)throw PrivilegeTargetIdentity.Conflict();
        var recipientIdentity = recipientIsPublic || !string.IsNullOrWhiteSpace(recipient)
            ? await PrivilegeRecipientIdentity.ReadAsync(connection, transaction, recipient ?? "", recipientIsPublic, token).ConfigureAwait(false) : null;
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(target,exactName,exactSchema,oid,database,role,sessionUser,DateTimeOffset.UtcNow,owner,usage,enabled,forced,active,privileges.AsReadOnly(),columnsTruncated) {AclEntries=acl.Entries,AclTruncated=acl.Truncated,TargetIdentity=identity.Identity,RecipientIdentity=recipientIdentity};
    }
}
