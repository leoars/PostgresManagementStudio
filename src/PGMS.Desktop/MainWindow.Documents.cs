using System.IO;
using System.Windows;

namespace PGMS.Desktop;

/// <summary>Environment ▸ Documents and Tabs and Windows (B1 review, M1141): what PGMS does with query files besides opening and saving them.</summary>
public partial class MainWindow
{
    /// <summary>Answers "reload the file changed outside PGMS?" for a tab with unsaved changes; null asks. The test runner answers no.</summary>
    internal static Func<QueryView, bool>? ReloadChangedPrompt { get; set; }
    /// <summary>Answers "normalize the line endings?" when a file with mixed line endings opens; null asks. The test runner answers no.</summary>
    internal static Func<string, bool>? NormalizeLineEndingsPrompt { get; set; }
    /// <summary>Answers "apply window layout?" from the menu; null asks. Tests replace it.</summary>
    internal Func<string, bool>? ApplyLayoutPrompt { get; set; }

    private bool checkingChangedFiles;

    protected override async void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (documentSettings.DetectChanges) await CheckChangedFilesAsync();
    }

    protected override async void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (documentSettings.SaveInBackground) await SaveInBackgroundAsync();
    }

    /// <summary>Detect when file is changed outside the environment: each open query file whose content changed on disk is reloaded when its tab has
    /// no unsaved changes (Reload modified files unless there are unsaved changes), or PGMS asks first. A file that disappeared is left alone.</summary>
    internal async Task<int> CheckChangedFilesAsync()
    {
        if (checkingChangedFiles || closingWindow || shuttingDown) return 0;
        checkingChangedFiles = true;
        int reloaded = 0;
        try
        {
            var files = documents.Keys.Where(q => q.FilePath is not null && q.FileVersion is not null && !IsSaving(q)).Select(q => (Query: q, Path: q.FilePath!, Version: q.FileVersion!)).ToList();
            if (files.Count == 0) return 0;
            var changed = await fileIo.RunAsync(() => files.Where(f => File.Exists(f.Path) && SafeVersion(f.Path) is { } now && now.Sha256 != f.Version.Sha256).ToList());
            foreach (var (query, path, version) in changed)
            {
                if (!documents.ContainsKey(query) || !Equals(query.FileVersion, version)) continue;
                bool reload = !query.IsDirty && documentSettings.ReloadUnmodified
                    || (ReloadChangedPrompt?.Invoke(query) ?? LocalizedMessageBox.Show(this, $"{path}\n\nThis file has been modified outside of the source editor. Do you want to reload it?" + (query.IsDirty ? "\n\nYour unsaved changes in PGMS will be lost." : ""),
                        "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes);
                if (!reload)
                {
                    // Asked once per change: the version on disk becomes the one the tab knows, and the tab is marked changed.
                    if (SafeVersion(path) is { } seen) { query.FileVersion = seen; query.IsDirty = true; }
                    continue;
                }
                try
                {
                    var snapshot = await SqlFileStore.ReadAsync(path, CancellationToken.None);
                    query.Editor.Document.Text = snapshot.Text;
                    query.FileFormat = snapshot.Format; query.FileVersion = snapshot.Version; query.IsDirty = false;
                    documents[query].Title = Path.GetFileName(path);
                    await ForgetRecoveryAsync(query);
                    reloaded++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Status.Text = "The file could not be reloaded: " + ex.Message; }
            }
            if (reloaded > 0) Status.Text = reloaded == 1 ? "1 file changed outside PGMS was reloaded." : $"{reloaded} files changed outside PGMS were reloaded.";
            return reloaded;
        }
        finally { checkingChangedFiles = false; }
    }

    private static SqlFileVersion? SafeVersion(string path)
    {
        try { return SqlFileStore.CaptureVersion(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Automatically save files when PGMS is in the background: query tabs with a file and unsaved changes are saved.</summary>
    internal async Task<int> SaveInBackgroundAsync()
    {
        if (closingWindow || shuttingDown) return 0;
        int saved = 0;
        foreach (var query in documents.Keys.Where(q => q.FilePath is not null && q.IsDirty && !IsSaving(q)).ToList())
        {
            try { await SaveQueryFileAsync(query, query.FilePath!, true); saved++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { Status.Text = "Background save failed: " + ex.Message; }
        }
        return saved;
    }

    /// <summary>Open file using directory of currently active document: File ▸ Open starts in the folder of the active query file.</summary>
    internal string? OpenDialogDirectory() =>
        documentSettings.OpenFromActiveDirectory && ActiveQuery?.FilePath is { } active && Path.GetDirectoryName(active) is { } folder && Directory.Exists(folder) ? folder : null;

    /// <summary>After a file opens: read-only files stay read-only unless editing them is allowed, and mixed line endings can be normalized to CRLF.</summary>
    private void AfterFileOpened(QueryView query, string path)
    {
        try
        {
            if (!documentSettings.AllowEditingReadOnly && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                query.Editor.IsReadOnly = true;
                Status.Text = Path.GetFileName(path) + " is read-only and opened without editing (Tools ▸ Options ▸ Environment ▸ Documents).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!documentSettings.CheckLineEndings || !HasMixedLineEndings(query.Editor.Text)) return;
        bool normalize = NormalizeLineEndingsPrompt?.Invoke(path) ?? LocalizedMessageBox.Show(this, $"{path}\n\nThe line endings in this file are not consistent. Do you want to normalize them to Windows (CRLF)?",
            "Normalize Line Endings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        if (!normalize) return;
        query.Editor.Document.Text = NormalizeLineEndings(query.Editor.Text);
        Status.Text = "Line endings normalized to CRLF; save the file to keep them.";
    }

    internal static bool HasMixedLineEndings(string text)
    {
        bool crlf = false, lf = false, cr = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf = true; i++; } else cr = true; }
            else if (text[i] == '\n') lf = true;
            if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1) return true;
        }
        return false;
    }

    internal static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>SQL Formatter ▸ Formatting ▸ Format on save (M1142): the whole document is formatted (one Undo step) before it is written; SQL that
    /// cannot be read is saved as it is.</summary>
    private void FormatOnSave(QueryView query)
    {
        if (!SqlFormatter.Settings.FormatOnSave || query.Editor.IsReadOnly || query.Editor.Document.TextLength > SqlFormatter.MaximumCharacters) return;
        try { query.Editor.Select(query.Editor.CaretOffset, 0); SqlTextCommands.FormatSql(query.Editor); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { Status.Text = "Saved without formatting: " + ex.Message; }
    }

    /// <summary>Save files with a specific encoding: every save uses the chosen encoding; off, the file keeps its own.</summary>
    private SqlFileFormat SaveFormat(QueryView query) => documentSettings.UseSaveEncoding ? documentSettings.SaveEncoding : query.FileFormat;

    /// <summary>Window ▸ Apply Window Layout from the menu asks first, as SSMS does, unless Tabs and Windows skips the confirmation.</summary>
    internal void ApplyWindowLayoutFromMenu(string name)
    {
        if (!EnvironmentSettings.Load().TabsSettings.SkipLayoutConfirmation)
        {
            bool apply = ApplyLayoutPrompt?.Invoke(name) ?? LocalizedMessageBox.Show(this, $"Apply the window layout \"{name}\"? It replaces the current arrangement of the tool windows.",
                "Apply Window Layout", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
            if (!apply) return;
        }
        ApplyWindowLayout(name);
    }
}
