using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using Localization = PGMS.Desktop.Localization;

/// <summary>PGMS-085 (M1125): interface languages. The checks run in English, as every other check; a language is switched on only here and
/// switched back at the end.</summary>
internal static class LocalizationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(Localization.Current == "en" && Localization.T("_Tools") == "_Tools", "The checks start in English, with no table loaded");
        check(Localization.Languages.Select(l => l.Code).SequenceEqual(["en", "pt-BR", "es", "de", "it", "fr", "ko", "ja"]), "Eight languages, English first");

        // The language Windows gives when nothing is saved.
        check(Localization.Resolve(null, new CultureInfo("pt-BR")) == "pt-BR" && Localization.Resolve(null, new CultureInfo("pt-PT")) == "pt-BR" && Localization.Resolve(null, new CultureInfo("de-AT")) == "de"
            && Localization.Resolve(null, new CultureInfo("ja-JP")) == "ja" && Localization.Resolve(null, new CultureInfo("ru-RU")) == "en" && Localization.Resolve(null, CultureInfo.InvariantCulture) == "en",
            "Same as Microsoft Windows: the Windows language when PGMS has it (any Portuguese is pt-BR), English otherwise");
        check(Localization.Resolve("fr", new CultureInfo("pt-BR")) == "fr" && Localization.Resolve("xx", new CultureInfo("ko-KR")) == "ko", "A saved language wins; an unknown one falls back to Windows");

        // Every table loads, has the same keys, keeps one access key per menu item, and never translates SQL or PostgreSQL words in keys.
        var english = Localization.Load("pt-BR").Keys.ToHashSet();
        foreach (var (code, _) in Localization.Languages.Skip(1))
        {
            var table = Localization.Load(code);
            check(table.Count >= 60 && table.Keys.ToHashSet().SetEquals(english) && table.Values.All(v => v.Length > 0), $"{code}: the table loads with the same {english.Count} texts, none empty");
            var badKeys = table.Where(p => p.Key.Count(c => c == '_') == 1 && p.Value.Count(c => c == '_') != 1).Select(p => p.Key).ToList();
            check(badKeys.Count == 0, $"{code}: every text with an access key keeps exactly one" + (badKeys.Count > 0 ? ": " + string.Join(", ", badKeys) : ""));
            check(table["CHECK Constraint Operations…"].Contains("CHECK", StringComparison.Ordinal), $"{code}: the SQL word CHECK stays untranslated");
        }

        var options = new EnvironmentOptions(EnvironmentOptions.DefaultPath);
        var saved = options.Load();
        Localization.Use("pt-BR");
        var window = new MainWindow { ShowInTaskbar = false };
        OptionsDialog? dialog = null;
        try
        {
            check(Localization.Current == "pt-BR" && Localization.T("_Tools") == "_Ferramentas" && Localization.T("Not in the table") == "Not in the table", "T translates known texts and keeps the others in English");
            var commandsBefore = KeyboardMap.Commands((Menu)window.FindName("MainMenu")).Select(c => c.Name).ToList();
            window.Show(); await Idle();
            Localization.Translate(window);
            var menu = (Menu)window.FindName("MainMenu");
            var tools = menu.Items.OfType<MenuItem>().Single(i => Equals(Localization.EnglishHeader(i, i.Header), "_Tools"));
            check((string)tools.Header == "_Ferramentas" && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "_Arquivo") && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Aj_uda"),
                "The main menu is in Portuguese: " + string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header)));
            var optionsItem = tools.Items.OfType<MenuItem>().Single(i => Equals(Localization.EnglishHeader(i, i.Header), "_Options…"));
            check((string)optionsItem.Header == "_Opções…", "Tools ▸ Options… is translated");
            var theme = tools.Items.OfType<MenuItem>().Single(i => i.Name == "ThemeMenu");
            check(theme.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Cool Breeze") && theme.Items.OfType<MenuItem>().Any(i => (string)i.Header == "C_laro"), "Theme names stay as in SSMS; Light and Dark are translated");
            var commandsAfter = KeyboardMap.Commands(menu).Select(c => c.Name).ToList();
            check(commandsAfter.SequenceEqual(commandsBefore), "Keyboard command names stay in English, so saved shortcuts keep working in any language");

            // M1126: the other menus, the toolbars and the status line.
            MenuItem Top(string english) => menu.Items.OfType<MenuItem>().Single(i => Equals(Localization.EnglishHeader(i, i.Header), english));
            static IEnumerable<MenuItem> All(ItemsControl parent) => parent.Items.OfType<MenuItem>().SelectMany(i => All(i).Prepend(i));
            var portuguese = Localization.Load("pt-BR");
            var untranslated = new[] { "_File", "_Edit", "_View", "_Query", "_Window", "_Help", "_Permissions" }.SelectMany(h => All(Top(h)))
                .Where(i => i.Header is string h && h.Length > 0 && Localization.EnglishHeader(i, null) is null && !portuguese.ContainsKey(h) && !h.Contains("CRLF") && h is not ("LF" or "CR"))
                .Select(i => (string)i.Header).ToList();
            check(untranslated.Count == 0, "Every fixed item of File, Edit, View, Query, Window, Help and Permissions is translated" + (untranslated.Count > 0 ? ": " + string.Join(", ", untranslated.Take(12)) : ""));
            var execute = All(Top("_Query")).Single(i => Equals(Localization.EnglishHeader(i, i.Header), "_Execute"));
            check((string)execute.Header == "_Executar" && execute.InputGestureText == "Alt+X", "Query ▸ Execute is translated; its shortcut text stays");
            var buttons = ((ToolBar)window.FindName("StandardToolBar")).Items.OfType<FrameworkElement>().ToList();
            check(buttons.Any(b => b.ToolTip is "Salvar (Ctrl+S)" && System.Windows.Automation.AutomationProperties.GetName(b) == "Salvar"), "The Standard toolbar tooltips and names are translated");
            var status = (TextBlock)window.FindName("Status");
            status.Text = "Ready";
            check(status.Text == "Pronto", "Ready is translated: " + status.Text);
            status.Text = "Connected";
            check(status.Text == "Conectado", "A status message set by code is translated: " + status.Text);
            status.Text = "Save failed; query remains open: disk full";
            check(status.Text == "Falha ao salvar; a consulta continua aberta: disk full", "A message completed by code keeps the completion after the translated prefix: " + status.Text);
            status.Text = "Something PGMS does not know";
            check(status.Text == "Something PGMS does not know", "An unknown message stays in English");

            // M1127: Object Explorer folders and context menus, Connect.
            var catalog = new PGMS.PostgreSql.CatalogService("Host=127.0.0.1;Port=1");
            var tablesFolder = new PGMS.Desktop.ViewModels.CatalogNode(new PGMS.PostgreSql.CatalogEntry(PGMS.PostgreSql.CatalogKind.Tables, "Tables", "app", "public"), catalog);
            var table = new PGMS.Desktop.ViewModels.CatalogNode(new PGMS.PostgreSql.CatalogEntry(PGMS.PostgreSql.CatalogKind.Relation, "Tables", "app", "public", 42), catalog);
            check(tablesFolder.Name == "Tables" && table.Name == "Tables", "Object Explorer folders stay in English in every language (B1 review): " + tablesFolder.Name);
            tablesFolder.SetFilter("ord");
            check(tablesFolder.Name == "Tables (filtered)", "The filter suffix of a folder stays in English too: " + tablesFolder.Name);
            var folderMenu = new ContextMenu();
            window.FillExplorerMenu(folderMenu, tablesFolder);
            Localization.Translate(folderMenu); // What the ContextMenu.Opened handler of Localization.Initialize does.
            var newTable = folderMenu.Items.OfType<MenuItem>().Single(i => i.Name == "ExplorerNewTable");
            check(Equals(Localization.EnglishHeader(newTable, null), "New _Table…") && (string)newTable.Header == "Nova _Tabela…","The Object Explorer context menu is translated when it opens: " + newTable.Header);
            check(Localization.Message("Select Top 1000 Rows") == Localization.T("Select Top {0} Rows").Replace("{0}", "1000"), "A pattern keeps the number of Select Top n Rows: " + Localization.Message("Select Top 1000 Rows"));
            var connect = new ConnectionDialog { ShowInTaskbar = false };
            try
            {
                connect.Show(); await Idle();
                Localization.Translate(connect);
                var ssl = (ComboBox)connect.FindName("Ssl");
                check(connect.Title == "Conectar ao Servidor", "The Connect window is translated: " + connect.Title);
                check(ssl.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).Contains("Require"), "The SSL modes stay as the values PGMS reads back");
                check(FindAccessText(connect, "Nome do _servidor:") && !FindAccessText(connect, "_Server name:"), "Labels written with AccessText are translated");
            }
            finally { connect.Close(); }

            // M1138 (B1 review): PGMS message boxes, buttons in the interface language, same keys and defaults as the Windows box.
            var seen = new List<string>();
            void Inspect(PgmsMessageBox box)
            {
                var buttons = Descendants(box).OfType<Button>().ToList();
                seen.Add(string.Join("|", box.ButtonLabels));
                if (box.Choices.Count == 2 && box.Choices[0] == MessageBoxResult.Yes)
                {
                    check(box.ButtonLabels.SequenceEqual(["Sim", "Não"]) && box.Owner == window && !buttons.Any(b => b.IsCancel) && ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(box), buttons.Single(b => b.IsDefault)) && box.DefaultResult == MessageBoxResult.No
                        && box.Title == Localization.T("Drop Sequence"), "Yes/No in Portuguese, owned by the window, default No focused, no Esc: " + string.Join(", ", box.ButtonLabels));
                    check(box.Message.StartsWith("Remover este índice?", StringComparison.Ordinal) && box.Message.EndsWith("DROP INDEX public.ix;", StringComparison.Ordinal), "The message is translated line by line and keeps the SQL");
                    check(System.Windows.Automation.AutomationProperties.GetName(buttons[0]) == "Sim" && System.Windows.Automation.AutomationProperties.GetHelpText(box) == box.Message, "The Narrator reads the button names and the message");
                    check(box.CopyText().Contains("Remover este índice?", StringComparison.Ordinal) && box.CopyText().Contains("Sim   Não", StringComparison.Ordinal), "Ctrl+C copies caption, text and buttons, as in Windows");
                    check(Descendants(box).OfType<Image>().Any(i => i.Source is not null), "The question icon is shown");
                    box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, System.Windows.Input.Key.S) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    return; // S (Sim) answers without Alt, like Y in the English Windows box
                }
                check(buttons.Any(b => b.IsCancel), "A box with Cancel (or only OK) answers Esc: " + string.Join(", ", box.ButtonLabels));
                box.Answer(box.Choices[^1]);
            }
            PgmsMessageBox.Opened += Inspect;
            try
            {
                var yes = LocalizedMessageBox.Show(window, "Drop this index?\n\nDROP INDEX public.ix;", "Drop Sequence", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                check(yes == MessageBoxResult.Yes, "The access key of the translated Yes answers Yes: " + yes);
                check(LocalizedMessageBox.Show(window, "Something PGMS does not know", "x", MessageBoxButton.YesNoCancel) == MessageBoxResult.Cancel
                    && LocalizedMessageBox.Show(window, "Something PGMS does not know") == MessageBoxResult.OK && LocalizedMessageBox.Show(window, "x", "x", MessageBoxButton.OKCancel) == MessageBoxResult.Cancel,
                    "Yes/No/Cancel, OK and OK/Cancel return the button chosen");
                check(seen.Count == 4 && seen[1] == "Sim|Não|Cancelar" && seen[2] == "OK" && seen[3] == "OK|Cancelar", "Every button is in Portuguese: " + string.Join("; ", seen));
            }
            finally { PgmsMessageBox.Opened -= Inspect; }

            // M1139 (B1 review): titles of tabs and tool windows composed in code, and composed messages translated whole.
            check(DockTitleTranslation.Display("Showplan Comparison", false) == Localization.T("Showplan Comparison") && Localization.T("Showplan Comparison") != "Showplan Comparison",
                "A document title set in code is drawn in the language: " + DockTitleTranslation.Display("Showplan Comparison", false));
            check(DockTitleTranslation.Display("Recovered - draft-1.sql*", true) == "Recuperado - draft-1.sql*" && DockTitleTranslation.Display("My Errors.sql*", true) == "My Errors.sql*",
                "A restored query tab translates only its recovery mark; a query or file name stays as it is");
            check(DockTitleTranslation.Display("127.0.0.1:5432 (app) - Activity Monitor", false).StartsWith("127.0.0.1:5432 (app) - ", StringComparison.Ordinal)
                && !DockTitleTranslation.Display("127.0.0.1:5432 (app) - Activity Monitor", false).EndsWith("Activity Monitor", StringComparison.Ordinal),
                "The Activity Monitor title keeps the server and translates the rest: " + DockTitleTranslation.Display("127.0.0.1:5432 (app) - Activity Monitor", false));
            // A window opened after the start, as every window of PGMS is (Localization.Initialize starts it before the first window).
            var titled = new MainWindow { ShowInTaskbar = false };
            List<string> dockTexts;
            try
            {
                titled.Show(); await Idle();
                titled.ShowToolWindow("errorlist"); await Idle(); await Idle();
                dockTexts = VisualDescendants((DependencyObject)titled.FindName("DockManager")).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            }
            finally { titled.Close(); }
            check(dockTexts.Contains(Localization.T("Error List")) && !dockTexts.Contains("Error List"), "The Error List tool window shows its title in the language, in its tab and its caption: "
                + string.Join(", ", dockTexts.Where(t => t.Contains("Error") || t.Contains("Erro"))));
            status.Text = "Disconnected 3 queries. 1 running query keeps its connection.";
            check(status.Text == "3 consultas desconectadas. 1 consulta em execução mantém a conexão.", "A plural message is translated as a whole sentence: " + status.Text);
            var composed = Localization.Load("pt-BR").Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"\{\d+\}") && p.Value != p.Key).ToList();
            var untouched = composed.Where(p => { string sample = System.Text.RegularExpressions.Regex.Replace(p.Key, @"\{(\d+)\}", m => "Zq" + m.Groups[1].Value + "q"); return Localization.Message(sample) == sample; }).Select(p => p.Key).ToList();
            check(composed.Count > 400 && untouched.Count == 0, $"Every one of the {composed.Count} message patterns translates a composed message" + (untouched.Count > 0 ? ": " + string.Join(" | ", untouched.Take(5)) : ""));

            // M1128: message boxes line by line, and the query tab.
            check(LocalizedMessageBox.Text("Drop this index?\n\nDROP INDEX public.ix;") == "Remover este índice?\n\nDROP INDEX public.ix;", "A message box translates its sentences and keeps the SQL it quotes");
            check(LocalizedMessageBox.Text("Drop role app_reader? No owned objects or grants will be removed automatically.\r\nServer: db1:5432").StartsWith("Remover a role app_reader? ", StringComparison.Ordinal),
                "A message with a name uses its pattern and keeps the name: " + LocalizedMessageBox.Text("Drop role app_reader? No owned objects or grants will be removed automatically."));
            var query = new QueryView();
            Localization.Translate(query); // What the UserControl Loaded handler of Localization.Initialize does.
            check((string)((TabItem)query.FindName("ResultsTab")).Header == "Resultados" && (string)((TabItem)query.FindName("MessagesTab")).Header == "Mensagens", "The Results and Messages tabs of a query are translated");
            check(((TextBox)query.FindName("Messages")).Text == "Connect to a PostgreSQL server to execute this query.", "Text boxes keep their text (the Messages pane holds output)");

            // M1129: dialogs, the Search index, and data drawn on canvases.
            check(FeatureSearch.Text("ファイル(_F)") == "ファイル" && FeatureSearch.Text("Nova _Tabela…") == "Nova Tabela…", "Search shows menu texts without access keys, including the Japanese (_F) form");
            var index = new OptionsDialog(options) { ShowInTaskbar = false };
            try
            {
                Localization.Translate(index);
                var settings = FeatureSearch.Settings(index.SearchIndex());
                check(settings.Any(r => r.Path == "Ferramentas > Opções > Ambiente > Configurações Internacionais" && r.OptionsPage == "Ambiente > Configurações Internacionais"),
                    "The Search index of Options is in the interface language: " + string.Join(" | ", settings.Take(3).Select(r => r.Path)));
                check(index.SelectPagePath("Environment > International Settings") && index.SelectPagePath("Ambiente > Configurações Internacionais"), "An Options page opens by its English or its translated path");
            }
            finally { index.Close(); }
            var canvas = new Canvas();
            var columnName = new TextBlock { Text = "Value" };
            canvas.Children.Add(columnName);
            var host = new StackPanel { Children = { canvas, new TextBlock { Text = "Value" } } };
            Localization.Translate(host);
            check(columnName.Text == "Value" && ((TextBlock)host.Children[1]).Text != "Value", "Texts drawn on a canvas (diagram tables and columns) are data and stay; a fixed label is translated");

            dialog = new OptionsDialog(options) { ShowInTaskbar = false };
            dialog.Show(); await Idle();
            Localization.Translate(dialog);
            var pages = (TreeView)dialog.FindName("Pages");
            var environment = pages.Items.OfType<TreeViewItem>().First();
            check((string)environment.Header == "Ambiente" && dialog.Title == "Opções" && environment.Items.OfType<TreeViewItem>().Any(p => (string)p.Header == "Configurações Internacionais"),
                "The Options tree and title are translated");
            var queryOptions = new QueryOptionsDialog(new QueryOptions()) { ShowInTaskbar = false };
            queryOptions.Show(); await Idle();
            Localization.Translate(queryOptions);
            var queryPages = ((TreeView)queryOptions.FindName("Pages")).Items.OfType<TreeViewItem>().ToList();
            check(queryPages.Select(p => (string)p.Header).SequenceEqual(["Execução", "Resultados"]) && (string)queryPages[0].Items.OfType<TreeViewItem>().First().Header == "Geral",
                "The Query Options page tree is translated too (it stayed in English at the B5 pilot, M1134)");
            queryOptions.Close();
            dialog.SelectPage("International Settings"); await Idle();
            var panel = (FrameworkElement)dialog.FindName("InternationalPanel");
            var box = (ComboBox)dialog.FindName("LanguageBox");
            check(panel.Visibility == Visibility.Visible && box.Items.Count == 9 && box.SelectedIndex == 0 && (string)((ComboBoxItem)box.Items[0]).Content == "Igual ao Microsoft Windows"
                && box.Items.OfType<ComboBoxItem>().Skip(1).Select(i => (string)i.Content).SequenceEqual(Localization.Languages.Select(l => l.Name)),
                "International Settings opens by its English name and lists Same as Microsoft Windows, then the languages by their own names");
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(i => (string)i.Tag == "ja");
            ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            check(options.Load().Language == "ja" && Localization.Current == "pt-BR", "OK saves the language; the one in use changes only when PGMS starts again");
            dialog = new OptionsDialog(options) { ShowInTaskbar = false };
            dialog.Show(); await Idle();
            var again = (ComboBox)dialog.FindName("LanguageBox");
            again.SelectedIndex = 0;
            ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            check(options.Load().Language is null, "Same as Microsoft Windows is saved as no language");
        }
        finally
        {
            if (dialog is { IsVisible: true }) dialog.Close();
            window.Close();
            options.Update(_ => saved);
            Localization.Use("en");
        }
        check(Localization.Current == "en" && Localization.T("_Tools") == "_Tools", "Back to English for the other checks");
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(element); node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node)) yield return node;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandchild in VisualDescendants(child)) yield return grandchild;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }

    private static bool FindAccessText(DependencyObject root, string text)
    {
        if (root is AccessText access && access.Text == text) return true;
        return LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Any(c => FindAccessText(c, text));
    }
}
