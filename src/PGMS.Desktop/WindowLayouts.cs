using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PGMS.Desktop;

public sealed record WindowLayout(string Name, string Xml);

/// <summary>Saved window layouts (Window ▸ Save/Apply/Manage Window Layouts, PGMS-069, M1092): a JSON list next to layout.xml, at most ten like SSMS.</summary>
public sealed class WindowLayoutStore(string path)
{
    public const int Maximum = 10;
    public string Path { get; } = path;

    public IReadOnlyList<WindowLayout> Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<WindowLayout>>(File.ReadAllText(Path)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public void Save(IReadOnlyList<WindowLayout> layouts)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(layouts.Take(Maximum).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, true);
    }

    /// <summary>Adds or replaces (same name, ignoring case) a layout; refuses an eleventh one.</summary>
    public IReadOnlyList<WindowLayout> Put(WindowLayout layout)
    {
        var list = Load().ToList();
        int existing = list.FindIndex(l => string.Equals(l.Name, layout.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) list[existing] = layout;
        else if (list.Count >= Maximum) throw new InvalidOperationException($"At most {Maximum} window layouts can be saved. Delete one in Manage Window Layouts first.");
        else list.Add(layout);
        Save(list);
        return list;
    }
}

/// <summary>Name of a window layout (Save Window Layout and Rename).</summary>
public sealed class LayoutNameDialog : Window
{
    public TextBox NameBox { get; } = new() { Margin = new Thickness(0, 4, 0, 10) };
    public string LayoutName => NameBox.Text.Trim();

    public LayoutNameDialog(string title, string initial)
    {
        Title = title; Width = 325; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = "_Name:", Target = NameBox, Padding = new Thickness(0) });
        AutomationProperties.SetName(NameBox, "Name");
        NameBox.Text = initial; panel.Children.Add(NameBox);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
        ok.Click += (_, _) => { if (LayoutName.Length > 0) DialogResult = true; };
        NameBox.TextChanged += (_, _) => ok.IsEnabled = LayoutName.Length > 0;
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = panel;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }
}

/// <summary>Manage Window Layouts: saved layouts with Rename, Delete, Move Up and Move Down, as in SSMS 22.11.</summary>
public sealed class ManageLayoutsDialog : Window
{
    private readonly WindowLayoutStore store;
    public ListBox Layouts { get; } = new() { Height = 250, Margin = new Thickness(0, 4, 6, 10) };
    /// <summary>Asks the new name on Rename; tests replace it.</summary>
    public Func<string, string?>? RenamePrompt { get; set; }

    public ManageLayoutsDialog(WindowLayoutStore store)
    {
        this.store = store;
        Title = "Manage Window Layouts"; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = "_Saved layouts:", Target = Layouts, Padding = new Thickness(0) });
        AutomationProperties.SetName(Layouts, "Saved layouts");
        var up = new Button { Content = "▲", Width = 24, Height = 24, Margin = new Thickness(0, 4, 0, 6), ToolTip = "Move Up" };
        var down = new Button { Content = "▼", Width = 24, Height = 24, ToolTip = "Move Down" };
        AutomationProperties.SetName(up, "Move Up"); AutomationProperties.SetName(down, "Move Down");
        var row = new DockPanel();
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { up, down } };
        DockPanel.SetDock(arrows, Dock.Right); row.Children.Add(arrows); row.Children.Add(Layouts);
        panel.Children.Add(row);
        var rename = new Button { Content = "_Rename", MinWidth = 75, Margin = new Thickness(0, 0, 6, 0) };
        var delete = new Button { Content = "_Delete", MinWidth = 75 };
        var close = new Button { Content = "Close", MinWidth = 75, IsCancel = true };
        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close); buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { rename, delete } });
        panel.Children.Add(buttons);
        Content = panel;
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        rename.Click += (_, _) => Rename(); delete.Click += (_, _) => Delete();
        close.Click += (_, _) => Close();
        Reload();
    }

    private void Reload(int select = 0)
    {
        Layouts.ItemsSource = store.Load().Select(l => l.Name).ToArray();
        if (Layouts.Items.Count > 0) Layouts.SelectedIndex = Math.Clamp(select, 0, Layouts.Items.Count - 1);
    }

    public bool Move(int delta)
    {
        var list = store.Load().ToList(); int i = Layouts.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return false;
        (list[i], list[j]) = (list[j], list[i]); store.Save(list); Reload(j);
        return true;
    }

    public bool Delete()
    {
        var list = store.Load().ToList(); int i = Layouts.SelectedIndex;
        if (i < 0 || i >= list.Count) return false;
        list.RemoveAt(i); store.Save(list); Reload(i);
        return true;
    }

    public bool Rename()
    {
        var list = store.Load().ToList(); int i = Layouts.SelectedIndex;
        if (i < 0 || i >= list.Count) return false;
        string? name = (RenamePrompt ?? (current => { var d = new LayoutNameDialog("Rename", current) { Owner = this }; return d.ShowDialog() == true ? d.LayoutName : null; }))(list[i].Name)?.Trim();
        if (string.IsNullOrEmpty(name) || list.Where((_, k) => k != i).Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        list[i] = list[i] with { Name = name }; store.Save(list); Reload(i);
        return true;
    }
}
