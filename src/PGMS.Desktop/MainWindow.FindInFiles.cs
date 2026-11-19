using System.IO;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>Edit ▸ Find and Replace ▸ Find in Files / Replace in Files and the Find Results pane (PGMS-066, M1091).</summary>
public partial class MainWindow
{
    private const string FindResultsId = "findresults";
    public static RoutedUICommand ReplaceInFilesCommand { get; } = new("Replace in Files", "ReplaceInFiles", typeof(MainWindow), [new KeyGesture(Key.H, ModifierKeys.Control | ModifierKeys.Shift)]);
    private FindInFilesWindow? findInFilesWindow;
    private FindResultsView? findResults;
    /// <summary>Confirms Replace All; replaced by tests.</summary>
    internal Func<string, bool>? ReplaceAllConfirm { get; set; }

    internal FindResultsView FindResults => findResults ??= new FindResultsView { OpenMatch = OpenFindMatchAsync };

    internal FindInFilesWindow FindInFilesWindow
    {
        get
        {
            if (findInFilesWindow is { } open) return open;
            var window = new FindInFilesWindow
            {
                Owner = this,
                FindAllRequested = r => Guarded(() => FindAllInFilesAsync(r)),
                ReplaceAllRequested = (r, text) => Guarded(() => ReplaceAllInFilesAsync(r, text))
            };
            window.Closed += (_, _) => findInFilesWindow = null;
            return findInFilesWindow = window;
        }
    }

    private async Task Guarded(Func<Task> search)
    {
        try { await search(); }
        catch (Exception ex) when (ex is System.Text.RegularExpressions.RegexMatchTimeoutException or IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError(ex is System.Text.RegularExpressions.RegexMatchTimeoutException ? new InvalidOperationException("The regular expression took too long on one of the files; simplify it.", ex) : ex); }
    }

    private void ConfigureFindInFilesCommands() =>
        CommandBindings.Add(new CommandBinding(ReplaceInFilesCommand, (_, _) => ShowFindInFiles(true)));

    private void FindInFilesClicked(object sender, RoutedEventArgs e) => ShowFindInFiles(false);

    /// <summary>Opens the Find and Replace window on one of its tabs, with the selection or the word at the caret, as SSMS does.</summary>
    internal FindInFilesWindow ShowFindInFiles(bool replace)
    {
        var window = FindInFilesWindow;
        string? lookIn = ActiveQuery?.FilePath is { } file ? Path.GetDirectoryName(file) : FindInFilesWindow.AllOpenDocuments;
        window.Prepare(replace, ActiveQuery is { } q ? SearchTextAtCaret(q) : null, lookIn);
        if (IsVisible) { window.Show(); window.Activate(); }
        return window;
    }

    private static string? SearchTextAtCaret(QueryView query)
    {
        var editor = query.Editor;
        if (editor.SelectionLength > 0) return editor.SelectedText.Contains('\n') ? null : editor.SelectedText;
        string text = editor.Text; int caret = editor.CaretOffset, start = caret, end = caret;
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        while (start > 0 && Word(text[start - 1])) start--;
        while (end < text.Length && Word(text[end])) end++;
        return end > start ? text[start..end] : null;
    }

    private static string Describe(string verb, FileSearchRequest request)
    {
        var o = request.Options;
        string text = $"{verb} \"{o.Pattern}\"" + (o.MatchCase ? ", Match case" : "") + (o.WholeWord ? ", Whole word" : "") + (o.UseRegex ? ", Regular expressions" : "");
        return text + request.Scope switch
        {
            FileSearchScope.AllOpenDocuments => ", All open documents",
            FileSearchScope.CurrentDocument => ", Current document",
            _ => (request.Subfolders ? ", Subfolders" : "") + $", \"{request.Folder}\", \"{string.Join(";", FileSearch.ParseTypes(request.Types))}\""
        };
    }

    private string DisplayName(QueryView query) => query.FilePath is { } path ? Path.GetFileName(path) : documents[query].Title.TrimEnd('*');

