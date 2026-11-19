using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>A command of the main menu as Environment ▸ Keyboard lists it: "Query.Execute", and the shortcut PGMS gives it (empty when none).</summary>
public sealed record KeyboardCommand(string Name, string DefaultShortcut);

/// <summary>Environment ▸ Keyboard (PGMS-072, M1106). As in the Visual Studio shell of SSMS 22.11, commands are named "Menu.Command"; a shortcut
/// the user assigns runs the menu command in the whole main window and wins over the shortcut PGMS gives other commands. PGMS shortcuts cannot
/// be removed (they stay listed), and chords (Ctrl+K, Ctrl+C) cannot be assigned.</summary>
public static class KeyboardMap
{
    /// <summary>Submenus whose items PGMS builds at run time (files, layouts, external tools): their items change, so they are not listed.</summary>
    private static readonly HashSet<string> DynamicMenus = ["RecentFilesMenu", "ApplyLayoutMenu"];

    /// <summary>A header as part of a command name: letters and digits only ("Display E_stimated Execution Plan" → "DisplayEstimatedExecutionPlan").</summary>
    public static string Part(object? header) => new((header as string ?? (header as TextBlock)?.Text ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The leaf commands of the menu, in menu order, with their names. Items without a name-worthy header are skipped.</summary>
    public static IReadOnlyList<(string Name, MenuItem Item)> Commands(ItemsControl menu)
    {
        var result = new List<(string, MenuItem)>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(ItemsControl parent, string prefix)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                string part = Part(Localization.EnglishHeader(item, item.Header));
                if (part.Length == 0 || item.Tag is ExternalTool) continue;
                string name = prefix.Length == 0 ? part : prefix + "." + part;
                if (item.Items.Count > 0 || DynamicMenus.Contains(item.Name)) { if (!DynamicMenus.Contains(item.Name)) Walk(item, name); continue; }
                if (prefix.Length > 0 && seen.Add(name)) result.Add((name, item));
            }
        }
        Walk(menu, "");
        return result;
    }

    /// <summary>The shortcut name of a key press, in the SSMS order (Ctrl+Shift+Alt+Key), or null when it cannot be a shortcut: modifier keys alone,
    /// and keys without Ctrl or Alt other than F1–F24 (they type text or move the caret).</summary>
    public static string? GestureName(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return null;
        bool function = key is >= Key.F1 and <= Key.F24;
        if (!function && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return null;
        string keyName = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (char)('0' + (key - Key.NumPad0)),
            _ => new KeyConverter().ConvertToInvariantString(key) ?? key.ToString(),
        };
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + keyName;
    }

    /// <summary>Runs a menu command as a click would: the parent menus' opening handlers run first, so check marks and enabled state are current.
    /// False when the command is disabled.</summary>
    public static bool Invoke(MenuItem item)
    {
        var parents = new List<MenuItem>();
        for (var parent = ItemsControl.ItemsControlFromItemContainer(item) as MenuItem; parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as MenuItem) parents.Insert(0, parent);
        foreach (var parent in parents) parent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, parent));
        if (!item.IsEnabled) return false;
        try { ((IInvokeProvider)new MenuItemAutomationPeer(item)).Invoke(); return true; }
        catch (System.Windows.Automation.ElementNotEnabledException) { return false; }
    }
}

/// <summary>Tools ▸ External Tools macros (PGMS-072, M1106), the Visual Studio ones that make sense for a query file.</summary>
public static class ExternalTools
{
    public static readonly IReadOnlyList<(string Macro, string Meaning)> Macros =
    [
        ("$(ItemPath)", "Full path of the active query file"), ("$(ItemDir)", "Folder of the active query file"), ("$(ItemFileName)", "File name without extension"),
        ("$(ItemExt)", "Extension of the file"), ("$(CurLine)", "Line of the caret"), ("$(CurCol)", "Column of the caret"), ("$(CurText)", "Selected text"),
    ];

    /// <summary>Replaces the macros; values that do not exist (no file, no query tab) become empty. Other text, $(…) included, is kept.</summary>
    public static string Expand(string text, string? itemPath, int? line, int? column, string? selection)
    {
        string Value(string macro) => macro switch
        {
            "$(ItemPath)" => itemPath ?? "",
            "$(ItemDir)" => itemPath is null ? "" : Path.GetDirectoryName(itemPath) ?? "",
            "$(ItemFileName)" => itemPath is null ? "" : Path.GetFileNameWithoutExtension(itemPath),
            "$(ItemExt)" => itemPath is null ? "" : Path.GetExtension(itemPath),
            "$(CurLine)" => line?.ToString(CultureInfo.InvariantCulture) ?? "",
            "$(CurCol)" => column?.ToString(CultureInfo.InvariantCulture) ?? "",
            "$(CurText)" => selection ?? "",
            _ => macro,
        };
        foreach (var (macro, _) in Macros) text = text.Replace(macro, Value(macro), StringComparison.OrdinalIgnoreCase);
        return text;
    }
}

