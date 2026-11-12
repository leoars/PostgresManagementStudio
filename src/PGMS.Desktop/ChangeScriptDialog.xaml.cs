using System.IO;
using System.Text;
using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>The SQL a designer is about to run. Save requires this review (decision of 06/11/2026); Generate Change Script opens it without Apply, like the SSMS 22.11 Save Change Script.</summary>
public partial class ChangeScriptDialog : Window
{
    /// <param name="intro">Replaces the first line when Apply does not run the script in one transaction.</param>
    public ChangeScriptDialog(TableDesignChange change, bool canApply, string? intro = null)
    {
        InitializeComponent();
        Script.Text = change.Sql.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        Intro.Text = intro ?? (canApply ? "Review the change script. Apply runs it in one transaction." : "Do you want to save this change script to a text file?");
        ApplyButton.Visibility = canApply ? Visibility.Visible : Visibility.Collapsed;
        if (!canApply) CancelButton.Content = "_Close";
        if (change.Warnings.Count > 0) { Warnings.Text = string.Join(Environment.NewLine, change.Warnings); Warnings.Visibility = Visibility.Visible; }
    }
    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
    private void SaveFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", DefaultExt = ".sql", FileName = "ChangeScript.sql" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, Script.Text, new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "PGMS", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
