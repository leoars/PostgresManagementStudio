using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Edit Top n Rows (M1044), after SSMS 22.11: an editable grid of the first rows, saved when the row is left (pencil while it has changes),
/// Esc cancels, a new row at the bottom (*), NULL in italics, Ctrl+0 sets NULL, Delete asks before deleting the selected rows. A row that cannot be
/// saved stays in edit with the SSMS message. Values are PostgreSQL text (numbers keep the dot), not regional formats as in SSMS. The Query
/// Designer panes (Diagram, Criteria, SQL) are not offered.</summary>
public partial class EditRowsView : UserControl
{
    private readonly TableRowsService service;
    private TableRowsSnapshot? snapshot;
    private DataTable table = new();
    private bool committingRow;
    private bool saving;

    public string ConnectionString { get; }
    public string Schema { get; }
    public string Table { get; }
    public int Top { get; private set; }
    public string Title { get; }
    public bool IsBusy => saving || loading;
    private bool loading;
    /// <summary>Shown instead of a message box; tests replace them.</summary>
    internal Func<string, MessageBoxResult> ConfirmDelete { get; set; }
    internal Action<string> ShowSaveError { get; set; }
    public event Action? StateChanged;

    public EditRowsView(string connectionString, string schema, string tableName, int top)
    {
        InitializeComponent();
        ConnectionString = connectionString; Schema = schema; Table = tableName; Top = top;
        service = new TableRowsService(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Title = $"{builder.Host}.{builder.Database} - {schema}.{tableName}";
        ConfirmDelete = text => MessageBox.Show(Window.GetWindow(this), text, "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        ShowSaveError = text => MessageBox.Show(Window.GetWindow(this), text, "PGMS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Targets(string connection, string schema, string tableName) => ConnectionString == connection && Schema == schema && Table == tableName;
    public int RowCount => table.Rows.Count;
    internal DataTable Data => table;
    internal TableRowsSnapshot? Snapshot => snapshot;

    /// <summary>Reads the first rows again (Execute SQL in SSMS). Refused while a row has unsaved changes.</summary>
    public async Task LoadAsync(int? top = null)
    {
        if (IsBusy) return;
        if (IsRowEditing) { StatusText.Text = "Save or cancel (Esc) the row being edited before reloading."; return; }
        loading = true; StateChanged?.Invoke();
        StatusText.Text = "Reading rows…";
        try
        {
            if (top is { } n) Top = n;
            snapshot = await service.LoadAsync(Schema, Table, Top);
            Bind(snapshot);
            StatusText.Text = snapshot.HasKey ? $"Rows are saved by primary key when you leave the row." : "No primary key: rows are found by all their values; identical rows cannot be changed.";
            if (snapshot.RelationKind is "v" or "f") StatusText.Text += " Changes go through the view or foreign table; PostgreSQL may refuse them.";
        }
        catch (Exception ex) when (ex is NpgsqlException or TableRowsException or InvalidOperationException or System.IO.IOException or TimeoutException)
        {
            StatusText.Text = "Could not read the rows: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message);
        }
        finally { loading = false; StateChanged?.Invoke(); }
    }

    private bool IsRowEditing => Rows.ItemContainerGenerator.ContainerFromItem(Rows.CurrentItem) is DataGridRow { IsEditing: true }
        || Rows.Items.OfType<DataRowView>().Any(v => v.IsEdit || v.IsNew);

    private void Bind(TableRowsSnapshot rows)
    {
        table = new DataTable();
        foreach (var column in rows.Columns) table.Columns.Add(new DataColumn("c" + table.Columns.Count, typeof(string)) { Caption = column.Name, AllowDBNull = true });
        foreach (var values in rows.Rows) table.Rows.Add(values.Select(v => (object?)v ?? DBNull.Value).ToArray());
        table.AcceptChanges();
        Rows.Columns.Clear();
        var italic = new Style(typeof(TextBlock));
        italic.Triggers.Add(new Trigger { Property = TextBlock.TextProperty, Value = "NULL", Setters = { new Setter(TextBlock.FontStyleProperty, FontStyles.Italic) } });
        italic.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        for (int i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];
            Rows.Columns.Add(new DataGridTextColumn
            {
                Header = column.Name, IsReadOnly = column.ReadOnly, MaxWidth = 400, ElementStyle = italic,
                Binding = new Binding($"[c{i}]") { Converter = NullText.Instance, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = false }
            });
        }
        Rows.ItemsSource = table.DefaultView;
        UpdatePosition();
    }

    private static Dictionary<int, string?> Values(DataRowView view, int count, DataRowVersion? compareTo)
    {
        var values = new Dictionary<int, string?>();
        for (int i = 0; i < count; i++)
        {
            string? proposed = view[i] is string s ? s : null;
            if (compareTo is { } version) { string? before = view.Row[i, version] is string b ? b : null; if (before == proposed) continue; }
            else if (proposed is null) continue;
            values[i] = proposed;
        }
        return values;
    }

    /// <summary>Leaving a row saves it, as in SSMS. The edit stays open while the row is saved; a refused row stays in edit with the message.</summary>
    private async void RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        if (committingRow || e.EditAction != DataGridEditAction.Commit || snapshot is null || e.Row.Item is not DataRowView view) return;
        bool isNew = view.IsNew;
        var changes = isNew ? Values(view, snapshot.Columns.Count, null) : Values(view, snapshot.Columns.Count, DataRowVersion.Current);
        if (!isNew && changes.Count == 0) return;
        e.Cancel = true;
        await SaveRowAsync(view, isNew, changes, e.Row.GetIndex() + 1);
    }

    internal async Task<bool> SaveRowAsync(DataRowView view, bool isNew, Dictionary<int, string?> changes, int rowNumber)
    {
        if (snapshot is null || saving) return false;
        saving = true; StateChanged?.Invoke();
        Rows.IsHitTestVisible = false;
        StatusText.Text = "Saving row…";
        try
        {
            var original = isNew ? [] : Enumerable.Range(0, snapshot.Columns.Count).Select(i => view.Row[i, DataRowVersion.Current] as string).ToArray();
            var stored = isNew ? await service.InsertAsync(snapshot, changes) : await service.UpdateAsync(snapshot, original, changes);
            committingRow = true;
            try { Rows.CommitEdit(DataGridEditingUnit.Row, true); } finally { committingRow = false; }
            if (view.IsEdit || view.IsNew) view.EndEdit();
            var row = view.Row;
            for (int i = 0; i < stored.Length; i++) row[i] = (object?)stored[i] ?? DBNull.Value;
            if (row.RowState == DataRowState.Detached) table.Rows.Add(row);
            row.AcceptChanges();
            StatusText.Text = isNew ? "Row added." : "Row saved.";
            UpdatePosition();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TableRowsException or InvalidOperationException or System.IO.IOException or TimeoutException)
        {
            // SSMS 22.11 wording (docs/MARCO-M1044.md); the source names PostgreSQL or PGMS instead of DataTools.
            string source = ex is PostgresException ? "PostgreSQL" : ex is NpgsqlException ? "Npgsql" : "PGMS";
            string message = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" + (pg.Detail is { Length: > 0 } d ? " " + d : "") : ex.Message;
            StatusText.Text = "The row was not saved. Correct it and leave the row again, or press Esc to cancel the changes.";
            ShowSaveError($"No row was updated.\n\nThe data in row {rowNumber} was not committed.\nError Source: {source}.\nError Message: {message}\n\nCorrect the errors and retry or press ESC to cancel the changes.");
            return false;
        }
        finally { saving = false; Rows.IsHitTestVisible = true; StateChanged?.Invoke(); }
    }

    /// <summary>Whole rows selected: by the row header, or with every cell of the row selected.</summary>
    internal IReadOnlyList<DataRowView> SelectedRows => Rows.SelectedCells.GroupBy(c => c.Item).Where(g => g.Select(c => c.Column).Distinct().Count() == Rows.Columns.Count).Select(g => g.Key)
        .Concat(Rows.SelectedItems.Cast<object>()).OfType<DataRowView>().Where(v => !v.IsNew).Distinct().ToList();

    /// <summary>Deletes whole selected rows after the SSMS question; all or none.</summary>
    internal async Task<bool> DeleteSelectedAsync()
    {
        if (snapshot is null || IsBusy) return false;
        var selected = SelectedRows;
        if (selected.Count == 0) return false;
        if (ConfirmDelete($"Number of rows to delete: {selected.Count} \n\nClick Yes to permanently delete these rows. You won't be able to undo those changes.") != MessageBoxResult.Yes) return false;
        saving = true; StateChanged?.Invoke();
        try
        {
            var values = selected.Select(v => Enumerable.Range(0, snapshot.Columns.Count).Select(i => v.Row[i, DataRowVersion.Current] as string).ToArray()).ToList();
            await service.DeleteAsync(snapshot, values);
            foreach (var view in selected) view.Row.Delete();
            table.AcceptChanges();
            StatusText.Text = selected.Count == 1 ? "Row deleted." : $"{selected.Count} rows deleted.";
            UpdatePosition();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TableRowsException or InvalidOperationException or System.IO.IOException or TimeoutException)
        {
            string message = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
            StatusText.Text = "No row was deleted.";
            ShowSaveError($"No rows were deleted.\n\nError Message: {message}");
            return false;
        }
        finally { saving = false; StateChanged?.Invoke(); }
    }

    private async void RowsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool editingCell = Keyboard.FocusedElement is TextBox { IsReadOnly: false };
        // Arrows confirm the cell like Enter and Tab (pedido do usuário, 08/11/2026), as the SSMS grid (Windows Forms DataGridView) does: Up/Down
        // always; Left/Right only at the start/end of the text (or with all of it selected), otherwise they move the caret.
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } box && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            bool all = box.SelectionLength == box.Text.Length && box.Text.Length > 0;
            bool leave = e.Key switch
            {
                Key.Left => all || box.SelectionLength == 0 && box.CaretIndex == 0,
                Key.Right => all || box.SelectionLength == 0 && box.CaretIndex == box.Text.Length,
                _ => true
            };
            if (!leave) return;
            e.Handled = true;
            await MoveFromEditAsync(e.Key);
            return;
        }
        // SSMS deletes only whole selected rows; Delete on single cells does nothing.
        if (e.Key == Key.Delete && !editingCell && SelectedRows.Count > 0)
        { e.Handled = true; await DeleteSelectedAsync(); }
        else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SetCurrentCellNull(); }
    }

    /// <summary>Confirms the cell and moves with an arrow. Going to another row confirms the row too, which saves it; on failure the edit stays where it is.</summary>
    internal async Task MoveFromEditAsync(Key key)
    {
        if (Rows.CurrentItem is not { } item || Rows.CurrentColumn is not { } column) return;
        int row = Rows.Items.IndexOf(item), col = column.DisplayIndex;
        int targetRow = key switch { Key.Up => row - 1, Key.Down => row + 1, _ => row };
        int targetCol = key switch { Key.Left => col - 1, Key.Right => col + 1, _ => col };
        if (!Rows.CommitEdit(DataGridEditingUnit.Cell, true)) return;
        if (targetRow < 0 || targetRow >= Rows.Items.Count || targetCol < 0 || targetCol >= Rows.Columns.Count) return;
        if (targetRow != row)
        {
            Rows.CommitEdit(DataGridEditingUnit.Row, true); // RowEditEnding saves; the row stays in edit until the server accepts it
            for (int i = 0; i < 400 && saving; i++) await Task.Delay(25);
            if (Rows.Items.OfType<DataRowView>().Any(v => v.IsEdit || v.IsNew)) return; // not saved: stay on the row, as SSMS does
            if (targetRow >= Rows.Items.Count) targetRow = Rows.Items.Count - 1;
        }
        var targetColumn = Rows.Columns.OrderBy(c => c.DisplayIndex).ElementAt(targetCol);
        Rows.CurrentCell = new DataGridCellInfo(Rows.Items[targetRow], targetColumn);
        Rows.SelectedCells.Clear(); Rows.SelectedCells.Add(Rows.CurrentCell);
        Rows.ScrollIntoView(Rows.Items[targetRow], targetColumn);
        Rows.Focus();
        UpdatePosition();
    }

    /// <summary>Ctrl+0, as in SSMS: puts NULL in the current cell (saved with the row).</summary>
    internal void SetCurrentCellNull()
    {
        if (Rows.CurrentColumn is not DataGridTextColumn { IsReadOnly: false } column || Rows.CurrentItem is null) return;
        Rows.BeginEdit();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (column.GetCellContent(Rows.CurrentItem) is TextBox box) { box.Text = "NULL"; box.SelectAll(); }
    }

    private void CurrentCellMoved(object? sender, EventArgs e) => UpdatePosition();

    private void UpdatePosition()
    {
        int count = table.Rows.Count;
        int index = Rows.CurrentItem is DataRowView view ? table.DefaultView.Cast<DataRowView>().ToList().IndexOf(view) : -1;
        PositionBox.Text = index >= 0 ? (index + 1).ToString(CultureInfo.CurrentCulture) : Rows.CurrentItem is null ? "" : (count + 1).ToString(CultureInfo.CurrentCulture);
        CountText.Text = "of " + count.ToString(CultureInfo.CurrentCulture);
    }

    private void MoveTo(int index)
    {
        if (Rows.Items.Count == 0) return;
        index = Math.Clamp(index, 0, Rows.Items.Count - 1);
        var item = Rows.Items[index];
        var column = Rows.CurrentColumn ?? Rows.Columns.FirstOrDefault();
        if (column is null) return;
        Rows.CurrentCell = new DataGridCellInfo(item, column);
        Rows.SelectedCells.Clear(); Rows.SelectedCells.Add(Rows.CurrentCell);
        Rows.ScrollIntoView(item, column);
        Rows.Focus();
        UpdatePosition();
    }
    private int CurrentIndex => Rows.CurrentItem is { } item ? Rows.Items.IndexOf(item) : -1;
    private void MoveFirst(object sender, RoutedEventArgs e) => MoveTo(0);
    private void MovePrevious(object sender, RoutedEventArgs e) => MoveTo(Math.Max(0, CurrentIndex - 1));
    private void MoveNext(object sender, RoutedEventArgs e) => MoveTo(Math.Min(table.Rows.Count - 1, CurrentIndex + 1));
    private void MoveLast(object sender, RoutedEventArgs e) => MoveTo(table.Rows.Count - 1);
    private void MoveNew(object sender, RoutedEventArgs e) => MoveTo(Rows.Items.Count - 1);
    private void PositionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse(PositionBox.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int position) && position >= 1) MoveTo(position - 1);
        else UpdatePosition();
    }
}

/// <summary>Text shown and edited in a cell: NULL for a null value; typing NULL sets the value to NULL (as in SSMS).</summary>
public sealed class NullText : IValueConverter
{
    public static readonly NullText Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null or DBNull ? "NULL" : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is null or "NULL" ? DBNull.Value : value;
}

/// <summary>Row header glyph: pencil while the row is being edited, * for the new row, ▶ for the current row.</summary>
public sealed class EditRowGlyph : IMultiValueConverter
{
    public static readonly EditRowGlyph Instance = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [true, ..] ? "✎" : values is [_, true, ..] ? "*" : values.Length >= 4 && values[2] is not null && ReferenceEquals(values[2], values[3]) ? "▶" : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
