using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class ColumnTypeDialog : Window
{
    private sealed record Choice(ColumnTypeConversion Value,string Label);
    private readonly string connection,schema,table,column;
    private readonly uint tableOid;
    private readonly short position;
    private readonly bool add;
    private bool ready,busy;
    private CancellationTokenSource? cancellation;
    private SelectedColumnType? selectedType;
    public ExpressionIndexTarget? Snapshot {get;private set;}
    public bool SnapshotIsStale {get;private set;}=true;
    public bool WasApplied {get;private set;}
    public bool RequiresParentRefresh {get;private set;}
    public string? ScriptSql {get;private set;}
    public ColumnTypeDialog(string connection,TableProperties selectedTable,TableColumnInfo? selectedColumn,string newColumn="")
    {
        this.connection=connection;schema=selectedTable.Schema;table=selectedTable.Name;column=selectedColumn?.Name??"";tableOid=selectedTable.Oid;position=selectedColumn?.Position??0;add=selectedColumn is null;
        InitializeComponent();Title=add?"Add Catalog Column":"Change Column Type";Target.Text=Title+"\n"+ObjectSql.Relation(schema,table)+(add?"":"."+SqlIdentifier.Quote(column));
        NewColumnFields.Visibility=add?Visibility.Visible:Visibility.Collapsed;ConversionFields.Visibility=add?Visibility.Collapsed:Visibility.Visible;NewColumnName.Text=newColumn;
        OperationHelp.Text=add?"Takes an exclusive table lock. Existing rows receive the domain default, if present, otherwise NULL. Domain constraints and NOT NULL can reject the addition. No identity generation is added.":"This operation takes an exclusive table lock and may rewrite rows. Explicit casts can truncate text or round numeric values. Defaults and dependent objects can prevent conversion; they are not removed automatically.";
        if(add)Reviewed.Content="I reviewed the new column, domain defaults and NULL constraints.";
        Conversion.ItemsSource=new[]{new Choice(ColumnTypeConversion.Cast,"Cast existing value"),new(ColumnTypeConversion.TrimText,"Trim text, then cast"),new(ColumnTypeConversion.EmptyTextToNull,"Empty text to NULL, then cast"),new(ColumnTypeConversion.LowerText,"Lowercase text, then cast")};
        Conversion.SelectedIndex=0;ready=true;Update();Loaded+=(_,_)=>InspectButton.Focus();
    }
    private InspectedColumnTypeEdit Request=>new(Snapshot is not null && !SnapshotIsStale?Snapshot:throw new ArgumentException("Inspect the selected table/column first."),add?NewColumnName.Text:column,selectedType??throw new ArgumentException("Choose a destination type."),add:add,nullable:AllowNull.IsChecked==true,conversion:add?ColumnTypeConversion.Cast:(ColumnTypeConversion)(Conversion.SelectedValue??ColumnTypeConversion.Cast));
    private void Changed(object sender,RoutedEventArgs e){if(ready){if(!ReferenceEquals(sender,Reviewed))Reviewed.IsChecked=false;Update();}}
    private void Update(bool preserve=false)
    {
        Fields.IsEnabled=InspectButton.IsEnabled=CloseButton.IsEnabled=!busy;CancelButton.IsEnabled=busy && cancellation?.IsCancellationRequested==false;
        SelectedTypeText.Text=selectedType?.ToSql()??"No destination type selected.";
        var current=Snapshot?.Columns.SingleOrDefault(c=>c.Name==column);
        Observation.Text=add && Snapshot is not null?$"{(SnapshotIsStale?"Outdated observation. Inspect again.\n":"")}Table OID: {Snapshot.Oid}   Existing columns: {Snapshot.Columns.Count}":current is null?"No current column observation.":$"{(SnapshotIsStale?"Outdated observation. Inspect again.\n":"")}Table OID: {Snapshot!.Oid}   Column: {current.Position}   Type OID: {current.TypeOid}   Modifier: {current.TypeModifier}";
        try{Preview.Text=Request.ToSql();ScriptButton.IsEnabled=!busy;ApplyButton.IsEnabled=!busy && Reviewed.IsChecked==true;if(!preserve)Feedback.Text=(add?"Review the new column and domain rules.":"Review stored-value conversion.")+" Script omits inspection guards; Apply checks the inspected identities.";}
        catch(ArgumentException ex){if(!SnapshotIsStale)Preview.Clear();ScriptButton.IsEnabled=ApplyButton.IsEnabled=false;if(!preserve)Feedback.Text=ex.Message;}
    }
    internal void SetType(SelectedColumnType type){if(busy)return;selectedType=type;Reviewed.IsChecked=false;Update();}
    private void ChooseType(object sender,RoutedEventArgs e){if(busy)return;var picker=new ColumnTypePicker(connection){Owner=this};if(picker.ShowDialog()==true && picker.SelectedType is {} type)SetType(type);}
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;busy=true;Update(true);CancelButton.Focus();Feedback.Text=add?"Inspecting table…":"Inspecting column…";
        try
        {
            var snapshot=await new ExpressionIndexCatalog(connection).InspectAsync(schema,table,source.Token);
            if(snapshot.Oid!=tableOid || !add && snapshot.Columns.SingleOrDefault(c=>c.Name==column)?.Position!=position)throw new InvalidOperationException("Original table or column was replaced. Reopen from the table inspection.");
            source.Token.ThrowIfCancellationRequested();Snapshot=snapshot;SnapshotIsStale=false;Reviewed.IsChecked=false;Feedback.Text="Target inspected. Choose and review the destination type.";
        }
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){SnapshotIsStale=true;Feedback.Text=source.IsCancellationRequested?"Inspection canceled. Inspect again.":ex.Message;}
        finally{cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;var request=Request;
        string confirmation=add?"Add this column? Domain defaults and constraints apply to existing rows.":"Convert stored values? Casts may truncate or round values. Review backups before applying.";
        if(MessageBox.Show(this,Target.Text+"\n\n"+request.ToSql()+"\n\n"+confirmation,add?"Confirm Column Addition":"Confirm Column Conversion",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Operation canceled. Nothing was sent.";return;}
        using var source=new CancellationTokenSource();cancellation=source;busy=true;RequiresParentRefresh=true;Update(true);CancelButton.Focus();Feedback.Text=add?"Adding column…":"Converting column…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Command completed. Inspect the column before another change.";}
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){Feedback.Text=(source.IsCancellationRequested?"Conversion interrupted.":ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message)+" Result not confirmed. Inspect before retrying; cancellation cannot undo a completed commit.";}
        finally{SnapshotIsStale=true;ready=false;Reviewed.IsChecked=false;ready=true;cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private void Script(object sender,RoutedEventArgs e){if(busy || !ScriptButton.IsEnabled)return;ScriptSql=Request.ToSql();DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;cancellation.Cancel();CancelButton.IsEnabled=false;Feedback.Text="Canceling… Waiting for cleanup.";}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private void OnPreviewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.F5){e.Handled=true;_=InspectAsync();}else if(e.Key==Key.Escape && busy){e.Handled=true;CancelCurrentOperation();}}
}