    /// <summary>The texts the request covers: open documents by their editor text, folder files from disk unless a tab has them open (then its unsaved text).</summary>
    private async Task<(List<(string Path, string Name, string Text, QueryView? Query)> Sources, int Unreadable)> GatherAsync(FileSearchRequest request)
    {
        var sources = new List<(string, string, string, QueryView?)>();
        if (request.Scope != FileSearchScope.Folder)
        {
            // Restored tabs not shown yet are mounted so their text is searched and Replace All changes their editor (M1133).
            if (request.Scope == FileSearchScope.AllOpenDocuments) MountDeferredQueries();
            var queries = request.Scope == FileSearchScope.CurrentDocument ? (ActiveQuery is { } a ? [a] : []) : QueriesInCreationOrder();
            foreach (var q in queries) sources.Add((q.FilePath ?? "", DisplayName(q), q.Editor.Text, q));
            return (sources, 0);
        }
        var open = documents.Keys.Where(q => q.FilePath is not null).GroupBy(q => Path.GetFullPath(q.FilePath!), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (Query: g.First(), Text: g.First().Editor.Text), StringComparer.OrdinalIgnoreCase);
        string folder = request.Folder!;
        int unreadable = 0;
        var found = await Task.Run(() =>
        {
            var list = new List<(string, string, string, QueryView?)>();
            foreach (var file in FileSearch.EnumerateFiles(folder, request.Types, request.Subfolders))
            {
                string name = Path.GetRelativePath(folder, file);
                if (open.TryGetValue(file, out var tab)) { list.Add((file, name, tab.Text, tab.Query)); continue; }
                if (FileSearch.ReadText(file) is { } text) list.Add((file, name, text, null)); else unreadable++;
            }
            return list;
        });
        sources.AddRange(found);
        return (sources, unreadable);
    }

    private static string? UnreadableNote(int unreadable) => unreadable == 0 ? null : $"{unreadable} file(s) skipped: binary, larger than 16 MiB, not UTF-8/16/32 or not readable.";

    /// <summary>Find All: fills the Find Results pane and shows it.</summary>
    internal async Task<IReadOnlyList<FileMatch>> FindAllInFilesAsync(FileSearchRequest request)
    {
        var regex = request.Options.ToRegex();
        Status.Text = "Searching…";
        var (sources, unreadable) = await GatherAsync(request);
        var matches = new List<FileMatch>();
        bool truncated = false;
        await Task.Run(() =>
        {
            foreach (var (path, name, text, query) in sources)
            {
                foreach (var m in FileSearch.Matches(path, name, text, regex))
                {
                    if (matches.Count >= FileSearch.MaximumMatches) { truncated = true; return; }
                    matches.Add(m with { Query = query });
                }
            }
        });
        FindResults.Show(Describe("Find all", request), matches, sources.Count, truncated, UnreadableNote(unreadable));
        ShowFindResults();
        Status.Text = matches.Count == 0 ? "The text was not found." : $"{matches.Count} matching line(s) found.";
        return matches;
    }

