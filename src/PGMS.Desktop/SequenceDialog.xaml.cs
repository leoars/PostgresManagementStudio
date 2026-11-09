using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class SequenceDialog : Window
{
    private readonly string connection;
    private bool ready, busy, closeAfterApply;
    private UIElement? focusBeforeBusy;
    private uint? expectedExplorerOid;
    private CancellationTokenSource? inspection, application;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public SequenceSnapshot? Snapshot { get; private set; }
    /// <summary>Set by Object Explorer: read the current state as soon as the window opens, as SSMS does.</summary>
    public bool InspectOnOpen { get; init; }
    public SequenceDialog(string connection, string? schema = null, string? name = null, uint? expectedOid = null)
    {
        this.connection = connection; InitializeComponent();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Sequence Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        SchemaName.Text = schema ?? "public"; SequenceName.Text = name ?? "";
        DataType.ItemsSource = Enum.GetValues<SequenceDataType>(); DataType.SelectedItem = SequenceDataType.BigInt;
        ParameterType.ItemsSource = Enum.GetValues<SequenceDataType>(); ParameterType.SelectedItem = SequenceDataType.BigInt;
        Operation.ItemsSource = Enum.GetValues<SequenceOperation>(); Operation.SelectedItem = SequenceOperation.Inspect;
        expectedExplorerOid = expectedOid; ready = true; UpdatePreview();
        Loaded += async (_, _) => { Operation.Focus(); if (InspectOnOpen) await InspectAsync(); }; // Opened on a sequence: read it right away.
    }
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (sender == SchemaName || sender == SequenceName) { Snapshot = null; Summary.Clear(); expectedExplorerOid = null; }
        if (sender == SchemaName || sender == SequenceName || sender == OwnerTable) OwnerColumn.ItemsSource = null;
        OwnerSummary.Text = OwnerColumn.SelectedItem is SequenceOwnerTarget target
            ? $"Table OID: {target.TableOid}   Column number: {target.ColumnNumber}   Owner: {target.Owner}\nDestination: {target.Schema}.{target.Table}.{target.Column}"
            : "Read destination columns and choose a column.";
        UpdatePreview();
    }
    private bool CanInspect() { try { ObjectSql.Relation(SchemaName.Text, SequenceName.Text); return true; } catch (ArgumentException) { return false; } }
    private SequenceOperation SelectedOperation => (SequenceOperation)Operation.SelectedItem;
    private static long? ReadNumber(string text, string label, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(text)) return null;
        if (long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long value)) return value;
        throw new ArgumentException(label + " must be a whole number within the 64-bit range.");
    }
    private bool IsParameterOperation => SelectedOperation is SequenceOperation.SetIncrement or SequenceOperation.SetCache or SequenceOperation.SetCycle or SequenceOperation.SetStart or SequenceOperation.SetBounds or SequenceOperation.SetDataType or SequenceOperation.Restart;
    private IAdministrativeEdit Request => SelectedOperation switch
    {
        SequenceOperation.Create => new SequenceCreateEdit(SchemaName.Text, SequenceName.Text)
        {
            DataType = (SequenceDataType)DataType.SelectedItem, Increment = ReadNumber(Increment.Text, "Increment")!.Value,
            Start = ReadNumber(StartValue.Text, "Start", true), Minimum = ReadNumber(Minimum.Text, "Minimum", true),
            Maximum = ReadNumber(Maximum.Text, "Maximum", true), Cache = ReadNumber(Cache.Text, "Cache")!.Value, Cycle = Cycle.IsChecked == true
        },
        SequenceOperation.SetOwnedBy => new SequenceAttachEdit(Snapshot ?? throw new ArgumentException("Inspect the sequence first."), OwnerColumn.SelectedItem as SequenceOwnerTarget ?? throw new ArgumentException("Read destination columns and choose a column first.")),
        SequenceOperation.DetachOwnedBy => new SequenceDetachEdit(SchemaName.Text, SequenceName.Text) { Expected = Snapshot },
        SequenceOperation.Restart => new SequenceRestartEdit(SchemaName.Text, SequenceName.Text, ReadNumber(ParameterValue.Text, "Restart value")!.Value) { Expected = Snapshot },
        SequenceOperation.SetDataType => new SequenceParameterEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { Expected = Snapshot, DataType = (SequenceDataType)ParameterType.SelectedItem },
        SequenceOperation.SetBounds => new SequenceParameterEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { Expected = Snapshot, Value = ReadNumber(ParameterValue.Text, "Minimum")!.Value, Maximum = ReadNumber(ParameterMaximum.Text, "Maximum")!.Value },
        SequenceOperation.SetCycle => new SequenceParameterEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { Expected = Snapshot, Cycle = ParameterCycle.IsChecked == true },
        SequenceOperation.SetIncrement or SequenceOperation.SetCache or SequenceOperation.SetStart => new SequenceParameterEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { Expected = Snapshot, Value = ReadNumber(ParameterValue.Text, "Value")!.Value },
        SequenceOperation.Drop => new SequenceChangeEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { ExpectedOid = Snapshot?.Oid },
        SequenceOperation.Comment => new SequenceChangeEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { Comment = CommentText.Text, RemoveComment = RemoveComment.IsChecked == true, ExpectedOid = Snapshot?.Oid },
        SequenceOperation.Rename => new SequenceChangeEdit(SchemaName.Text, SequenceName.Text, SelectedOperation) { NewName = NewName.Text, ExpectedOid = Snapshot?.Oid },
        _ => throw new ArgumentException("Choose Create to generate SQL, or Inspect to read an existing sequence.")
    };
    private string OperationHint => SelectedOperation switch
    {
        SequenceOperation.SetOwnedBy => "Associates the sequence with the inspected column. Dropping that column/table will also drop the sequence. Existing defaults and values stay unchanged. Same schema/owner and SELECT on both relations required. Apply checks identities and confirms; Script has no conflict guard.",
        SequenceOperation.DetachOwnedBy => "Removes OWNED BY only. Dropping the former column/table will no longer drop this sequence. Existing column defaults and values stay unchanged. Identity sequences are not supported. Inspect before Script or Apply; Apply confirms and checks the association, Script does not.",
        SequenceOperation.Restart => "Restarts at an explicit value; may repeat values already used by rows or clients. Configured START stays unchanged. Inspect before Script or Apply. Apply checks configuration, not values consumed since inspection, and requires confirmation. Script has no conflict guard.",
        SequenceOperation.SetDataType => "Changes the sequence type while preserving inspected limits and current value. Inspect before Script or Apply. Narrowing requires compatible bounds and increment; adjust those first if needed. Apply checks conflicts; Script does not.",
        SequenceOperation.SetBounds => "Changes both limits, without resetting values. Bounds must include configured START; PostgreSQL also checks current state. Apply checks inspected configuration; SELECT and ownership required. Script has no conflict guard.",
        SequenceOperation.SetStart => "Changes configured START only; does not restart or change the current value. START is used by a future RESTART. Apply checks inspected configuration; SELECT and ownership required. Script has no conflict guard.",
        SequenceOperation.SetCycle => "Cycle can repeat values after a limit; NO CYCLE stops generation at the limit. Cached values in other sessions may remain. Apply checks inspected configuration; SELECT and ownership required. Script has no conflict guard.",
        SequenceOperation.SetCache => "Changes cache only. Larger caches can leave gaps; other sessions retain cached values. Apply checks inspected configuration; SELECT and ownership required. Script has no conflict guard.",
        SequenceOperation.SetIncrement => "Changes increment only; limits stay unchanged. Other sessions may still use cached values. Apply compares inspected configuration under an ALTER lock; SELECT and ownership required. Script has no conflict guard.",
        SequenceOperation.Create => "Apply creates a permanent sequence. Cycle may repeat values; cache can leave gaps. Script returns SQL without executing.",
        SequenceOperation.Rename => "Inspect before Apply. Rename preserves the sequence and dependencies. Apply checks OID under a read lock; SELECT and ownership required. Script has no identity guard.",
        SequenceOperation.Comment => "Replaces the current comment; concurrent comment edits are not compared. Apply requires inspection, SELECT and ownership. Script has no identity guard.",
        SequenceOperation.Drop => "Drop uses RESTRICT; dependencies are not removed automatically. Apply requires inspection, SELECT and ownership. Script has no identity guard.",
        _ => "Inspect reads configuration without consuming values. Current/next values are not shown."
    };
    private void UpdatePreview()
    {
        OwnerFields.Visibility = SelectedOperation == SequenceOperation.SetOwnedBy ? Visibility.Visible : Visibility.Collapsed;
        LoadParameterButton.Content = SelectedOperation == SequenceOperation.Restart ? "_Load configured START" : "_Load inspected value";
        ParameterMaximumFields.Visibility = SelectedOperation == SequenceOperation.SetBounds ? Visibility.Visible : Visibility.Collapsed;
        bool typeParameter = SelectedOperation == SequenceOperation.SetDataType;
        ParameterTypeFields.Visibility = typeParameter ? Visibility.Visible : Visibility.Collapsed;
        bool cycleParameter = SelectedOperation == SequenceOperation.SetCycle;
        ParameterCycle.Visibility = cycleParameter ? Visibility.Visible : Visibility.Collapsed;
        ParameterLabel.Visibility = ParameterValue.Visibility = cycleParameter || typeParameter ? Visibility.Collapsed : Visibility.Visible;
        ParameterLabel.Content = SelectedOperation switch { SequenceOperation.Restart => "Restart with value:", SequenceOperation.SetBounds => "New minimum:", SequenceOperation.SetCache => "New cache size:", SequenceOperation.SetStart => "New configured START (does not restart):", _ => "New increment:" };
        ParameterFields.Visibility = IsParameterOperation ? Visibility.Visible : Visibility.Collapsed;
        CommentFields.Visibility = SelectedOperation == SequenceOperation.Comment ? Visibility.Visible : Visibility.Collapsed;
        CommentText.IsEnabled = RemoveComment.IsChecked != true;
        RenameFields.Visibility = SelectedOperation == SequenceOperation.Rename ? Visibility.Visible : Visibility.Collapsed;
        CreateFields.Visibility = SelectedOperation == SequenceOperation.Create ? Visibility.Visible : Visibility.Collapsed;
        bool inspecting = SelectedOperation == SequenceOperation.Inspect;
        Preview.Visibility = inspecting ? Visibility.Collapsed : Visibility.Visible;
        PreviewRow.MinHeight = inspecting ? 0 : 80;
        PreviewRow.Height = inspecting ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        try { Preview.Text = Request.ToSql(); Feedback.Text = OperationHint; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = SelectedOperation == SequenceOperation.Inspect ? "Inspect reads configuration without consuming values. Current/next values are not shown." : ex.Message; }
        UpdateActions();
    }
    private void UpdateActions()
    {
        ReadOwnerButton.IsEnabled = !busy;
        LoadParameterButton.IsEnabled = !busy && Snapshot is not null;
        LoadCommentButton.IsEnabled = !busy && Snapshot is not null;
        InspectButton.IsEnabled = !busy && CanInspect();
        ScriptButton.IsEnabled = !busy && Preview.Text.Length > 0;
        ApplyButton.IsEnabled = ScriptButton.IsEnabled && (SelectedOperation == SequenceOperation.Create || Snapshot is not null);
    }
    private void LoadParameter(object sender, RoutedEventArgs e)
    {
        if (Snapshot is null || busy) return;
        ParameterMaximum.Text = Snapshot.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ParameterCycle.IsChecked = Snapshot.Cycle;
        ParameterValue.Text = (SelectedOperation switch { SequenceOperation.SetBounds => Snapshot.Minimum, SequenceOperation.SetCache => Snapshot.Cache, SequenceOperation.SetStart or SequenceOperation.Restart => Snapshot.Start, _ => Snapshot.Increment }).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (SelectedOperation == SequenceOperation.SetDataType)
        {
            ParameterType.SelectedItem = Snapshot.DataType switch { "smallint" => SequenceDataType.SmallInt, "integer" => SequenceDataType.Integer, _ => SequenceDataType.BigInt };
            ParameterType.Focus();
        }
        else if (SelectedOperation == SequenceOperation.SetCycle) ParameterCycle.Focus();
        else { ParameterValue.Focus(); ParameterValue.SelectAll(); }
    }
    private void LoadComment(object sender, RoutedEventArgs e)
    {
        if (Snapshot is null || busy) return;
        CommentText.Text = Snapshot.Comment ?? ""; RemoveComment.IsChecked = false;
        CommentText.Focus(); CommentText.SelectAll();
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        if (SelectedOperation != SequenceOperation.Create && Snapshot is null)
        {
            Feedback.Text = "Inspect this sequence before applying changes.";
            InspectButton.Focus();
            return;
        }
        using var cancellation = new CancellationTokenSource();
        SetBusy(true); Feedback.Text = "Applying sequence operation...";
        try
        {
            var request = Request;
            if (SelectedOperation == SequenceOperation.Drop && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nDrop this sequence? Dependent objects will not be removed automatically.", "Confirm sequence deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { Feedback.Text = "Sequence deletion canceled. No change applied."; return; }
            if (SelectedOperation == SequenceOperation.Restart && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nRestart this sequence? Values already used may be generated again. Existing rows are not checked or changed.", "Confirm sequence restart", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { Feedback.Text = "Sequence restart canceled. No change applied."; return; }
            if (SelectedOperation == SequenceOperation.DetachOwnedBy && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nRemove the association with " + Snapshot!.OwnedBy + "? Dropping that column/table will no longer remove this sequence. Existing defaults stay unchanged.", "Confirm sequence association removal", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { Feedback.Text = "Sequence association removal canceled. No change applied."; return; }
            if (SelectedOperation == SequenceOperation.SetOwnedBy && MessageBox.Show(this, Target.Text + "\n\n" + request.ToSql() + "\n\nReplace the current OWNED BY association (" + (Snapshot!.OwnedBy ?? "none") + ")? Dropping the destination column/table will also drop this sequence. Existing defaults stay unchanged.", "Confirm sequence association", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { Feedback.Text = "Sequence association canceled. No change applied."; return; }
            application = cancellation; CancelApplyButton.Visibility = Visibility.Visible; CancelApplyButton.IsEnabled = true; CancelApplyButton.Focus();
            await new AdministrativeEditService(connection).ApplyAsync(request, cancellation.Token); WasApplied = true; busy = false; DialogResult = true;
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        {
            Snapshot = null; Summary.Clear(); Preview.Clear(); focusBeforeBusy = InspectButton;
            Feedback.Text = "Cancellation requested; operation has finished. Inspect the server state before retrying. Do not assume the outcome if cancellation raced with commit.";
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { application = null; CancelApplyButton.IsEnabled = false; CancelApplyButton.Visibility = Visibility.Collapsed; SetBusy(false); if (closeAfterApply) { closeAfterApply = false; if (IsVisible) Close(); } }
    }
    private void SetBusy(bool value)
    {
        if (value) focusBeforeBusy = Keyboard.FocusedElement as UIElement;
        busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; UpdateActions();
        if (!value)
        {
            var current = Keyboard.FocusedElement as UIElement;
            if (IsVisible && (current is null || current == this || current == CancelInspectionButton || current == CancelApplyButton || !current.IsEnabled)
                && focusBeforeBusy is { IsEnabled: true, IsVisible: true } previous)
                previous.Focus();
            focusBeforeBusy = null;
        }
    }
    private async void ReadOwner(object sender, RoutedEventArgs e) => await ReadOwnerColumnsAsync();
    public async Task ReadOwnerColumnsAsync()
    {
        if (busy) return;
        try { ObjectSql.Relation(SchemaName.Text, OwnerTable.Text); }
        catch (ArgumentException ex) { Feedback.Text = ex.Message; OwnerTable.Focus(); return; }
        string? selected = (OwnerColumn.SelectedItem as SequenceOwnerTarget)?.Column;
        OwnerColumn.ItemsSource = null;
        using var cancellation = new CancellationTokenSource(); inspection = cancellation;
        SetBusy(true); CancelInspectionButton.IsEnabled = true; CancelInspectionButton.Focus(); Feedback.Text = "Reading destination columns...";
        try
        {
            var columns = await new SequenceOwnerTargetService(connection).ListColumnsAsync(SchemaName.Text, OwnerTable.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            OwnerColumn.ItemsSource = columns;
            OwnerColumn.SelectedItem = columns.FirstOrDefault(x => x.Column == selected);
            UpdatePreview();
            Feedback.Text = columns.Count == 0 ? "No eligible columns found. Use a permanent ordinary table with common columns." : "Destination columns inspected. Choose a column and review its identity before Apply.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Destination inspection canceled. Read destination columns again before Apply."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy || !CanInspect()) return;
        using var cancellation = new CancellationTokenSource(); inspection = cancellation;
        SetBusy(true); CancelInspectionButton.IsEnabled = true; CancelInspectionButton.Focus(); Feedback.Text = "Inspecting sequence...";
        try
        {
            var snapshot = await new SequenceService(connection).InspectAsync(SchemaName.Text, SequenceName.Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (expectedExplorerOid is { } oid && snapshot.Oid != oid) throw new InvalidOperationException("Sequence changed since Explorer loaded. Refresh Object Explorer before editing.");
            Snapshot = snapshot;
            UpdatePreview();
            Summary.Text = $"Schema: {snapshot.Schema}\nSequence: {snapshot.Name}\nOID: {snapshot.Oid}\nOwner: {snapshot.Owner}\nType: {snapshot.DataType}\nStart: {snapshot.Start}\nIncrement: {snapshot.Increment}\nMinimum: {snapshot.Minimum}\nMaximum: {snapshot.Maximum}\nCache: {snapshot.Cache}\nCycle: {snapshot.Cycle}\nOwned by: {snapshot.OwnedBy ?? "(none)"}\n\nComment:\n{snapshot.Comment ?? "(none)"}";
            Feedback.Text = "Snapshot loaded. Start is configured START, not current/next value. " + (SelectedOperation != SequenceOperation.Inspect && Preview.Text.Length == 0 ? Feedback.Text : OperationHint);
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or TimeoutException)
        { Feedback.Text = "Inspection canceled. Previous snapshot retained; inspect again to retry."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { inspection = null; CancelInspectionButton.IsEnabled = false; SetBusy(false); }
    }
    private void CancelApply(object sender, RoutedEventArgs e) => RequestApplyCancellation();
    private void RequestApplyCancellation()
    {
        if (application is null || application.IsCancellationRequested) return;
        CancelApplyButton.IsEnabled = false; Feedback.Text = "Canceling operation; waiting for transaction cleanup...";
        application.Cancel();
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => RequestCancellation();
    private void RequestCancellation() { if (inspection is null || inspection.IsCancellationRequested) return; CancelInspectionButton.IsEnabled = false; Feedback.Text = "Canceling read..."; inspection.Cancel(); }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (application is not null) { RequestApplyCancellation(); e.Handled = true; }
        else if (inspection is not null) { RequestCancellation(); e.Handled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        if (application is not null) { closeAfterApply = true; RequestApplyCancellation(); }
        else RequestCancellation();
    }
}
