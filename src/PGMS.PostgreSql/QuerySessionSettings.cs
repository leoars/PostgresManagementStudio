using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed partial class QuerySession
{
    public Task<string> SetApplicationNameAsync(QuerySessionSnapshot expected,string value,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(value.Length>63 || value.Any(c=>c is <' ' or >'~'))throw new ArgumentException("Use up to 63 printable ASCII characters for application name.",nameof(value));
        return SetSessionSettingAsync(expected,"application_name",value,token);
    }
    public Task<string> SetStatementTimeoutAsync(QuerySessionSnapshot expected,int milliseconds,CancellationToken token=default)
        => SetSessionSettingAsync(expected,"statement_timeout",TimeoutValue(milliseconds),token);
    public Task<string> SetLockTimeoutAsync(QuerySessionSnapshot expected,int milliseconds,CancellationToken token=default)
        => SetSessionSettingAsync(expected,"lock_timeout",TimeoutValue(milliseconds),token);
    private static string TimeoutValue(int milliseconds)
    {
        if(milliseconds is <0 or >86400000)throw new ArgumentOutOfRangeException(nameof(milliseconds),"Use 0 through 86400000 milliseconds.");
        return milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+"ms";
    }
    public Task<string> SetSearchPathAsync(QuerySessionSnapshot expected,string value,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(value.Length>2048 || value.Contains('\0'))throw new ArgumentException("Search path supports up to 2048 characters without NUL.",nameof(value));
        _=new UTF8Encoding(false,true).GetByteCount(value);
        return SetSessionSettingAsync(expected,"search_path",value,token);
    }
    private async Task<string> SetSessionSettingAsync(QuerySessionSnapshot expected,string name,string value,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if(!expected.Settings.TryGetValue(name,out string? previous))throw new ArgumentException("Setting was not inspected.",nameof(expected));
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!IsConnected || expected.SessionIdentity!=sessionIdentity || expected.BackendPid!=connection.ProcessID || expected.Database!=connection.Database)
                throw new InvalidOperationException("Session identity changed. Inspect again.");
            const string sql="""
                SELECT pg_catalog.set_config(@name,@value,false)
                WHERE pg_catalog.current_setting(@name) OPERATOR(pg_catalog.=) @previous
                  AND pg_catalog.pg_backend_pid() OPERATOR(pg_catalog.=) @pid AND current_user::pg_catalog.text OPERATOR(pg_catalog.=) @actor
                """;
            await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=15};
            command.Parameters.AddWithValue("name",name);command.Parameters.AddWithValue("value",value);
            command.Parameters.AddWithValue("previous",previous);command.Parameters.AddWithValue("pid",expected.BackendPid);command.Parameters.AddWithValue("actor",expected.CurrentUser);
            var result=await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is string actual?actual:throw new InvalidOperationException("Session setting or role changed. Inspect again before applying.");
        }
        finally{execution.Release();}
    }
}
