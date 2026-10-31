using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class EnumDialog : Window
{
    private sealed record Choice(EnumValueOperation Value, string Label);
    private sealed record LabelChoice(string Value, string Display);
    private readonly string connection, schema, name;
    private readonly uint oid;
    private bool ready, busy;
    private CancellationTokenSource? cancellation;
    public EnumSnapshot? Snapshot { get; private set; }
    public bool SnapshotIsStale { get; private set; } = true;
    public bool WasApplied { get; private set; }
    public bool RequiresParentRefresh { get; private set; }
    public string? ScriptSql { get; private set; }
    public EnumDialog(string connection, string schema, string name, uint oid)
    {
        this.connection = connection; this.schema = schema; this.name = name; this.oid = oid;
        InitializeComponent(); Target.Text = "Enum Values\n" + ObjectSql.Relation(schema, name);
        Operation.ItemsSource = new[] { new Choice(EnumValueOperation.Append,"Append value"), new(EnumValueOperation.Before,"Add before existing value"), new(EnumValueOperation.After,"Add after existing value"), new(EnumValueOperation.Rename,"Rename existing value") };
        Operation.SelectedIndex = 0; ready = true; Update(); Loaded += (_,_) => InspectButton.Focus();
    }
    private EnumValueEdit Request
    {
        get
        {
            if (Snapshot is null || SnapshotIsStale) throw new ArgumentException("Inspect the selected enum first.");
            string value = EmptyValue.IsChecked == true ? "" : NewValue.Text;
            if (value.Length == 0 && EmptyValue.IsChecked != true) throw new ArgumentException("Enter a new value or explicitly choose an empty string.");
            var operation = (EnumValueOperation)Operation.SelectedValue;
            return new(Snapshot, operation, value, operation == EnumValueOperation.Append ? null : Existing.SelectedValue as string);
        }
    }
    private void Changed(object sender, RoutedEventArgs e)
    { if (ready) { if (!ReferenceEquals(sender,Reviewed)) Reviewed.IsChecked = false; Update(); } }
    private void Update(bool preserve = false)
    {
        Fields.IsEnabled = InspectButton.IsEnabled = CloseButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && cancellation?.IsCancellationRequested == false;
        NewValue.IsEnabled = EmptyValue.IsChecked != true;
        Existing.IsEnabled = (EnumValueOperation?)Operation.SelectedValue != EnumValueOperation.Append;
        Observation.Text = Snapshot is null ? "No current enum observation." : $"{(SnapshotIsStale ? "Outdated observation. Inspect again.\n" : "")}Type OID: {Snapshot.Type.Oid}   Values: {Snapshot.Labels.Count}";
        try { Preview.Text = Request.ToSql(); ScriptButton.IsEnabled = !busy; ApplyButton.IsEnabled = !busy && Reviewed.IsChecked == true; if (!preserve) Feedback.Text = "Review the ordered values and SQL before applying."; }
        catch (ArgumentException ex) { if (!SnapshotIsStale) Preview.Clear(); ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; if (!preserve) Feedback.Text = ex.Message; }
    }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        using var source = new CancellationTokenSource(); cancellation = source; busy = true; Update(true); CancelButton.Focus(); Feedback.Text = "Inspecting enum…";
        try
        {
            var snapshot = await new EnumCatalog(connection).InspectAsync(schema,name,source.Token);
            if (snapshot.Type.Oid != oid) throw new InvalidOperationException("Original enum was replaced. Refresh Explorer and reopen.");
            source.Token.ThrowIfCancellationRequested(); Snapshot = snapshot; SnapshotIsStale = false;
            string? previous = Existing.SelectedValue as string;
            var choices = snapshot.Labels.Select((x,i) => new LabelChoice(x.Value, $"{i+1}. " + System.Text.Json.JsonSerializer.Serialize(x.Value, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))).ToArray();
            ready = false; Labels.ItemsSource = Existing.ItemsSource = choices; Existing.SelectedValue = previous; Reviewed.IsChecked = false; ready = true;
            Feedback.Text = "Enum inspected. Review a single value change.";
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        { SnapshotIsStale = true; Feedback.Text = source.IsCancellationRequested ? "Inspection canceled. Inspect again." : ex.Message; }
        finally { cancellation = null; busy = false; Update(true); InspectButton.Focus(); }
    }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy || !ApplyButton.IsEnabled) return;
        var request = Request;
        if (MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nRenaming changes existing values as displayed. Review applications that depend on these labels.", "Confirm Enum Change", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { Feedback.Text = "Operation canceled. Nothing was sent."; return; }
        using var source = new CancellationTokenSource(); cancellation = source; busy = true; RequiresParentRefresh = true; Update(true); CancelButton.Focus(); Feedback.Text = "Applying enum change…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request,source.Token); WasApplied = true; Feedback.Text = "Command completed. Inspect before another change."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        { Feedback.Text = (source.IsCancellationRequested ? "Enum change interrupted." : ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message) + " Result not confirmed. Inspect before retrying; cancellation cannot undo a completed commit."; }
        finally { SnapshotIsStale = true; ready = false; Reviewed.IsChecked = false; ready = true; cancellation = null; busy = false; Update(true); InspectButton.Focus(); }
    }
    private void Script(object sender, RoutedEventArgs e) { if (busy || !ScriptButton.IsEnabled) return; ScriptSql = Request.ToSql(); DialogResult = true; }
    private void Cancel(object sender, RoutedEventArgs e) => CancelCurrentOperation();
    public void CancelCurrentOperation() { if (cancellation is null) return; cancellation.Cancel(); CancelButton.IsEnabled = false; Feedback.Text = "Canceling… Waiting for cleanup."; }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; CancelCurrentOperation(); } }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.F5) { e.Handled = true; _ = InspectAsync(); } else if (e.Key == Key.Escape && busy) { e.Handled = true; CancelCurrentOperation(); } }
}
