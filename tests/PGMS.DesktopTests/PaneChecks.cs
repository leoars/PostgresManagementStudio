using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using PGMS.Desktop;

/// <summary>PGMS-069 (M1092): Window ▸ Next/Previous Pane, Show Results Pane, Split Tab, Dock/Auto Hide/Move To New Window, window layouts, Auto Hide All; View ▸ Full Screen.</summary>
internal static class PaneChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static bool Gesture(RoutedUICommand command, Key key, ModifierKeys modifiers) => command.InputGestures.OfType<KeyGesture>().Any(g => g.Key == key && g.Modifiers == modifiers);
        check(Gesture(MainWindow.NextPaneCommand, Key.F6, ModifierKeys.None) && Gesture(MainWindow.PreviousPaneCommand, Key.F6, ModifierKeys.Shift) && Gesture(MainWindow.ShowResultsPaneCommand, Key.R, ModifierKeys.Control) && Gesture(MainWindow.FullScreenCommand, Key.Enter, ModifierKeys.Shift | ModifierKeys.Alt), "F6, Shift+F6, Ctrl+R and Shift+Alt+Enter, as in SSMS 22.11");

        // Layout store on its own file.
        string file = Path.Combine(Path.GetTempPath(), "pgms-layouts-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var store = new WindowLayoutStore(file);
            for (int i = 1; i <= 10; i++) store.Put(new WindowLayout("L" + i, "<x/>"));
            bool full = false; try { store.Put(new WindowLayout("L11", "<x/>")); } catch (InvalidOperationException) { full = true; }
            store.Put(new WindowLayout("l3", "<y/>"));
            check(full && store.Load().Count == 10 && store.Load()[2] is { Name: "l3", Xml: "<y/>" }, "At most ten layouts; the same name (any case) replaces one in place");
            var manage = new ManageLayoutsDialog(store) { ShowInTaskbar = false, RenamePrompt = _ => "First" };
            manage.Layouts.SelectedIndex = 1;
            check(manage.Move(-1) && store.Load()[0].Name == "L2" && manage.Layouts.SelectedIndex == 0, "Move Up reorders and keeps the selection");
            check(manage.Rename() && store.Load()[0].Name == "First", "Rename");
            manage.RenamePrompt = _ => "l3";
            check(!manage.Rename() && store.Load()[0].Name == "First", "Rename refuses a name already used");
            check(manage.Delete() && store.Load().Count == 9 && store.Load()[0].Name == "L1", "Delete");
            manage.Close();
        }
        finally { File.Delete(file); }

        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        var errors = new List<string>(); shell.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            var menu = (MenuItem)shell.FindName("WindowMenu");
            var names = menu.Items.OfType<MenuItem>().Select(i => i.Name).Take(13).ToArray();
            check(names.SequenceEqual(["NextPaneMenu", "PreviousPaneMenu", "ShowResultsPaneMenu", "SplitTabMenu", "DockMenu", "AutoHideMenu", "MoveToNewWindowMenu", "SaveWindowLayoutMenu", "ApplyLayoutMenu", "ManageLayoutsMenu", "", "AutoHideAllMenu", "CloseAllTabsMenu"]), "Window lists the SSMS items in its order (Pin Tab left out): " + string.Join(",", names));
            check(((MenuItem)shell.FindName("FullScreenMenu")).InputGestureText == "Shift+Alt+Enter", "View ▸ Full Screen");

            var query = shell.NewQueryTab("select 1;\nselect 2;\n"); await Idle();
            check(query.IsResultsPaneVisible && !shell.ToggleResultsPane() && !query.IsResultsPaneVisible && query.OutputTabs.Visibility == Visibility.Collapsed, "Ctrl+R hides the results pane");
            check(!query.MovePane(true), "With the results hidden and no split there is no other pane");
            check(shell.ToggleResultsPane() && query.IsResultsPaneVisible, "Ctrl+R shows it again");

            query.SetSplit(true); await Idle();
            check(query.SplitEditor is { } split && split.Document == query.Editor.Document && split.IsVisible && query.Editor.IsVisible, "Split Tab adds a second view of the same document");
            query.SplitEditor!.Document.Insert(0, "-- x\n");
            check(query.Editor.Text.StartsWith("-- x\n", StringComparison.Ordinal), "An edit in one view shows in the other");
            var windowMenu = (MenuItem)shell.FindName("WindowMenu"); windowMenu.IsSubmenuOpen = true; await Idle();
            check((string)((MenuItem)shell.FindName("SplitTabMenu")).Header == "Unsplit Tab" && ((MenuItem)shell.FindName("ShowResultsPaneMenu")).IsChecked, "The menu says Unsplit Tab and checks Show Results Pane");
            windowMenu.IsSubmenuOpen = false; await Idle();

            shell.Activate(); query.Editor.TextArea.Focus(); await Idle();
            bool moved = query.MovePane(true); await Idle();
            bool inResults = query.OutputTabs.IsKeyboardFocusWithin;
            query.MovePane(true); await Idle();
            bool inSplit = query.SplitEditor.IsKeyboardFocusWithin;
            query.MovePane(false); await Idle();
            check(moved && inResults && inSplit && query.OutputTabs.IsKeyboardFocusWithin, $"F6 goes editor → results → split view and Shift+F6 back ({inResults}, {inSplit})");
            query.SetSplit(false); await Idle();
            check(query.SplitEditor is null && query.Editor.Parent is DockPanel && query.Editor.IsVisible && query.Editor.Text.StartsWith("-- x", StringComparison.Ordinal), "Unsplit Tab leaves the editor where it was, with the text");

            // Full Screen.
            var style = shell.WindowStyle;
            shell.SetFullScreen(true); await Idle();
            check(shell.IsFullScreen && shell.WindowStyle == WindowStyle.None && shell.WindowState == WindowState.Maximized && ((FrameworkElement)shell.FindName("MainToolBars")).Visibility == Visibility.Collapsed, "Full Screen: no title bar, maximized, no toolbars");
            shell.SetFullScreen(false); await Idle();
            check(!shell.IsFullScreen && shell.WindowStyle == style && ((FrameworkElement)shell.FindName("MainToolBars")).Visibility == Visibility.Visible, "The same command returns");

            // Layouts: save, auto hide all, apply.
            shell.LayoutNamePrompt = s => s == "Layout 1" ? "Mine" : null;
            check(shell.SaveWindowLayout() && shell.WindowLayouts.Load().Any(l => l.Name == "Mine"), "Save Window Layout under the name given");
            LayoutAnchorable Explorer() => shell.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().First(a => a.ContentId == "explorer");
            check(shell.AutoHideAll() >= 1 && Explorer().IsAutoHidden, "Auto Hide All sends Object Explorer to the edge");
            check(shell.ApplyWindowLayout("Mine") && !Explorer().IsAutoHidden && Explorer().Content is not null, "Apply Window Layout brings the saved docking back");
            await Idle();
            var doc = shell.DockManager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.Content == query);
            check(doc is not null && shell.QueryDocuments.Contains(query), "Open documents stay open after applying a layout");
            check(!shell.ApplyWindowLayout("missing"), "An unknown layout does nothing");

            // Move To New Window and Dock for the active document.
            doc!.IsActive = true; await Idle();
            check(shell.FloatActive() && doc.IsFloating, "Move To New Window floats the active tab");
            await Idle();
            doc.IsActive = true;
            check(shell.DockActive() && !doc.IsFloating, "Dock puts it back");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in shell.QueryDocuments.ToArray()) q.IsDirty = false;
            shell.Close(); await shell.CloseCompletion;
        }
    }
}
