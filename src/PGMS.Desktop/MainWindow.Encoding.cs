using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Set Encoding ▸ Reopen with Encoding / Save with Encoding (PGMS-049), as in SSMS 22.11 (M1078) with the Unicode encodings PGMS reads and writes.</summary>
public partial class MainWindow
{
    /// <summary>Chooses the encoding (null = cancelled; for Reopen, a null format inside means Auto-Detect); tests replace it to avoid the modal dialog.</summary>
    internal Func<QueryView, bool, (bool Accepted, SqlFileFormat? Format)>? PickEncoding { get; set; }
    /// <summary>Confirms discarding unsaved edits before reopening; tests replace it.</summary>
    internal Func<QueryView, bool>? ConfirmReopenDiscard { get; set; }

    private void ConfigureEncodingCommands()
    {
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.SaveWithEncoding, async (_, _) => { if (ActiveQuery is { } query) await SaveWithEncodingAsync(query); },
            (_, e) => e.CanExecute = ActiveQuery is { } q && !IsSaving(q) && !closingWindow && !closeRequested && !closingQueries.Contains(q)));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.ReopenWithEncoding, async (_, _) => { if (ActiveQuery is { } query) await ReopenWithEncodingAsync(query); },
            (_, e) => e.CanExecute = ActiveQuery is { FilePath: not null } q && !IsSaving(q) && !q.IsBusy && !closingWindow && !closeRequested && !closingQueries.Contains(q)));
    }

    /// <summary>Saves the query with the chosen encoding (Save As when it has no file yet); cancelling keeps the previous encoding.</summary>
    internal async Task<bool> SaveWithEncodingAsync(QueryView query)
    {
        var (accepted, format) = (PickEncoding ?? ShowEncodingDialog)(query, false);
        if (!accepted || format is not { } chosen) return false;
        var previous = query.FileFormat;
        query.FileFormat = chosen;
        if (await SaveAsync(query)) return true;
        query.FileFormat = previous;
        return false;
    }

    /// <summary>Reads the file again with the chosen encoding, replacing the text (unsaved edits only after confirmation). Nothing is executed.</summary>
    internal async Task<bool> ReopenWithEncodingAsync(QueryView query)
    {
        if (query.FilePath is not { } path) return false;
        var (accepted, format) = (PickEncoding ?? ShowEncodingDialog)(query, true);
        if (!accepted) return false;
        if (query.IsDirty && !(ConfirmReopenDiscard ?? (q => LocalizedMessageBox.Show(this, $"Discard the unsaved changes in {Path.GetFileName(path)} and reopen it?", "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes))(query)) return false;
        try
        {
            var file = await SqlFileStore.ReadAsync(path, format);
            if (!documents.ContainsKey(query)) return false;
            query.Editor.Text = file.Text;
            query.Editor.Document.UndoStack.ClearAll();
            query.FileFormat = file.Format; query.FileVersion = file.Version; query.IsDirty = false;
            documents[query].Title = Path.GetFileName(file.Path);
            Status.Text = $"Reopened with {QueryView.EncodingName(file.Format)}";
            await ForgetRecoveryAsync(query);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); return false; }
    }

    private (bool, SqlFileFormat?) ShowEncodingDialog(QueryView query, bool reopen)
    {
        var formats = Enum.GetValues<SqlFileFormat>();
        var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        AutomationPropertiesHelper.SetName(combo, "Encoding");
        if (reopen) combo.Items.Add("(Auto-Detect)");
        foreach (var f in formats) combo.Items.Add(QueryView.EncodingName(f));
        combo.SelectedIndex = reopen ? 0 : Array.IndexOf(formats, query.FileFormat);
        var ok = new Button { Content = "OK", IsDefault = true, Width = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 88 };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "File:" });
        panel.Children.Add(new TextBlock { Text = query.FilePath ?? "(not saved yet)", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) });
        panel.Children.Add(new Label { Content = "_Encoding:", Target = combo, Padding = new Thickness(0) });
        panel.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var window = new Window { Title = "Encoding", Content = panel, Owner = this, Width = 511, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return (false, null);
        int index = combo.SelectedIndex - (reopen ? 1 : 0);
        return (true, index < 0 ? null : formats[index]);
    }
}

internal static class AutomationPropertiesHelper
{
    internal static void SetName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
