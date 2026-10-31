using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class ExpressionIndexDialog:Window
{
    private sealed record OpclassChoice(IndexOperatorClass? Value,string Label);
    private readonly string connection,schema,table;
    private readonly uint expectedOid;
    private readonly ObservableCollection<ExpressionIndexKey> keys=[];
    private IReadOnlyList<IndexOperatorClass> opclasses=[];
    private ExpressionIndexTarget? target;
    private bool busy,ready,stale=true;
    private CancellationTokenSource? cancellation;
    public bool WasApplied{get;private set;}
    public bool RequiresParentRefresh{get;private set;}
    public string? ScriptSql{get;private set;}
    public ExpressionIndexDialog(string connection,string schema,string table,uint expectedOid)
    {
        this.connection=connection;this.schema=schema;this.table=table;this.expectedOid=expectedOid;InitializeComponent();
        var c=new NpgsqlConnectionStringBuilder(connection);Target.Text=$"Expression Index\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}\n{ObjectSql.Relation(schema,table)}";
        KeysGrid.ItemsSource=keys;Transform.ItemsSource=PredicateTransform.ItemsSource=Enum.GetValues<IndexTransform>();Transform.SelectedIndex=PredicateTransform.SelectedIndex=0;
        Sort.ItemsSource=Enum.GetValues<IndexSortOrder>();NullOrder.ItemsSource=Enum.GetValues<IndexNullPlacement>();Comparison.ItemsSource=Enum.GetValues<IndexComparison>();Sort.SelectedIndex=NullOrder.SelectedIndex=0;Comparison.SelectedItem=IndexComparison.IsNotNull;
        Opclass.ItemsSource=new[]{new OpclassChoice(null,"Server default")};Opclass.SelectedIndex=0;ready=true;Update();Loaded+=(_,_)=>InspectButton.Focus();
    }
    private ExpressionIndexEdit Request
    {
        get
        {
            if(target is null || stale)throw new ArgumentException("Inspect the selected table before preparing an index.");
            IndexPredicate? predicate=null;
            if(UsePredicate.IsChecked==true)
            {
                var comparison=(IndexComparison)Comparison.SelectedItem;
                predicate=new(new(PredicateColumn.SelectedItem as string??throw new ArgumentException("Choose a predicate column."),(IndexTransform)PredicateTransform.SelectedItem),comparison,comparison is IndexComparison.IsNull or IndexComparison.IsNotNull?"":PredicateValue.Text);
            }
            return new(target,IndexName.Text,keys,Unique.IsChecked==true,NullsEqual.IsChecked==true,RelationEdit.ParseColumns(Included.Text),predicate);
        }
    }
    private void Changed(object sender,RoutedEventArgs e){if(ready)Update();}
    private void KeyChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return;ready=false;
        var choices=new List<OpclassChoice>{new(null,"Server default")};
        if(target is not null && Column.SelectedItem is string column)
        {
            try{uint type=new ExpressionIndexEdit(target,"preview",[]).ResultType(new(column,(IndexTransform)Transform.SelectedItem));choices.AddRange(opclasses.Where(o=>o.InputTypeOid==type).Select(o=>new OpclassChoice(o,o.Schema+"."+o.Name)));}
            catch(ArgumentException){}
        }
        Opclass.ItemsSource=choices;Opclass.SelectedIndex=0;ready=true;Update();
    }
    private void AddKey(object sender,RoutedEventArgs e)
    {
        if(busy || stale || target is null)return;
        try
        {
            var key=new ExpressionIndexKey(new(Column.SelectedItem as string??throw new ArgumentException("Choose a column."),(IndexTransform)Transform.SelectedItem),(IndexSortOrder)Sort.SelectedItem,(IndexNullPlacement)NullOrder.SelectedItem){OperatorClass=(Opclass.SelectedItem as OpclassChoice)?.Value};
            _=new ExpressionIndexEdit(target,"validate",keys.Append(key)).ToSql();keys.Add(key);Update();
        }
        catch(ArgumentException ex){Feedback.Text=ex.Message;}
    }
    private void RemoveKey(object sender,RoutedEventArgs e){if(!busy && KeysGrid.SelectedItem is ExpressionIndexKey key){keys.Remove(key);Update();}}
    private void MoveKey(object sender,RoutedEventArgs e){if(!busy && KeysGrid.SelectedIndex>0){int index=KeysGrid.SelectedIndex;keys.Move(index,index-1);KeysGrid.SelectedIndex=index-1;Update();}}
    private void Update(bool keep=false)
    {
        PredicateFields.Visibility=UsePredicate.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        PredicateValue.IsEnabled=Comparison.SelectedItem is not(IndexComparison.IsNull or IndexComparison.IsNotNull);
        AddKeyButton.IsEnabled=!busy && !stale;
        ConcurrentButton.IsEnabled=MaintainButton.IsEnabled=!busy;
        try{Preview.Text=Request.ToSql();ApplyButton.IsEnabled=ScriptButton.IsEnabled=!busy;if(!keep)Feedback.Text="Review the inspected target and generated SQL. No expression is executed during inspection.";}
        catch(ArgumentException ex){Preview.Clear();ApplyButton.IsEnabled=ScriptButton.IsEnabled=false;if(!keep)Feedback.Text=ex.Message;}
    }
    private void SetBusy(bool value){busy=value;Fields.IsEnabled=InspectButton.IsEnabled=!value;CancelButton.Visibility=value?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=value;Update(true);}
    private static bool Handled(Exception ex)=>ex is NpgsqlException or System.IO.IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException;
    private static string Error(Exception ex)=>ex is PostgresException p?$"{p.SqlState}: {p.MessageText}":ex.Message;
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;stale=true;SetBusy(true);Feedback.Text="Inspecting table and built-in operator classes…";
        try
        {
            var catalog=new ExpressionIndexCatalog(connection);var observed=await catalog.InspectAsync(schema,table,source.Token);
            if(observed.Oid!=expectedOid)throw new InvalidOperationException("Selected table was replaced. Refresh Object Explorer.");
            var classes=await catalog.InspectOperatorClassesAsync(source.Token);target=observed;opclasses=classes;stale=false;
            ready=false;Column.ItemsSource=PredicateColumn.ItemsSource=observed.Columns.Select(c=>c.Name).ToArray();Column.SelectedIndex=PredicateColumn.SelectedIndex=0;ready=true;KeyChanged(this,new());Feedback.Text="Inspection complete. Keys and proposals must still be reviewed.";
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex);}
        finally{cancellation=null;SetBusy(false);}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;var request=Request;using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);RequiresParentRefresh=true;Feedback.Text="Creating index…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Index created. Inspect again before another change.";}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex)+" Result not confirmed; inspect before retrying. Cancel cannot undo a completed commit.";}
        finally{stale=true;cancellation=null;SetBusy(false);InspectButton.Focus();}
    }
    private async void Maintain(object sender,RoutedEventArgs e)
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);
        MaintenanceIndexSnapshot? inspected=null;
        try
        {
            inspected=await new IndexMaintenanceCatalog(connection).InspectAsync(schema,table,IndexName.Text,source.Token);
            if(inspected.Table.Oid!=expectedOid)throw new InvalidOperationException("Selected table was replaced. Refresh Object Explorer.");
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex);inspected=null;}
        finally{cancellation=null;SetBusy(false);}
        if(inspected is null)return;
        var dialog=new ConcurrentIndexDialog(connection,maintenance:inspected){Owner=this};dialog.ShowDialog();
        RequiresParentRefresh|=dialog.RequiresParentRefresh;WasApplied|=dialog.WasApplied;
        if(dialog.RequiresParentRefresh){stale=true;Update();}
    }
    private void Concurrent(object sender,RoutedEventArgs e)
    {
        if(busy || !ConcurrentButton.IsEnabled)return;
        ExpressionIndexEdit? proposal=null;
        try{proposal=Request;_=proposal.ToSql();}catch(ArgumentException){proposal=null;}
        var dialog=new ConcurrentIndexDialog(connection,proposal){Owner=this};dialog.ShowDialog();
        RequiresParentRefresh|=dialog.RequiresParentRefresh;WasApplied|=dialog.WasApplied;
        if(dialog.RequiresParentRefresh){stale=true;Update();}
    }
    private void Script(object sender,RoutedEventArgs e){if(!busy && ScriptButton.IsEnabled){ScriptSql=Request.ToSql();DialogResult=true;}}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;CancelButton.IsEnabled=false;Feedback.Text="Canceling; waiting for cleanup…";cancellation.Cancel();}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
}
