using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class RangeExclusionDialog:Window
{
    private readonly string connection,schema,table;
    private readonly uint expectedOid;
    private ExpressionIndexTarget? target;
    private bool ready,busy,stale=true;
    private CancellationTokenSource? cancellation;
    public bool WasApplied{get;private set;}public bool RequiresParentRefresh{get;private set;}public string? ScriptSql{get;private set;}
    public RangeExclusionDialog(string connection,string schema,string table,uint expectedOid)
    {
        this.connection=connection;this.schema=schema;this.table=table;this.expectedOid=expectedOid;InitializeComponent();var c=new NpgsqlConnectionStringBuilder(connection);
        Target.Text=$"Range Exclusion Constraint\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}\n{ObjectSql.Relation(schema,table)}";Operator.ItemsSource=Enum.GetValues<RangeExclusionOperator>();Operator.SelectedIndex=0;ready=true;Update();Loaded+=(_,_)=>InspectButton.Focus();
    }
    private RangeExclusionEdit Request=>target is null || stale?throw new ArgumentException("Inspect the selected table and choose built-in range columns."):new(target,ConstraintName.Text,Columns.SelectedItems.Cast<string>().Select(c=>new RangeExclusionKey(c,(RangeExclusionOperator)Operator.SelectedItem)),Deferrable.IsChecked==true,InitiallyDeferred.IsChecked==true);
    private void Changed(object sender,RoutedEventArgs e){if(ready)Update();}
    private void Update(bool keep=false)
    {
        try{Preview.Text=Request.ToSql();ApplyButton.IsEnabled=ScriptButton.IsEnabled=!busy;if(!keep)Feedback.Text="Review the SQL. Standard ALTER TABLE can block other sessions. Exported scripts resolve names when executed.";}
        catch(ArgumentException ex){Preview.Clear();ApplyButton.IsEnabled=ScriptButton.IsEnabled=false;if(!keep)Feedback.Text=ex.Message;}
    }
    private void SetBusy(bool value){busy=value;Fields.IsEnabled=InspectButton.IsEnabled=!value;CancelButton.Visibility=value?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=value;Update(true);}
    private static bool Handled(Exception e)=>e is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException;
    private static string Error(Exception e)=>e is PostgresException p?$"{p.SqlState}: {p.MessageText}":e.Message;
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;stale=true;SetBusy(true);Feedback.Text="Inspecting range columns…";
        try{var observed=await new ExpressionIndexCatalog(connection).InspectAsync(schema,table,source.Token);if(observed.Oid!=expectedOid)throw new InvalidOperationException("Selected table was replaced. Refresh Object Explorer.");target=observed;stale=false;Columns.ItemsSource=observed.Columns.Where(c=>c.TypeOid is 3904 or 3906 or 3908 or 3910 or 3912 or 3926).Select(c=>c.Name).ToArray();Feedback.Text="Inspection complete. Select columns and review the conflict operator.";}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex);}
        finally{cancellation=null;SetBusy(false);}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;var request=Request;using var source=new CancellationTokenSource();cancellation=source;SetBusy(true);RequiresParentRefresh=true;Feedback.Text="Creating exclusion constraint…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Constraint created. Inspect again before another operation.";}
        catch(Exception ex)when(Handled(ex)){Feedback.Text=Error(ex)+" Result not confirmed; inspect before retrying. Cancel cannot undo a completed commit.";}
        finally{stale=true;cancellation=null;SetBusy(false);InspectButton.Focus();}
    }
    private void Script(object sender,RoutedEventArgs e){if(!busy && ScriptButton.IsEnabled){ScriptSql=Request.ToSql();DialogResult=true;}}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;CancelButton.IsEnabled=false;Feedback.Text="Canceling; waiting for cleanup…";cancellation.Cancel();}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
}
