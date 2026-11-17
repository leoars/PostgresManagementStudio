using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using Npgsql;
using PGMS.Desktop;

/// <summary>PGMS-070 (M1093): View ▸ Properties Window (F4), docked on the right, with the active query tab's connection and last execution.</summary>
internal static class PropertiesWindowChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 600 && !done(); i++) { await Task.Delay(50); await Idle(); } }
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            await Idle();
            var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
            LayoutAnchorable? Anchorable() => dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "properties");
            string Value(string name) => FindEditor(window.PropertiesGrid, name) is TextBox box ? box.Text : "<missing>";

            var menu = (MenuItem)window.FindName("PropertiesWindowMenu");
            var view = (MenuItem)menu.Parent;
            check(menu.InputGestureText == "F4" && view.Items.IndexOf(menu) == view.Items.Count - 1, "View ▸ Properties Window (F4) is the last item of View, as in SSMS 22.11");
            check(Anchorable() is null, "The Properties window does not exist until it is asked for");

            var query = await window.OpenQueryTextAsync(connection, "SELECT g FROM generate_series(1, 7) AS g");
            await Idle();
            var builder = new NpgsqlConnectionStringBuilder(connection);
            query.Editor.Focus(); Keyboard.Focus(query.Editor.TextArea); await Idle();
            var f4 = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query.Editor.TextArea)!, 0, Key.F4) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            query.Editor.TextArea.RaiseEvent(f4);
            await Idle();
            var anchorable = Anchorable();
            check(f4.Handled && anchorable is { IsVisible: true, Title: "Properties", IsActive: true }, "F4 in the editor opens the Properties window and activates it");
            check(anchorable?.Parent is LayoutAnchorablePane pane && dock.Layout.Descendents().OfType<LayoutAnchorSide>().All(_ => true) && !(anchorable.IsFloating), "The Properties window is docked, not floating");
            check(ReferenceEquals(window.PropertiesSource, query), "Activating the Properties window keeps showing the query tab it came from");

            var categories = Visuals<ToggleButton>(window.PropertiesGrid).Where(b => b.Content is string).Select(b => (string)b.Content).ToList();
            check(categories.Contains("Aggregate Status") && categories.Contains("Connection") && categories.Contains("Connection Details"), "Categorized view groups Aggregate Status, Connection and Connection Details: " + string.Join(", ", categories));
            string pid = Value("Backend PID");
            check(Value("Login name") == builder.Username && Value("Database") == builder.Database && int.TryParse(pid, out _),
                "Connection Details show the login, the database and the backend PID");
            string expectedServer = builder.Port == 5432 ? builder.Host! : $"{builder.Host}:{builder.Port}";
            check(Value("Server name") == expectedServer && Value("Connection name") == expectedServer && Value("Name") == expectedServer, "Server name, Connection name and Name are host:port: " + Value("Server name"));
            check(Value("Server version").Length > 0 && Value("State") == "Open" && Value("Rows returned") == "0" && Value("Start time") == "", "Before any execution: version known, state Open, 0 rows and no start time");
            check(Value("Backend PID") == query.ConnectionDetails?.BackendPid.ToString(), "Backend PID is the one Npgsql reports for the tab's own session");
            check(FindEditor(window.PropertiesGrid, "TDS protocol version") is null && FindEditor(window.PropertiesGrid, "Session Tracing ID") is null, "SQL Server-only rows are not listed");

            await query.ExecuteAsync();
            await Until(() => Value("Rows returned") == "7");
            check(Value("Rows returned") == "7" && Value("Connection rows returned") == "7", "After Execute, Rows returned shows the last execution's 7 rows");
            check(Value("Start time").Length > 0 && Value("Finish time").Length > 0 && Value("Elapsed time").StartsWith("00:00:", StringComparison.Ordinal), "Start, finish and elapsed time of the last execution: " + Value("Elapsed time"));

            // Alphabetical order, like the second toolbar button in SSMS.
            var alphabetical = Visuals<ToggleButton>(window.PropertiesGrid).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Alphabetical");
            alphabetical.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            var names = Visuals<TextBox>(window.PropertiesGrid).Select(t => System.Windows.Automation.AutomationProperties.GetName(t)).ToList();
            check(names.SequenceEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) && names.Count > 15 && !Visuals<ToggleButton>(window.PropertiesGrid).Any(b => b.Content is "Connection Details"),
                "Alphabetical lists every property in name order without categories");
            var categorized = Visuals<ToggleButton>(window.PropertiesGrid).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Categorized");
            categorized.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();

            // Read-only: nothing can be typed into a value.
            check(FindEditor(window.PropertiesGrid, "Login name") is TextBox { IsReadOnly: true }, "Values are read-only");

            // A second query tab: the window follows the active document.
            var second = await window.OpenQueryTextAsync(connection, "SELECT 1");
            await Idle();
            check(ReferenceEquals(window.PropertiesSource, second) && Value("Rows returned") == "0" && Value("Backend PID") != pid, "Activating another query tab shows its own connection and executions");
            // Object Explorer becoming active keeps the document's properties (observed in SSMS 22.11).
            dock.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "explorer").IsActive = true; await Idle();
            check(ReferenceEquals(window.PropertiesSource, second), "Activating Object Explorer keeps the last document's properties");

            // A disconnected tab shows state Closed and empty connection values.
            var empty = window.NewQueryTab("SELECT 2"); await Idle();
            check(ReferenceEquals(window.PropertiesSource, empty) && Value("State") == "Closed" && Value("Login name") == "" && Value("Server name") == "", "A query tab without a connection shows state Closed and no connection values");

            // Closing the shown tab leaves no stale properties.
            empty.IsDirty = false;
            await window.CloseQueryAsync(empty); await Idle();
            check(!ReferenceEquals(window.PropertiesSource, empty), "Closing the shown tab drops its properties");

            // F4 in a combo box still opens the combo list instead (Windows behavior).
            var databases = (ComboBox)window.FindName("DatabaseName");
            bool handled = false;
            if (databases.IsEnabled)
            {
                databases.Focus(); await Idle();
                var comboF4 = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(databases)!, 0, Key.F4) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                anchorable!.Hide(); await Idle();
                databases.RaiseEvent(comboF4); await Idle();
                handled = comboF4.Handled && Anchorable()?.IsVisible == true;
                databases.IsDropDownOpen = false;
            }
            check(!handled, "F4 inside a combo box is left to the combo box");
            window.ShowPropertiesWindow(); await Idle();
            check(Anchorable()?.IsVisible == true, "View ▸ Properties Window shows the hidden window again");
            check(errors.Count == 0, "No error was shown: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var inner in Visuals<T>(child)) yield return inner;
        }
    }

    private static TextBox? FindEditor(DependencyObject root, string name) =>
        Visuals<TextBox>(root).FirstOrDefault(t => System.Windows.Automation.AutomationProperties.GetName(t) == name && t.IsVisible);
}
