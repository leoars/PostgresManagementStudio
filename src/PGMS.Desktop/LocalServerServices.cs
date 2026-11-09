using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>A PostgreSQL Windows service registered by pg_ctl register (EDB installers do this): service name, data directory and the port read from its configuration.</summary>
public sealed record LocalPostgresService(string Name, string DataDirectory, int? Port);

public enum LocalServiceState { Unknown, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }
public enum LocalServiceAction { Start, Stop, Restart }

/// <summary>Start, Stop and Restart of a local PostgreSQL service, like the SSMS 22.11 server menu (docs/MARCO-M1035.md). Discovery and state need no privileges;
/// control runs one elevated PowerShell command per action behind a UAC prompt (decision of 05/11/2026), so PGMS itself never runs as administrator.</summary>
public static partial class LocalServerServices
{
    public const int DefaultPort = 5432;

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    [GeneratedRegex("""(?:^|\s)-(?<flag>[ND])\s+(?:"(?<value>[^"]*)"|(?<value>\S+))""")]
    private static partial Regex ServiceArgument();

    /// <summary>Service name (-N) and data directory (-D) from a "pg_ctl runservice" image path; null when it is not one.</summary>
    public static LocalPostgresService? ParseImagePath(string? imagePath, string serviceName)
    {
        if (imagePath is null || !imagePath.Contains("pg_ctl", StringComparison.OrdinalIgnoreCase) || !imagePath.Contains("runservice", StringComparison.OrdinalIgnoreCase)) return null;
        string? data = null, name = null;
        foreach (Match m in ServiceArgument().Matches(imagePath))
            if (m.Groups["flag"].Value == "D") data = m.Groups["value"].Value; else name = m.Groups["value"].Value;
        if (data is null) return null;
        return new(name ?? serviceName, data, null);
    }

    [GeneratedRegex(@"^\s*port\s*=?\s*'?(?<port>\d{1,5})'?\s*(?:#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PortSetting();

    /// <summary>The last port assignment in postgresql.conf, then postgresql.auto.conf (ALTER SYSTEM), as the server reads them; 5432 when none.
    /// Null when the files cannot be read. include directives are not followed.</summary>
    public static int? ReadPort(string dataDirectory)
    {
        int? port = null;
        bool read = false;
        foreach (string file in new[] { "postgresql.conf", "postgresql.auto.conf" })
        {
            try
            {
                foreach (string line in File.ReadLines(Path.Combine(dataDirectory, file)))
                    if (PortSetting().Match(line) is { Success: true } m && int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is > 0 and < 65536) port = value;
                read = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return read ? port ?? DefaultPort : null;
    }

    public static IReadOnlyList<LocalPostgresService> Discover()
    {
        var found = new List<LocalPostgresService>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return found;
            foreach (string name in services.GetSubKeyNames())
            {
                using var key = services.OpenSubKey(name);
                if (ParseImagePath(key?.GetValue("ImagePath") as string, name) is { } service) found.Add(service with { Port = ReadPort(service.DataDirectory) });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }

    /// <summary>The single local service listening on the port of a connection to this computer; null for remote hosts, no match or more than one.</summary>
    public static LocalPostgresService? Find(string? host, int port, IEnumerable<LocalPostgresService>? services = null)
    {
        if (!IsLocalHost(host)) return null;
        var matches = (services ?? Discover()).Where(s => s.Port == port).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    [GeneratedRegex(@":\s*(?<code>[1-7])\s+(?:STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b")]
    private static partial Regex StateLine();

    /// <summary>Parses "sc.exe query" output. The field label is translated ("STATE", "ESTADO"), so the line is found by the numeric code and the
    /// state name, which Windows prints in English in every display language.</summary>
    public static LocalServiceState ParseState(string scQueryOutput) =>
        StateLine().Match(scQueryOutput) is { Success: true } m ? (LocalServiceState)int.Parse(m.Groups["code"].Value, CultureInfo.InvariantCulture) : LocalServiceState.Unknown;

    public static LocalServiceState QueryState(string serviceName)
    {
        if (!IsValidServiceName(serviceName)) return LocalServiceState.Unknown;
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("query"); info.ArgumentList.Add(serviceName);
            using var process = Process.Start(info);
            if (process is null) return LocalServiceState.Unknown;
            string output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) ? ParseState(output) : LocalServiceState.Unknown;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return LocalServiceState.Unknown; }
    }

    // Windows service names cannot contain slashes; this narrower set also keeps the name safe inside a single-quoted PowerShell string.
    public static bool IsValidServiceName(string? name) => name is { Length: > 0 and <= 256 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');

    /// <summary>Elevated Windows PowerShell running one service cmdlet; the cmdlets wait until the service reaches the new state.</summary>
    public static ProcessStartInfo CreateControlStartInfo(string serviceName, LocalServiceAction action)
    {
        if (!IsValidServiceName(serviceName)) throw new ArgumentException("Invalid service name.", nameof(serviceName));
        string cmdlet = action switch { LocalServiceAction.Start => "Start-Service", LocalServiceAction.Stop => "Stop-Service", _ => "Restart-Service" };
        string force = action == LocalServiceAction.Start ? "" : " -Force";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference = 'Stop'; {cmdlet} -Name '{serviceName}'{force}\""
        };
        return info;
    }

    /// <summary>Runs the action elevated and returns the service state afterwards. Throws OperationCanceledException when the UAC prompt is declined.</summary>
    public static async Task<LocalServiceState> ControlAsync(string serviceName, LocalServiceAction action, CancellationToken token = default)
    {
        Process? process;
        try { process = Process.Start(CreateControlStartInfo(serviceName, action)); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("The administrator prompt was declined.", ex); } // ERROR_CANCELLED
        if (process is null) throw new InvalidOperationException("The service command did not start.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var state = await Task.Run(() => QueryState(serviceName), token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"The service command failed (exit code {process.ExitCode}); the service is now {Describe(state)}.");
            return state;
        }
    }

    public static string Describe(LocalServiceState state) => state switch
    {
        LocalServiceState.Running => "running", LocalServiceState.Stopped => "stopped", LocalServiceState.StartPending => "starting", LocalServiceState.StopPending => "stopping",
        LocalServiceState.Paused => "paused", LocalServiceState.Unknown => "in an unknown state", _ => state.ToString()
    };
}
