using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-066 (M1091): Edit ▸ Find and Replace ▸ Find in Files / Replace in Files and the Find Results pane, in a temporary folder.</summary>
internal static class FindInFilesChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // Pure search and replace.
        var regex = new FileSearchOptions("select").ToRegex();
        var hits = FileSearch.Matches("a.sql", "a.sql", "select a,\r\n  b\r\nfrom t; SELECT 1;\nselectx", regex).ToArray();
        check(hits.Length == 3 && hits[0] is { Line: 1, Column: 1, LineText: "select a," } && hits[1] is { Line: 3, Column: 9 } && hits[2] is { Line: 4, Column: 1, LineText: "selectx" }, "Matches give line, column and the line text, without the CR: " + string.Join(" | ", hits.Select(h => $"{h.Line},{h.Column} {h.LineText}")));
        check(FileSearch.Matches("", "", "select SELECT", new FileSearchOptions("select", MatchCase: true).ToRegex()).Count() == 1, "Match case");
        check(FileSearch.Matches("", "", "select selectx _select x.select", new FileSearchOptions("select", WholeWord: true).ToRegex()).Count() == 2, "Match whole word keeps letters, digits and _ as word characters");
        check(FileSearch.Replace("a $1 a", new FileSearchOptions("a").ToRegex(), "$0b", false) == ("$0b $1 $0b", 2), "Without regular expressions the replacement is literal");
        check(FileSearch.Replace("t1 t22", new FileSearchOptions(@"t(\d+)", UseRegex: true).ToRegex(), "x$1", true) == ("x1 x22", 2), "With regular expressions $1 refers to the group");
        bool invalid = false; try { new FileSearchOptions("(", UseRegex: true).ToRegex(); } catch (ArgumentException ex) { invalid = ex.Message.StartsWith("The regular expression is not valid", StringComparison.Ordinal); }
        bool empty = false; try { new FileSearchOptions("").ToRegex(); } catch (ArgumentException) { empty = true; }
        check(invalid && empty, "An invalid expression or empty text is refused with a message");
        check(FileSearch.ParseTypes(" *.sql ; *.txt,*.SQL ").SequenceEqual(["*.sql", "*.txt"]) && FileSearch.ParseTypes("").SequenceEqual(["*.*"]), "File types are split on ; or , and empty means *.*");

        string root = Path.Combine(Path.GetTempPath(), "pgms-fif-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        string a = Path.Combine(root, "a.sql"), b = Path.Combine(root, "sub", "b.sql"), c = Path.Combine(root, "c.txt"), bin = Path.Combine(root, "d.sql"), bom = Path.Combine(root, "e.sql");
        File.WriteAllText(a, "select 1;\nselect 2;\n", new UTF8Encoding(false));
        File.WriteAllText(b, "-- select in sub\r\nSELECT 3;\r\n", new UTF8Encoding(false));
        File.WriteAllText(c, "select in text\n", new UTF8Encoding(false));
        File.WriteAllBytes(bin, [0x73, 0x65, 0x6C, 0x65, 0x63, 0x74, 0, 1, 2]);
        File.WriteAllText(bom, "select é;\n", new UTF8Encoding(true));
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        var errors = new List<string>(); shell.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            var edit = (MenuItem)shell.FindName("FindAndReplaceMenu");
            check(edit.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["_Quick Find", "Quick _Replace", "Find in _Files", "Replace in F_iles"]) && ((MenuItem)shell.FindName("ReplaceInFilesMenu")).InputGestureText == "Ctrl+Shift+H", "Edit ▸ Find and Replace has Quick Find, Quick Replace, Find in Files and Replace in Files (Ctrl+Shift+H), as in SSMS 22.11");
            check(MainWindow.ReplaceInFilesCommand.InputGestures.OfType<System.Windows.Input.KeyGesture>().Any(g => g.Key == System.Windows.Input.Key.H && g.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)), "Ctrl+Shift+H is Replace in Files");

            // Folder search: subfolders, file types, binary skipped, open tab text used instead of disk.
            var open = await shell.OpenQueryFileAsync(a); await Idle();
            open.Editor.Document.Insert(0, "select 0;\n"); // unsaved edit
            var request = new FileSearchRequest(new FileSearchOptions("select"), FileSearchScope.Folder, root, "*.sql", true);
            var found = await shell.FindAllInFilesAsync(request); await Idle();
            check(found.Count(m => m.Path == a) == 3 && found.Any(m => m.Path == b && m.Line == 2) && !found.Any(m => m.Path == c || m.Path == bin) && found.Any(m => m.Path == bom), "Folder search covers subfolders and *.sql, skips the binary file and reads the open tab's unsaved text: " + string.Join(", ", found.Select(m => $"{m.DisplayName}:{m.Line}")));
            var results = shell.FindResults;
            check(results.Header.Text == $"Find all \"select\", Subfolders, \"{root}\", \"*.sql\"" && results.Summary.Text.StartsWith("Matching lines: 6  Matching files: 3  Total files searched: 3", StringComparison.Ordinal) && results.Summary.Text.Contains("1 file(s) skipped"), "Find Results shows the search line and the counts: " + results.Header.Text + " / " + results.Summary.Text);
            check(results.Tree.Items.Count == 3 && ((TreeViewItem)results.Tree.Items[0]).Header is string h && h.EndsWith("(3)", StringComparison.Ordinal), "Matches are grouped by file with their count");
            var noSub = await shell.FindAllInFilesAsync(request with { Subfolders = false, Types = "*.sql;*.txt" });
            check(!noSub.Any(m => m.Path == b) && noSub.Any(m => m.Path == c), "Without subfolders only the folder itself; types accept several patterns");

            // Open documents and the current document.
            var untitled = shell.NewQueryTab("SELECT x FROM selected;"); await Idle();
            var all = await shell.FindAllInFilesAsync(new FileSearchRequest(new FileSearchOptions("select", WholeWord: true), FileSearchScope.AllOpenDocuments));
            check(all.Count(m => m.Query == untitled) == 1 && all.Count(m => m.Query == open) == 3, "All open documents searches every tab, saved or not (whole word skips 'selected')");
            var current = await shell.FindAllInFilesAsync(new FileSearchRequest(new FileSearchOptions("select"), FileSearchScope.CurrentDocument));
            check(current.All(m => m.Query == untitled) && current.Count == 2, "Current document searches only the active tab");

            // Opening a match: a closed file opens at the match.
            var inSub = found.First(m => m.Path == b && m.Line == 2);
            await shell.OpenFindMatchAsync(inSub); await Idle();
            var subTab = shell.QueryDocuments.FirstOrDefault(q => string.Equals(q.FilePath, b, StringComparison.OrdinalIgnoreCase));
            check(subTab is not null && subTab.Editor.SelectedText == "SELECT" && subTab.Editor.TextArea.Caret.Line == 2, "Opening a result opens the file and selects the match");

            // The window: prefilled with the word at the caret, Look in and validation.
            await shell.OpenFindMatchAsync(all.First(m => m.Query == untitled)); await Idle();
            var window = shell.ShowFindInFiles(false);
            check(!window.IsReplace && window.FindText.Text == "SELECT", "Find in Files opens on its tab with the word at the caret");
            window.LookIn.Text = Path.Combine(root, "missing");
            check(!await window.FindAllAsync() && window.Feedback.Contains("does not exist"), "A missing folder is refused in the window");
            window.LookIn.Text = FindInFilesWindow.CurrentDocument; window.UseRegex.IsChecked = true; window.FindText.Text = "(";
            check(!await window.FindAllAsync() && window.Feedback.StartsWith("The regular expression is not valid", StringComparison.Ordinal), "An invalid regular expression is reported in the window");
            window.UseRegex.IsChecked = false; window.FindText.Text = "x";
            check(!window.Subfolders.IsEnabled && !window.FileTypes.IsEnabled, "Subfolders and file types apply only to a folder");
            check(shell.ShowFindInFiles(true).IsReplace, "Replace in Files opens the other tab of the same window");
            window.Close();

            // Replace All: No changes nothing; Yes changes the closed file on disk (encoding kept) and the open tab in its editor.
            string? asked = null;
            shell.ReplaceAllConfirm = q => { asked = q; return false; };
            var replaceRequest = new FileSearchRequest(new FileSearchOptions("select"), FileSearchScope.Folder, root, "*.sql", true);
            check(await shell.ReplaceAllInFilesAsync(replaceRequest, "SELECT") == 0 && File.ReadAllText(bom).StartsWith("select", StringComparison.Ordinal) && asked?.Contains("cannot be undone") == true, "Replace All asks first and No replaces nothing: " + asked);
            shell.ReplaceAllConfirm = _ => true;
            string beforeOpen = open.Editor.Text;
            int replaced = await shell.ReplaceAllInFilesAsync(replaceRequest with { Options = new FileSearchOptions("select", MatchCase: true) }, "SELECT"); await Idle();
            byte[] bomBytes = File.ReadAllBytes(bom);
            check(replaced == 5 && File.ReadAllText(bom) == "SELECT é;\n" && bomBytes[0] == 0xEF && bomBytes[1] == 0xBB, "A closed file is rewritten on disk with its encoding (match case left SELECT alone): " + replaced);
            check(open.Editor.Text == "SELECT 0;\nSELECT 1;\nSELECT 2;\n" && File.ReadAllText(a) == "select 1;\nselect 2;\n" && open.IsDirty, "An open tab changes in its editor and its file stays as it was");
            check(subTab!.Editor.Text == "-- SELECT in sub\r\nSELECT 3;\r\n" && File.ReadAllText(b) == "-- select in sub\r\nSELECT 3;\r\n", "The file opened from the results is also changed in its tab, not on disk");
            open.Editor.Undo();
            check(open.Editor.Text == beforeOpen, "One Undo restores the open tab");
            check(shell.FindResults.Summary.Text.StartsWith("Total replaced: 5", StringComparison.Ordinal), "Find Results reports the replacement: " + shell.FindResults.Summary.Text);
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in shell.QueryDocuments.ToArray()) q.IsDirty = false;
            shell.Close(); await shell.CloseCompletion;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
