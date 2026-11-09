using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>IntelliSense of the SQL editor (PGMS-035). Observed in SSMS 22.11 (docs/MARCO-M1051.md): Ctrl+Space and Ctrl+J open the list, typing filters it, Tab accepts, Esc closes,
/// Enter on a list where nothing was typed just starts a new line, and another character closes it without inserting. Names come from a cache read once per connection.</summary>
public partial class QueryView
{
    private bool intelliSenseEnabled = true;
    /// <summary>Query ▸ IntelliSense Enabled (Ctrl+B, Ctrl+I).</summary>
    public bool IntelliSenseEnabled
    {
        get => intelliSenseEnabled;
        set { intelliSenseEnabled = value; if (!value) completionWindow?.Close(); StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    private Task<SqlCompletionCatalog>? catalogLoad;
    private string? catalogConnection;
    private SqlCompletionWindow? completionWindow;
    internal CompletionWindow? OpenCompletionWindow => completionWindow;
    private long intelliSenseChordDeadline;

    private void ConfigureCompletion()
    {
        Editor.TextArea.TextEntering += (_, e) =>
        {
            // Only names go on filtering; punctuation or a space ends the list without inserting.
            if (completionWindow is not null && e.Text.Length > 0 && !(char.IsLetterOrDigit(e.Text[0]) || e.Text[0] is '_' or '$' or '"')) completionWindow.Close();
        };
        Editor.TextArea.TextEntered += async (_, e) => { if (e.Text == ".") await ShowCompletionAsync(automatic: true); };
        Editor.PreviewKeyDown += async (_, e) =>
        {
            if (ProcessIntelliSenseKey(e.Key, Keyboard.Modifiers, Environment.TickCount64)) { e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Space or Key.J) { e.Handled = true; await ShowCompletionAsync(); }
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.R) { e.Handled = true; RefreshLocalCache(); }
        };
        Editor.LostKeyboardFocus += (_, _) => intelliSenseChordDeadline = 0;
    }

    /// <summary>The Ctrl+B, Ctrl+I chord. Ctrl+I alone stays with Generate SQL with AI.</summary>
    internal bool ProcessIntelliSenseKey(Key key, ModifierKeys modifiers, long now)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return false;
        if (intelliSenseChordDeadline > 0)
        {
            bool valid = now <= intelliSenseChordDeadline;
            intelliSenseChordDeadline = 0;
            if (valid && modifiers == ModifierKeys.Control && key == Key.I) { IntelliSenseEnabled = !IntelliSenseEnabled; return true; }
        }
        if (key == Key.B && modifiers == ModifierKeys.Control) { intelliSenseChordDeadline = now + 2000; return true; }
        return false;
    }

    /// <summary>Edit ▸ IntelliSense ▸ Refresh Local Cache: the next list reads the catalog again.</summary>
    public void RefreshLocalCache()
    {
        catalogLoad = null;
        QueryStatus.Text = "IntelliSense cache will be read again.";
    }

    private Task<SqlCompletionCatalog> CatalogAsync(string connection)
    {
        if (catalogLoad is null || catalogConnection != connection) { catalogConnection = connection; catalogLoad = Task.Run(() => SqlCompletionCatalog.LoadAsync(connection)); }
        return catalogLoad;
    }

    /// <summary>List Members and Complete Word. An automatic request gives up silently when the text moved on while the catalog was being read.</summary>
    internal async Task ShowCompletionAsync(bool automatic = false)
    {
        if (!intelliSenseEnabled || completionWindow is not null) return;
        if (ConnectionString is not { } connection) { if (!automatic) QueryStatus.Text = "Connect this query to use IntelliSense."; return; }
        long revision = EditRevision;
        int caret = Editor.CaretOffset;
        SqlCompletionCatalog catalog;
        try { catalog = await CatalogAsync(connection); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            catalogLoad = null;
            QueryStatus.Text = "IntelliSense could not read the catalog: " + ex.Message;
            return;
        }
        if (revision != EditRevision || caret != Editor.CaretOffset || ConnectionString != connection || completionWindow is not null || !Editor.IsLoaded) return;
        if (SqlCompletionEngine.Complete(Editor.Text, caret, catalog) is not { Items.Count: > 0 } result) return;
        var window = new SqlCompletionWindow(Editor.TextArea) { StartOffset = result.ReplaceStart, EndOffset = caret, Width = result.Items.Any(i => i.Kind == SqlCompletionKind.Join) ? 560 : 340, MaxHeight = 260 };
        foreach (var item in result.Items) window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        window.Closed += (_, _) => { if (ReferenceEquals(completionWindow, window)) completionWindow = null; };
        completionWindow = window;
        window.Show();
        if (result.Prefix.Length > 0) window.CompletionList.SelectItem(result.Prefix);
    }
}

