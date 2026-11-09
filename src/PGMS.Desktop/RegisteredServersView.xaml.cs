using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PGMS.Desktop;

public enum RegisteredServerNodeKind { Root, Group, Server }

public sealed class RegisteredServerNode(RegisteredServerNodeKind kind, string name, string? path, ConnectionProfile? profile) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public RegisteredServerNodeKind Kind { get; } = kind;
    public string Name { get; } = name;
    /// <summary>Group path for groups (null for the root); the containing group for servers.</summary>
    public string? Path { get; } = path;
    public ConnectionProfile? Profile { get; } = profile;
    public List<RegisteredServerNode> Children { get; } = [];
    private bool expanded = kind != RegisteredServerNodeKind.Server, selected;
    public bool IsExpanded { get => expanded; set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public ImageSource? Icon => CatalogIconConverter.Icon(Kind == RegisteredServerNodeKind.Server ? "server" : IsExpanded ? "folder-open" : "folder");
    public string? Detail => Profile is { } p ? $"{p.Host}:{p.Port} · {p.Database} · {p.Username}" : null;
}

/// <summary>Registered Servers tool window, like SSMS 22.11 View ▸ Registered Servers (Ctrl+Alt+G): saved connection profiles organized in groups
/// (decision of 05/11/2026). Profiles are the same ones the Connect dialog saves; groups are stored in the same file.</summary>
public partial class RegisteredServersView : UserControl
{
    public const string RootName = "Local Server Groups";
    private readonly ConnectionProfileStore store;

    // Host actions, set by MainWindow; replaceable in tests.
    public Func<ConnectionProfile, Task>? ConnectObjectExplorer { get; set; }
    public Func<ConnectionProfile, Task>? NewQuery { get; set; }
    public Func<ConnectionProfile, LocalServiceAction, Task>? ControlService { get; set; }
    public Func<string?, int, (LocalPostgresService? Service, LocalServiceState State)> ResolveService { get; set; } = (_, _) => (null, LocalServiceState.Unknown);
    /// <summary>Opens the Connect dialog on a profile (or a new one in a group) and returns the saved profile id, if any.</summary>
    public Func<Guid?, string?, Guid?>? EditProfile { get; set; }
    public Func<string, string?>? PromptGroupName { get; set; }
    public Func<string, bool>? Confirm { get; set; }

    public RegisteredServersView(string? profilePath = null)
    {
        InitializeComponent();
        store = new(profilePath ?? ConnectionProfileStore.DefaultPath);
        Reload();
    }

    public RegisteredServerNode? Root => Tree.Items.Count > 0 ? (RegisteredServerNode)Tree.Items[0] : null;
    // From the model (kept in sync by the two-way IsSelected binding) rather than TreeView.SelectedItem, which lags behind a rebuilt tree
    // until its containers exist, for example while the tab is hidden.
    public RegisteredServerNode? Selected => (Root is { } root ? Flatten(root).FirstOrDefault(n => n.IsSelected) : null) ?? Tree.SelectedItem as RegisteredServerNode;

