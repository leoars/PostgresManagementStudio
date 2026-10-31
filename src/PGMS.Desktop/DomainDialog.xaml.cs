using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class DomainDialog : Window
{
    private sealed record Choice(IndexComparison Value,string Label);
    private readonly string connection,schema,name;
    private readonly uint oid;
    private bool ready,busy;
    private CancellationTokenSource? cancellation;
    public DomainSnapshot? Snapshot {get;private set;}
    public bool SnapshotIsStale {get;private set;}=true;
    public bool WasApplied {get;private set;}
    public bool RequiresParentRefresh {get;private set;}
    public string? ScriptSql {get;private set;}
    public DomainDialog(string connection,string schema,string name,uint oid)
    {
        this.connection=connection;this.schema=schema;this.name=name;this.oid=oid;
        InitializeComponent();Target.Text="Domain Constraints\n"+ObjectSql.Relation(schema,name);
        Operation.ItemsSource=Enum.GetValues<DomainConstraintOperation>();Operation.SelectedIndex=0;
        Comparison.ItemsSource=new[]{new Choice(IndexComparison.Equal,"VALUE = literal"),new(IndexComparison.NotEqual,"VALUE <> literal"),new(IndexComparison.Less,"VALUE < literal"),new(IndexComparison.LessOrEqual,"VALUE <= literal"),new(IndexComparison.Greater,"VALUE > literal"),new(IndexComparison.GreaterOrEqual,"VALUE >= literal"),new(IndexComparison.IsNull,"VALUE IS NULL"),new(IndexComparison.IsNotNull,"VALUE IS NOT NULL")};Comparison.SelectedIndex=0;
        ready=true;Update();Loaded+=(_,_)=>InspectButton.Focus();
    }
    private DomainConstraintOperation SelectedOperation=>(DomainConstraintOperation)Operation.SelectedItem;
    private IndexComparison SelectedComparison=>(IndexComparison)Comparison.SelectedValue;
    private DomainConstraintEdit Request
    {
        get
        {
            if(Snapshot is null || SnapshotIsStale)throw new ArgumentException("Inspect the selected domain first.");
            bool add=SelectedOperation==DomainConstraintOperation.Add;
            return new(Snapshot,SelectedOperation,add?ConstraintName.Text:Existing.SelectedValue as string??"",add?new DomainRule(SelectedComparison,SelectedComparison is IndexComparison.IsNull or IndexComparison.IsNotNull?"":Literal.Text):null,add && NotValid.IsChecked==true);
        }
    }
    private void Changed(object sender,RoutedEventArgs e){if(ready){if(!ReferenceEquals(sender,Reviewed))Reviewed.IsChecked=false;Update();}}
    private void Update(bool preserve=false)
    {
        Fields.IsEnabled=InspectButton.IsEnabled=CloseButton.IsEnabled=!busy;CancelButton.IsEnabled=busy && cancellation?.IsCancellationRequested==false;
        bool add=SelectedOperation==DomainConstraintOperation.Add;AddFields.Visibility=add?Visibility.Visible:Visibility.Collapsed;Existing.IsEnabled=!add;
        Literal.IsEnabled=SelectedComparison is not(IndexComparison.IsNull or IndexComparison.IsNotNull);
        Observation.Text=Snapshot is null?"No current domain observation.":$"{(SnapshotIsStale?"Outdated observation. Inspect again.\n":"")}Type OID: {Snapshot.Type.Oid}   Base: {Snapshot.BaseTypeSql}\nDefault: {Snapshot.DefaultSql??"(none)"}   NOT NULL: {Snapshot.NotNull}";
        try{Preview.Text=Request.ToSql();ScriptButton.IsEnabled=!busy;ApplyButton.IsEnabled=!busy && Reviewed.IsChecked==true;if(!preserve)Feedback.Text="Review the domain rule. Script omits inspection guards.";}
        catch(ArgumentException ex){if(!SnapshotIsStale)Preview.Clear();ScriptButton.IsEnabled=ApplyButton.IsEnabled=false;if(!preserve)Feedback.Text=ex.Message;}
    }
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;busy=true;Update(true);CancelButton.Focus();Feedback.Text="Inspecting domain…";
        try
        {
            var snapshot=await new DomainCatalog(connection).InspectAsync(schema,name,source.Token);
            if(snapshot.Type.Oid!=oid)throw new InvalidOperationException("Original domain was replaced. Refresh Explorer and reopen.");
            source.Token.ThrowIfCancellationRequested();Snapshot=snapshot;SnapshotIsStale=false;string? previous=Existing.SelectedValue as string;
            ready=false;Existing.ItemsSource=snapshot.Constraints;Existing.SelectedValue=previous;Reviewed.IsChecked=false;ready=true;
            Constraints.Text=snapshot.Constraints.Count==0?"(no constraints)":string.Join("\n",snapshot.Constraints.Select(x=>$"{SqlIdentifier.Quote(x.Name)}   {(x.Validated?"Validated":"NOT VALID")}   OID {x.Oid}\nCHECK ({x.Expression})"));
            Feedback.Text="Domain inspected. Review a single constraint change.";
        }
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){SnapshotIsStale=true;Feedback.Text=source.IsCancellationRequested?"Inspection canceled. Inspect again.":ex.Message;}
        finally{cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;var request=Request;
        if(MessageBox.Show(this,Target.Text+"\n\n"+request.ToSql()+"\n\nThis affects all uses of the domain. Review validation timing and the effect of removing restrictions.","Confirm Domain Change",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Operation canceled. Nothing was sent.";return;}
        using var source=new CancellationTokenSource();cancellation=source;busy=true;RequiresParentRefresh=true;Update(true);CancelButton.Focus();Feedback.Text="Applying domain change…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Command completed. Inspect before another change.";}
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){Feedback.Text=(source.IsCancellationRequested?"Domain change interrupted.":ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message)+" Result not confirmed. Inspect before retrying; cancellation cannot undo a completed commit.";}
        finally{SnapshotIsStale=true;ready=false;Reviewed.IsChecked=false;ready=true;cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private void Script(object sender,RoutedEventArgs e){if(busy || !ScriptButton.IsEnabled)return;ScriptSql=Request.ToSql();DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;cancellation.Cancel();CancelButton.IsEnabled=false;Feedback.Text="Canceling… Waiting for cleanup.";}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private void OnPreviewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.F5){e.Handled=true;_=InspectAsync();}else if(e.Key==Key.Escape && busy){e.Handled=true;CancelCurrentOperation();}}
}
