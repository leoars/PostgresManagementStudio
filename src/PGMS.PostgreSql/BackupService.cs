using System.Diagnostics;
using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed class BackupService(string connectionString, string toolsDirectory)
{
    /// <summary>A new, empty database to restore into, from template0 as pg_restore --create does; outside a transaction, on its own connection.</summary>
    public static async Task CreateRestoreDatabaseAsync(string connectionString, string name, CancellationToken token = default)
    {
        string sql = $"CREATE DATABASE {SqlIdentifier.Quote(name)} TEMPLATE template0";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public Task BackupAsync(string destination, CancellationToken token = default)
        =>BackupWithOptionsAsync(destination,new BackupOptions(),token);

    public async Task BackupWithOptionsAsync(string destination,BackupOptions options,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(options);string[] arguments=options.ToArguments();
        RestoreInputStaging? filter=null;
        try
        {
            if(options.Selection is not null)
            {
                string temporaryDirectory=Path.GetFullPath(Path.GetTempPath());
                if(OperatingSystem.IsWindows() && temporaryDirectory.Any(c=>c>127))throw new ArgumentException("Schema filters require an ASCII temporary directory for PostgreSQL tools on Windows.");
                filter=await RestoreInputStaging.CreateTextAsync(options.Selection.FilterText,temporaryDirectory,token).ConfigureAwait(false);
                arguments=[..arguments,"--filter",filter.FilePath];
            }
            await ExportAsync(destination,"pg_dump",arguments,token,options.Format==BackupFormat.Directory).ConfigureAwait(false);
        }
        finally{if(filter is not null)await filter.DisposeAsync().ConfigureAwait(false);}
    }

    public Task BackupGlobalsAsync(string destination, CancellationToken token=default)
        => ExportAsync(destination,"pg_dumpall",["--globals-only","--no-role-passwords","--encoding=UTF8"],token);

    // Global scripts can contain CREATE TABLESPACE, which cannot run in a transaction.
    // A failed restore may have applied preceding statements; never retry automatically.
    public async Task RestoreGlobalsAsync(string source,CancellationToken token=default)
    {
        try { await RestoreFileAsync(source,"psql",["--no-psqlrc","--set","ON_ERROR_STOP=1"],token).ConfigureAwait(false); }
        catch(IOException ex) { throw new IOException("Global restore failed. Earlier statements may already be committed. Inspect the destination before retrying.\n"+ex.Message,ex); }
    }

    private async Task ExportAsync(string destination,string tool,string[] arguments,CancellationToken token,bool directoryFormat=false)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose a new destination. Existing backups are never overwritten.");
        string directory=Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory,"PGMS-"+Guid.NewGuid().ToString("N")+".partial");
        try
        {
            await RunAsync(tool, [..arguments, "--file", Path.GetFileName(temporary)], token,directory).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(directoryFormat)Directory.Move(temporary,destination);else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if(directoryFormat && Directory.Exists(temporary))CleanPartialDirectory(temporary,directory);
        }
    }

    // pg_dump directory archives are flat. Never recurse into unexpected entries
    // or follow links when cleaning a failed operation's private random path.
    private static void CleanPartialDirectory(string temporary,string parent)
    {
        if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporary)),Path.GetFullPath(parent),StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(temporary)&FileAttributes.ReparsePoint)!=0)return;
        var entries=Directory.GetFileSystemEntries(temporary);
        if(entries.Any(p=>(File.GetAttributes(p)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0))return;
        foreach(string entry in entries)File.Delete(entry);
        Directory.Delete(temporary);
    }

    public Task RestoreAsync(string source, CancellationToken token = default)
        =>RestoreWithOptionsAsync(source,new RestoreOptions(),token);

    public Task RestoreWithOptionsAsync(string source,RestoreOptions options,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RestoreFileAsync(source,"pg_restore",options.ToArguments(),token);
    }

    public Task RestoreSqlAsync(string source,CancellationToken token=default)
        =>RestoreFileAsync(source,"psql",SqlRestoreArguments(),token);

    public async Task RestoreDirectoryAsync(string source,RestoreOptions? options=null,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        source=Path.GetFullPath(source);
        if(!Directory.Exists(source) || !File.Exists(Path.Combine(source,"toc.dat")))throw new DirectoryNotFoundException("Select a PostgreSQL directory archive containing toc.dat.");
        // The Unicode directory is conveyed by CreateProcessW's working directory;
        // only an ASCII relative path reaches pg_restore's CRT argv parser.
        await RunAsync("pg_restore",[..(options??new RestoreOptions()).ToArguments(),"--","."],token,source).ConfigureAwait(false);
    }

    public static string[] SqlRestoreArguments()=>["--no-psqlrc","--single-transaction","--set","ON_ERROR_STOP=1"];

    private async Task RestoreFileAsync(string source,string tool,string[] arguments,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source)) throw new FileNotFoundException("Select an existing PostgreSQL archive.", source);
        source=Path.GetFullPath(source);RestoreInputStaging? staging=null;
        try
        {
            if(OperatingSystem.IsWindows() && Path.GetFileName(source).Any(c=>c>127))
            {
                // CRT argv cannot represent every filename. Keep the original
                // untouched and copy asynchronously to an ASCII temporary leaf.
                staging=await RestoreInputStaging.CopyAsync(source,Path.GetTempPath(),token).ConfigureAwait(false);source=staging.FilePath;
            }
            else await Task.Run(()=>RestoreInputStaging.CleanAbandoned(Path.GetTempPath(),token,source),token).ConfigureAwait(false);
            string[] fileArguments=tool=="psql"?["--file",Path.GetFileName(source)]:["--",Path.GetFileName(source)];
            await RunAsync(tool, [..arguments,..fileArguments], token,Path.GetDirectoryName(source)).ConfigureAwait(false);
        }
        finally{if(staging is not null)await staging.DisposeAsync().ConfigureAwait(false);}
    }

    private async Task RunAsync(string tool, string[] arguments, CancellationToken token,string? workingDirectory=null)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if(!string.IsNullOrEmpty(connection.SslPassword))throw new ArgumentException("Encrypted client key passwords are not supported by backup and restore tools. No operation was started.");
        var certificateCopies=new List<RestoreInputStaging>();
        async Task<string?> PrepareCertificate(string? path)
        {
            token.ThrowIfCancellationRequested();
            if(string.IsNullOrEmpty(path))return path;
            path=Path.GetFullPath(path);
            if(!File.Exists(path))throw new FileNotFoundException("A configured TLS certificate or key file does not exist. Select an existing file before backup or restore.",path);
            if(OperatingSystem.IsWindows() && path.Any(c=>c>127))
            {
                string stagingDirectory=Path.GetFullPath(Path.GetTempPath());
                if(stagingDirectory.Any(c=>c>127))throw new ArgumentException("Unicode certificate paths require an ASCII temporary directory for PostgreSQL tools on Windows.");
                var copy=await RestoreInputStaging.CopyAsync(path,stagingDirectory,token).ConfigureAwait(false);
                certificateCopies.Add(copy);return copy.FilePath;
            }
            return path;
        }
        try
        {
            connection.RootCertificate=await PrepareCertificate(connection.RootCertificate).ConfigureAwait(false);
            connection.SslCertificate=await PrepareCertificate(connection.SslCertificate).ConfigureAwait(false);
            connection.SslKey=await PrepareCertificate(connection.SslKey).ConfigureAwait(false);
            await RunCoreAsync(connection,tool,arguments,token,workingDirectory).ConfigureAwait(false);
        }
        finally{foreach(var copy in certificateCopies)await copy.DisposeAsync().ConfigureAwait(false);}
    }

    private async Task RunCoreAsync(NpgsqlConnectionStringBuilder connection,string tool,string[] arguments,CancellationToken token,string? workingDirectory)
    {
        if (string.IsNullOrEmpty(connection.Host) || string.IsNullOrEmpty(connection.Database) || string.IsNullOrEmpty(connection.Username))
            throw new ArgumentException("An explicit server, database and user are required.");
        // Pass non-secret identifiers as ASCII URI components. Windows CRT getenv
        // and argv conversions must not reinterpret Unicode names as ANSI bytes.
        string Encode(string value)
        {
            if(value.Contains('\0'))throw new ArgumentException("Connection identifiers cannot contain NUL.");
            _=new UTF8Encoding(false,true).GetByteCount(value);
            return Uri.EscapeDataString(value);
        }
        string databaseUri="postgresql:///?host="+Encode(connection.Host)+"&port="+connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)+"&user="+Encode(connection.Username)+"&dbname="+Encode(connection.Database);
        // Non-secret random value suppresses OpenSSL's hidden console prompt.
        // Encrypted keys fail rather than waiting indefinitely for interactive input.
        databaseUri+="&sslpassword="+Guid.NewGuid().ToString("N");
        // Windows CRT getenv converts Unicode through the ANSI code page. The
        // tools share PostgreSQL's UTF-8 stdin prompt, so bypass that conversion.
        bool prompt=OperatingSystem.IsWindows() && connection.Password?.Any(c=>c>127)==true;
        if(prompt && connection.Password!.Any(c=>c is '\r' or '\n'))
            throw new ArgumentException("PostgreSQL tool password prompts cannot carry Unicode passwords with line breaks. Change this password before using backup or restore.");
        string executable = Path.GetFullPath(Path.Combine(toolsDirectory, tool + (OperatingSystem.IsWindows() ? ".exe" : "")));
        if (!File.Exists(executable)) throw new FileNotFoundException($"Select the PostgreSQL bin folder containing {tool}.", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if(workingDirectory is not null)start.WorkingDirectory=workingDirectory;
        if(prompt){start.RedirectStandardInput=true;start.StandardInputEncoding=new UTF8Encoding(false,true);start.Environment["OSTYPE"]="msys";}
        // Credentials stay in the child environment or redirected UTF-8 prompt,
        // never in arguments or files.
        // Clear inherited libpq overrides so the selected connection remains authoritative.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if(!prompt)start.Environment["PGPASSWORD"] = connection.Password ?? "";
        // Do not silently use personal libpq password/certificate files when the
        // selected connection did not configure them. This path is never created.
        string absent=Path.Combine(Path.GetTempPath(),"PGMS-no-default-credentials-"+Guid.NewGuid().ToString("N"));
        start.Environment["PGPASSFILE"]=absent;
        start.Environment["PGSSLCERT"]=string.IsNullOrEmpty(connection.SslCertificate)?absent:Path.GetFullPath(connection.SslCertificate);
        start.Environment["PGSSLKEY"]=string.IsNullOrEmpty(connection.SslKey)?absent:Path.GetFullPath(connection.SslKey);
        start.Environment["PGSSLROOTCERT"]=string.IsNullOrEmpty(connection.RootCertificate)?absent:Path.GetFullPath(connection.RootCertificate);
        start.Environment["PGCONNECT_TIMEOUT"] = "10";
        start.Environment["PGAPPNAME"] = "PGMS.BackupRestore";
        start.Environment["PGSSLMODE"] = connection.SslMode switch
        {
            SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Prefer => "prefer",
            SslMode.Require => "require", SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
            _ => throw new ArgumentException("Unsupported SSL mode.")
        };
        start.ArgumentList.Add(prompt?"--password":"--no-password");
        // Both tools expand the explicit URI through libpq. It contains no password.
        // pg_restore also requires -d to execute rather than emit SQL to stdout.
        start.ArgumentList.Add("--dbname");start.ArgumentList.Add(databaseUri);
        if(tool=="pg_dumpall")
        {
            // pg_dumpall discards dbname from --dbname, but expands --database
            // through PQconnectdbParams. Encode even ASCII names so '=' or a
            // URI-looking database name cannot be interpreted as connection options.
            start.ArgumentList.Add("--database");start.ArgumentList.Add(databaseUri);
        }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        process.Start();
        start.Environment.Remove("PGPASSWORD");start.Environment.Remove("PGSSLPASSWORD");
        Task<string> errors = DrainAsync(process.StandardError);
        Task<string> output = DrainAsync(process.StandardOutput);
        try
        {
            if(prompt)
            {
                await process.StandardInput.WriteLineAsync(connection.Password!.AsMemory(),token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(errors, output).ConfigureAwait(false);
            throw;
        }
        await output.ConfigureAwait(false);
        string detail = await errors.ConfigureAwait(false);
        if (!string.IsNullOrEmpty(connection.Password)) detail = detail.Replace(connection.Password, "[redacted]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(connection.SslPassword)) detail = detail.Replace(connection.SslPassword, "[redacted]", StringComparison.Ordinal);
        if (process.ExitCode != 0) throw new IOException($"{tool} failed (exit {process.ExitCode}).\n{detail}");
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (result.Length < 16384) result.Append(buffer, 0, Math.Min(count, 16384 - result.Length));
        return result.ToString();
    }
}
