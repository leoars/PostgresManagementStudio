using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Template Explorer (View ▸ Template Explorer, Ctrl+Alt+T; PGMS-065, M1083): templates grouped in folders; opening one starts a query tab with its text.</summary>
public sealed class TemplateExplorerView : UserControl
{
    private readonly TreeView tree = new() { BorderThickness = new Thickness(0) };
    private readonly string? userDirectory;
    /// <summary>Opens a template in a new query tab.</summary>
    public Action<SqlTemplate>? OpenTemplate { get; set; }
    /// <summary>Opens the user's template folder outside PGMS.</summary>
    public Action<string>? OpenFolder { get; set; }
    public TreeView Tree => tree;
    /// <summary>B4 (M1134): the node that takes the focus when the window is shown (the selected one, else the first folder). Reload rebuilds the
    /// nodes, so focusing the TreeView itself left the focus on a tree with no node, and the Narrator read only "Template Explorer, tree".
    /// Tab stays inside the window, as in every AvalonDock tool pane (and the Visual Studio shell); Ctrl+Tab and Ctrl+F6 leave it.</summary>
    public UIElement FocusTarget => tree.SelectedItem as TreeViewItem ?? (tree.Items.Count > 0 ? tree.Items[0] as TreeViewItem : null) ?? (UIElement)tree;

    public TemplateExplorerView(string? userDirectory = null)
    {
        this.userDirectory = userDirectory;
        AutomationProperties.SetName(tree, "Template Explorer");
        var open = new MenuItem { Header = "_Open" }; open.Click += (_, _) => OpenSelected();
        var refresh = new MenuItem { Header = "_Refresh" }; refresh.Click += (_, _) => Reload();
        var folder = new MenuItem { Header = "Open Templates _Folder", ToolTip = "Your own .sql templates go here, one subfolder per group; Refresh lists them" };
        folder.Click += (_, _) => { string path = UserDirectory; Directory.CreateDirectory(path); OpenFolder?.Invoke(path); };
        tree.ContextMenu = new ContextMenu { Items = { open, new Separator(), refresh, folder } };
        tree.MouseDoubleClick += (_, e) => { if (tree.SelectedItem is TreeViewItem { Tag: SqlTemplate }) { OpenSelected(); e.Handled = true; } };
        tree.KeyDown += (_, e) => { if (e.Key == Key.Enter && tree.SelectedItem is TreeViewItem { Tag: SqlTemplate }) { OpenSelected(); e.Handled = true; } };
        Content = tree;
        Reload();
    }

    public string UserDirectory => userDirectory ?? SqlTemplateCatalog.UserTemplatesDirectory;

    public void Reload()
    {
        tree.Items.Clear();
        foreach (var group in SqlTemplateCatalog.Templates(UserDirectory).GroupBy(t => t.Folder).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = new TreeViewItem { Header = group.Key, Tag = group.Key };
            AutomationProperties.SetName(folder, group.Key);
            foreach (var template in group.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new TreeViewItem { Header = template.Name, Tag = template, ToolTip = template.Path ?? "Built-in template" };
                AutomationProperties.SetName(item, template.Name);
                folder.Items.Add(item);
            }
            tree.Items.Add(folder);
        }
    }

    public SqlTemplate? SelectedTemplate => (tree.SelectedItem as TreeViewItem)?.Tag as SqlTemplate;

    public bool OpenSelected()
    {
        if (SelectedTemplate is not { } template) return false;
        OpenTemplate?.Invoke(template);
        return true;
    }

    /// <summary>Selects a template by folder and name (tests and keyboard users).</summary>
    public bool Select(string folder, string name)
    {
        foreach (TreeViewItem group in tree.Items)
            if ((string)group.Tag == folder)
                foreach (TreeViewItem item in group.Items)
                    if (item.Tag is SqlTemplate t && t.Name == name) { group.IsExpanded = true; item.IsSelected = true; return true; }
        return false;
    }
}
