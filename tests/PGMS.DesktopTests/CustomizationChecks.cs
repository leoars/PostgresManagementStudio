using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-072 (M1106): Environment ▸ Keyboard, View ▸ Toolbars with Tools ▸ Customize, and Tools ▸ External Tools. Nothing is started:
/// the launcher is replaced, and the dialogs are handed to the test instead of shown.</summary>
internal static class CustomizationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string folder = Path.GetFullPath(Path.Combine("artifacts", "customization", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var options = new EnvironmentOptions(EnvironmentOptions.DefaultPath);
        var original = options.Load();
        options.Update(d => d with { KeyboardShortcuts = null, ExternalTools = null, HiddenToolbars = null });
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        var started = new List<ProcessStartInfo>();
        window.ExternalToolLauncher = info => { started.Add(info); return true; };
        window.ExternalToolArgumentsPrompt = (_, _) => throw new InvalidOperationException("prompt not expected");
        try
        {
            await Idle();
            MenuItem Named(string name) => (MenuItem)window.FindName(name);
            var tools = (ItemsControl)Named("OptionsMenu").Parent;
            int Index(string name) => tools.Items.IndexOf(Named(name));
            check(Index("ExternalToolsMenu") == Index("ImportExportSettingsMenu") - 3 && Index("ThemeMenu") == Index("ImportExportSettingsMenu") - 2 && Index("EditorAppearanceMenu") == Index("ImportExportSettingsMenu") - 1 &&Index("CustomizeMenu") == Index("ImportExportSettingsMenu") + 1 && Index("CustomizeMenu") == Index("OptionsMenu") - 1,
                "Tools: External Tools…, Import and Export Settings…, Customize…, Options…, in the SSMS order");
            var toolbarsMenu = Named("ToolbarsMenu");
            check(toolbarsMenu.Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(["Standard", "SQL Editor", "_Customize…"]), "View ▸ Toolbars lists Standard, SQL Editor and Customize…");

            // Shortcut names
            check(KeyboardMap.GestureName(Key.E, ModifierKeys.Control | ModifierKeys.Shift) == "Ctrl+Shift+E" && KeyboardMap.GestureName(Key.K, ModifierKeys.Shift | ModifierKeys.Alt) == "Shift+Alt+K"
                && KeyboardMap.GestureName(Key.D1, ModifierKeys.Control) == "Ctrl+1" && KeyboardMap.GestureName(Key.F12, ModifierKeys.None) == "F12", "Shortcut names follow the SSMS order Ctrl+Shift+Alt+Key");
            check(KeyboardMap.GestureName(Key.A, ModifierKeys.None) is null && KeyboardMap.GestureName(Key.A, ModifierKeys.Shift) is null && KeyboardMap.GestureName(Key.LeftCtrl, ModifierKeys.Control) is null,
                "Plain or Shift letters and modifier keys alone cannot be shortcuts");

            var commands = window.KeyboardCommands();
            check(commands.Any(c => c.Name == "Query.Execute" && c.DefaultShortcut == "Alt+X") && commands.Any(c => c.Name == "View.ObjectExplorer" && c.DefaultShortcut == "F8") && commands.Any(c => c.Name == "View.WordWrap"),
                "Keyboard lists the menu commands as Menu.Command with the PGMS shortcut");
            check(!commands.Any(c => c.Name.StartsWith("File.RecentFiles", StringComparison.Ordinal)) && commands.Select(c => c.Name).Distinct().Count() == commands.Count, "Recent Files entries are not listed and names are unique");

            // Keyboard page
            var query = window.NewQueryTab("SELECT 1;\nSELECT 2;");
            await Idle();
            var dialog = new OptionsDialog(options, null, commands) { ShowInTaskbar = false };
            dialog.SelectPage("Keyboard");
            check(((FrameworkElement)dialog.FindName("KeyboardPanel")).Visibility == Visibility.Visible, "SelectPage opens Environment ▸ Keyboard");
            ((TextBox)dialog.FindName("KeyboardFilterBox")).Text = "wordwrap";
            check(dialog.ShownCommands.Contains("View.WordWrap") && dialog.ShownCommands.All(n => n.Contains("WordWrap", StringComparison.OrdinalIgnoreCase)) && dialog.ShownCommands.Count < commands.Count, "Show commands containing filters the list");
            dialog.SelectKeyboardCommand("View.WordWrap");
            dialog.PressGesture("Ctrl+Shift+Alt+W");
            check(dialog.UsedBy("Ctrl+Shift+Alt+W").Length == 0 && dialog.AssignShortcut() && dialog.ShortcutsOf("View.WordWrap") == "Ctrl+Shift+Alt+W", "Assign gives the selected command the pressed shortcut");
            dialog.PressGesture("Alt+X");
            check(dialog.UsedBy("Alt+X") == "Query.Execute (PGMS)", "Shortcut currently used by names the PGMS command");
            dialog.SelectKeyboardCommand("View.ObjectExplorer");
            dialog.PressGesture("Ctrl+Shift+Alt+W");
            check(dialog.AssignShortcut() && !dialog.AssignedShortcuts.ContainsKey("View.WordWrap") && dialog.ShortcutsOf("View.ObjectExplorer") == "Ctrl+Shift+Alt+W; F8 (PGMS)", "Assigning a shortcut again takes it from the other command");
            dialog.RemoveSelectedShortcut();
            check(dialog.AssignedShortcuts.Count == 0 && dialog.ShortcutsOf("View.ObjectExplorer") == "F8 (PGMS)", "Remove drops only the assigned shortcut");
            dialog.SelectKeyboardCommand("View.WordWrap"); dialog.PressGesture("Ctrl+Shift+Alt+W"); dialog.AssignShortcut();
            dialog.Accept(dialog, new RoutedEventArgs());
            check(options.Load().CustomShortcuts.TryGetValue("View.WordWrap", out var saved) && saved == "Ctrl+Shift+Alt+W", "OK saves the assignment");
            window.ApplyCustomization();
            check(Named("WordWrapMenu").InputGestureText == "Ctrl+Shift+Alt+W", "The menu shows the assigned shortcut");
            bool wrapBefore = query.WordWrap;
            // A menu click runs after the next render, as WPF does for every MenuItem.
            bool ran = window.RunAssignedShortcut("Ctrl+Shift+Alt+W"); await Idle();
            check(ran && query.WordWrap != wrapBefore, "The assigned shortcut runs the command, check mark state included");
            ran = window.RunAssignedShortcut("Ctrl+Shift+Alt+W"); await Idle();
            check(ran && query.WordWrap == wrapBefore, "Pressed again it toggles back");
            check(!window.RunAssignedShortcut("Ctrl+Shift+Alt+Q"), "A shortcut nobody assigned does nothing");
            var reset = new OptionsDialog(options, null, commands) { ShowInTaskbar = false };
            reset.SelectPage("Keyboard");
            ((Button)((DockPanel)((TextBlock)reset.FindName("KeyboardMessage")).Parent).Children[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            reset.Accept(reset, new RoutedEventArgs());
            window.ApplyCustomization();
            check(options.Load().KeyboardShortcuts is null && Named("WordWrapMenu").InputGestureText == "" && Named("ExecuteMenu").InputGestureText == "Alt+X", "Reset removes the assignments and the menus show the PGMS shortcuts again");

            // Toolbars
            var standard = (ToolBar)window.FindName("StandardToolBar");
            window.SetToolbarVisible("Standard", false);
            check(standard.Visibility == Visibility.Collapsed && options.Load().HiddenToolbars is ["Standard"], "Hiding a toolbar collapses it and remembers it");
            Named("ToolbarsMenu").RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, Named("ToolbarsMenu")));
            check(Named("StandardToolbarMenu").IsChecked == false && Named("SqlEditorToolbarMenu").IsChecked == true, "View ▸ Toolbars shows which toolbars are on");
            Window? customize = null;
            window.CustomizeDialogShown = d => customize = d;
            window.ShowCustomize();
            var boxes = customize is null ? [] : FindAll<CheckBox>(customize.Content as DependencyObject).ToList();
            check(boxes.Select(b => (string)b.Content).SequenceEqual(["Standard", "SQL Editor"]) && boxes[0].IsChecked == false && boxes[1].IsChecked == true, "Customize lists the toolbars with their state");
            boxes[0].IsChecked = true; boxes[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(standard.Visibility == Visibility.Visible && options.Load().HiddenToolbars is null, "Checking a toolbar in Customize shows it again");
            customize?.Close();
            options.Update(d => d with { HiddenToolbars = ["SQL Editor"] });
            var restartedWindow = new MainWindow { ShowInTaskbar = false };
            check(((ToolBar)restartedWindow.FindName("SqlEditorToolBar")).Visibility == Visibility.Collapsed && ((ToolBar)restartedWindow.FindName("StandardToolBar")).Visibility == Visibility.Visible, "A new window starts with the toolbars hidden as saved");
            restartedWindow.Close();
            options.Update(d => d with { HiddenToolbars = null });
            window.ApplyCustomization();

            // External Tools
            ExternalToolsDialog? tooling = null;
            window.ExternalToolsDialogShown = d => tooling = d;
            window.ShowExternalTools();
            check(tooling is not null && tooling.Tools.Count == 0, "External Tools opens empty");
            tooling!.Add(); tooling.Edit("Open in Notepad", "notepad.exe", "\"$(ItemPath)\" L$(CurLine) C$(CurCol)", "$(ItemDir)");
            tooling.Add(); tooling.Edit("Echo", "cmd.exe", "/c echo $(CurText)", "", promptForArguments: true);
            tooling.Add(); // left empty: dropped on OK
            tooling.SelectedIndex = 1; tooling.Move(-1);
            check(tooling.Tools.Select(t => t.Title).SequenceEqual(["Echo", "Open in Notepad", ""]), "Move Up changes the order");
            tooling.Accept();
            var savedTools = options.Load().Tools;
            check(savedTools.Select(t => t.Title).SequenceEqual(["Echo", "Open in Notepad"]) && started.Count == 0, "OK saves the tools in order, drops the empty one and runs nothing");
            var items = window.ExternalToolItems;
            check(items.Count == 2 && tools.Items.IndexOf(items[0]) == Index("ExternalToolsMenu") - 2 && (string)items[1].Header == "Open in Notepad", "The tools appear in the Tools menu right before External Tools…");
            check(!window.KeyboardCommands().Any(c => c.Name.Contains("Notepad", StringComparison.Ordinal)), "External tools are not Keyboard commands");

            string file = Path.Combine(folder, "report.sql");
            query.FilePath = file;
            query.Editor.Select(query.Editor.Text.IndexOf("SELECT 2", StringComparison.Ordinal), 8);
            query.Editor.TextArea.Caret.Offset = query.Editor.Text.IndexOf("SELECT 2", StringComparison.Ordinal) + 3;
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(started is [{ FileName: "notepad.exe" } first] && first.Arguments == $"\"{file}\" L2 C4" && first.WorkingDirectory == folder && first.UseShellExecute, "Picking a tool starts it with the macros expanded");
            string? offered = null;
            window.ExternalToolArgumentsPrompt = (_, args) => { offered = args; return null; };
            check(!window.RunExternalTool(savedTools[0]) && started.Count == 1, "Prompt for arguments canceled runs nothing");
            window.ExternalToolArgumentsPrompt = (_, _) => "/c echo edited";
            check(window.RunExternalTool(savedTools[0]) && started[^1].Arguments == "/c echo edited" && offered is not null && offered.StartsWith("/c echo ", StringComparison.Ordinal), "Prompt for arguments runs with the edited arguments");
            window.ExternalToolLauncher = _ => throw new Win32Exception(2, "The system cannot find the file specified.");
            check(!window.RunExternalTool(savedTools[1]) && ((TextBlock)window.FindName("Status")).Text.StartsWith("External tool Open in Notepad could not start", StringComparison.Ordinal), "A program that cannot start is reported in the status bar");
            check(ExternalTools.Expand("$(ItemPath)|$(ItemDir)|$(CurText)|$(Other)", null, null, null, null) == "|||$(Other)", "Without a file or query the macros are empty and unknown ones stay");

            string exported = Path.Combine(folder, "settings.pgmssettings");
            SettingsTransfer.Export(options, exported);
            check(!File.ReadAllText(exported).Contains("notepad.exe", StringComparison.Ordinal), "Exported settings leave the external tools out");
            SettingsTransfer.Reset(options);
            check(options.Load().Tools.Count == 2, "Resetting the settings keeps this machine's external tools");
        }
        finally
        {
            options.Update(_ => original);
            window.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in FindAll<T>(child)) yield return found;
    }
}
