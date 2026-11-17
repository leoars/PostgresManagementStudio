using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Quick Info (Ctrl+K, Ctrl+I and mouse hover) and Parameter Info (Ctrl+Shift+Space, and on typing "(" or ","), PGMS-058,
/// with the SSMS 22.11 texts piloted in M1079. Both read the IntelliSense cache of the connection; nothing is executed.</summary>
public partial class QueryView
{
    /// <summary>Tests give a catalog without a connection.</summary>
    internal SqlCompletionCatalog? TestCatalog { get; set; }
    private InsightWindow? quickInfoWindow;
    private OverloadInsightWindow? parameterWindow;
    private SqlParameterProvider? parameterProvider;
    private ToolTip? hoverTip;
    internal InsightWindow? OpenQuickInfoWindow => quickInfoWindow;
    internal OverloadInsightWindow? OpenParameterWindow => parameterWindow;
    internal SqlParameterProvider? ParameterProvider => parameterProvider;
    internal ToolTip? HoverTip => hoverTip;

    private void ConfigureQuickInfo()
    {
        Editor.TextArea.TextEntered += async (_, e) => { if (e.Text is "(" or ",") await ShowParameterInfoAsync(automatic: true); };
        Editor.TextArea.Caret.PositionChanged += (_, _) => RefreshParameterInfo();
        Editor.PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Space) { e.Handled = true; await ShowParameterInfoAsync(); }
        };
        Editor.MouseHover += async (_, e) =>
        {
            if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is not { } position) return;
            int offset = Editor.Document.GetOffset(position.Location);
            if (await QuickInfoAtAsync(offset, automatic: true) is not { } info) return;
            hoverTip ??= new ToolTip { PlacementTarget = Editor, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
            hoverTip.Content = info.Text; hoverTip.IsOpen = true;
            e.Handled = true;
        };
        Editor.MouseHoverStopped += (_, _) => { if (hoverTip is not null) hoverTip.IsOpen = false; };
    }

    private async Task<SqlCompletionCatalog?> QuickCatalogAsync(bool automatic)
    {
        if (TestCatalog is { } test) return test;
        if (!intelliSenseEnabled) return null;
        if (ConnectionString is not { } connection) { if (!automatic) QueryStatus.Text = "Connect this query to use IntelliSense."; return null; }
        try { return await CatalogAsync(connection); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            catalogLoad = null;
            if (!automatic) QueryStatus.Text = "IntelliSense could not read the catalog: " + ex.Message;
            return null;
        }
    }

    internal async Task<SqlQuickInfo?> QuickInfoAtAsync(int offset, bool automatic)
    {
        long revision = EditRevision;
        if (await QuickCatalogAsync(automatic) is not { } catalog || revision != EditRevision) return null;
        return SqlCompletionEngine.QuickInfo(Editor.Text, offset, catalog);
    }

    /// <summary>Edit ▸ IntelliSense ▸ Quick Info: the tip of the name at the caret, below it; nothing when it is a keyword or unknown (as in SSMS).</summary>
    internal async Task ShowQuickInfoAsync()
    {
        quickInfoWindow?.Close();
        int caret = Editor.CaretOffset;
        if (await QuickInfoAtAsync(caret, automatic: false) is not { } info || caret != Editor.CaretOffset || !Editor.IsLoaded) return;
        var window = new InsightWindow(Editor.TextArea) { Content = new TextBlock { Text = info.Text, Margin = new Thickness(4, 2, 4, 2) }, StartOffset = info.Start, EndOffset = info.End, CloseAutomatically = true };
        window.Closed += (_, _) => { if (ReferenceEquals(quickInfoWindow, window)) quickInfoWindow = null; };
        quickInfoWindow = window;
        window.Show();
    }

    /// <summary>Edit ▸ IntelliSense ▸ Parameter Info: the signature of the function whose parentheses hold the caret, with the current argument in bold.</summary>
    internal async Task ShowParameterInfoAsync(bool automatic = false)
    {
        int caret = Editor.CaretOffset;
        if (await QuickCatalogAsync(automatic) is not { } catalog || caret != Editor.CaretOffset || !Editor.IsLoaded) return;
        if (SqlCompletionEngine.ParameterInfo(Editor.Text, caret, catalog) is not { } info) { if (!automatic) QueryStatus.Text = "No function parameters at the caret."; return; }
        if (parameterWindow is not null && parameterProvider is not null && parameterProvider.Info.OpenParenthesis == info.OpenParenthesis) { parameterProvider.Update(info); return; }
        parameterWindow?.Close();
        var provider = new SqlParameterProvider(info);
        var window = new OverloadInsightWindow(Editor.TextArea) { Provider = provider, CloseAutomatically = false };
        window.Closed += (_, _) => { if (ReferenceEquals(parameterWindow, window)) { parameterWindow = null; parameterProvider = null; } };
        parameterWindow = window; parameterProvider = provider;
        window.Show();
    }

    /// <summary>Follows the caret: the bold argument changes, and the tip closes when the caret leaves the parentheses.</summary>
    private void RefreshParameterInfo()
    {
        if (parameterWindow is null || parameterProvider is null) return;
        var catalog = TestCatalog ?? (catalogLoad is { IsCompletedSuccessfully: true } load ? load.Result : null);
        var info = catalog is null ? null : SqlCompletionEngine.ParameterInfo(Editor.Text, Editor.CaretOffset, catalog);
        if (info is null || info.OpenParenthesis != parameterProvider.Info.OpenParenthesis) { parameterWindow.Close(); return; }
        parameterProvider.Update(info);
    }
}

