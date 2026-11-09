using System.Windows;

namespace PGMS.Desktop;

/// <summary>Name of a new Registered Servers group (SSMS: New Server Group).</summary>
public partial class ServerGroupDialog : Window
{
    public string GroupNameValue => GroupName.Text.Trim();

    public ServerGroupDialog(string parent)
    {
        InitializeComponent();
        ParentText.Text = $"New group in: {parent}";
        Loaded += (_, _) => GroupName.Focus();
    }

    private void Accept(object sender, RoutedEventArgs e)
    {
        try { ServerGroups.Validate(GroupNameValue); }
        catch (ArgumentException ex) { Error.Text = ex.Message; GroupName.Focus(); return; }
        if (GroupNameValue.Contains(ServerGroups.Separator)) { Error.Text = "A group name cannot contain '/'."; return; }
        DialogResult = true;
    }
}