/// <summary>Enter accepts only something the user chose or typed for; with nothing selected the list closes and the editor gets the new line (observed in SSMS 22.11).</summary>
internal sealed class SqlCompletionWindow(TextArea textArea) : CompletionWindow(textArea)
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CompletionList.SelectedItem is null) { Close(); return; }
        base.OnKeyDown(e);
    }
}

internal sealed class SqlCompletionData(SqlCompletionItem item) : ICompletionData
{
    private static readonly Dictionary<SqlCompletionKind, ImageSource> Images = [];
    public ImageSource Image => Images.TryGetValue(item.Kind, out var image) ? image : Images[item.Kind] = Glyph(item.Kind);
    /// <summary>What AvalonEdit filters on. The accent-free form follows the name, so "usuar" finds "Usuários" and "usuár" still does too.</summary>
    public string Text { get; } = WithoutAccents(item.Filter ?? item.Text) is var plain && plain != (item.Filter ?? item.Text) ? $"{item.Filter ?? item.Text}\u0001{plain}" : item.Filter ?? item.Text;
    public object Content => item.Text;
    public object? Description => item.Description.Length > 0 ? item.Description : null;
    public double Priority => item.Kind == SqlCompletionKind.Join ? 1 : 0;
    public SqlCompletionKind Kind => item.Kind;
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => textArea.Document.Replace(completionSegment, item.Text);

    private static string WithoutAccents(string text) =>
        string.Concat(text.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));

    private static ImageSource Glyph(SqlCompletionKind kind)
    {
        var (data, color) = kind switch
        {
            SqlCompletionKind.Table => ("M1,3 L15,3 15,13 1,13 Z M1,6.5 L15,6.5 M1,10 L15,10 M6,3 L6,13 M11,3 L11,13", Color.FromRgb(0x2B, 0x57, 0x97)),
            SqlCompletionKind.View => ("M1,3 L15,3 15,13 1,13 Z M1,6.5 L15,6.5 M4,9 L12,9 M4,11 L9,11", Color.FromRgb(0x2D, 0x7D, 0x3B)),
            SqlCompletionKind.Column => ("M5,1 L11,1 11,15 5,15 Z M5,5 L11,5 M5,8 L11,8 M5,11 L11,11", Color.FromRgb(0x4B, 0x59, 0x64)),
            SqlCompletionKind.Function => ("M11,2 C7,2 8,6 7,9 C6,13 5,14 3,14 M4,7 L11,7", Color.FromRgb(0x7A, 0x3E, 0x9D)),
            SqlCompletionKind.Join => ("M1,4 L7,4 7,12 1,12 Z M9,4 L15,4 15,12 9,12 Z M7,8 L9,8", Color.FromRgb(0xC2, 0x5B, 0x12)),
            SqlCompletionKind.Schema => ("M2,4 L7,4 8.5,6 14,6 14,13 2,13 Z", Color.FromRgb(0xB5, 0x83, 0x2A)),
            _ => ("M3,2 L3,14 M12,2 L4,8 12,14", Color.FromRgb(0x1E, 0x6F, 0xC6)),
        };
        var pen = new Pen(new SolidColorBrush(color), 1.2);
        var image = new DrawingImage(new GeometryDrawing(Brushes.Transparent, pen, Geometry.Parse(data + " M0,0 M16,16")));
        image.Freeze();
        return image;
    }
}
