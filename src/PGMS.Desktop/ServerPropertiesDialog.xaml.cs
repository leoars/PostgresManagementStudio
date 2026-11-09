using System.IO;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class ServerPropertiesDialog : Window
{
    private const string SettingsPage = "Settings";
    private readonly ServerPropertiesService service;
    public ServerPropertiesSnapshot? Snapshot { get; private set; }

    public ServerPropertiesDialog(string connection)
    {
        InitializeComponent(); service = new(connection);
        var c = new NpgsqlConnectionStringBuilder(connection);
        Title = $"Server Properties - {c.Host}:{c.Port}";
        ConnectionInfo.Text = $"Server:\n{c.Host}:{c.Port}\n\nConnection:\n{c.Username}";
        Pages.ItemsSource = new[] { "General", "Memory", "Connections", "Security", SettingsPage };
        Loaded += async (_, _) => await LoadAsync();
    }

    public async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false; Feedback.Text = "Reading server properties…";
        try
        {
            Snapshot = await service.ReadAsync();
            Feedback.Text = $"Read at {Snapshot.ReadAt:HH:mm:ss}. Read-only. To change a setting, run ALTER SYSTEM in a query window and use Reload Configuration (settings marked as needing a restart apply only after one).";
            if (Pages.SelectedIndex < 0) Pages.SelectedIndex = 0; else ShowPage();
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void Refresh(object sender, RoutedEventArgs e) => await LoadAsync();
    private void PageChanged(object sender, SelectionChangedEventArgs e) => ShowPage();
    private void FilterChanged(object sender, RoutedEventArgs e) => ShowPage();

    internal void ShowPage()
    {
        if (Snapshot is null || Pages.SelectedItem is not string page) return;
        bool settings = page == SettingsPage;
        Properties.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        Settings.Visibility = FilterPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (settings)
        {
            string filter = Filter.Text.Trim();
            Settings.ItemsSource = Snapshot.Settings.Where(s => (filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                && (ChangedOnly.IsChecked != true || s.Source is not ("default" or "override"))).ToList();
        }
        else Properties.ItemsSource = Snapshot.Properties.Where(p => p.Page == page).ToList();
        DescriptionTitle.Text = Description.Text = "";
    }

    private void RowChanged(object sender, SelectionChangedEventArgs e)
    {
        switch ((sender as DataGrid)?.SelectedItem)
        {
            case ServerProperty p: DescriptionTitle.Text = p.Name; Description.Text = p.Description; break;
            case ServerSetting s: DescriptionTitle.Text = $"{s.Name} ({s.Category})"; Description.Text = s.Description + (s.PendingRestart ? " A changed value waits for a server restart." : ""); break;
        }
    }
}
