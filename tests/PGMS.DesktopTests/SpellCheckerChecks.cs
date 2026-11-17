using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-080 (M1109): Edit ▸ Advanced ▸ Toggle Spell Checker, only comments and strings, with the Windows spell checker.</summary>
internal static class SpellCheckerChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        const string sql = "SELECT 'helo wrld' AS greeting -- a coment here\nFROM t /* outer /* nested */ text */ WHERE x = 'it''s' AND $$ body teh $$ = \"Colunm\";";
        var ranges = SqlSpellChecker.TextRanges(sql).Select(r => sql.Substring(r.Start, r.Length)).ToList();
        check(ranges.SequenceEqual(["helo wrld", " a coment here", " outer /* nested */ text ", "it''s"]), "Only line comments, nested block comments and string literals are checked: " + string.Join(" | ", ranges));

        var fake = SqlSpellChecker.Misspellings(sql, text => text.Contains("helo", StringComparison.Ordinal) ? [(0, 4)] : text.Contains("coment", StringComparison.Ordinal) ? [(3, 6)] : []);
        check(fake.Select(m => sql.Substring(m.Start, m.Length)).SequenceEqual(["helo", "coment"]), "Errors found in a range are placed at their offsets in the whole text");

        if (SqlSpellChecker.Language is { } language)
        {
            var real = SqlSpellChecker.Misspellings("SELECT 1; -- thiss is a tset of the speling checker\nSELECT 'ok';").Select(m => m.Length).ToList();
            check(real.Count >= 2, $"The Windows spell checker ({language}) finds the misspelled words of a comment");
            check(SqlSpellChecker.Check("column_name table2 xyzzy_1").Count() == 0, "Words with digits or underscores are not reported");
        }
        else check(SqlSpellChecker.Check("thiss").Count() == 0, "Without a Windows spell checker nothing is underlined");

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var advanced = (MenuItem)window.FindName("AdvancedEditMenu");
            var toggle = (MenuItem)window.FindName("SpellCheckerMenu");
            check(advanced.Items.IndexOf(toggle) == 0 && (string)toggle.Header == "Toggle Spell Checker", "Toggle Spell Checker is the first item of Edit ▸ Advanced, as in SSMS");
            var query = window.NewQueryTab("SELECT 1; -- helo");
            await Idle();
            query.SpellCheckOverride = text => text.Contains("helo", StringComparison.Ordinal) ? [(text.IndexOf("helo", StringComparison.Ordinal), 4)] : [];
            advanced.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, advanced));
            check(toggle.IsEnabled && toggle.IsChecked == false && !query.SpellCheck, "It starts off");
            toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(query.SpellCheck && toggle.IsChecked == true && query.Misspellings is [(13, 4)], "Turning it on underlines the misspelled word");
            query.Editor.Document.Insert(query.Editor.Document.TextLength, " and helo again");
            query.RefreshSpelling();
            check(query.Misspellings.Count == 1 && query.Misspellings[0].Start == 13, "Edits are checked again (the override reports the first match of each range)");
            toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(!query.SpellCheck && query.Misspellings.Count == 0, "Turning it off removes the underlines");

        }
        finally { foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false; window.Close(); }
    }
}
