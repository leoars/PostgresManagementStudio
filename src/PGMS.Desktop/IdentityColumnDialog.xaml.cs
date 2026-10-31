using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class IdentityColumnDialog:Window
{
    private sealed record Choice(IdentityColumnOperation Value,string Label);
    private readonly string connection,schema,table,column;
    private readonly uint expectedTableOid;
    private readonly short expectedPosition;
    private readonly DialogFocus focus=new();
    private bool ready,busy;
    private CancellationTokenSource? cancellation;
    private string? validationError;
    public IdentityColumnSnapshot? Snapshot { get; private set; }
    public bool SnapshotIsStale { get; private set; }
    public bool WasApplied { get; private set; }
    public bool RequiresParentRefresh { get; private set; }
    public string? ScriptSql { get; private set; }
    public IdentityColumnDialog(string connection,TableProperties selectedTable,TableColumnInfo selectedColumn)
    {
        this.connection=connection;schema=selectedTable.Schema;table=selectedTable.Name;column=selectedColumn.Name;expectedTableOid=selectedTable.Oid;expectedPosition=selectedColumn.Position;
        InitializeComponent();var c=new NpgsqlConnectionStringBuilder(connection);
        Target.Text=$"Identity Column\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}\n{ObjectSql.Relation(schema,table)}.{SqlIdentifier.Quote(column)}";
        Operation.DisplayMemberPath="Label";ready=true;Update(true);Loaded+=(_,_)=>InspectButton.Focus();
    }
    private IdentityColumnOperation SelectedOperation=>Operation.SelectedItem is Choice choice?choice.Value:throw new ArgumentException("Inspect this column and select an identity operation.");
    private IdentityColumnEdit Request
    {
        get
        {
            if(Snapshot is null || SnapshotIsStale)throw new ArgumentException("Inspect the selected column before applying or scripting.");
            long? value=null;
            if(SelectedOperation==IdentityColumnOperation.Restart)
            {
                if(!long.TryParse(RestartValue.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out long parsed))throw new ArgumentException("Enter an explicit integer restart value within the inspected sequence bounds.");
                value=parsed;
            }
            bool numeric=SelectedOperation is IdentityColumnOperation.SetIncrement or IdentityColumnOperation.SetStart or IdentityColumnOperation.SetBounds or IdentityColumnOperation.SetCache;
            long Parse(TextBox box,string label)=>long.TryParse(box.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out long parsed)?parsed:throw new ArgumentException("Enter an explicit integer "+label+".");
            return new(Snapshot,SelectedOperation,value){ParameterValue=numeric?Parse(ParameterValue,"parameter value"):null,Maximum=SelectedOperation==IdentityColumnOperation.SetBounds?Parse(MaximumValue,"maximum"):null,Cycle=SelectedOperation==IdentityColumnOperation.SetCycle?CycleValue.IsChecked==true:null};
        }
    }
    private void Changed(object sender,RoutedEventArgs e){if(ready)Update();}
    private void Update(bool preserveFeedback=false)
    {
        bool restart=Operation.SelectedItem is Choice{Value:IdentityColumnOperation.Restart};
        var operation=(Operation.SelectedItem as Choice)?.Value;
        bool numeric=operation is IdentityColumnOperation.SetIncrement or IdentityColumnOperation.SetStart or IdentityColumnOperation.SetBounds or IdentityColumnOperation.SetCache;
        RestartFields.Visibility=restart?Visibility.Visible:Visibility.Collapsed;
        ParameterFields.Visibility=numeric?Visibility.Visible:Visibility.Collapsed;MaximumFields.Visibility=operation==IdentityColumnOperation.SetBounds?Visibility.Visible:Visibility.Collapsed;CycleValue.Visibility=operation==IdentityColumnOperation.SetCycle?Visibility.Visible:Visibility.Collapsed;
        LoadConfiguredButton.Visibility=numeric || operation==IdentityColumnOperation.SetCycle?Visibility.Visible:Visibility.Collapsed;
        LoadConfiguredButton.IsEnabled=!busy && !SnapshotIsStale && Snapshot?.Sequence is not null;
        ParameterLabel.Content=operation switch{IdentityColumnOperation.SetIncrement=>"_Increment:",IdentityColumnOperation.SetStart=>"Configured _start:",IdentityColumnOperation.SetBounds=>"_Minimum:",IdentityColumnOperation.SetCache=>"_Cache:",_=>"_Value:"};
        OperationHelp.Text=Operation.SelectedItem is Choice{Value:IdentityColumnOperation.Drop}
            ?"Drop removes automatic generation and its internal sequence. Existing values and NOT NULL remain. This requires confirmation."
            :restart?"Restart sets the next value explicitly. Existing rows are unchanged and may conflict with future values. The next value is not sampled by inspection."
            :operation==IdentityColumnOperation.SetStart?"Configured START is used by a future restart without a value. Changing it does not reset the next value; use Restart identity explicitly."
            :operation==IdentityColumnOperation.SetCache?"Cache reserves values per session and can create gaps. Inspection reads configuration only; it does not sample cached or next values."
            :operation==IdentityColumnOperation.SetCycle?"Cycle wraps at the sequence bounds and can generate existing values again. Identity does not ensure uniqueness."
            :operation==IdentityColumnOperation.SetBounds?"Bounds must fit the inspected sequence type and include configured START. PostgreSQL also checks current sequence state; no automatic restart is performed."
            :operation==IdentityColumnOperation.SetIncrement?"Increment must be nonzero and fit the inspected sequence type. Negative increments count down; existing rows are unchanged."
            :"Generation changes affect future INSERT and UPDATE commands; existing values are unchanged. Adding identity does not infer a starting value from your data. Script omits the inspection guards.";
        Fields.IsEnabled=!busy;InspectButton.IsEnabled=!busy;CloseButton.IsEnabled=!busy;
        try{Preview.Text=Request.ToSql();validationError=null;ScriptButton.IsEnabled=ApplyButton.IsEnabled=!busy;if(!preserveFeedback)Feedback.Text="Review the SQL. Apply checks the inspected column and underlying sequence configuration.";}
        catch(ArgumentException ex){validationError=ex.Message;if(!SnapshotIsStale)Preview.Clear();ScriptButton.IsEnabled=ApplyButton.IsEnabled=false;if(!preserveFeedback)Feedback.Text=ex.Message;}
    }
    private void LoadConfigured(object sender,RoutedEventArgs e)
    {
        if(busy || SnapshotIsStale || Snapshot?.Sequence is not { } s)return;
        if(SelectedOperation==IdentityColumnOperation.SetCycle)CycleValue.IsChecked=s.Cycle;
        else{ParameterValue.Text=(SelectedOperation switch{IdentityColumnOperation.SetIncrement=>s.Increment,IdentityColumnOperation.SetStart=>s.Start,IdentityColumnOperation.SetBounds=>s.Minimum,IdentityColumnOperation.SetCache=>s.Cache,_=>throw new InvalidOperationException("Select a sequence parameter.")}).ToString(CultureInfo.InvariantCulture);if(SelectedOperation==IdentityColumnOperation.SetBounds)MaximumValue.Text=s.Maximum.ToString(CultureInfo.InvariantCulture);ParameterValue.Focus();ParameterValue.SelectAll();}
        Update();
    }
    private static string Mode(string code)=>code switch{"a"=>"Always","d"=>"By default",_=>"None"};
    private void ShowObservation()
    {
        if(Snapshot is not { } s)return;
        string type=s.TypeOid switch{21=>"smallint",23=>"integer",20=>"bigint",_=>"Unsupported identity type"};
        Observation.Text=$"{(SnapshotIsStale?"Outdated observation. Inspect again.\n":"")}Generation: {Mode(s.Generation)}   Type: {type}   Nullable: {s.Nullable}\nDefault: {s.DefaultExpression??"None"}   Generated expression: {(s.Generated.Length==0?"None":"Present")}\nTable OID: {s.TableOid}   Column number: {s.Position}   Database OID: {s.DatabaseOid}\n"+
            (s.Sequence is{ } seq?$"Sequence: {ObjectSql.Relation(seq.Schema,seq.Name)} (OID {seq.Oid})\nStart: {seq.Start}   Min: {seq.Minimum}   Max: {seq.Maximum}\nIncrement: {seq.Increment}   Cache: {seq.Cache}   Cycle: {seq.Cycle}":"No identity sequence. Existing values will not be rewritten when adding identity.")+
            "\nInspection does not read table data or advance the sequence.";
    }
    private void SetBusy(bool value)
    {
        if(value)focus.Capture();busy=value;Update(true);CancelButton.Visibility=value?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=value;if(value)CancelButton.Focus();else if(SnapshotIsStale)InspectButton.Focus();else focus.Restore(this,CancelButton);
    }
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);Feedback.Text="Inspecting selected identity column…";
        try
        {
            var snapshot=await new IdentityColumnService(connection).InspectAsync(schema,table,column,source.Token,expectedTableOid,expectedPosition);source.Token.ThrowIfCancellationRequested();Snapshot=snapshot;SnapshotIsStale=false;
            var prior=(Operation.SelectedItem as Choice)?.Value;
            var choices=snapshot.HasIdentity
                ?new[]{new Choice(IdentityColumnOperation.SetByDefault,"Set generation: by default"),new(IdentityColumnOperation.SetAlways,"Set generation: always"),new(IdentityColumnOperation.Restart,"Restart identity"),new(IdentityColumnOperation.Drop,"Drop identity"),new(IdentityColumnOperation.SetIncrement,"Set sequence increment"),new(IdentityColumnOperation.SetStart,"Set configured sequence start"),new(IdentityColumnOperation.SetBounds,"Set sequence bounds"),new(IdentityColumnOperation.SetCache,"Set sequence cache"),new(IdentityColumnOperation.SetCycle,"Set sequence cycle")}
                :[new Choice(IdentityColumnOperation.AddByDefault,"Add identity: by default"),new(IdentityColumnOperation.AddAlways,"Add identity: always")];
            Operation.ItemsSource=choices;Operation.SelectedItem=choices.FirstOrDefault(c=>c.Value==prior)??choices[0];ShowObservation();Update(true);Feedback.Text=validationError??"Identity metadata loaded. No changes made; current and next values were not sampled.";
        }
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {SnapshotIsStale=true;ShowObservation();Feedback.Text=source.IsCancellationRequested?"Inspection canceled. Inspect again to retry.":(ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message)+" Inspect the table again if its selected column changed.";}
        finally{cancellation=null;SetBusy(false);}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;
        var request=Request;
        if(request.Operation is IdentityColumnOperation.Drop or IdentityColumnOperation.Restart)
        {
            string message=request.Operation==IdentityColumnOperation.Drop?"Remove automatic generation and its internal sequence? Existing values and NOT NULL remain.":"Restart the next identity value at "+request.RestartValue!.Value.ToString(CultureInfo.InvariantCulture)+"? Existing rows are unchanged; future values may conflict with them.";
            if(MessageBox.Show(this,Target.Text+"\n\n"+request.ToSql()+"\n\n"+message,"Confirm Identity Change",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Identity change canceled. Nothing was sent.";return;}
        }
        using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);Feedback.Text="Applying identity change…";RequiresParentRefresh=true;
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Command completed. Inspect current identity metadata before another change.";}
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {Feedback.Text=source.IsCancellationRequested?"Identity Apply interrupted. Result not confirmed; inspect before retrying. Canceling cannot undo a completed commit.":(ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message)+" Result not confirmed. Inspect before retrying.";}
        finally{SnapshotIsStale=true;ShowObservation();cancellation=null;SetBusy(false);}
    }
    private void Script(object sender,RoutedEventArgs e){if(busy || !ScriptButton.IsEnabled)return;ScriptSql=Request.ToSql();DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;CancelButton.IsEnabled=false;Feedback.Text="Canceling identity operation… Waiting for cleanup.";cancellation.Cancel();}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private void OnPreviewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.F5){e.Handled=true;_ = InspectAsync();}else if(busy && e.Key==Key.Escape){e.Handled=true;CancelCurrentOperation();}}
}
