using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record QuerySessionSnapshot(Guid SessionIdentity,int BackendPid,string Database,string SessionUser,string CurrentUser,IReadOnlyDictionary<string,string> Settings,DateTimeOffset CapturedAt)
{
    public int StatementTimeoutMilliseconds{get;init;}
    public int LockTimeoutMilliseconds{get;init;}
}

public sealed partial class QuerySession
{
    private Guid sessionIdentity=Guid.NewGuid();
    public async Task<QuerySessionSnapshot> InspectSessionAsync(CancellationToken token=default)
    {
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            const string sql="""
                SELECT pg_catalog.pg_backend_pid()::pg_catalog.text,pg_catalog.current_database()::pg_catalog.text,session_user::pg_catalog.text,current_user::pg_catalog.text,
                  pg_catalog.current_setting('search_path'),pg_catalog.current_setting('transaction_isolation'),pg_catalog.current_setting('transaction_read_only'),
                  pg_catalog.current_setting('statement_timeout'),pg_catalog.current_setting('lock_timeout'),pg_catalog.current_setting('application_name'),pg_catalog.current_schemas(true)::pg_catalog.text,
                  (SELECT setting FROM pg_catalog.pg_settings WHERE name OPERATOR(pg_catalog.=) 'statement_timeout'),
                  (SELECT setting FROM pg_catalog.pg_settings WHERE name OPERATOR(pg_catalog.=) 'lock_timeout')
                """;
            await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=15};
            await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,token).ConfigureAwait(false);
            try
            {
                if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Session inspection returned no row.");
                string[] values=new string[13];char[] scratch=new char[4096];
                for(int i=0;i<values.Length;i++)
                {
                    using var text=await reader.GetTextReaderAsync(i,token).ConfigureAwait(false);
                    values[i]=await ReadBoundedAsync(text,4096,scratch,token).ConfigureAwait(false) ?? throw new InvalidDataException("A session field exceeds 4096 characters; snapshot refused.");
                }
                string[] names=["search_path","transaction_isolation","transaction_read_only","statement_timeout","lock_timeout","application_name","effective_schemas"];
                var settings=new Dictionary<string,string>(StringComparer.Ordinal);
                for(int i=0;i<names.Length;i++)settings.Add(names[i],values[i+4]);
                return new(sessionIdentity,int.Parse(values[0],CultureInfo.InvariantCulture),values[1],values[2],values[3],new ReadOnlyDictionary<string,string>(settings),DateTimeOffset.UtcNow)
                {StatementTimeoutMilliseconds=int.Parse(values[11],CultureInfo.InvariantCulture),LockTimeoutMilliseconds=int.Parse(values[12],CultureInfo.InvariantCulture)};
            }
            catch{try{await Task.Run(command.Cancel).ConfigureAwait(false);}catch{}throw;}
        }
        finally{execution.Release();}
    }
}
