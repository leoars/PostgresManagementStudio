using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PGMS.Desktop;

/// <summary>Object Explorer buttons the user hid with Add or Remove Buttons.</summary>
public sealed class ExplorerToolbarPreferences(string path)
{
    private sealed record Document(int Version, List<string> Hidden);
    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "explorer-toolbar.json");
    /// <summary>An unreadable file only costs the customization, so it reads as no hidden buttons.</summary>
    public IReadOnlySet<string> Load()
    {
        try { return JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Version: 1, Hidden: { } hidden } ? hidden.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new HashSet<string>(); }
    }
    public void Save(IEnumerable<string> hidden)
    {
        string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, hidden.Order(StringComparer.Ordinal).ToList())));
        File.Move(temporary, full, overwrite: true);
    }
}

public partial class MainWindow
{
    internal ExplorerToolbarPreferences ExplorerToolbarStore { get; set; } = new(ExplorerToolbarPreferences.DefaultPath);
    // In SSMS order. Group by schema and Show Policy Health State have no PostgreSQL counterpart: the tree already groups by schema.
    private (string Id, string Header, string? Icon, string? Gesture, FrameworkElement Button)[] ExplorerToolbarButtons =>
    [
        ("Connect", "Connect", null, null, ExplorerConnectMenuButton),
        ("ConnectExplorer", "Connect Object Explorer…", "plug", null, ExplorerConnectButton),
        ("Disconnect", "Disconnect", "plug-off", null, DisconnectExplorerButton),
        ("Stop", "Stop", "stop", null, CancelExplorerLoadButton),
        ("Filter", "Filter", "filter", null, ExplorerFilterButton),
        ("Refresh", "Refresh", "refresh", "F5", ExplorerRefreshButton)
    ];

    private void InitializeExplorerToolbar()
    {
        ExplorerConnectIcon.Source = CatalogIconConverter.Icon("plug");
        ExplorerDisconnectIcon.Source = CatalogIconConverter.Icon("plug-off");
        ExplorerFilterIcon.Source = CatalogIconConverter.Icon("filter");
        ExplorerRefreshIcon.Source = CatalogIconConverter.Icon("refresh");
        // A faded red would read as pink; SSMS shows the idle Stop button gray.
        void ShowStop() => ExplorerStopIcon.Source = CatalogIconConverter.Icon(CancelExplorerLoadButton.IsEnabled ? "stop" : "stop-idle");
        CancelExplorerLoadButton.IsEnabledChanged += (_, _) => ShowStop();
        ShowStop();
        var hidden = ExplorerToolbarStore.Load();
        foreach (var button in ExplorerToolbarButtons) button.Button.Visibility = hidden.Contains(button.Id) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenExplorerToolbarOptions(object sender, RoutedEventArgs e) => OpenBelow(ExplorerToolbarOptionsButton, CreateExplorerToolbarMenu());
    private static void OpenBelow(FrameworkElement target, ContextMenu menu)
    {
        menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    internal ContextMenu CreateExplorerToolbarMenu()
    {
        var buttons = new MenuItem { Header = "Add or Remove Buttons", Name = "ExplorerAddRemoveButtons" };
        foreach (var button in ExplorerToolbarButtons)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Image { Width = 16, Height = 16, Margin = new(0, 0, 6, 0), Source = button.Icon is null ? null : CatalogIconConverter.Icon(button.Icon) });
            header.Children.Add(new TextBlock { Text = button.Header, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header, Name = "ExplorerToolbarToggle" + button.Id, IsCheckable = true, StaysOpenOnClick = true, IsChecked = button.Button.Visibility == Visibility.Visible, InputGestureText = button.Gesture ?? "" };
            System.Windows.Automation.AutomationProperties.SetName(item, button.Header);
            var target = button.Button;
            item.Click += (_, _) => { target.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed; SaveExplorerToolbar(); };
            buttons.Items.Add(item);
        }
        buttons.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset Toolbar", Name = "ExplorerToolbarReset" };
        reset.Click += (_, _) =>
        {
            foreach (var button in ExplorerToolbarButtons) button.Button.Visibility = Visibility.Visible;
            foreach (var item in buttons.Items.OfType<MenuItem>().Where(i => i.IsCheckable)) item.IsChecked = true;
            SaveExplorerToolbar();
        };
        buttons.Items.Add(reset);
        return new ContextMenu { Items = { buttons } };
    }

    private void SaveExplorerToolbar()
    {
        try { ExplorerToolbarStore.Save(ExplorerToolbarButtons.Where(b => b.Button.Visibility != Visibility.Visible).Select(b => b.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "The toolbar choice could not be saved: " + ex.Message; }
    }
}