    /// <summary>Replace All: open documents change in their editor (one Undo step each); closed files are rewritten on disk with their encoding, after confirmation.</summary>
    internal async Task<int> ReplaceAllInFilesAsync(FileSearchRequest request, string replacement)
    {
        var regex = request.Options.ToRegex();
        var (sources, unreadable) = await GatherAsync(request);
        var counted = sources.Select(s => (Source: s, Count: FileSearch.Matches(s.Path, s.Name, s.Text, regex).Count())).Where(s => s.Count > 0).ToList();
        if (counted.Count == 0) { FindResults.ShowReplace(Describe("Replace all", request), [], sources.Count, UnreadableNote(unreadable)); ShowFindResults(); Status.Text = "The text was not found."; return 0; }
        int closed = counted.Count(c => c.Source.Query is null), total = counted.Sum(c => c.Count);
        string question = $"Replace {total} occurrence(s) of \"{request.Options.Pattern}\" in {counted.Count} file(s)?"
            + (closed > 0 ? $"\n\n{closed} file(s) not open in a tab are changed on disk; that cannot be undone." : "")
            + (counted.Count > closed ? "\nOpen documents change in their editor (Ctrl+Z undoes it) and stay unsaved." : "");
        bool yes = ReplaceAllConfirm?.Invoke(question) ?? LocalizedMessageBox.Show(this, question, "Replace All", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!yes) { Status.Text = "Nothing was replaced."; return 0; }
        var done = new List<(string, string, int)>();
        var failures = new List<string>();
        foreach (var ((path, name, _, query), _) in counted)
        {
            if (query is not null && documents.ContainsKey(query))
            {
                var document = query.Editor.Document;
                var hits = regex.Matches(document.Text).Where(m => m.Length > 0).ToArray();
                document.BeginUpdate();
                try { foreach (var m in hits.Reverse()) document.Replace(m.Index, m.Length, request.Options.UseRegex ? m.Result(replacement) : replacement); }
                finally { document.EndUpdate(); }
                done.Add((name, path, hits.Length));
                continue;
            }
            try
            {
                int count = await Task.Run(() =>
                {
                    var snapshot = SqlFileStore.Read(path);
                    var (text, n) = FileSearch.Replace(snapshot.Text, regex, replacement, request.Options.UseRegex);
                    if (n > 0) SqlFileStore.Write(path, text, snapshot.Format, snapshot.Version);
                    return n;
                });
                done.Add((name, path, count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { failures.Add($"{name}: {ex.Message}"); }
        }
        string? note = string.Join("  ", new[] { failures.Count > 0 ? $"Not changed: {string.Join("; ", failures)}" : null, UnreadableNote(unreadable) }.Where(n => n is not null));
        FindResults.ShowReplace(Describe("Replace all", request) + $", \"{replacement}\"", done, sources.Count, note.Length == 0 ? null : note);
        ShowFindResults();
        int replaced = done.Sum(d => d.Item3);
        Status.Text = $"{replaced} occurrence(s) replaced" + (failures.Count > 0 ? $"; {failures.Count} file(s) could not be changed." : ".");
        if (failures.Count > 0) ShowError(new IOException("Some files were not changed:\n" + string.Join("\n", failures)));
        return replaced;
    }

    /// <summary>Docks Find Results at the bottom, as SSMS does.</summary>
    internal void ShowFindResults()
    {
        var anchorable = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == FindResultsId);
        if (anchorable is null)
        {
            anchorable = new LayoutAnchorable { Title = "Find Results", ContentId = FindResultsId, Content = FindResults, CanClose = false, CanHide = true, FloatingHeight = 200 };
            anchorable.AddToLayout(DockManager, AnchorableShowStrategy.Bottom);
        }
        else if (anchorable.Content is null) anchorable.Content = FindResults;
        anchorable.Show();
    }

    /// <summary>Opens the file of a match (or activates its tab) and selects the match; a changed text keeps the line and column.</summary>
    internal async Task OpenFindMatchAsync(FileMatch match)
    {
        QueryView? query = match.Query is { } q && documents.ContainsKey(q) ? q : null;
        if (query is null && match.Path.Length > 0)
        {
            try { query = await OpenQueryFileAsync(match.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); return; }
        }
        if (query is null || !documents.TryGetValue(query, out var tab)) { Status.Text = "That document is no longer open."; return; }
        tab.IsActive = true;
        var document = query.Editor.Document;
        int lineNumber = Math.Clamp(match.Line, 1, document.LineCount);
        var line = document.GetLineByNumber(lineNumber);
        int offset = line.Offset + Math.Min(match.Column - 1, line.Length);
        int length = Math.Min(match.Length, document.TextLength - offset);
        query.Editor.Select(offset, length);
        query.Editor.ScrollTo(lineNumber, match.Column);
        query.Editor.Focus();
    }
}
