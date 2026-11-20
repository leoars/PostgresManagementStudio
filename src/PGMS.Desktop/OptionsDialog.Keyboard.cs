using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Environment ▸ Keyboard (PGMS-072, M1106), the Visual Studio page SSMS 22.11 shows: filter, commands, the shortcuts of the selected one,
/// Press shortcut keys, Assign, Remove, "Shortcut currently used by" and Reset. Changes are written on OK.</summary>
public partial class OptionsDialog
{
    private IReadOnlyList<KeyboardCommand> keyboardCommands = [];
    /// <summary>Command name → assigned shortcut, the working copy written on OK.</summary>
    private readonly Dictionary<string, string> keyboardShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private string? pressedGesture;

    private void LoadKeyboard(EnvironmentOptions.Document document, IReadOnlyList<KeyboardCommand> commands)
    {
        keyboardCommands = commands;
        foreach (var (name, gesture) in document.CustomShortcuts) if (!string.IsNullOrWhiteSpace(gesture)) keyboardShortcuts[name] = gesture.Trim();
        FillKeyboardCommands();
    }

    /// <summary>Selects a page by its header ("Keyboard"); Customize ▸ Keyboard… opens there.</summary>
    internal void SelectPage(string header)
    {
        foreach (var category in Pages.Items.OfType<TreeViewItem>())
            foreach (var page in category.Items.OfType<TreeViewItem>().Prepend(category))
                if ((string.Equals(page.Header as string, header, StringComparison.OrdinalIgnoreCase) || string.Equals(Localization.EnglishHeader(page, null) as string, header, StringComparison.OrdinalIgnoreCase)) && page.Items.Count == 0) { page.IsSelected = true; return; }
    }

    internal IReadOnlyList<string> ShownCommands => KeyboardCommandList.Items.Cast<string>().ToList();
    internal IReadOnlyDictionary<string, string> AssignedShortcuts => keyboardShortcuts;

    private void FillKeyboardCommands()
    {
        string filter = KeyboardFilterBox.Text.Trim();
        var selected = KeyboardCommandList.SelectedItem as string;
        KeyboardCommandList.ItemsSource = keyboardCommands.Select(c => c.Name).Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected is not null && KeyboardCommandList.Items.Contains(selected)) KeyboardCommandList.SelectedItem = selected;
        else if (KeyboardCommandList.Items.Count > 0) KeyboardCommandList.SelectedIndex = 0;
        ShowKeyboardCommand();
    }

    private void KeyboardFilterChanged(object sender, TextChangedEventArgs e) => FillKeyboardCommands();
    private void KeyboardCommandChanged(object sender, SelectionChangedEventArgs e) => ShowKeyboardCommand();

    /// <summary>The shortcuts of a command as the page lists them: the assigned one, then the one PGMS gives it.</summary>
    internal string ShortcutsOf(string name)
    {
        var parts = new List<string>();
        if (keyboardShortcuts.TryGetValue(name, out var assigned)) parts.Add(assigned);
        if (keyboardCommands.FirstOrDefault(c => c.Name == name) is { DefaultShortcut.Length: > 0 } command) parts.Add(command.DefaultShortcut + " (PGMS)");
        return string.Join("; ", parts);
    }

    private void ShowKeyboardCommand()
    {
        var name = KeyboardCommandList.SelectedItem as string;
        KeyboardCurrentBox.Text = name is null ? "" : ShortcutsOf(name);
        KeyboardRemoveButton.IsEnabled = name is not null && keyboardShortcuts.ContainsKey(name);
        KeyboardAssignButton.IsEnabled = name is not null && pressedGesture is not null;
    }

    private void KeyboardGestureKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return; // Tab leaves the box
        e.Handled = true;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { PressGesture(null); return; }
        PressGesture(KeyboardMap.GestureName(key, Keyboard.Modifiers));
    }

    /// <summary>Takes a shortcut as if its keys were pressed in the box (also used by tests); null clears it.</summary>
    internal void PressGesture(string? gesture)
    {
        pressedGesture = gesture;
        KeyboardGestureBox.Text = gesture ?? "";
        KeyboardUsedByBox.Text = gesture is null ? "" : UsedBy(gesture);
        ShowKeyboardCommand();
    }

    /// <summary>Which commands already answer to a shortcut: assigned ones, then PGMS ones (exact text, as the menus show them).</summary>
    internal string UsedBy(string gesture)
    {
        var users = keyboardShortcuts.Where(p => string.Equals(p.Value, gesture, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)
            .Concat(keyboardCommands.Where(c => c.DefaultShortcut.Equals(gesture, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name + " (PGMS)"))
            .Distinct().ToList();
        return string.Join("; ", users);
    }

    private void KeyboardAssign(object sender, RoutedEventArgs e) => AssignShortcut();

    /// <summary>Assigns the pressed shortcut to the selected command, taking it from another command it was assigned to.</summary>
    internal bool AssignShortcut()
    {
        if (KeyboardCommandList.SelectedItem is not string name || pressedGesture is not { } gesture) return false;
        foreach (var other in keyboardShortcuts.Where(p => string.Equals(p.Value, gesture, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList()) keyboardShortcuts.Remove(other);
        keyboardShortcuts[name] = gesture;
        KeyboardUsedByBox.Text = UsedBy(gesture);
        ShowKeyboardCommand();
        return true;
    }

    private void KeyboardRemove(object sender, RoutedEventArgs e)
    {
        if (KeyboardCommandList.SelectedItem is string name && keyboardShortcuts.Remove(name)) ShowKeyboardCommand();
    }

    /// <summary>Tests: Remove for the selected command.</summary>
    internal void RemoveSelectedShortcut() => KeyboardRemove(this, new RoutedEventArgs());

    private void KeyboardReset(object sender, RoutedEventArgs e)
    {
        keyboardShortcuts.Clear();
        if (pressedGesture is { } gesture) KeyboardUsedByBox.Text = UsedBy(gesture);
        ShowKeyboardCommand();
    }

    /// <summary>Tests: selects a command in the list (the filter is cleared when it hides it).</summary>
    internal void SelectKeyboardCommand(string name)
    {
        if (!KeyboardCommandList.Items.Contains(name)) KeyboardFilterBox.Text = "";
        KeyboardCommandList.SelectedItem = name;
    }
}
