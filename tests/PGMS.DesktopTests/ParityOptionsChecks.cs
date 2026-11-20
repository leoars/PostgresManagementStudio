using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-082 (M1122): Code Snippets Manager, Text Editor ▸ Transact-SQL and the stacked results option.</summary>
internal static class ParityOptionsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string scratch = Path.Combine(Path.GetTempPath(), "pgms-m1122-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        try
        {
            await Idle();
            var menu = (MenuItem)shell.FindName("CodeSnippetsManagerMenu");
            var tools = (ItemsControl)menu.Parent;
            check(menu.InputGestureText == "Ctrl+K, Ctrl+B" && tools.Items.IndexOf(menu) == tools.Items.IndexOf(shell.FindName("SchemaCompareMenu")) + 1, "Tools ▸ Code Snippets Manager… follows Schema Compare, with Ctrl+K, Ctrl+B, as in SSMS 22.11");

            // Code Snippets Manager over a user folder of the test.
            string user = Path.Combine(scratch, "Snippets");
            var dialog = new CodeSnippetsManagerDialog(user);
            var folders = dialog.Tree.Items.OfType<TreeViewItem>().ToList();
            var builtIn = folders.SelectMany(f => f.Items.OfType<TreeViewItem>()).First();
            check(folders.Count > 0 && builtIn.Tag is SqlSnippet, "The manager lists the PGMS snippets by folder");
            builtIn.IsSelected = true;
            check(dialog.LocationText == "Built into PGMS" && dialog.Selected?.SourceFile is null && !dialog.RemoveSelected(), "A built-in snippet says where it is and cannot be removed");
            string file = Path.Combine(scratch, "count rows.snippet");
            File.WriteAllText(file, """
                <?xml version="1.0" encoding="utf-8"?>
                <CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
                  <CodeSnippet Format="1.0.0"><Header><Title>Count rows of a table</Title><Description>SELECT count(*)</Description><SnippetTypes><SnippetType>Expansion</SnippetType></SnippetTypes></Header>
                  <Snippet><Declarations><Literal><ID>t</ID><ToolTip>Table</ToolTip><Default>my_table</Default></Literal></Declarations><Code Language="SQL"><![CDATA[SELECT count(*) FROM $t$;$end$]]></Code></Snippet></CodeSnippet>
                </CodeSnippets>
                """);
            dialog.PickFile = () => file;
            string? imported = dialog.Import();
            check(imported is not null && File.Exists(imported) && Path.GetDirectoryName(imported) == user && dialog.Selected?.Title == "Count rows of a table" && dialog.LocationText == imported,
                "Import copies the .snippet into the user's folder and selects it: " + dialog.StatusText);
            check(SqlTemplateCatalog.Snippets(user).Any(s => s.Title == "Count rows of a table" && s.Folder == SqlTemplateCatalog.UserRootFolder), "The imported snippet is offered by Insert Snippet (folder Custom)");
            string? again = dialog.Import();
            check(again is not null && Path.GetFileName(again) == "count rows (2).snippet", "Importing the same name again keeps both files");
            string bad = Path.Combine(scratch, "bad.snippet"); File.WriteAllText(bad, "<CodeSnippets/>");
            dialog.PickFile = () => bad;
            check(dialog.Import() is null && dialog.StatusText.Contains("no code snippet PGMS can read", StringComparison.Ordinal) && !File.Exists(Path.Combine(user, "bad.snippet")), "A file without snippets is not imported and the reason is shown");
            dialog.Fill(again);
            dialog.ConfirmRemove = _ => false;
            check(!dialog.RemoveSelected() && File.Exists(again), "Remove asks first; No keeps the file");
            dialog.ConfirmRemove = _ => true;
            check(dialog.RemoveSelected() && !File.Exists(again!) && File.Exists(imported!), "Yes deletes only that file");
            bool refused = false;
            try { CodeSnippetFiles.Remove(file, user); } catch (InvalidOperationException) { refused = true; }
            check(refused && File.Exists(file), "Files outside the user's snippets folder are never deleted");
            dialog.Close();
            CodeSnippetsManagerDialog? shown = null; shell.CodeSnippetsManagerShown = d => shown = d;
            MainWindow.CodeSnippetsManagerCommand.Execute(null, shell);
            check(shown is not null, "The command opens the manager"); shown?.Close();

            // Text Editor ▸ Transact-SQL.
            var settings = new EnvironmentOptions(Path.Combine(scratch, "environment.json"));
            var options = new OptionsDialog(settings);
            options.SelectPage("Transact-SQL");
            check(((System.Windows.FrameworkElement)options.FindName("TextEditorPanel")).Visibility == System.Windows.Visibility.Visible, "Text Editor ▸ Transact-SQL shows its page");
            var tabSize = (TextBox)options.FindName("EditorTabSizeBox");
            check(tabSize.Text == "4" && ((CheckBox)options.FindName("EditorLineNumbersBox")).IsChecked == true && ((RadioButton)options.FindName("EditorKeepTabsBox")).IsChecked == true && ((CheckBox)options.FindName("EditorIntelliSenseBox")).IsChecked == true && ((CheckBox)options.FindName("EditorWordWrapBox")).IsChecked == false,
                "Defaults are the editor as it was: line numbers, tabs of 4 kept, IntelliSense on, no word wrap");
            tabSize.Text = "0";
            options.Accept(options, new System.Windows.RoutedEventArgs());
            check(((TextBlock)options.FindName("TextEditorError")).Visibility == System.Windows.Visibility.Visible && settings.Load().TextEditor is null, "A tab size of 0 is refused and nothing is saved");
            tabSize.Text = "2";
            ((CheckBox)options.FindName("EditorWordWrapBox")).IsChecked = true;
            ((CheckBox)options.FindName("EditorLineNumbersBox")).IsChecked = false;
            ((RadioButton)options.FindName("EditorInsertSpacesBox")).IsChecked = true;
            ((CheckBox)options.FindName("EditorIntelliSenseBox")).IsChecked = false;
            options.Accept(options, new System.Windows.RoutedEventArgs());
            var saved = settings.Load().EditorSettings;
            check(saved == new TextEditorSettings(true, false, 2, true, false), "OK saves the Transact-SQL settings: " + saved);
            var open = shell.NewQueryTab("select 1;"); await Idle();
            bool wrapBefore = open.WordWrap, intelliBefore = open.IntelliSenseEnabled;
            open.ApplyTextEditorSettings(saved, newTab: false);
            check(!open.Editor.ShowLineNumbers && open.Editor.Options.IndentationSize == 2 && open.Editor.Options.ConvertTabsToSpaces && open.WordWrap == wrapBefore && open.IntelliSenseEnabled == intelliBefore,
                "An open window takes line numbers and tabs at once and keeps its word wrap and IntelliSense");
            var fresh = shell.NewQueryTab(); await Idle();
            fresh.ApplyTextEditorSettings(saved, newTab: true);
            check(fresh.WordWrap && !fresh.IntelliSenseEnabled && !fresh.Editor.ShowLineNumbers, "A new window starts with word wrap and IntelliSense as set");
            check(new TextEditorSettings(TabSize: 99).Normalized().TabSize == 4, "A hand-edited tab size out of range falls back to 4");

            // Results to Grid ▸ stacked option on the shared Query Options page.
            var queryOptions = new QueryOptionsDialog(new QueryOptions());
            check(queryOptions.FindName("OptionPages") is QueryOptionsPages pages && pages.FindName("StackResultsBox") is CheckBox { IsChecked: false }, "Results to Grid offers Show multiple result sets stacked in one tab, off by default");
            queryOptions.Close();
            foreach (var tab in shell.OpenQueryViews()) tab.IsDirty = false;
        }
        finally
        {
            foreach (var tab in shell.OpenQueryViews()) tab.IsDirty = false;
            shell.Close(); await shell.CloseCompletion;
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
