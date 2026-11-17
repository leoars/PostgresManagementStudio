using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>New Extension… and Update Extension… from the Extensions folder (PGMS-060, M1090): OK runs the statement shown, Script opens it in a query editor.</summary>
public sealed class ExtensionDialog : Window
{
    public const string DefaultChoice = "(default)";
    private readonly string connectionString;
    private readonly CatalogEntry? installedEntry;
    private ExtensionEdits.Installed? installed;
    private IReadOnlyList<ExtensionEdits.Available> available = [];
    private bool busy, loaded;
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBox preview = new() { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, MinHeight = 40, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock feedback = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly Button ok = new() { Content = "OK", Padding = new Thickness(16, 3, 16, 3), IsDefault = true, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button script = new() { Content = "_Script", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button cancel = new() { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
    public ComboBox Extension { get; } = new() { Margin = new Thickness(0, 2, 0, 0) };
    public ComboBox Schema { get; } = new() { IsEditable = true, Margin = new Thickness(0, 2, 0, 8) };
    public ComboBox Version { get; } = new() { Margin = new Thickness(0, 2, 0, 8) };
    public CheckBox Cascade { get; } = new() { Content = "_CASCADE (also install the extensions it requires)", Margin = new Thickness(0, 0, 0, 8) };
    public bool IsUpdate => installedEntry is not null;
    public string Sql { get; private set; } = "";
    /// <summary>Set when Script was chosen: the statement to open in a query editor.</summary>
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public string Feedback => feedback.Text;
    public bool CanAccept => ok.IsEnabled;

    /// <param name="extension">An installed extension to update, or null for New Extension.</param>
    public ExtensionDialog(string connectionString, CatalogEntry? extension = null)
    {
        this.connectionString = connectionString; installedEntry = extension;
        Title = IsUpdate ? "Update Extension" : "New Extension";
        Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        heading.Text = $"Database: {new NpgsqlConnectionStringBuilder(connectionString).Database}" + (IsUpdate ? $"  |  Extension: {extension!.Name}" : "");
        panel.Children.Add(heading);
        if (!IsUpdate)
        {
            panel.Children.Add(new Label { Content = "_Extension:", Target = Extension, Padding = new Thickness(0) });
            AutomationProperties.SetName(Extension, "Extension");
            panel.Children.Add(Extension);
        }
        panel.Children.Add(details);
        if (!IsUpdate)
        {
            panel.Children.Add(new Label { Content = "Sc_hema (empty: the extension's default):", Target = Schema, Padding = new Thickness(0) });
            AutomationProperties.SetName(Schema, "Schema");
            panel.Children.Add(Schema);
        }
        panel.Children.Add(new Label { Content = IsUpdate ? "Update _to version:" : "_Version:", Target = Version, Padding = new Thickness(0) });
        AutomationProperties.SetName(Version, "Version");
        panel.Children.Add(Version);
        if (!IsUpdate) panel.Children.Add(Cascade);
        panel.Children.Add(new TextBlock { Text = "Statement:" });
        AutomationProperties.SetName(preview, "Statement");
        panel.Children.Add(preview);
        panel.Children.Add(feedback);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, script, cancel } });
        Content = panel;
        Extension.SelectionChanged += async (_, _) => await ExtensionChangedAsync();
        Schema.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdatePreview()));
        Schema.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePreview);
        Version.SelectionChanged += (_, _) => UpdatePreview();
        Cascade.Checked += (_, _) => UpdatePreview(); Cascade.Unchecked += (_, _) => UpdatePreview();
        ok.Click += async (_, _) => await ApplyAsync();
        script.Click += (_, _) => Script();
        Closing += (object? _, CancelEventArgs e) => { if (busy) e.Cancel = true; };
        Loaded += async (_, _) => { await LoadAsync(); (IsUpdate ? (Control)Version : Extension).Focus(); };
    }

    /// <summary>Reads the available extensions (New) or the installed one and its update targets (Update). Called when the window loads; tests call it directly.</summary>
    public async Task LoadAsync()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (IsUpdate)
            {
                installed = await ExtensionEdits.InspectAsync(connectionString, installedEntry!.Oid, installedEntry.Name);
                details.Text = $"Installed version {installed.Version} in schema {installed.Schema}." + (installed.Comment is { Length: > 0 } c ? " " + c : "")
                    + (installed.UpdateTargets.Count == 0 ? "\nThe server has no update script from this version." : "");
                Version.ItemsSource = installed.UpdateTargets;
                if (installed.UpdateTargets.Count > 0) Version.SelectedIndex = 0;
            }
            else
            {
                available = await ExtensionEdits.ListAvailableAsync(connectionString);
                Extension.ItemsSource = available.Where(a => a.InstalledVersion is null).Select(a => a.Name).ToArray();
                Schema.ItemsSource = await SchemasAsync();
                if (Extension.Items.Count == 0) details.Text = "Every extension available on the server is already installed in this database.";
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
        }
        UpdatePreview();
    }

    private async Task<string[]> SchemasAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT nspname FROM pg_catalog.pg_namespace WHERE nspname <> 'information_schema' AND nspname !~ '^pg_' ORDER BY nspname", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<string>();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return [.. list];
    }

    private async Task ExtensionChangedAsync()
    {
        Version.ItemsSource = null;
        if (Extension.SelectedItem is not string name) { details.Text = ""; UpdatePreview(); return; }
        var info = available.FirstOrDefault(a => a.Name == name);
        details.Text = (info?.Comment ?? "") + (info?.DefaultVersion is { } v ? $" Default version {v}." : "");
        try
        {
            var versions = await ExtensionEdits.VersionsAsync(connectionString, name);
            if (Extension.SelectedItem as string != name) return;
            Version.ItemsSource = new[] { DefaultChoice }.Concat(versions).ToArray();
            Version.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { feedback.Text = ex.Message; }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (busy) return;
        try
        {
            string? version = Version.SelectedItem as string is { } v && v != DefaultChoice ? v : null;
            if (IsUpdate)
                Sql = installed is { UpdateTargets.Count: > 0 } && version is not null ? ExtensionEdits.UpdateSql(installed.Name, version) : "";
            else
                Sql = Extension.SelectedItem is string name ? ExtensionEdits.CreateSql(name, Schema.Text?.Trim(), version, Cascade.IsChecked == true) : "";
            if (feedback.Text.StartsWith("Names", StringComparison.Ordinal) || feedback.Text.StartsWith("Enter", StringComparison.Ordinal)) feedback.Text = "";
        }
        catch (ArgumentException ex) { Sql = ""; feedback.Text = ex.Message; }
        preview.Text = Sql;
        ok.IsEnabled = script.IsEnabled = Sql.Length > 0;
    }

    /// <summary>Script: keeps the statement to open in a query editor and closes; nothing runs.</summary>
    public bool Script()
    {
        if (busy || Sql.Length == 0) return false;
        ScriptSql = Sql;
        if (IsVisible) DialogResult = true;
        return true;
    }

    /// <summary>OK: runs the statement; the dialog stays open with the server's message when it fails.</summary>
    public async Task<bool> ApplyAsync()
    {
        if (busy || Sql.Length == 0) return false;
        string sql = Sql;
        busy = true; ok.IsEnabled = script.IsEnabled = cancel.IsEnabled = false; feedback.Text = "";
        try
        {
            if (IsUpdate) await ExtensionEdits.InspectAsync(connectionString, installedEntry!.Oid, installedEntry.Name);
            await ExtensionEdits.ExecuteAsync(connectionString, sql);
            WasApplied = true;
            busy = false;
            if (IsVisible) DialogResult = true;
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
            return false;
        }
        finally
        {
            busy = false; cancel.IsEnabled = true; ok.IsEnabled = script.IsEnabled = Sql.Length > 0;
        }
    }
}
