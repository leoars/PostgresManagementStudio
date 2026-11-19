using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

public static class SqlBookmarkCommands
{
    public static readonly RoutedUICommand ToggleBookmark=new("Toggle Bookmark",nameof(ToggleBookmark),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand PreviousBookmark=new("Previous Bookmark",nameof(PreviousBookmark),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand NextBookmark=new("Next Bookmark",nameof(NextBookmark),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand ClearBookmarks=new("Clear Bookmarks",nameof(ClearBookmarks),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand PreviousBookmarkInDocument=new("Previous Bookmark In Document",nameof(PreviousBookmarkInDocument),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand NextBookmarkInDocument=new("Next Bookmark In Document",nameof(NextBookmarkInDocument),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand ClearBookmarksInDocument=new("Clear All Bookmarks In Document",nameof(ClearBookmarksInDocument),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand ToggleOutliningExpansion=new("Toggle Outlining Expansion",nameof(ToggleOutliningExpansion),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand ToggleAllOutlining=new("Toggle All Outlining",nameof(ToggleAllOutlining),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand StopOutlining=new("Stop Outlining",nameof(StopOutlining),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand StartOutlining=new("Start Automatic Outlining",nameof(StartOutlining),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand NextIssue=new("Go To Next Issue in File",nameof(NextIssue),typeof(SqlBookmarkCommands));
    public static readonly RoutedUICommand PreviousIssue=new("Go To Previous Issue in File",nameof(PreviousIssue),typeof(SqlBookmarkCommands));
    /// <summary>Commands of one query (the main window forwards them to the active query); Clear Bookmarks is the main window's, for every query.</summary>
    internal static readonly IReadOnlyList<(RoutedUICommand Command,Action<QueryView> Run,Func<QueryView,bool> CanRun)> ViewCommands=
    [
        (ToggleBookmark,q=>q.ToggleBookmark(),_=>true),
        (PreviousBookmark,q=>q.GoToBookmark(false),q=>q.BookmarkCount>0),
        (NextBookmark,q=>q.GoToBookmark(true),q=>q.BookmarkCount>0),
        (PreviousBookmarkInDocument,q=>q.GoToBookmark(false),q=>q.BookmarkCount>0),
        (NextBookmarkInDocument,q=>q.GoToBookmark(true),q=>q.BookmarkCount>0),
        (ClearBookmarksInDocument,q=>q.ClearBookmarks(),q=>q.BookmarkCount>0),
        (ToggleOutliningExpansion,q=>q.ToggleOutliningExpansion(),q=>q.OutliningEnabled),
        (ToggleAllOutlining,q=>q.ToggleAllOutlining(),q=>q.OutliningEnabled),
        (StopOutlining,q=>q.OutliningEnabled=false,q=>q.OutliningEnabled),
        (StartOutlining,q=>q.OutliningEnabled=true,q=>!q.OutliningEnabled),
        (NextIssue,q=>q.GoToIssue(),q=>q.HasIssue),
        (PreviousIssue,q=>q.GoToIssue(),q=>q.HasIssue),
    ];
}

/// <summary>Bookmarks and outlining of the SQL editor (PGMS-057), modelled on SSMS 22.11 as piloted in M1080.
/// Bookmarks belong to the query tab and follow the edits; outlining folds each statement of more than one line and each multi-line block comment.</summary>
public partial class QueryView
{
    private readonly List<TextAnchor> bookmarks = [];
    private BookmarkMargin? bookmarkMargin;
    private FoldingManager? foldingManager;
    private DispatcherTimer? foldingTimer;
    internal const int MaximumOutliningCharacters = SqlFormatter.MaximumCharacters;

    internal int BookmarkCount { get { bookmarks.RemoveAll(a => a.IsDeleted); return bookmarks.Count; } }
    internal IReadOnlyList<int> BookmarkLines => [.. bookmarks.Where(a => !a.IsDeleted).Select(a => a.Line).Distinct().Order()];
    internal FoldingManager? Folding => foldingManager;

    private void ConfigureBookmarks()
    {
        foreach (var (command, run, canRun) in SqlBookmarkCommands.ViewCommands)
            CommandBindings.Add(new CommandBinding(command, (_, e) => { run(this); e.Handled = true; }, (_, e) => { e.CanExecute = canRun(this); e.Handled = true; }));
        bookmarkMargin = new BookmarkMargin(this);
        Editor.TextArea.LeftMargins.Insert(0, bookmarkMargin);
        foldingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { foldingTimer!.Stop(); UpdateFoldings(); }, Dispatcher);
        foldingTimer.Stop();
        Editor.TextChanged += (_, _) => { if (foldingManager is not null) { foldingTimer.Stop(); foldingTimer.Start(); } };
        // A folding manager belongs to one document: a new document (open, recovery) gets a new manager, and bookmarks start empty.
        Editor.DocumentChanged += (_, _) =>
        {
            bookmarks.Clear(); bookmarkMargin?.InvalidateVisual();
            if (foldingManager is null) return;
            FoldingManager.Uninstall(foldingManager);
            foldingManager = FoldingManager.Install(Editor.TextArea);
            UpdateFoldings();
        };
        OutliningEnabled = true;
    }

    /// <summary>Edit ▸ Outlining ▸ Stop Outlining / Start Automatic Outlining.</summary>
    public bool OutliningEnabled
    {
        get => foldingManager is not null;
        set
        {
            if (value == OutliningEnabled) return;
            if (value) { foldingManager = FoldingManager.Install(Editor.TextArea); UpdateFoldings(); }
            else { FoldingManager.Uninstall(foldingManager!); foldingManager = null; }
        }
    }

    internal void UpdateFoldings()
    {
        if (foldingManager is null) return;
        if (Editor.Document.TextLength > MaximumOutliningCharacters) { foldingManager.Clear(); return; }
        if (SqlFolding.TakePrepared(Editor.Document) is { } ready) { foldingManager.UpdateFoldings(ready, -1); return; }
        try { foldingManager.UpdateFoldings(SqlFolding.Create(Editor.Document), -1); }
        catch (FormatException) { } // An unclosed string or comment keeps the previous outline until the text reads again.
    }

    internal void ToggleBookmark()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        bookmarks.RemoveAll(a => a.IsDeleted);
        if (bookmarks.RemoveAll(a => a.Line == line.LineNumber) == 0)
        {
            var anchor = Editor.Document.CreateAnchor(line.Offset); anchor.MovementType = AnchorMovementType.BeforeInsertion; bookmarks.Add(anchor);
        }
        bookmarkMargin?.InvalidateVisual();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Next or previous bookmarked line, going around the end of the document (as in SSMS).</summary>
    internal void GoToBookmark(bool next)
    {
        var lines = BookmarkLines;
        if (lines.Count == 0) return;
        int current = Editor.TextArea.Caret.Line;
        int target = next ? lines.FirstOrDefault(l => l > current, lines[0]) : lines.LastOrDefault(l => l < current, lines[^1]);
        var line = Editor.Document.GetLineByNumber(target);
        foreach (var section in foldingManager?.GetFoldingsContaining(line.Offset) ?? []) section.IsFolded = false;
        Editor.Select(line.Offset, 0); Editor.CaretOffset = line.Offset;
        Editor.ScrollToLine(target);
        Editor.Focus();
    }

    internal void ClearBookmarks() { bookmarks.Clear(); bookmarkMargin?.InvalidateVisual(); CommandManager.InvalidateRequerySuggested(); }

    /// <summary>Toggle Outlining Expansion: the innermost region at the caret line.</summary>
    internal void ToggleOutliningExpansion()
    {
        if (foldingManager is null) return;
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var section = foldingManager.AllFoldings.Where(f => f.StartOffset <= line.EndOffset && f.EndOffset >= line.Offset).OrderBy(f => f.Length).FirstOrDefault();
        if (section is not null) section.IsFolded = !section.IsFolded;
    }

    /// <summary>Toggle All Outlining: collapses every region, or expands them all when they already are.</summary>
    internal void ToggleAllOutlining()
    {
        if (foldingManager is null) return;
        UpdateFoldings();
        bool fold = foldingManager.AllFoldings.Any(f => !f.IsFolded);
        foreach (var section in foldingManager.AllFoldings) section.IsFolded = fold;
    }

    /// <summary>The bookmark glyph in the left margin, like the SSMS one.</summary>
    private sealed class BookmarkMargin(QueryView view) : AbstractMargin
    {
        private static readonly Pen Outline = CreatePen();
        private static Pen CreatePen() { var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), 1); pen.Freeze(); return pen; }
        protected override Size MeasureOverride(Size availableSize) => new(16, 0);
        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += Redraw;
        }
        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (TextView is not { VisualLinesValid: true } textView) return;
            var lines = view.BookmarkLines.ToHashSet();
            if (lines.Count == 0) return;
            foreach (var visual in textView.VisualLines)
            {
                int number = visual.FirstDocumentLine.LineNumber;
                if (!lines.Contains(number)) continue;
                double top = visual.VisualTop - textView.VerticalOffset, height = Math.Min(visual.Height, 16);
                var geometry = Geometry.Parse($"M4,{top + 2:0.##} L12,{top + 2:0.##} 12,{top + height - 2:0.##} 8,{top + height - 5:0.##} 4,{top + height - 2:0.##} Z");
                context.DrawGeometry(Brushes.White, Outline, geometry);
            }
        }
    }
}

/// <summary>Regions of the SQL outline: each statement of more than one line (up to its semicolon) and each multi-line block comment.</summary>
internal static class SqlFolding
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextDocument, Tuple<ITextSourceVersion, List<NewFolding>>> prepared = [];

    /// <summary>Reads the outline of a document being prepared on a worker thread; <see cref="TakePrepared"/> returns it once, if the text did not change.</summary>
    internal static void Prepare(TextDocument document)
    {
        if (document.TextLength > QueryView.MaximumOutliningCharacters) return;
        try { prepared.AddOrUpdate(document, Tuple.Create(document.Version, Create(document).ToList())); }
        catch (FormatException) { }
    }

    internal static List<NewFolding>? TakePrepared(TextDocument document)
    {
        if (!prepared.TryGetValue(document, out var entry)) return null;
        prepared.Remove(document);
        return entry.Item1.BelongsToSameDocumentAs(document.Version) && entry.Item1.CompareAge(document.Version) == 0 ? entry.Item2 : null;
    }

    internal static IEnumerable<NewFolding> Create(TextDocument document)
    {
        string text = document.Text;
        var tokens = SqlFormatter.Tokenize(text);
        var result = new List<NewFolding>();
        int depth = 0, start = -1, lastEnd = 0;
        void Close(int end)
        {
            if (start >= 0 && document.GetLineByOffset(start).LineNumber < document.GetLineByOffset(end).LineNumber)
                result.Add(new NewFolding(start, end) { Name = "..." });
            start = -1;
        }
        foreach (var token in tokens)
        {
            int end = token.Start + token.Text.Length;
            if (token.Kind == SqlTokenKind.BlockComment)
            {
                if (document.GetLineByOffset(token.Start).LineNumber < document.GetLineByOffset(end).LineNumber) result.Add(new NewFolding(token.Start, end) { Name = "/* ... */" });
                continue;
            }
            if (token.Kind == SqlTokenKind.LineComment) continue;
            if (start < 0) start = token.Start;
            if (token.Kind is SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket) depth++;
            else if (token.Kind is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket) depth = Math.Max(0, depth - 1);
            lastEnd = end;
            if (token.Kind == SqlTokenKind.Semicolon && depth == 0) Close(end);
        }
        Close(lastEnd);
        return result.OrderBy(f => f.StartOffset);
    }
}
