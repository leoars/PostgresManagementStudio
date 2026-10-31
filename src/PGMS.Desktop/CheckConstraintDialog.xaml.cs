using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using PGMS.PostgreSql;
using Npgsql;

namespace PGMS.Desktop;

public sealed record CheckConstraintRow(string Name, string Definition) { public ConstraintIdentity? Identity { get; init; } public string Summary => $"CHECK: {Name} — {Definition}"; }

public partial class CheckConstraintDialog : Window
{
    private readonly string connection;
    private readonly ObservableCollection<CheckConstraintRow> constraints = [];
    private bool ready, busy;
    private readonly DialogFocus focus = new();
    private CancellationTokenSource? inspection;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }

    public CheckConstraintDialog(string connection, string? schema = null, string? table = null)
    {
        this.connection = connection; InitializeComponent();
        Loaded += (_, _) => Operation.Focus();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"CHECK Constraint Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        ExistingConstraints.ItemsSource = constraints;
        Operation.ItemsSource = new[] { new { Value = CheckConstraintOperation.Add, Label = "Add CHECK Constraint" }, new { Value = CheckConstraintOperation.Drop, Label = "Drop CHECK Constraint" }, new { Value = CheckConstraintOperation.Validate, Label = "Validate CHECK" } };
        Operation.DisplayMemberPath = "Label"; Operation.SelectedValuePath = "Value"; Operation.SelectedValue = CheckConstraintOperation.Add;
        SchemaName.Text = schema ?? "public"; TableName.Text = table ?? ""; ready = true; Update();
    }

    private CheckConstraintEdit Request => new((CheckConstraintOperation)Operation.SelectedValue, SchemaName.Text, TableName.Text, ObjectName.Text, Expression.Text) { NotValid = NotValid.IsChecked == true, ExpectedIdentity = constraints.FirstOrDefault(c => c.Name == ObjectName.Text)?.Identity };
    private void Changed(object sender, RoutedEventArgs e) { if (!ready) return; if (sender == SchemaName || sender == TableName) constraints.Clear(); Update(); }
    private void Update()
    {
        bool existing = Operation.SelectedValue is CheckConstraintOperation.Drop or CheckConstraintOperation.Validate;
        bool validate = Operation.SelectedValue is CheckConstraintOperation.Validate;
        ExpressionEntry.Visibility = existing ? Visibility.Collapsed : Visibility.Visible;
        NotValid.Visibility = ExpressionEntry.Visibility;
        try { Preview.Text = Request.ToSql(); Feedback.Text = validate ? "Validate scans existing rows and may take time. Inspect and select a constraint, or enter its exact name. Apply checks matching inspected identity; Script has no identity check." : existing ? "Only CHECK constraints can be dropped here. Inspect and select one, or enter its exact name. Drop uses RESTRICT. Matching inspected names are checked for identity on Apply; Script has no identity check." : "PostgreSQL validates the expression against existing and future rows. Review SQL expressions carefully."; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; }
        if (!existing && NotValid.IsChecked == true && Preview.Text.Length > 0) Feedback.Text = "Existing rows are not checked. New inserts and updates must satisfy CHECK. Use Validate CHECK after repairing existing data.";
        UpdateActions();
    }
    private void UpdateActions()
    {
        bool valid = Preview.Text.Length > 0; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy && valid;
        InspectButton.IsEnabled = !busy && CanInspect(); ExistingConstraints.IsEnabled = !busy && constraints.Count > 0;
    }
    private bool CanInspect() { try { ObjectSql.Relation(SchemaName.Text, TableName.Text); return true; } catch (ArgumentException) { return false; } }
    private void SetBusy(bool value) { if (value) focus.Capture(); busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; UpdateActions(); if (!value) focus.Restore(this, CancelInspectionButton); }
    private void ConstraintSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (ready && !busy && Operation.SelectedValue is (CheckConstraintOperation.Drop or CheckConstraintOperation.Validate) && ExistingConstraints.SelectedItem is CheckConstraintRow selected) ObjectName.Text = selected.Name; }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy || !CanInspect()) return; using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.IsEnabled = true;
        SetBusy(true); CancelInspectionButton.Focus(); Feedback.Text = "Inspecting CHECK constraints…";
        try { var inspected = await new CheckConstraintService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); constraints.Clear(); foreach (var c in inspected) constraints.Add(new(c.Name, c.Definition) { Identity = c.Identity }); Feedback.Text = constraints.Count == 0 ? "No CHECK constraints found for this table." : $"Found {constraints.Count} CHECK constraint(s). Select one to copy its exact name."; }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. Previous results retained; inspect again to refresh."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return; var request = Request;
        if (request.Operation == CheckConstraintOperation.Drop && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nDrop this CHECK constraint?", "Confirm constraint deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying operation…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => RequestInspectionCancellation();
    private void RequestInspectionCancellation()
    {
        if (inspection is null || inspection.IsCancellationRequested) return;
        CancelInspectionButton.IsEnabled = false;
        Feedback.Text = "Canceling read…";
        inspection.Cancel();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (inspection is not null && e.Key == System.Windows.Input.Key.Escape)
        { RequestInspectionCancellation(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; RequestInspectionCancellation(); }
    }
}
