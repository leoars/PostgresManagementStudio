using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Globalization;
using System.Net;
using Npgsql;

namespace PGMS.PostgreSql;

public enum RolePasswordTransport { Tls, Loopback, Unencrypted }

public static class RolePasswordService
{
    public static RolePasswordTransport TransportOf(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull) return RolePasswordTransport.Tls;
        return IsLoopback(builder.Host) ? RolePasswordTransport.Loopback : RolePasswordTransport.Unencrypted;
    }

    // Every host in a multi-host list must be loopback; a name other than localhost is never resolved here.
    public static bool IsLoopback(string? hosts) => !string.IsNullOrWhiteSpace(hosts) && hosts.Split(',').All(entry =>
    {
        string host = entry.Trim();
        if (host.StartsWith('[')) host = host[1..Math.Max(1, host.IndexOf(']'))];
        else if (host.Count(c => c == ':') == 1) host = host[..host.IndexOf(':')];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    });

    public static void ValidatePassword(SecureString password)
    {
        byte[] bytes = PreparePassword(password);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static byte[] PreparePassword(SecureString password)
    {
        IntPtr memory = IntPtr.Zero;
        char[] chars = new char[password.Length];
        try
        {
            memory = Marshal.SecureStringToGlobalAllocUnicode(password);
            Marshal.Copy(memory, chars, 0, chars.Length);
            return ScramPasswordPreparation.Prepare(chars);
        }
        finally
        {
            Array.Clear(chars);
            if (memory != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(memory);
        }
    }

    public static async Task SetAsync(string connectionString, string role, SecureString password, CancellationToken token = default, bool allowUnencrypted = false)
    {
        token.ThrowIfCancellationRequested();
        SqlIdentifier.Quote(role);
        if (role.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("PostgreSQL predefined roles cannot be changed here.");
        if (password.Length == 0) throw new ArgumentException("Enter a non-empty password.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (TransportOf(connectionString) == RolePasswordTransport.Unencrypted && !allowUnencrypted)
            throw new InvalidOperationException("Role passwords can be changed only over TLS, on a loopback connection, or after explicitly allowing an unencrypted connection. Set this connection profile to Require, VerifyCA or VerifyFull and reconnect.");

        byte[]? passwordBytes = null, salt = null, saltedPassword = null, clientKey = null, storedKey = null, serverKey = null;
        string? sql = null;
        try
        {
            passwordBytes = PreparePassword(password);
            builder.Pooling = false;
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using (var iterationCommand = new NpgsqlCommand("SELECT current_setting('scram_iterations', true)", connection) { CommandTimeout = 10 })
            {
                string? configured = await iterationCommand.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                int iterations = int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 4096;
                if (iterations is < 1 or > 1_000_000) throw new InvalidOperationException("Server scram_iterations is outside the supported range 1..1000000.");
                salt = RandomNumberGenerator.GetBytes(16);
                saltedPassword = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, 32);
                clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
                storedKey = SHA256.HashData(clientKey);
                serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);
                string verifier = $"SCRAM-SHA-256${iterations.ToString(CultureInfo.InvariantCulture)}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
                sql = $"ALTER ROLE {SqlIdentifier.Quote(role)} WITH PASSWORD '{verifier}';";
            }
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "SELECT 1";
        }
        finally
        {
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (saltedPassword is not null) CryptographicOperations.ZeroMemory(saltedPassword);
            if (clientKey is not null) CryptographicOperations.ZeroMemory(clientKey);
            if (storedKey is not null) CryptographicOperations.ZeroMemory(storedKey);
            if (serverKey is not null) CryptographicOperations.ZeroMemory(serverKey);
            sql = null;
        }
    }
}
