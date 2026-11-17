using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PGMS.Desktop;

/// <summary>
/// Global handling of unexpected errors (PGMS-081, beta preparation). Every exception that nothing else caught is written to
/// a daily log under the state directory (<c>%LOCALAPPDATA%\PGMS\logs</c>, or <c>logs</c> under <c>PGMS_STATE_DIRECTORY</c>).
/// Errors on the interface thread show <see cref="CrashWindow"/> and let the user continue or close PGMS; errors on other
/// threads end the process, so they are logged and announced as well as possible; unobserved task errors are only logged.
/// The log has the version, Windows, .NET and the exception; password values that appear in key=value text are masked.
/// Query text and result values are never added by PGMS, but an exception message can quote what the server sent.
/// </summary>
internal static partial class CrashReporter
{
    internal enum Choice { Continue, Close }

    internal const int KeptLogDays = 30;
    private static readonly object Gate = new();
    private static bool showing;
    private static bool installed;

    /// <summary>Folder of the daily logs; tests may replace it.</summary>
    internal static string LogDirectory { get; set; } = Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "logs");

    /// <summary>Asks the user what to do after an interface error; tests replace it.</summary>
    internal static Func<Exception, string?, Choice> Prompt { get; set; } = (error, logFile) => CrashWindow.Ask(error, logFile);

    /// <summary>Ends PGMS after the user chose Close or after a fatal error; tests replace it.</summary>
    internal static Action Shutdown { get; set; } = () => Application.Current?.Shutdown(1);

    internal static void Install(Application application)
    {
        if (installed) return;
        installed = true;
        application.DispatcherUnhandledException += (_, e) => e.Handled = HandleInterfaceError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatalError(e.ExceptionObject as Exception ?? new InvalidOperationException(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture)), e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("Unobserved task error", e.Exception); e.SetObserved(); };
        Prune(DateTime.Now);
    }

    /// <summary>Logs an error raised on the interface thread and asks the user; returns true when the error was handled.</summary>
    internal static bool HandleInterfaceError(Exception error)
    {
        string? logFile = Write("Unhandled error on the interface thread", error);
        lock (Gate)
        {
            // An error raised while the dialog is open (for example by a timer) is only logged, so dialogs do not pile up.
            if (showing) return true;
            showing = true;
        }
        try
        {
            if (Prompt(error, logFile) == Choice.Close) Shutdown();
        }
        catch (Exception promptError)
        {
            Write("The error dialog failed", promptError);
        }
        finally
        {
            lock (Gate) showing = false;
        }
        return true;
    }

    /// <summary>Logs an error from another thread; when the process is ending, tries to tell the user where the log is.</summary>
    internal static void HandleFatalError(Exception error, bool terminating)
    {
        string? logFile = Write(terminating ? "Fatal error; PGMS is closing" : "Unhandled error on a background thread", error);
        if (!terminating) return;
        try
        {
            MessageBox.Show($"PGMS hit an unexpected error and has to close.{Environment.NewLine}{Environment.NewLine}{Mask(error.Message)}{Environment.NewLine}{Environment.NewLine}" + (logFile is null ? "The details could not be saved." : $"The details were saved to:{Environment.NewLine}{logFile}"), "PGMS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Nothing else can be done while the process ends.
        }
    }

    /// <summary>Appends one report to today's log; returns its path, or null when it could not be written.</summary>
    internal static string? Write(string context, Exception error)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string file = Path.Combine(LogDirectory, $"pgms-{DateTime.Now:yyyy-MM-dd}.log");
            lock (Gate) File.AppendAllText(file, Report(context, error, DateTimeOffset.Now), new UTF8Encoding(false));
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The text of one report.</summary>
    internal static string Report(string context, Exception error, DateTimeOffset when)
    {
        var text = new StringBuilder();
        text.AppendLine(new string('=', 72));
        text.AppendLine(CultureInfo.InvariantCulture, $"{when:yyyy-MM-dd HH:mm:ss.fff zzz}  {context}");
        text.AppendLine(CultureInfo.InvariantCulture, $"PGMS: {VersionLine()}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.VersionString} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process)");
        text.AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Culture: {CultureInfo.CurrentUICulture.Name}");
        text.AppendLine(Mask(error.ToString()));
        return text.ToString();
    }

    internal static string VersionLine()
    {
        string? informational = typeof(CrashReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{AboutWindow.VersionText(informational)} ({informational ?? "unknown"}, {BuildInfo.Milestone})";
    }

    /// <summary>Masks the value of password-like keys (connection strings, environment-style text).</summary>
    internal static string Mask(string text) => PasswordPattern().Replace(text, m => m.Groups["key"].Value + "***");

    /// <summary>Removes daily logs older than <see cref="KeptLogDays"/> days.</summary>
    internal static void Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "pgms-*.log"))
            {
                string stamp = Path.GetFileNameWithoutExtension(file)["pgms-".Length..];
                if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) && day < now.Date.AddDays(-KeptLogDays))
                    File.Delete(file);
            }
        }
        catch
        {
            // A log that cannot be removed now is tried again on the next start.
        }
    }

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|pgpassword|key\s*password|sslpassword)\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^;\s,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();
}
