using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>Global handling of unexpected errors (PGMS-081): daily log, masking, retention, the dialog and its choices.</summary>
internal static class CrashReportChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string originalDirectory = CrashReporter.LogDirectory;
        var originalPrompt = CrashReporter.Prompt;
        var originalShutdown = CrashReporter.Shutdown;
        string folder = Path.Combine(Path.GetTempPath(), "pgms-crash-" + Guid.NewGuid().ToString("N"));
        CrashReporter.LogDirectory = folder;
        try
        {
            check(originalDirectory.EndsWith(Path.Combine("logs"), StringComparison.OrdinalIgnoreCase) && originalDirectory.StartsWith(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY")!, StringComparison.OrdinalIgnoreCase), "Logs live in the logs folder of the state directory");

            Exception Thrown(string message) { try { throw new InvalidOperationException(message); } catch (InvalidOperationException e) { return e; } }
            var error = Thrown("Host=db;Username=app;Password=s3cret;Database=x and pwd = 'abc def', PGPASSWORD=xyz");
            string report = CrashReporter.Report("Test context", error, new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(-3)));
            check(report.Contains("2026-11-17 10:00:00.000 -03:00  Test context", StringComparison.Ordinal) && report.Contains("PGMS: ", StringComparison.Ordinal) && report.Contains("Windows: ", StringComparison.Ordinal) && report.Contains(".NET: ", StringComparison.Ordinal), "The report has the time, context, version, Windows and .NET");
            check(report.Contains("System.InvalidOperationException", StringComparison.Ordinal) && report.Contains(nameof(CrashReportChecks), StringComparison.Ordinal), "The report has the exception type and stack");
            check(!report.Contains("s3cret", StringComparison.Ordinal) && !report.Contains("abc def", StringComparison.Ordinal) && !report.Contains("xyz", StringComparison.Ordinal) && report.Contains("Password=***", StringComparison.Ordinal) && report.Contains("Username=app", StringComparison.Ordinal), "Password values are masked and the rest is kept");
            check(CrashReporter.Mask("no secrets here") == "no secrets here", "Text without passwords is unchanged");

            string? file = CrashReporter.Write("First", error);
            CrashReporter.Write("Second", Thrown("other"));
            check(file == Path.Combine(folder, $"pgms-{DateTime.Now:yyyy-MM-dd}.log") && File.Exists(file), "The log goes to the daily file");
            string logged = File.ReadAllText(file!);
            check(logged.Contains("  First", StringComparison.Ordinal) && logged.Contains("  Second", StringComparison.Ordinal) && !logged.Contains("s3cret", StringComparison.Ordinal), "Reports are appended and masked");

            string old = Path.Combine(folder, "pgms-2026-09-01.log"), recent = Path.Combine(folder, "pgms-2026-11-01.log"), other = Path.Combine(folder, "notes.txt");
            File.WriteAllText(old, "old"); File.WriteAllText(recent, "recent"); File.WriteAllText(other, "keep");
            CrashReporter.Prune(new DateTime(2026, 11, 17));
            check(!File.Exists(old) && File.Exists(recent) && File.Exists(other) && File.Exists(file), "Logs older than 30 days are removed and other files kept");

            CrashReporter.LogDirectory = Path.Combine(folder, "missing\0bad");
            check(CrashReporter.Write("Unwritable", error) is null, "A log that cannot be written does not throw");
            CrashReporter.LogDirectory = folder;

            var prompts = new List<string?>();
            int shutdowns = 0;
            CrashReporter.Shutdown = () => shutdowns++;
            CrashReporter.Prompt = (e, log) => { prompts.Add(log); return CrashReporter.Choice.Continue; };
            check(CrashReporter.HandleInterfaceError(Thrown("continue")) && prompts.Count == 1 && prompts[0] == file && shutdowns == 0, "Continue handles the error without closing");
            CrashReporter.Prompt = (_, _) => CrashReporter.Choice.Close;
            check(CrashReporter.HandleInterfaceError(Thrown("close")) && shutdowns == 1, "Close PGMS shuts the application down");

            int nested = 0;
            CrashReporter.Prompt = (_, _) => { nested++; CrashReporter.HandleInterfaceError(Thrown("while the dialog is open")); return CrashReporter.Choice.Continue; };
            CrashReporter.HandleInterfaceError(Thrown("outer"));
            check(nested == 1 && File.ReadAllText(file!).Contains("while the dialog is open", StringComparison.Ordinal), "An error while the dialog is open is only logged");
            CrashReporter.Prompt = (_, _) => throw new InvalidOperationException("dialog failed");
            check(CrashReporter.HandleInterfaceError(Thrown("dialog breaks")) && File.ReadAllText(file!).Contains("The error dialog failed", StringComparison.Ordinal), "A failing dialog is logged and the error still handled");
            CrashReporter.Prompt = (_, _) => CrashReporter.Choice.Continue;
            check(CrashReporter.HandleInterfaceError(Thrown("after")), "The dialog can be shown again after the previous one closed");

            CrashReporter.HandleFatalError(Thrown("background"), terminating: false);
            check(File.ReadAllText(file!).Contains("Unhandled error on a background thread", StringComparison.Ordinal), "Background errors are logged");

            var window = new CrashWindow(error, file) { ShowInTaskbar = false };
            var opened = new List<string>(); var copied = new List<string>();
            window.OpenTarget = opened.Add; window.CopyText = copied.Add;
            window.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                check(window.Title == "PGMS - Unexpected Error" && window.Summary.Text.StartsWith("InvalidOperationException: ", StringComparison.Ordinal) && !window.Summary.Text.Contains("s3cret", StringComparison.Ordinal), "The dialog names the error with passwords masked");
                check(window.LogLocation.Text.Contains(file!, StringComparison.Ordinal) && window.Details.IsReadOnly && !window.Details.Text.Contains("s3cret", StringComparison.Ordinal) && window.Details.Text.Contains("System.InvalidOperationException", StringComparison.Ordinal), "The dialog shows the log path and the masked details");
                check(window.ContinueButton.IsDefault && window.ContinueButton.IsCancel && window.OpenLogButton.IsEnabled, "Continue is the default and Escape continues");
                window.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(copied.SequenceEqual([window.Details.Text]) && window.Status.Text == "The details were copied.", "Copy Details copies the details");
                window.OpenLogButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.ReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(opened.SequenceEqual([folder, CrashWindow.IssuesAddress]), "Open Log Folder and Report Issue open their targets");
                window.OpenTarget = _ => throw new InvalidOperationException("no browser");
                window.ReportButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(window.Status.Text.StartsWith("Could not open it", StringComparison.Ordinal) && window.IsVisible, "A target that cannot open is reported in the dialog");
                window.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(window.Choice == CrashReporter.Choice.Close && !window.IsVisible, "Close PGMS returns the close choice");
            }
            finally { if (window.IsVisible) window.Close(); }

            var unsaved = new CrashWindow(error, null) { ShowInTaskbar = false };
            check(!unsaved.OpenLogButton.IsEnabled && unsaved.LogLocation.Text.StartsWith("The details could not be saved", StringComparison.Ordinal) && unsaved.Choice == CrashReporter.Choice.Continue, "Without a log the dialog says so and continues by default");
            unsaved.Close();
        }
        finally
        {
            CrashReporter.LogDirectory = originalDirectory;
            CrashReporter.Prompt = originalPrompt;
            CrashReporter.Shutdown = originalShutdown;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
