using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using PGMS.Desktop;

/// <summary>PGMS-082 (M1121): Navigate Backward/Forward, All Windows, Pin Tab, Reset Current Object Explorer Details View and the toolbar buttons.</summary>
internal static class NavigationTabChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        try
        {
            await Idle();
            MenuItem Named(string name) => (MenuItem)shell.FindName(name);
            var view = (ItemsControl)Named("FullScreenMenu").Parent;
            int full = view.Items.IndexOf(Named("FullScreenMenu"));
            check(view.Items.IndexOf(Named("AllWindowsMenu")) == full + 1 && view.Items.IndexOf(Named("NavigateBackwardMenu")) == full + 2 && view.Items.IndexOf(Named("NavigateForwardMenu")) == full + 3 && view.Items.IndexOf(Named("PropertiesWindowMenu")) == full + 4,
                "View: Full Screen, All Windows, Navigate Backward, Navigate Forward, Properties Window, as in SSMS 22.11");
            check(Named("AllWindowsMenu").InputGestureText == "Shift+Alt+M" && Named("NavigateBackwardMenu").InputGestureText == "Ctrl+-" && Named("NavigateForwardMenu").InputGestureText == "Ctrl+Shift+-", "The SSMS shortcuts are shown");
            check(MainWindow.NavigateBackwardCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.OemMinus && g.Modifiers == ModifierKeys.Control)
                && MainWindow.NavigateForwardCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.OemMinus && g.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                && MainWindow.AllWindowsCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.M && g.Modifiers == (ModifierKeys.Shift | ModifierKeys.Alt)), "Ctrl+-, Ctrl+Shift+- and Shift+Alt+M are bound");
            var windowMenu = Named("WindowMenu");
            check(windowMenu.Items.IndexOf(Named("PinTabMenu")) == windowMenu.Items.IndexOf(Named("MoveToNewWindowMenu")) + 1 && windowMenu.Items.IndexOf(Named("ResetObjectDetailsMenu")) < windowMenu.Items.IndexOf(Named("WindowsDialogMenu")) && windowMenu.Items.IndexOf(Named("ResetObjectDetailsMenu")) > windowMenu.Items.IndexOf(Named("RestoreClosedTabMenu")),
                "Window: Pin Tab after Move To New Window; Reset Current Object Explorer Details View before Windows…");

            // Navigate Backward/Forward across tabs and caret jumps.
            string text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"select {i};"));
            var first = shell.NewQueryTab(text); await Idle();
            var second = shell.NewQueryTab("select 'second';"); await Idle();
            var status = (TextBlock)shell.FindName("Status");
            check(!MainWindow.NavigateForwardCommand.CanExecute(null, shell), "Nothing to go forward to at first");
            check(shell.NavigateBackward() && shell.DockManager.ActiveContent == first, "Navigate Backward returns to the tab used before");
            check(shell.NavigateForward() && shell.DockManager.ActiveContent == second, "Navigate Forward goes to it again");
            check(shell.NavigateBackward() && shell.DockManager.ActiveContent == first, "Back again to the first tab");
            first.Editor.CaretOffset = 0; await Idle();
            first.Editor.CaretOffset = first.Editor.Document.GetLineByNumber(50).Offset; await Idle();
            int line50 = first.Editor.CaretOffset;
            first.Editor.CaretOffset = first.Editor.Document.GetLineByNumber(52).Offset; await Idle();
            check(shell.NavigateBackward() && shell.DockManager.ActiveContent == first && first.Editor.CaretOffset == 0, "A jump of 10 lines or more is a place: back returns to line 1");
            check(shell.NavigateForward() && first.Editor.CaretOffset == first.Editor.Document.GetLineByNumber(52).Offset, "Forward returns to where the caret was after the jump (small moves update the place)");
            check(line50 > 0, "The jump was made");
            first.Editor.Document.Insert(first.Editor.Document.TextLength, "\n" + string.Join("\n", Enumerable.Range(1, 20).Select(i => "-- pasted"))); await Idle();
            int places = shell.Navigation.Places.Count;
            first.Editor.CaretOffset = first.Editor.Document.TextLength; await Idle();
            check(shell.Navigation.Places.Count == places + 1, "Ctrl+End style jump without an edit is remembered");
            int afterEdit = shell.Navigation.Places.Count;
            first.Editor.Document.Insert(0, string.Concat(Enumerable.Repeat("-- line\n", 15))); await Idle();
            check(shell.Navigation.Places.Count == afterEdit, "A caret moved by an edit is not a navigation place");
            await Idle();
            var third = shell.NewQueryTab("select 3;"); await Idle();
            third.IsDirty = false;
            check(shell.NavigateBackward() && shell.DockManager.ActiveContent == first, "Back from a new tab");
            check(MainWindow.NavigateForwardCommand.CanExecute(null, shell), "Forward is possible after going back");
            await shell.CloseQueryAsync(third); await Idle();
            check(!MainWindow.NavigateForwardCommand.CanExecute(null, shell), "A closed tab is dropped from the places");
            status.Text = "";
            while (shell.NavigateBackward()) await Idle();
            check(status.Text == "No earlier place to go back to.", "At the first place the status says so");

            // Pin Tab.
            // New tabs go first by default since M1143 (as in SSMS), so the tab opened first is the later one: that one is pinned.
            var firstDoc = shell.DockManager.Layout.Descendents().OfType<LayoutDocument>().First(d => d.Content == second);
            var secondDoc = shell.DockManager.Layout.Descendents().OfType<LayoutDocument>().First(d => d.Content == first);
            second.IsDirty = false;
            var pane = (LayoutDocumentPane)secondDoc.Parent;
            check(pane.IndexOfChild(secondDoc) > pane.IndexOfChild(firstDoc), "The tab opened first is after the one opened next (new tabs go first, as in SSMS)");
            check(shell.TogglePinTab(secondDoc) && pane.IndexOfChild(secondDoc) == 0 && secondDoc.IconSource is not null && shell.IsPinned(secondDoc), "Pin Tab moves the tab to the start and shows a pin");
            secondDoc.IsActive = true; await Idle();
            windowMenu.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent, windowMenu)); await Idle();
            check(Named("PinTabMenu").IsChecked && Named("PinTabMenu").IsEnabled, "Window ▸ Pin Tab is checked for a pinned tab");
            first.IsDirty = false;
            shell.CloseAllPrompt = _ => System.Windows.MessageBoxResult.No;
            await shell.CloseAllTabsAsync(); await Idle();
            var left = shell.DockManager.Layout.Descendents().OfType<LayoutDocument>().ToList();
            check(left.Count == 1 && left[0] == secondDoc, "Close All Tabs keeps the pinned tab");
            check(!shell.TogglePinTab(secondDoc) && secondDoc.IconSource is null && !shell.IsPinned(secondDoc), "The same command unpins it");

            // Reset Current Object Explorer Details View.
            var details = shell.ShowObjectExplorerDetails(); await Idle();
            details.Grid.Columns[2].DisplayIndex = 0;
            details.Grid.Columns[0].Width = 40;
            details.Grid.Columns[1].SortDirection = System.ComponentModel.ListSortDirection.Descending;
            details.Grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Type", System.ComponentModel.ListSortDirection.Descending));
            details.Search.Text = "abc";
            windowMenu.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent, windowMenu)); await Idle();
            check(Named("ResetObjectDetailsMenu").IsEnabled, "Reset Current Object Explorer Details View is available while Object Explorer Details is the active tab");
            check(shell.ResetObjectDetailsView(), "Reset runs");
            check(details.Grid.Columns.Select(c => c.DisplayIndex).SequenceEqual([0, 1, 2, 3]) && details.Grid.Columns[0].Width.IsStar && details.Grid.Columns[0].Width.Value == 2 && details.Grid.Columns.All(c => c.SortDirection is null) && details.Grid.Items.SortDescriptions.Count == 0 && details.Search.Text == "",
                "Columns back in order and width, no sorting, no search");
            secondDoc.IsActive = true; await Idle();
            windowMenu.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent, windowMenu)); await Idle();
            check(!Named("ResetObjectDetailsMenu").IsEnabled, "It is unavailable when another tab is active, as in SSMS");

            // All Windows in Full Screen.
            check(!MainWindow.AllWindowsCommand.CanExecute(null, shell), "All Windows is unavailable outside Full Screen, as observed in SSMS 22.11");
            shell.SetFullScreen(true); await Idle();
            check(MainWindow.AllWindowsCommand.CanExecute(null, shell), "In Full Screen it is available");
            var explorer = shell.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "explorer");
            check(!shell.ToggleAllWindows() && explorer.IsHidden && !shell.AreAllWindowsShown, "All Windows hides the tool windows, leaving the documents");
            check(shell.ToggleAllWindows() && !explorer.IsHidden, "The same command shows them again");
            shell.ToggleAllWindows(); await Idle();
            shell.SetFullScreen(false); await Idle();
            check(!explorer.IsHidden && shell.AreAllWindowsShown, "Leaving Full Screen shows the tool windows again");

            // Toolbar buttons.
            string[] standard = ["Navigate Backward", "Navigate Forward", "Open File", "Save", "Save All", "New Query", "Database Engine Query", "Undo", "Redo", "Activity Monitor", "Find in Files"];
            var standardBar = (ToolBar)shell.FindName("StandardToolBar");
            check(standardBar.Items.OfType<System.Windows.Controls.Primitives.ButtonBase>().Select(System.Windows.Automation.AutomationProperties.GetName).SequenceEqual(standard), "Standard toolbar buttons in the SSMS 22.11 order");
            string[] editor = ["Execute", "Cancel Executing Query", "Parse", "Display Estimated Execution Plan", "Query Options", "IntelliSense Enabled", "Include Actual Execution Plan", "Include Live Query Statistics", "Include Client Statistics", "Results to Text", "Results to Grid", "Results to File", "Comment Selection", "Uncomment Selection", "Decrease Line Indent", "Increase Line Indent", "Specify Values for Template Parameters"];
            var editorBar = (ToolBar)shell.FindName("SqlEditorToolBar");
            var editorNames = editorBar.Items.OfType<System.Windows.Controls.Primitives.ButtonBase>().Select(System.Windows.Automation.AutomationProperties.GetName).Where(n => n.Length > 0).ToList();
            check(editorNames.SequenceEqual(editor), "SQL Editor toolbar buttons in the SSMS 22.11 order: " + string.Join(", ", editorNames));
            var query = shell.NewQueryTab("select 1;"); await Idle();
            var intelliButton = (System.Windows.Controls.Primitives.ToggleButton)shell.FindName("IntelliSenseButton");
            var clientButton = (System.Windows.Controls.Primitives.ToggleButton)shell.FindName("ClientStatisticsButton");
            var liveButton = (System.Windows.Controls.Primitives.ToggleButton)shell.FindName("LiveStatisticsButton");
            check(intelliButton.IsEnabled && intelliButton.IsChecked == query.IntelliSenseEnabled && clientButton.IsEnabled && liveButton.IsEnabled && ((System.Windows.Controls.Button)shell.FindName("ParseButton")).IsEnabled == false, "The toggle buttons follow the active query; Parse needs a connection");
            bool wasClient = query.IncludeClientStatistics;
            clientButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(query.IncludeClientStatistics != wasClient && clientButton.IsChecked == query.IncludeClientStatistics, "Include Client Statistics button toggles the query option");
            bool wasLive = query.IncludeLiveStatistics;
            liveButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(query.IncludeLiveStatistics != wasLive && liveButton.IsChecked == query.IncludeLiveStatistics, "Include Live Query Statistics button toggles the query option");
            bool wasIntelli = query.IntelliSenseEnabled;
            intelliButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(query.IntelliSenseEnabled != wasIntelli && intelliButton.IsChecked == query.IntelliSenseEnabled, "IntelliSense Enabled button toggles IntelliSense");
            query.Editor.Text = "select 1;\nselect 2;"; query.Editor.SelectAll(); query.Editor.Focus(); await Idle();
            ((RoutedCommand)((System.Windows.Controls.Button)shell.FindName("CommentSelectionButton")).Command).Execute(null, query.Editor.TextArea);
            await Idle();
            check(query.Editor.Text.StartsWith("--", StringComparison.Ordinal), "Comment Selection button comments the selected lines:\n" + query.Editor.Text);
            query.IsDirty = false;
        }
        finally { foreach (var tab in shell.OpenQueryViews()) tab.IsDirty = false; shell.Close(); await shell.CloseCompletion; }
    }
}
