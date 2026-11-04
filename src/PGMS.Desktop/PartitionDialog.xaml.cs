using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public sealed class PartitionInput : INotifyPropertyChanged
{
    public static PartitionValueKind[] Kinds { get; }=Enum.GetValues<PartitionValueKind>();
    public string Label { get; init; }="Value";
    private PartitionValueKind lowerKind,upperKind;
    private string lowerText="",upperText="";
    public PartitionValueKind LowerKind {get=>lowerKind;set{lowerKind=value;Notify(nameof(LowerKind));}}
    public PartitionValueKind UpperKind {get=>upperKind;set{upperKind=value;Notify(nameof(UpperKind));}}
    public string LowerText {get=>lowerText;set{lowerText=value;Notify(nameof(LowerText));}}
    public string UpperText {get=>upperText;set{upperText=value;Notify(nameof(UpperText));}}
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string property)=>PropertyChanged?.Invoke(this,new(property));
    public PartitionValue Lower()=>new(LowerKind,LowerKind==PartitionValueKind.Literal?LowerText:"");
    public PartitionValue Upper()=>new(UpperKind,UpperKind==PartitionValueKind.Literal?UpperText:"");
}

public partial class PartitionDialog:Window
{
    private readonly string connection,schema,name;
    private readonly uint expectedOid;
    private readonly ObservableCollection<PartitionInput> values=[];
    private readonly ObservableCollection<string> subKeys=[];
    private PartitionInspection? snapshot;
    private PartitionAttachCandidate? candidate;
    private bool ready,busy,stale=true;
    private CancellationTokenSource? cancellation;
    public bool WasApplied {get;private set;}
    public bool RequiresParentRefresh {get;private set;}
    public string? ScriptSql {get;private set;}
    public PartitionDialog(string connection,string schema,string name,uint expectedOid)
    {
        this.connection=connection;this.schema=schema;this.name=name;this.expectedOid=expectedOid;InitializeComponent();
        var c=new NpgsqlConnectionStringBuilder(connection);Target.Text=$"Partitions\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}\n{ObjectSql.Relation(schema,name)}";
        Operation.ItemsSource=new[]{"Create partition","Attach existing table","Detach partition"};Operation.SelectedIndex=0;ChildSchema.Text=schema;ValuesGrid.ItemsSource=values;
        SubStrategy.ItemsSource=new[]{"None","RANGE","LIST","HASH"};SubStrategy.SelectedIndex=0;SubKeys.ItemsSource=subKeys;ready=true;Update();Loaded+=(_,_)=>InspectButton.Focus();
    }
    private PartitionBounds Bounds
    {
        get
        {
            var parent=snapshot!.Parent;
            if(DefaultBound.IsChecked==true)return PartitionBounds.Default(parent.Strategy);
            return parent.Strategy switch
            {
                PartitionStrategy.Range=>PartitionBounds.Range(values.Select(v=>v.Lower()),values.Select(v=>v.Upper())),
                PartitionStrategy.List=>PartitionBounds.List(values.Select(v=>v.Lower())),
                _=>int.TryParse(Modulus.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int m)&&int.TryParse(Remainder.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int r)?PartitionBounds.Hash(m,r):throw new ArgumentException("Enter integer HASH modulus and remainder.")
            };
        }
    }
    private IAdministrativeEdit Request
    {
        get
        {
            if(stale || snapshot is null)throw new ArgumentException("Inspect the selected parent before scripting or applying.");
            return Operation.SelectedIndex switch
            {
                0=>new CreatePartitionEdit(snapshot.Parent,ChildSchema.Text,ChildName.Text,Bounds){Partitioning=SubStrategy.SelectedIndex>0?new TablePartitioning((PartitionStrategy)(SubStrategy.SelectedIndex-1),subKeys):null},
                1=>new AttachPartitionEdit(snapshot.Parent,candidate??throw new ArgumentException("Inspect the existing child before attaching."),Bounds),
                2=>new DetachPartitionEdit(snapshot.Parent,Children.SelectedItem as PartitionChild??throw new ArgumentException("Select an inspected partition to detach.")),
                _=>throw new ArgumentException("Select a partition operation.")
            };
        }
    }
    private PartitionChild? SelectedChild=>Children.SelectedItem as PartitionChild;
    private bool Standalone=>Operation.SelectedIndex==2 && (ConcurrentDetach.IsChecked==true || SelectedChild?.DetachPending==true);
    private string ScriptFor(IAdministrativeEdit request)=>"BEGIN;\nSET LOCAL search_path=pg_catalog;\nSET LOCAL DateStyle='ISO, YMD';\nSET LOCAL TimeZone='UTC';\n"+request.ToSql()+"\nCOMMIT;";
    private string PreviewSql()
    {
        if(!Standalone)return ScriptFor(Request);
        if(stale || snapshot is null)throw new ArgumentException("Inspect the selected parent before scripting or applying.");
        return "-- Standalone statement: run it outside any transaction block.\n"+ConcurrentPartitionDetachService.ToSql(snapshot.Parent,SelectedChild??throw new ArgumentException("Select an inspected partition to detach."));
    }
    private void Changed(object sender,RoutedEventArgs e){if(ready)Update();}
    private void ChildChanged(object sender,RoutedEventArgs e){candidate=null;if(ready)Update();}
    private void AddRow(){var row=new PartitionInput{Label="Key / value "+(values.Count+1)};row.PropertyChanged+=(_,_)=>{if(ready)Update();};values.Add(row);}
    private void AddValue(object sender,RoutedEventArgs e){if(!busy && values.Count<1000){AddRow();Update();}}
    private void RemoveValue(object sender,RoutedEventArgs e){if(!busy && ValuesGrid.SelectedItem is PartitionInput row){values.Remove(row);Update();}}
    private void AddSubKey(object sender,RoutedEventArgs e){if(!busy && SubColumn.SelectedItem is string column && !subKeys.Contains(column)){subKeys.Add(column);Update();}}
    private void RemoveSubKey(object sender,RoutedEventArgs e){if(!busy && SubKeys.SelectedItem is string column){subKeys.Remove(column);Update();}}
    private void MoveSubKey(object sender,RoutedEventArgs e){if(busy || SubKeys.SelectedIndex<=0)return;int index=SubKeys.SelectedIndex;subKeys.Move(index,index-1);SubKeys.SelectedIndex=index-1;Update();}
    private void Update(bool preserveFeedback=false)
    {
        bool detach=Operation.SelectedIndex==2,hash=snapshot?.Parent.Strategy==PartitionStrategy.Hash,list=snapshot?.Parent.Strategy==PartitionStrategy.List;
        ChildFields.Visibility=detach?Visibility.Collapsed:Visibility.Visible;DetachFields.Visibility=detach?Visibility.Visible:Visibility.Collapsed;BoundFields.Visibility=detach?Visibility.Collapsed:Visibility.Visible;
        CandidateButton.Visibility=Operation.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;CandidateButton.IsEnabled=!busy && !stale;
        bool sub=Operation.SelectedIndex==0 && SubStrategy.SelectedIndex>0;SubPartitionFields.Visibility=Operation.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;SubKeyFields.IsEnabled=sub;
        DefaultBound.Visibility=hash?Visibility.Collapsed:Visibility.Visible;
        HashFields.Visibility=hash?Visibility.Visible:Visibility.Collapsed;LiteralFields.Visibility=!hash && DefaultBound.IsChecked!=true?Visibility.Visible:Visibility.Collapsed;
        ListButtons.Visibility=list?Visibility.Visible:Visibility.Collapsed;UpperKindColumn.Visibility=UpperTextColumn.Visibility=list?Visibility.Collapsed:Visibility.Visible;
        bool pending=SelectedChild?.DetachPending==true;ConcurrentDetach.IsEnabled=!pending;
        ChildObservation.Text=SelectedChild is PartitionChild child?$"{ObjectSql.Relation(child.Schema,child.Name)}\n{child.Bound}"+(child.Kind=="p"?"\nPartitioned table: its own partitions stay attached to it and move with it.":"")+(pending?"\nDetach pending: Apply runs FINALIZE to complete it.":""):"";
        Help.Text=pending?"A previous concurrent detach was canceled or interrupted. FINALIZE completes it outside a transaction. The child table and data are kept. Confirmation is required."
            :detach && ConcurrentDetach.IsChecked==true?"Concurrent detach keeps the child table and data and waits for running queries on the parent instead of blocking them. It runs outside a transaction: if canceled or interrupted, the partition stays pending detach and can be finalized. Not available while the parent has a DEFAULT partition."
            :detach?"Detach keeps the child table and data. Its rows stop appearing through the parent. Confirmation is required."
            :Operation.SelectedIndex==1?"Attach validates existing rows and may block concurrent operations. A partitioned table is validated through all of its partitions. Tables and data are preserved if the transaction fails."
            :sub?"Create an empty partition that is itself partitioned. It stores no rows: inserts into its range fail until you create partitions under it with Partitions on the new table. Key columns come from the inspected parent."
            :"Create an empty child partition. PostgreSQL rejects overlapping bounds and conflicting rows in a DEFAULT partition.";
        try{Preview.Text=PreviewSql();ApplyButton.IsEnabled=ScriptButton.IsEnabled=!busy;if(!preserveFeedback)Feedback.Text="Review the SQL. Apply uses the inspected identities; an exported script resolves names when executed.";}
        catch(ArgumentException ex){Preview.Clear();ApplyButton.IsEnabled=ScriptButton.IsEnabled=false;if(!preserveFeedback)Feedback.Text=ex.Message;}
    }
    private void SetBusy(bool value){busy=value;Fields.IsEnabled=InspectButton.IsEnabled=!value;CancelButton.Visibility=value?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=value;Update(true);}
    private static bool Handled(Exception e)=>e is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or PartitionDetachUncertainException;
    private static string Outcome(PartitionDetachUncertainException e)=>(e.InnerException is null?"":Error(e.InnerException)+" ")+e.Observed switch
    {
        PartitionDetachState.Attached=>"The partition is still attached; nothing was detached.",
        PartitionDetachState.DetachPending=>"The partition is pending detach. Inspect again and Apply to FINALIZE it.",
        PartitionDetachState.Detached=>"The partition is detached.",
        _=>"Result not confirmed; inspect before retrying."
    };
    private static string Error(Exception e)=>e is PostgresException p?$"{p.SqlState}: {p.MessageText}":e.Message;
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;stale=true;candidate=null;SetBusy(true);Feedback.Text="Inspecting parent and direct partitions…";
        try
        {
            var observed=await new PartitionCatalog(connection).InspectAsync(schema,name,source.Token);
            if(observed.Parent.Oid!=expectedOid)throw new InvalidOperationException("Selected parent was replaced. Refresh Object Explorer.");
            snapshot=observed;stale=false;Observation.Text=$"{observed.Parent.Definition}\nDirect partitions: {observed.Children.Count}";Children.ItemsSource=observed.Children;Children.SelectedIndex=-1;
            ready=false;DefaultBound.IsChecked=false;values.Clear();SubColumn.ItemsSource=observed.Columns;foreach(var key in subKeys.Where(k=>!observed.Columns.Contains(k)).ToArray())subKeys.Remove(key);for(int i=0;i<(observed.Parent.Strategy==PartitionStrategy.Range?observed.Parent.KeyCount:1);i++)AddRow();ready=true;
            Feedback.Text="Inspection complete. No changes made.";
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex);}
        finally{cancellation=null;SetBusy(false);}
    }
    private async void InspectCandidate(object sender,RoutedEventArgs e)=>await InspectCandidateAsync();
    public async Task InspectCandidateAsync()
    {
        if(busy || stale)return;using var source=new CancellationTokenSource();cancellation=source;candidate=null;SetBusy(true);
        try{candidate=await new PartitionAttachCatalog(connection).InspectAsync(ChildSchema.Text,ChildName.Text,source.Token);Feedback.Text=candidate.Kind=="p"?"Existing partitioned table captured with its partitions. Review bounds before attaching.":"Existing child identity captured. Review bounds before attaching.";}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex);}
        finally{cancellation=null;SetBusy(false);}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;
        if(Standalone){await ApplyStandaloneAsync();return;}
        var request=Request;
        if(request is DetachPartitionEdit && MessageBox.Show(this,Target.Text+"\n\n"+request.ToSql()+"\n\nKeep the child table but remove its rows from parent queries?","Confirm Detach Partition",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Detach canceled. Nothing was sent.";return;}
        using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);RequiresParentRefresh=true;Feedback.Text="Applying partition change…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Command completed. Inspect again before another change.";}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex)+" Result not confirmed; inspect before retrying. Cancel cannot undo a completed commit.";}
        finally{stale=true;candidate=null;cancellation=null;SetBusy(false);InspectButton.Focus();}
    }
    private async Task ApplyStandaloneAsync()
    {
        var parent=snapshot!.Parent;var child=SelectedChild!;string sql=ConcurrentPartitionDetachService.ToSql(parent,child);
        if(MessageBox.Show(this,Target.Text+"\n\n"+sql+"\n\nThis runs outside a transaction and cannot be rolled back. Keep the child table but remove its rows from parent queries?",child.DetachPending?"Confirm Finalize Detach":"Confirm Concurrent Detach",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Detach canceled. Nothing was sent.";return;}
        using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);RequiresParentRefresh=true;Feedback.Text=child.DetachPending?"Finalizing pending detach…":"Detaching concurrently; waiting for running queries on the parent…";
        try{await new ConcurrentPartitionDetachService(connection).ApplyAsync(parent,child,true,source.Token);WasApplied=true;Feedback.Text="Partition detached and verified. Inspect again before another change.";}
        catch(PartitionDetachUncertainException ex){Feedback.Text=Outcome(ex);}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex)+" Nothing was sent.";}
        finally{stale=true;candidate=null;cancellation=null;SetBusy(false);InspectButton.Focus();}
    }
    private void Script(object sender,RoutedEventArgs e){if(busy || !ScriptButton.IsEnabled)return;ScriptSql=PreviewSql();DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;CancelButton.IsEnabled=false;Feedback.Text="Canceling; waiting for cleanup…";cancellation.Cancel();}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private void OnKey(object sender,KeyEventArgs e){if(e.Key==Key.F5){e.Handled=true;_=InspectAsync();}else if(busy && e.Key==Key.Escape){e.Handled=true;CancelCurrentOperation();}}
}
