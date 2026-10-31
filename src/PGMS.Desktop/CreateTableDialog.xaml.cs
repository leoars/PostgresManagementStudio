using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class CreateTableDialog : Window
{
    private readonly string connection;
    private readonly ObservableCollection<TableColumn> columns = [];
    private readonly ObservableCollection<string> partitionKeys = [];
    private bool ready, busy;
    private ColumnIdentity previousIdentity;
    private bool nullableBeforeIdentity=true;
    private SelectedColumnType? inspectedType;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public CreateTableDialog(string connection, string? schema = null)
    {
        this.connection = connection; InitializeComponent();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"New Table\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        ColumnGrid.ItemsSource = columns; DataType.ItemsSource = Enum.GetValues<ColumnType>(); DataType.SelectedIndex = 0;
        IdentitySelector.ItemsSource=ColumnIdentityChoices.All;IdentitySelector.DisplayMemberPath="Label";IdentitySelector.SelectedIndex=0;
        PartitionStrategySelector.ItemsSource = new[] { "None", "RANGE", "LIST", "HASH" };
        PartitionStrategySelector.SelectedIndex = 0;
        PartitionColumn.ItemsSource = columns; PartitionColumn.DisplayMemberPath = "Name";
        PartitionKeys.ItemsSource = partitionKeys;
        SchemaName.Text = schema ?? "public"; ready = true; Update();
    }
    private CreateTableEdit Request => new(SchemaName.Text, TableName.Text, columns.ToArray())
    {
        Partitioning = PartitionStrategySelector.SelectedIndex > 0
            ? new TablePartitioning((PartitionStrategy)(PartitionStrategySelector.SelectedIndex - 1), partitionKeys) : null
    };
    private void AddPartitionKey(object sender, RoutedEventArgs e)
    {
        if (busy || PartitionColumn.SelectedItem is not TableColumn column) return;
        if (!partitionKeys.Contains(column.Name)) partitionKeys.Add(column.Name);
        Update();
    }
    private void RemovePartitionKey(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (PartitionKeys.SelectedItem is string name) partitionKeys.Remove(name);
        Update();
    }
    private void MovePartitionKey(object sender, RoutedEventArgs e)
    {
        if (busy || PartitionKeys.SelectedIndex <= 0) return;
        int index = PartitionKeys.SelectedIndex;
        partitionKeys.Move(index, index - 1); PartitionKeys.SelectedIndex = index - 1; Update();
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private ColumnIdentity IdentityMode=>IdentitySelector.SelectedItem is ColumnIdentityChoice choice?choice.Value:ColumnIdentity.None;
    private void ColumnOptionsChanged(object sender,RoutedEventArgs e){if(ready)UpdateColumnOptions();}
    private void UpdateColumnOptions()
    {
        var mode=IdentityMode;
        if(mode!=ColumnIdentity.None && previousIdentity==ColumnIdentity.None){nullableBeforeIdentity=Nullable.IsChecked==true;Nullable.IsChecked=false;}
        if(mode==ColumnIdentity.None && previousIdentity!=ColumnIdentity.None)Nullable.IsChecked=nullableBeforeIdentity;
        previousIdentity=mode;Nullable.IsEnabled=!busy && mode==ColumnIdentity.None;
        bool valid=mode==ColumnIdentity.None || DataType.SelectedItem is ColumnType type && ColumnIdentityChoices.Supports(type);
        AddButton.IsEnabled=!busy && valid;
        CatalogTypeButton.IsEnabled=!busy && mode==ColumnIdentity.None;
        DataType.IsEnabled=IdentitySelector.IsEnabled=!busy && inspectedType is null;
        ClearTypeButton.IsEnabled=!busy && inspectedType is not null;
        CatalogTypeLabel.Text=inspectedType?.ToSql() ?? "Built-in type selected above.";
        IdentityHelp.Text=!valid?"Choose smallint, integer or bigint for identity, or select None."
            :mode==ColumnIdentity.None?"Identity is optional. Only added columns appear in the SQL."
            :"Identity generates values from an implicit sequence and requires NOT NULL. Add a key separately if uniqueness is needed.";
        IdentityHelp.Foreground=valid?System.Windows.Media.Brushes.Black:System.Windows.Media.Brushes.Firebrick;
    }
    private void Update()
    {
        UpdateColumnOptions();
        PartitionKeyFields.IsEnabled = !busy && PartitionStrategySelector.SelectedIndex > 0;
        try { Preview.Text = Request.ToSql(); Feedback.Text = PartitionStrategySelector.SelectedIndex > 0
            ? "Creates the partitioned parent only. Add partitions before inserting rows. No primary key or unique constraint is added automatically."
            : "Only added columns appear in the SQL. Identity is explicit; no primary key or unique constraint is added automatically."; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void AddColumn(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var column = new TableColumn(ColumnName.Text, (ColumnType)DataType.SelectedItem, Nullable.IsChecked == true){Identity=IdentityMode,InspectedType=inspectedType}; column.ToSql();
            if (columns.Count >= 100 || columns.Any(c => c.Name == column.Name)) throw new ArgumentException("Use a unique column name; maximum 100 columns.");
            columns.Add(column); ColumnName.Clear(); Update();
        }
        catch (ArgumentException ex) { Feedback.Text = ex.Message; }
    }
    private void RemoveColumn(object sender, RoutedEventArgs e) { if (!busy && ColumnGrid.SelectedItem is TableColumn column) { columns.Remove(column); Update(); } }
    private void ChooseCatalogType(object sender,RoutedEventArgs e)
    {
        if(busy || IdentityMode!=ColumnIdentity.None)return;
        var picker=new ColumnTypePicker(connection){Owner=this};
        if(picker.ShowDialog()==true && picker.SelectedType is {} selected)SetInspectedType(selected);
    }
    internal void SetInspectedType(SelectedColumnType selected){if(busy || IdentityMode!=ColumnIdentity.None)return;inspectedType=selected;Update();}
    private void ClearCatalogType(object sender,RoutedEventArgs e){if(busy)return;inspectedType=null;Update();}
    private void SetBusy(bool value) { busy = value; Names.IsEnabled = ColumnFields.IsEnabled = ColumnGrid.IsEnabled = CloseButton.IsEnabled = !value; ApplyButton.IsEnabled = ScriptButton.IsEnabled = !value && Preview.Text.Length > 0; }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        SetBusy(true); Feedback.Text = "Creating table…";
        try { var request = Request; await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
