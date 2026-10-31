using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class CreateTypeDialog : Window
{
    private readonly string connection;
    private bool ready,busy;
    private SelectedColumnType? baseType;
    private CancellationTokenSource? cancellation;
    public TypeCreationSchema? Snapshot {get;private set;}
    public bool SnapshotIsStale {get;private set;}=true;
    public bool WasApplied {get;private set;}
    public bool RequiresParentRefresh {get;private set;}
    public string? ScriptSql {get;private set;}
    public CreateTypeDialog(string connection,string? schema=null)
    {
        this.connection=connection;InitializeComponent();Kind.ItemsSource=new[]{"Enum","Domain"};Kind.SelectedIndex=0;SchemaName.Text=schema??"public";
        ready=true;Update();Loaded+=(_,_)=>SchemaName.Focus();
    }
    private IAdministrativeEdit Request
    {
        get
        {
            if(Snapshot is null || SnapshotIsStale || Snapshot.Name!=SchemaName.Text)throw new ArgumentException("Inspect the destination schema first.");
            if(Kind.SelectedIndex==1)return new CreateDomainEdit(Snapshot,TypeName.Text,baseType??throw new ArgumentException("Choose an inspected base type."),UseDefault.IsChecked==true?DefaultValue.Text:null,NotNull.IsChecked==true);
            var labels=Labels.Text.Replace("\r\n","\n",StringComparison.Ordinal).Split('\n');
            if(AllowEmptyLabels.IsChecked!=true && labels.Any(x=>x.Length==0))throw new ArgumentException("Remove blank lines or explicitly allow empty labels.");
            return new CreateEnumEdit(Snapshot,TypeName.Text,labels);
        }
    }
    private void Changed(object sender,RoutedEventArgs e)
    {if(ready){if(ReferenceEquals(sender,SchemaName))SnapshotIsStale=true;if(!ReferenceEquals(sender,Reviewed))Reviewed.IsChecked=false;Update();}}
    private void Update(bool preserve=false)
    {
        Fields.IsEnabled=InspectButton.IsEnabled=CloseButton.IsEnabled=!busy;CancelButton.IsEnabled=busy && cancellation?.IsCancellationRequested==false;
        bool domain=Kind.SelectedIndex==1;EnumFields.Visibility=domain?Visibility.Collapsed:Visibility.Visible;DomainFields.Visibility=domain?Visibility.Visible:Visibility.Collapsed;
        DefaultValue.IsEnabled=UseDefault.IsChecked==true;BaseTypeText.Text=baseType?.ToSql()??"No base selected.";
        Observation.Text=Snapshot is null?"No current schema observation.":$"{(SnapshotIsStale?"Outdated observation. Inspect again.\n":"")}Schema: {Snapshot.Name}   OID: {Snapshot.Oid}   Creator OID: {Snapshot.CreatorOid}";
        try{Preview.Text=Request.ToSql();ScriptButton.IsEnabled=!busy;ApplyButton.IsEnabled=!busy && Reviewed.IsChecked==true;if(!preserve)Feedback.Text="Review the definition. Script omits inspection guards.";}
        catch(ArgumentException ex){if(!SnapshotIsStale)Preview.Clear();ScriptButton.IsEnabled=ApplyButton.IsEnabled=false;if(!preserve)Feedback.Text=ex.Message;}
    }
    internal void SetBaseType(SelectedColumnType type){if(busy)return;baseType=type;Reviewed.IsChecked=false;Update();}
    private void ChooseBase(object sender,RoutedEventArgs e){if(busy)return;var picker=new ColumnTypePicker(connection){Owner=this};if(picker.ShowDialog()==true && picker.SelectedType is {} type)SetBaseType(type);}
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    public async Task InspectAsync()
    {
        if(busy)return;using var source=new CancellationTokenSource();cancellation=source;busy=true;Update(true);CancelButton.Focus();Feedback.Text="Inspecting schema…";
        try{var snapshot=await new TypeCreationCatalog(connection).InspectSchemaAsync(SchemaName.Text,source.Token);source.Token.ThrowIfCancellationRequested();Snapshot=snapshot;SnapshotIsStale=false;ready=false;Reviewed.IsChecked=false;ready=true;Feedback.Text="Schema inspected. Review the new type.";}
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){SnapshotIsStale=true;Feedback.Text=source.IsCancellationRequested?"Inspection canceled. Inspect again.":ex.Message;}
        finally{cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled)return;var request=Request;
        if(MessageBox.Show(this,request.ToSql()+"\n\nCreate this type in the inspected schema?","Confirm Type Creation",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes){Feedback.Text="Operation canceled. Nothing was sent.";return;}
        using var source=new CancellationTokenSource();cancellation=source;busy=true;RequiresParentRefresh=true;Update(true);CancelButton.Focus();Feedback.Text="Creating type…";
        try{await new AdministrativeEditService(connection).ApplyAsync(request,source.Token);WasApplied=true;Feedback.Text="Command completed. Inspect before another change.";}
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException){Feedback.Text=(source.IsCancellationRequested?"Type creation interrupted.":ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message)+" Result not confirmed. Inspect before retrying; cancellation cannot undo a completed commit.";}
        finally{SnapshotIsStale=true;ready=false;Reviewed.IsChecked=false;ready=true;cancellation=null;busy=false;Update(true);InspectButton.Focus();}
    }
    private void Script(object sender,RoutedEventArgs e){if(busy || !ScriptButton.IsEnabled)return;ScriptSql=Request.ToSql();DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){if(cancellation is null)return;cancellation.Cancel();CancelButton.IsEnabled=false;Feedback.Text="Canceling… Waiting for cleanup.";}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private void OnPreviewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.F5){e.Handled=true;_=InspectAsync();}else if(e.Key==Key.Escape && busy){e.Handled=true;CancelCurrentOperation();}}
}