    /// <summary>Rebuilds the tree from the profile file, keeping the selection and expanded groups.</summary>
    public void Reload(Guid? selectProfile = null, string? selectGroup = null)
    {
        var previous = Selected;
        var expanded = Root is { } old ? Flatten(old).Where(n => n.Kind == RegisteredServerNodeKind.Group && !n.IsExpanded).Select(n => n.Path).ToHashSet() : [];
        selectProfile ??= previous?.Profile?.Id;
        if (selectProfile is null && selectGroup is null && previous?.Kind == RegisteredServerNodeKind.Group) selectGroup = previous.Path;
        var root = new RegisteredServerNode(RegisteredServerNodeKind.Root, RootName, null, null);
        try
        {
            var groups = new Dictionary<string, RegisteredServerNode>(StringComparer.OrdinalIgnoreCase);
            RegisteredServerNode GroupNode(string? path) => path is null ? root : groups[path];
            foreach (string group in store.LoadGroups())
            {
                var node = new RegisteredServerNode(RegisteredServerNodeKind.Group, ServerGroups.Leaf(group), group, null) { IsExpanded = !expanded.Contains(group) };
                groups[group] = node; GroupNode(ServerGroups.Parent(group)).Children.Add(node);
            }
            foreach (var profile in store.Load()) GroupNode(string.IsNullOrEmpty(profile.Group) ? null : profile.Group).Children.Add(new(RegisteredServerNodeKind.Server, profile.Name, profile.Group, profile));
            foreach (var node in Flatten(root)) node.Children.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name)); // Groups first.
            Feedback.Text = "";
        }
        catch (Exception ex) when (IsProfileError(ex)) { Feedback.Text = "Registered servers could not be loaded. The file was preserved. " + ex.Message; }
        var target = Flatten(root).FirstOrDefault(n => selectProfile is { } id ? n.Profile?.Id == id : selectGroup is not null && n.Kind == RegisteredServerNodeKind.Group && n.Path == selectGroup) ?? root;
        // Deselect the old tree first, or its selected container passes the selection on to the new root.
        if (Root is { } oldRoot) foreach (var node in Flatten(oldRoot)) node.IsSelected = false;
        target.IsSelected = true;
        Tree.ItemsSource = new[] { root };
    }

    private static IEnumerable<RegisteredServerNode> Flatten(RegisteredServerNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    private static bool IsProfileError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or FormatException or InvalidDataException;
    private string? TargetGroup => Selected switch { { Kind: RegisteredServerNodeKind.Group } g => g.Path, { Kind: RegisteredServerNodeKind.Server } s => s.Path, _ => null };

    private void Refresh(object sender, RoutedEventArgs e) => Reload();
    private void NewGroup(object sender, RoutedEventArgs e) => NewGroup(TargetGroup);
    private void NewRegistration(object sender, RoutedEventArgs e) => NewRegistration(TargetGroup);

    public void NewGroup(string? parent)
    {
        if (PromptGroupName?.Invoke(parent is null ? RootName : parent) is not { } name) return;
        string path = ServerGroups.Combine(parent, name.Trim());
        try { store.AddGroup(path); Reload(selectGroup: path); Feedback.Text = $"Group \"{path}\" created."; }
        catch (Exception ex) when (IsProfileError(ex)) { Feedback.Text = "The group was not created. " + ex.Message; }
    }

    public void NewRegistration(string? group)
    {
        if (EditProfile?.Invoke(null, group) is { } id) Reload(selectProfile: id); else Reload();
    }

    public void DeleteGroup(string group)
    {
        if (Confirm?.Invoke($"Delete the server group \"{group}\"?") != true) return;
        try { store.DeleteGroup(group); Reload(selectGroup: ServerGroups.Parent(group)); Feedback.Text = $"Group \"{group}\" deleted."; }
        catch (Exception ex) when (IsProfileError(ex)) { Feedback.Text = ex.Message; }
    }

    public void DeleteServer(ConnectionProfile profile)
    {
        if (Confirm?.Invoke($"Delete the registered server \"{profile.Name}\" ({profile.Host}:{profile.Port}) and its saved password?") != true) return;
        try { store.Delete(profile.Id); Reload(selectGroup: profile.Group); Feedback.Text = $"\"{profile.Name}\" deleted."; }
        catch (Exception ex) when (IsProfileError(ex)) { Feedback.Text = "The server was not deleted. " + ex.Message; }
    }

    public void MoveServer(ConnectionProfile profile, string? group)
    {
        try { store.Move(profile.Id, group); Reload(selectProfile: profile.Id); Feedback.Text = $"\"{profile.Name}\" moved to {(group ?? RootName)}."; }
        catch (Exception ex) when (IsProfileError(ex)) { Feedback.Text = "The server was not moved. " + ex.Message; }
    }

    private async void TreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected?.Profile is { } profile && e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Tree, d) is not null && ConnectObjectExplorer is { } connect) { e.Handled = true; await connect(profile); }
    }
    private async void TreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected?.Profile is { } profile && ConnectObjectExplorer is { } connect) { e.Handled = true; await connect(profile); }
        else if (e.Key == Key.Delete && Selected is { } node) { e.Handled = true; if (node.Profile is { } p) DeleteServer(p); else if (node.Kind == RegisteredServerNodeKind.Group) DeleteGroup(node.Path!); }
        else if (e.Key == Key.F5) { e.Handled = true; Reload(); }
    }

    private void MenuOpening(object sender, ContextMenuEventArgs e) => FillMenu(Menu, Selected);

    internal void FillMenu(ContextMenu menu, RegisteredServerNode? node)
    {
        menu.Items.Clear();
        MenuItem Item(string header, Action click, string name, bool enabled = true, bool isDefault = false)
        {
            var item = new MenuItem { Header = header, Name = name, IsEnabled = enabled };
            if (isDefault) item.FontWeight = FontWeights.Bold;
            item.Click += (_, _) => click();
            return item;
        }
        void Add(MenuItem item) => menu.Items.Add(item);
        void Separator() { if (menu.Items.Count > 0 && menu.Items[^1] is not System.Windows.Controls.Separator) menu.Items.Add(new System.Windows.Controls.Separator()); }
        if (node is null) return;
        if (node.Profile is { } profile)
        {
            Add(Item("_Object Explorer", () => _ = ConnectObjectExplorer?.Invoke(profile), "RegisteredObjectExplorer", isDefault: true));
            Add(Item("New _Query", () => _ = NewQuery?.Invoke(profile), "RegisteredNewQuery"));
            Separator();
            var (service, state) = ResolveService(profile.Host, profile.Port);
            var control = new MenuItem { Header = "Service _Control", Name = "RegisteredServiceControl", IsEnabled = service is not null,
                ToolTip = service is null ? "Available for a PostgreSQL Windows service on this computer whose port matches this registration." : $"Windows service {service.Name} ({LocalServerServices.Describe(state)}); asks for administrator permission." };
            ToolTipService.SetShowOnDisabled(control, true);
            control.Items.Add(Item("_Start", () => _ = ControlService?.Invoke(profile, LocalServiceAction.Start), "RegisteredStartService", state == LocalServiceState.Stopped));
            control.Items.Add(Item("St_op", () => _ = ControlService?.Invoke(profile, LocalServiceAction.Stop), "RegisteredStopService", state == LocalServiceState.Running));
            control.Items.Add(Item("Res_tart", () => _ = ControlService?.Invoke(profile, LocalServiceAction.Restart), "RegisteredRestartService", state == LocalServiceState.Running));
            Add(control);
            Separator();
            var move = new MenuItem { Header = "_Move To", Name = "RegisteredMoveTo" };
            IReadOnlyList<string> groups = [];
            try { groups = store.LoadGroups(); } catch (Exception ex) when (IsProfileError(ex)) { }
            foreach (string? group in new string?[] { null }.Concat(groups))
                move.Items.Add(Item(group ?? RootName, () => MoveServer(profile, group), "RegisteredMoveTarget", !string.Equals(group ?? "", profile.Group ?? "", StringComparison.OrdinalIgnoreCase)));
            Add(move);
            Separator(); Add(Item("_Delete", () => DeleteServer(profile), "RegisteredDeleteServer"));
            Separator(); Add(Item("_Refresh", () => Reload(), "RegisteredRefresh"));
            Add(Item("_Properties", () => { if (EditProfile?.Invoke(profile.Id, null) is { } id) Reload(selectProfile: id); else Reload(); }, "RegisteredProperties"));
            return;
        }
        string? path = node.Kind == RegisteredServerNodeKind.Group ? node.Path : null;
        Add(Item("New Server _Group…", () => NewGroup(path), "RegisteredNewGroup"));
        Add(Item("New Server _Registration…", () => NewRegistration(path), "RegisteredNewRegistration"));
        if (path is not null) { Separator(); Add(Item("_Delete", () => DeleteGroup(path), "RegisteredDeleteGroup", node.Children.Count == 0)); ToolTipService.SetShowOnDisabled((MenuItem)menu.Items[^1], true); ((MenuItem)menu.Items[^1]).ToolTip = node.Children.Count == 0 ? null : "Move or delete the servers and subgroups first."; }
        Separator(); Add(Item("_Refresh", () => Reload(), "RegisteredRefresh"));
    }
}
