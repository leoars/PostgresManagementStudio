using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Code Snippets Manager… (Ctrl+K, Ctrl+B; PGMS-082, M1122).</summary>
public partial class MainWindow
{
    public static RoutedUICommand CodeSnippetsManagerCommand { get; } = new("Code Snippets Manager", "CodeSnippetsManager", typeof(MainWindow));

    /// <summary>Receives the dialog instead of showing it modally; tests set it.</summary>
    internal Action<CodeSnippetsManagerDialog>? CodeSnippetsManagerShown { get; set; }

    private void ConfigureCodeSnippets() =>
        CommandBindings.Add(new CommandBinding(CodeSnippetsManagerCommand, (_, e) => { ShowCodeSnippetsManager(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));

    internal CodeSnippetsManagerDialog ShowCodeSnippetsManager()
    {
        var dialog = new CodeSnippetsManagerDialog { Owner = this };
        if (CodeSnippetsManagerShown is { } shown) shown(dialog); else dialog.ShowDialog();
        return dialog;
    }
}