/// <summary>The overloads of Parameter Info, "1 of 3" with the arrows of the insight window.</summary>
internal sealed class SqlParameterProvider(SqlParameterInfo info) : IOverloadProvider
{
    public SqlParameterInfo Info { get; private set; } = info;
    private int selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int SelectedIndex { get => selected; set { selected = Math.Clamp(value, 0, Count - 1); Notify(); } }
    public int Count => Info.Signatures.Count;
    public string? CurrentIndexText => Count > 1 ? $"{selected + 1} of {Count}" : null;
    public object CurrentHeader => Header(Info.Signatures[selected], Info.ArgumentIndex);
    public object? CurrentContent => null;
    /// <summary>The plain text of the current signature (for tests and screen readers).</summary>
    public string CurrentText => Text(Info.Signatures[selected]);
    public string? BoldArgument => Info.ArgumentIndex < Info.Signatures[selected].Arguments.Count ? Info.Signatures[selected].Arguments[Info.ArgumentIndex] : null;

    internal void Update(SqlParameterInfo info) { Info = info; if (selected >= Count) selected = 0; Notify(); }
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(SelectedIndex))); PropertyChanged?.Invoke(this, new(nameof(CurrentIndexText)));
        PropertyChanged?.Invoke(this, new(nameof(CurrentHeader))); PropertyChanged?.Invoke(this, new(nameof(CurrentContent)));
    }

    internal static string Text(SqlSignature s) => s.Prefix + string.Join(",", s.Arguments) + s.Suffix;

    private static TextBlock Header(SqlSignature signature, int argument)
    {
        var block = new TextBlock();
        block.Inlines.Add(new Run(signature.Prefix));
        for (int i = 0; i < signature.Arguments.Count; i++)
        {
            if (i > 0) block.Inlines.Add(new Run(","));
            var run = new Run(signature.Arguments[i]);
            if (i == argument || i == signature.Arguments.Count - 1 && argument > i && signature.Arguments[i].StartsWith("VARIADIC ", StringComparison.Ordinal)) run.FontWeight = FontWeights.Bold;
            block.Inlines.Add(run);
        }
        block.Inlines.Add(new Run(signature.Suffix));
        System.Windows.Automation.AutomationProperties.SetName(block, Text(signature));
        return block;
    }
}
