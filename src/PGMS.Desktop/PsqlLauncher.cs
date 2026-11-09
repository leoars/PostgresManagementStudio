using System.Diagnostics;
using System.IO;
using Npgsql;

namespace PGMS.Desktop;

/// <summary>Start psql in its own console, connected like the selected Object Explorer node (SSMS: Start PowerShell).
/// The password goes to the child process environment only; it is never put on the command line.</summary>
internal static class PsqlLauncher
{
    /// <summary>psql.exe from PATH or the newest PostgreSQL installation under Program Files; null when none is found.</summary>
    public static string? Find()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string candidate = Path.Combine(directory.Trim('"'), "psql.exe"); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root).OrderByDescending(p => int.TryParse(Path.GetFileName(p), out int version) ? version : 0)
            .Select(p => Path.Combine(p, "bin", "psql.exe")).FirstOrDefault(File.Exists);
    }

    public static ProcessStartInfo CreateStartInfo(string connection, string psql)
    {
        var c = new NpgsqlConnectionStringBuilder(connection);
        var start = new ProcessStartInfo(psql) { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (string arg in new[] { "-h", c.Host ?? "localhost", "-p", c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", c.Username ?? "", "-d", c.Database ?? "postgres" }) start.ArgumentList.Add(arg);
        // Inherited PG* variables could silently point psql elsewhere; this console uses only the node's connection.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        if (!string.IsNullOrEmpty(c.Password)) start.Environment["PGPASSWORD"] = c.Password;
        start.Environment["PGSSLMODE"] = c.SslMode switch
        {
            SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full", _ => "prefer"
        };
        if (!string.IsNullOrEmpty(c.RootCertificate)) start.Environment["PGSSLROOTCERT"] = Path.GetFullPath(c.RootCertificate);
        start.Environment["PGAPPNAME"] = "PGMS psql";
        start.Environment["PGCLIENTENCODING"] = "UTF8";
        return start;
    }
}