/// <summary>PGMS-072 (M1106): Environment ▸ Keyboard shortcuts, View ▸ Toolbars with Tools ▸ Customize, and Tools ▸ External Tools.</summary>
public partial class MainWindow
{
    /// <summary>Assigned shortcut → command name, and the shortcut text PGMS shows for each command before an assignment replaced it.</summary>
    private Dictionary<string, string> assignedShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MenuItem, string> pgmsGestureTexts = [];
    private readonly List<MenuItem> externalToolItems = [];

    private void ConfigureCustomization()
    {
        foreach (var (_, item) in KeyboardMap.Commands(MainMenu)) pgmsGestureTexts[item] = item.InputGestureText ?? "";
        ApplyCustomization();
    }

    /// <summary>The menu commands for Environment ▸ Keyboard, with the shortcut PGMS gives them.</summary>
    internal IReadOnlyList<KeyboardCommand> KeyboardCommands() =>
        [.. KeyboardMap.Commands(MainMenu).Select(c => new KeyboardCommand(c.Name, pgmsGestureTexts.TryGetValue(c.Item, out var text) ? text : c.Item.InputGestureText ?? ""))];

    /// <summary>Applies the Keyboard, Toolbars and External Tools settings; called at startup and after Options, Customize or an import.</summary>
    internal void ApplyCustomization()
    {
        var document = EnvironmentSettings.Load();
        var commands = KeyboardMap.Commands(MainMenu).ToDictionary(c => c.Name, c => c.Item, StringComparer.OrdinalIgnoreCase);
        assignedShortcuts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, text) in pgmsGestureTexts) item.InputGestureText = text;
        foreach (var (name, gesture) in document.CustomShortcuts)
        {
            if (string.IsNullOrWhiteSpace(gesture) || !commands.TryGetValue(name, out var item)) continue;
            assignedShortcuts[gesture.Trim()] = name;
            item.InputGestureText = gesture.Trim();
        }
        StandardToolBar.Visibility = document.ToolbarVisible("Standard") ? Visibility.Visible : Visibility.Collapsed;
        SqlEditorToolBar.Visibility = document.ToolbarVisible("SQL Editor") ? Visibility.Visible : Visibility.Collapsed;
        BuildExternalToolItems(document.Tools);
    }

    /// <summary>An assigned shortcut runs its command. Only shortcuts the user assigned are looked at; PGMS shortcuts go on as before.</summary>
    private bool HandleAssignedShortcut(KeyEventArgs e)
    {
        if (assignedShortcuts.Count == 0 || KeyboardMap.GestureName(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) is not { } gesture || !assignedShortcuts.TryGetValue(gesture, out var name)) return false;
        if (KeyboardMap.Commands(MainMenu).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Item is not { } item) return false;
        e.Handled = true;
        if (!KeyboardMap.Invoke(item)) Status.Text = $"{name} ({gesture}) is not available now.";
        return true;
    }

    /// <summary>Runs an assigned shortcut by name, as a key press would (tests).</summary>
    internal bool RunAssignedShortcut(string gesture) =>
        assignedShortcuts.TryGetValue(gesture, out var name) && KeyboardMap.Commands(MainMenu).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Item is { } item && KeyboardMap.Invoke(item);

    private void ToolbarsMenuOpened(object sender, RoutedEventArgs e)
    {
        StandardToolbarMenu.IsChecked = StandardToolBar.Visibility == Visibility.Visible;
        SqlEditorToolbarMenu.IsChecked = SqlEditorToolBar.Visibility == Visibility.Visible;
    }

    private void ToggleToolbar(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } item) SetToolbarVisible(name, item.IsChecked);
    }

    /// <summary>Shows or hides a toolbar and remembers it, as View ▸ Toolbars and Customize do in SSMS.</summary>
    internal void SetToolbarVisible(string name, bool visible)
    {
        EnvironmentSettings.Update(d =>
        {
            var hidden = (d.HiddenToolbars ?? []).Where(n => n != name).ToList();
            if (!visible) hidden.Add(name);
            return d with { HiddenToolbars = hidden.Count == 0 ? null : hidden };
        });
        ApplyCustomization();
    }

    private void ShowCustomize(object sender, RoutedEventArgs e) => ShowCustomize();

    /// <summary>Tools ▸ Customize: the Toolbars page of the SSMS dialog (check to show). Commands and Extensions Menu have no PGMS counterpart;
    /// keyboard shortcuts are under Options ▸ Environment ▸ Keyboard, where the SSMS Keyboard… button leads.</summary>
    internal void ShowCustomize()
    {
        var boxes = new[] { ("Standard", StandardToolBar), ("SQL Editor", SqlEditorToolBar) }
            .Select(t => new CheckBox { Content = t.Item1, Tag = t.Item1, IsChecked = t.Item2.Visibility == Visibility.Visible, Margin = new Thickness(4, 3, 4, 3) }).ToList();
        var list = new StackPanel(); foreach (var box in boxes) list.Children.Add(box);
        var keyboard = new Button { Content = "_Keyboard…", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Opens Options ▸ Environment ▸ Keyboard" };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = new TextBlock { Text = "Toolbars:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(close, Dock.Bottom); DockPanel.SetDock(keyboard, Dock.Bottom);
        panel.Children.Add(header); panel.Children.Add(close); panel.Children.Add(keyboard);
        var frame = new Border { BorderThickness = new Thickness(1), Child = list };
        frame.SetResourceReference(Border.BorderBrushProperty, "Pgms.DialogBorder"); frame.SetResourceReference(Border.BackgroundProperty, "Pgms.ContentBackground");
        panel.Children.Add(frame);
        var dialog = new Window { Title = "Customize", Content = panel, Width = 360, Height = 280, Owner = this, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        foreach (var box in boxes) box.Click += (_, _) => SetToolbarVisible((string)box.Tag, box.IsChecked == true);
        bool openKeyboard = false;
        keyboard.Click += (_, _) => { openKeyboard = true; dialog.Close(); };
        if (CustomizeDialogShown is { } shown) { shown(dialog); return; }
        dialog.ShowDialog();
        if (openKeyboard) ShowOptions("Keyboard");
    }

    /// <summary>Tests: receives the Customize dialog instead of showing it.</summary>
    internal Action<Window>? CustomizeDialogShown { get; set; }

    private void ShowExternalTools(object sender, RoutedEventArgs e) => ShowExternalTools();

    /// <summary>Tests: receives the External Tools dialog instead of showing it modally; the dialog's OK writes the settings.</summary>
    internal Action<ExternalToolsDialog>? ExternalToolsDialogShown { get; set; }

    internal void ShowExternalTools()
    {
        var dialog = new ExternalToolsDialog(EnvironmentSettings.Load().Tools) { Owner = this };
        dialog.Saved += tools => { EnvironmentSettings.Update(d => d with { ExternalTools = tools.Count == 0 ? null : tools }); ApplyCustomization(); };
        if (ExternalToolsDialogShown is { } shown) { shown(dialog); return; }
        dialog.ShowDialog();
    }

    /// <summary>The configured tools go into the Tools menu right before External Tools…, as the Visual Studio shell lists them.</summary>
    private void BuildExternalToolItems(IReadOnlyList<ExternalTool> tools)
    {
        var toolsMenu = (ItemsControl)ExternalToolsMenu.Parent;
        foreach (var old in externalToolItems) toolsMenu.Items.Remove(old);
        externalToolItems.Clear();
        int at = toolsMenu.Items.IndexOf(ExternalToolsMenu);
        foreach (var tool in tools)
        {
            var item = new MenuItem { Header = tool.Title, Tag = tool, ToolTip = "Runs " + tool.Command + (tool.Arguments.Length > 0 ? " " + tool.Arguments : "") };
            item.Click += (_, _) => RunExternalTool(tool);
            toolsMenu.Items.Insert(at++, item);
            externalToolItems.Add(item);
        }
    }

    internal IReadOnlyList<MenuItem> ExternalToolItems => externalToolItems;

    /// <summary>Starts the program; tests replace it so nothing runs. The default lets Windows open the command as Run would.</summary>
    internal Func<ProcessStartInfo, bool> ExternalToolLauncher { get; set; } = info => { using var process = Process.Start(info); return true; };
    /// <summary>"Prompt for arguments": the arguments to use, or null when canceled; tests replace it.</summary>
    internal Func<ExternalTool, string, string?>? ExternalToolArgumentsPrompt { get; set; }

    /// <summary>Runs a configured external tool, only because the user picked it. Macros come from the active query tab.</summary>
    internal bool RunExternalTool(ExternalTool tool)
    {
        var query = ActiveQuery;
        string? path = query?.FilePath; var caret = query?.Editor.TextArea.Caret;
        string Expand(string text) => ExternalTools.Expand(text, path, caret?.Line, caret?.Column, query?.SelectedSql);
        string arguments = Expand(tool.Arguments ?? ""), directory = Expand(tool.InitialDirectory ?? "");
        if (tool.PromptForArguments)
        {
            if ((ExternalToolArgumentsPrompt ?? AskExternalToolArguments)(tool, arguments) is not { } edited) return false;
            arguments = edited;
        }
        var info = new ProcessStartInfo(tool.Command.Trim(), arguments) { UseShellExecute = true };
        if (directory.Length > 0) info.WorkingDirectory = directory;
        try
        {
            if (!ExternalToolLauncher(info)) return false;
            Status.Text = $"External tool {tool.Title} started.";
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Status.Text = $"External tool {tool.Title} could not start: {ex.Message}";
            return false;
        }
    }

    private string? AskExternalToolArguments(ExternalTool tool, string arguments)
    {
        var box = new TextBox { Text = arguments, Margin = new Thickness(0, 6, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(box, "Arguments");
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Arguments for " + tool.Title + ":" }, box, buttons } };
        var dialog = new Window { Title = tool.Title, Content = panel, Width = 460, SizeToContent = SizeToContent.Height, Owner = this, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }
}
