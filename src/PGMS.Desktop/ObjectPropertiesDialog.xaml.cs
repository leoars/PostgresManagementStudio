using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class ObjectPropertiesDialog : Window
{
    private readonly string connection;
    private readonly CatalogEntry target;
    private CancellationTokenSource? inspection;
    private readonly DialogFocus focus = new();
    public ObjectProperties? Snapshot { get; private set; }
    public bool IsBusy => inspection is not null;

    public ObjectPropertiesDialog(string connection, CatalogEntry target)
    {
        this.connection = connection; this.target = target;
        InitializeComponent();
        Loaded += async (_, _) => { InspectButton.Focus(); if (InspectOnOpen && Snapshot is null) await InspectAsync(); };
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Title = $"Object Properties — {target.Name}";
        Target.Text = $"{target.Kind}: {target.Name}\nServer: {settings.Host}:{settings.Port}   Connected database: {settings.Database}\nSchema: {target.Schema ?? "(not applicable)"}   Explorer OID: {target.Oid}";
        Feedback.Text = "Inspect to read catalog metadata. This window does not change objects or read their rows.";
    }
    /// <summary>Set by Object Explorer ▸ Properties: read the object as soon as the window opens, as SSMS shows properties already loaded.</summary>
    public bool InspectOnOpen { get; init; }

    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (IsBusy) return;
        focus.Capture();
        using var cancellation = new CancellationTokenSource(); inspection = cancellation;
        InspectButton.IsEnabled = CloseButton.IsEnabled = false; CancelInspectionButton.IsEnabled = true;
        CancelInspectionButton.Focus();
        Feedback.Text = "Reading catalog metadata…";
        try
        {
            var result = await new ObjectPropertiesService(connection).InspectAsync(target, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Snapshot = result;
            DefinitionTab.Visibility = result.Definition is null ? Visibility.Collapsed : Visibility.Visible;
            Definition.Text = result.Definition ?? "";
            DefinitionNotice.Text = result.DefinitionTruncated
                ? "Definition truncated at 262144 UTF-16 units. This is incomplete SQL; do not use it as a script."
                : "Definition reconstructed by PostgreSQL. This window does not execute it or refresh materialized data.";
            Summary.Text = string.Join("\n", result.Values.Select(p => $"{p.Name}: {p.Value}"));
            Feedback.Text = "Catalog snapshot loaded. Inspect again to refresh. No changes were made.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or IOException or TimeoutException)
        { Feedback.Text = "Inspection canceled. " + PreviousSnapshotNotice; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message) + " " + PreviousSnapshotNotice; }
        finally
        {
            inspection = null; InspectButton.IsEnabled = CloseButton.IsEnabled = true; CancelInspectionButton.IsEnabled = false;
            focus.Restore(this, CancelInspectionButton);
        }
    }
    private string PreviousSnapshotNotice => Snapshot is null ? "No snapshot loaded. Inspect to retry." : "Previous snapshot retained; it may be outdated. Inspect to retry.";
    private void CancelInspection(object sender, RoutedEventArgs e) => Cancel();
    public void Cancel()
    {
        if (inspection is null) return;
        CancelInspectionButton.IsEnabled = false;
        Feedback.Text = "Canceling inspection… Connection startup may wait for its timeout.";
        inspection.Cancel();
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await InspectAsync(); return; }
        if (IsBusy && e.Key == Key.Escape) { e.Handled = true; Cancel(); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsBusy) { e.Cancel = true; Cancel(); }
    }
}
