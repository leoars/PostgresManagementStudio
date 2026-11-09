using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using PGMS.PostgreSql;
using Npgsql;

namespace PGMS.Desktop;

public partial class TableConstraintDialog : Window
{
    private readonly string connection;
    private readonly ObservableCollection<TableConstraintInfo> constraints = [];
    private bool ready, busy;
    private readonly DialogFocus focus = new();
    private CancellationTokenSource? inspection;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }

    /// <summary>Set by Object Explorer: read the current state as soon as the window opens, as SSMS does.</summary>
    public bool InspectOnOpen { get; init; }
    public TableConstraintDialog(string connection, string? schema = null, string? table = null)
    {
        this.connection = connection;
        InitializeComponent();
        Loaded += async (_, _) => { Operation.Focus(); if (InspectOnOpen) await InspectAsync(); }; // Opened on a table: list its constraints right away.
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Constraint Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        ExistingConstraints.ItemsSource = constraints;
        Operation.ItemsSource = new[]
        {
            new { Value = TableConstraintOperation.AddPrimaryKey, Label = "Add Primary Key" },
            new { Value = TableConstraintOperation.AddUnique, Label = "Add Unique Constraint" },
            new { Value = TableConstraintOperation.Drop, Label = "Drop Constraint" }
        };
        Operation.DisplayMemberPath = "Label";
        Operation.SelectedValuePath = "Value";
        Operation.SelectedValue = TableConstraintOperation.AddPrimaryKey;
        SchemaName.Text = schema ?? "public";
        TableName.Text = table ?? "";
        ready = true;
        Update();
    }

    private TableConstraintEdit Request => new((TableConstraintOperation)Operation.SelectedValue, SchemaName.Text,
        TableName.Text, ObjectName.Text, TableConstraintEdit.ParseColumns(Columns.Text)) { ExpectedIdentity = constraints.FirstOrDefault(c => c.Name == ObjectName.Text)?.Identity };

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (sender == SchemaName || sender == TableName) constraints.Clear();
        Update();
    }

    private void Update()
    {
        bool drop = Operation.SelectedValue is TableConstraintOperation.Drop;
        Columns.IsEnabled = !drop;
        ColumnEntry.Visibility = drop ? Visibility.Collapsed : Visibility.Visible;
        ConstraintGroup.Header = drop
            ? "Existing PRIMARY KEY and UNIQUE constraints — select a row to use its name for Drop"
            : "Existing PRIMARY KEY and UNIQUE constraints";
        try
        {
            Preview.Text = Request.ToSql();
            Feedback.Text = drop
                ? "Only PRIMARY KEY and UNIQUE constraints can be dropped here. Inspect and select one, or enter its exact name. Drop uses RESTRICT. Matching inspected names are checked for identity on Apply; Script has no identity check."
                : dropMessage(Operation.SelectedValue is TableConstraintOperation.AddPrimaryKey);
        }
        catch (ArgumentException ex)
        {
            Preview.Clear();
            Feedback.Text = ex.Message;
        }
        UpdateActions();
    }

    private static string dropMessage(bool primaryKey) => primaryKey
        ? "PRIMARY KEY requires non-NULL values and enforces uniqueness across the selected columns."
        : "UNIQUE enforces distinct non-NULL values across the selected columns; PostgreSQL permits multiple NULL values by default.";

    private bool CanInspect()
    {
        try { ObjectSql.Relation(SchemaName.Text, TableName.Text); return true; }
        catch (ArgumentException) { return false; }
    }

    private void UpdateActions()
    {
        bool valid = Preview.Text.Length > 0;
        ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy && valid;
        InspectButton.IsEnabled = !busy && CanInspect();
        ExistingConstraints.IsEnabled = !busy && constraints.Count > 0;
    }

    private void SetBusy(bool value)
    {
        if (value) focus.Capture();
        busy = value;
        Fields.IsEnabled = CloseButton.IsEnabled = !value;
        UpdateActions();
        if (!value) focus.Restore(this, CancelInspectionButton);
    }

    private void ConstraintSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ready && !busy && Operation.SelectedValue is TableConstraintOperation.Drop && ExistingConstraints.SelectedItem is TableConstraintInfo selected)
            ObjectName.Text = selected.Name;
    }

    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();

    public async Task InspectAsync()
    {
        if (busy || !CanInspect()) return;
        using var cancellation = new CancellationTokenSource();
        inspection = cancellation; CancelInspectionButton.IsEnabled = true;
        SetBusy(true); CancelInspectionButton.Focus();
        Feedback.Text = "Inspecting table constraints…";
        try
        {
            var inspected = await new TableConstraintService(connection).InspectAsync(SchemaName.Text, TableName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            constraints.Clear();
            foreach (var constraint in inspected)
                constraints.Add(constraint);
            Feedback.Text = constraints.Count == 0 ? "No PRIMARY KEY or UNIQUE constraints found for this table." : $"Found {constraints.Count} PRIMARY KEY/UNIQUE constraint(s). Select a row to copy its exact name when dropping.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. Previous results retained; inspect again to refresh."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
        }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }

    private void Script(object sender, RoutedEventArgs e)
    {
        ScriptSql = Request.ToSql();
        DialogResult = true;
    }

    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();

    public async Task ApplyAsync()
    {
        if (busy) return;
        var request = Request;
        if (request.Operation == TableConstraintOperation.Drop &&
            MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nDrop this table constraint?", "Confirm constraint deletion",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetBusy(true);
        Feedback.Text = "Applying operation…";
        try
        {
            await new AdministrativeEditService(connection).ApplyAsync(request);
            WasApplied = true;
            busy = false;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
        }
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
