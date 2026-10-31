using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class ConcurrentIndexDialog : Window
{
    private sealed record MaintenanceChoice(IndexMaintenanceOperation Value,string Label);
    private readonly ExpressionIndexEdit? proposal;
    private readonly ConcurrentIndexStore store;
    private readonly ConcurrentIndexService service;
    private readonly string connection;
    private readonly MaintenanceIndexSnapshot? maintenance;
    private IDisposable? lease;
    private ConcurrentIndexReceipt? pending;
    private CancellationTokenSource? cancellation;
    private bool busy, initialized, unavailable, attempted, inspectedInactive;
    public bool WasApplied { get; private set; }
    public bool RequiresParentRefresh { get; private set; }
    public ConcurrentIndexDialog(string connection, ExpressionIndexEdit? proposal = null, MaintenanceIndexSnapshot? maintenance = null)
    {
        this.connection=connection;this.maintenance=maintenance;this.proposal=proposal;store=new(connection);service=new(connection,store);InitializeComponent();
        var profile=new NpgsqlConnectionStringBuilder(connection);
        Target.Text=$"Concurrent Index\nServer: {profile.Host}:{profile.Port}   Database: {profile.Database}   User: {profile.Username}";
        if(proposal is not null)Target.Text+="\n"+ObjectSql.Relation(proposal.Target.Schema,proposal.Target.Name);
        if(proposal is not null)
        {
        string prefix=proposal.Unique?"CREATE UNIQUE INDEX ":"CREATE INDEX ";
        Preview.Text=prefix+"CONCURRENTLY "+proposal.ToSql()[prefix.Length..];
        }
        if(maintenance is not null)
        {
            Title="Index Maintenance";Target.Text=Target.Text.Replace("Concurrent Index","Index Maintenance");Target.Text+="\n"+ObjectSql.Relation(maintenance.Table.Schema,maintenance.Name);
            Intro.Text="Rebuild or remove the inspected index. Standard REINDEX blocks writes and may block reads. Concurrent operations run outside a transaction and can leave residues. Review the operation and SQL.";
            Operation.Visibility=Visibility.Visible;Operation.ItemsSource=new[]{new MaintenanceChoice(IndexMaintenanceOperation.Reindex,"Rebuild (blocks writes)"),new MaintenanceChoice(IndexMaintenanceOperation.ReindexConcurrently,"Rebuild concurrently"),new MaintenanceChoice(IndexMaintenanceOperation.DropConcurrently,"Drop concurrently")};Operation.SelectedValue=IndexMaintenanceOperation.Reindex;
            ApplyButton.Content="_Apply maintenance";Preview.Text=new ReindexEdit(maintenance).ToSql();
        }
        try
        {
            lease=store.AcquireLease();pending=store.Read();
            if(pending is not null){Preview.Text=pending.Sql;Target.Text+=$"\nPending: {ObjectSql.Relation(pending.Schema,pending.Table)} / {pending.Index}";}
            Feedback.Text=pending is null?(proposal is null?"No pending operation. Prepare an index proposal in Expression Index to create concurrently.":"Review the standalone command before creating the index."):"A previous operation is pending. Inspect its recorded target; the stored SQL will not be executed.";
        }
        catch(Exception ex)when(Handled(ex)){unavailable=true;Feedback.Text=ex.Message;}
        initialized=true;Update();Closed+=(_,_)=>lease?.Dispose();
    }
    private static bool Handled(Exception e)=>e is NpgsqlException or IOException or CryptographicException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or ConcurrentIndexUncertainException or TimeoutException;
    private void Changed(object sender,RoutedEventArgs e){if(initialized)Update();}
    private void OperationChanged(object sender,RoutedEventArgs e)
    {
        if(!initialized || maintenance is null || pending is not null)return;
        Review.IsChecked=false;
        try{Preview.Text=MaintenanceSql();Feedback.Text="Review the selected operation. DROP requires confirmation.";}
        catch(ArgumentException ex){Preview.Clear();Feedback.Text=ex.Message;}
        Update();
    }
    private string MaintenanceSql()=>Operation.SelectedValue is IndexMaintenanceOperation.Reindex?new ReindexEdit(maintenance!).ToSql():ConcurrentIndexMaintenanceService.ToSql(maintenance!,(IndexMaintenanceOperation)Operation.SelectedValue);
    private void Update()
    {
        Review.IsEnabled=!busy && !unavailable && pending is null && (proposal is not null || maintenance is not null) && !attempted && Preview.Text.Length>0;
        ApplyButton.IsEnabled=Review.IsEnabled && Review.IsChecked==true;
        Operation.IsEnabled=!busy && pending is null && !attempted;
        InspectButton.IsEnabled=!busy && !unavailable && pending is not null;
        ArchiveButton.Visibility=pending is null?Visibility.Collapsed:Visibility.Visible;
        ArchiveButton.IsEnabled=!busy && !unavailable && inspectedInactive;
        CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=busy;
    }
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    public async Task ApplyAsync()
    {
        if(!ApplyButton.IsEnabled || (proposal is null && maintenance is null))return;
        var operation=maintenance is null?IndexMaintenanceOperation.Reindex:(IndexMaintenanceOperation)Operation.SelectedValue;
        if(maintenance is not null && operation==IndexMaintenanceOperation.DropConcurrently &&
            MessageBox.Show(this,"Drop the inspected index? Table rows are preserved. This standalone command cannot be rolled back.","Drop Index",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        using var source=new CancellationTokenSource();cancellation=source;busy=true;attempted=true;RequiresParentRefresh=true;Update();
        Feedback.Text=maintenance is not null && operation==IndexMaintenanceOperation.Reindex?"Rebuilding inside a transaction; waiting for the server…":"Executing outside a transaction. Cancel may leave residues; waiting for the server…";
        try
        {
            if(maintenance is not null && operation==IndexMaintenanceOperation.Reindex)
                await new AdministrativeEditService(connection).ApplyAsync(new ReindexEdit(maintenance),source.Token);
            else
            {
                var result=maintenance is null?await service.CreateAsync(proposal!,true,source.Token):await new ConcurrentIndexMaintenanceService(connection,store).ApplyAsync(maintenance,operation,true,source.Token);
                WasApplied=true;store.Remove(result.Receipt);
            }
            WasApplied=true;Feedback.Text="Server acknowledged the operation and the expected result was verified. Close and inspect before another change.";
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=ex.Message;}
        finally
        {
            try{pending=store.Read();}catch(Exception ex)when(Handled(ex)){unavailable=true;Feedback.Text=ex.Message;}
            busy=false;cancellation=null;Update();
        }
    }
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectPendingAsync();
    public async Task InspectPendingAsync()
    {
        if(!InspectButton.IsEnabled || pending is null)return;
        using var source=new CancellationTokenSource();cancellation=source;busy=true;Update();
        inspectedInactive=false;
        try
        {
            var observed=await service.InspectReceiptAsync(pending,source.Token);
            bool? active=await service.IsWorkerActiveAsync(pending,source.Token);inspectedInactive=active==false;
            Feedback.Text=$"Observed: {observed.Kind}. Index OID: {observed.IndexOid}. Ready: {observed.Ready}; live: {observed.Live}. This observation does not prove which operation created the object. Pending receipt retained; no retry or cleanup was executed.";
            Feedback.Text+="\nRecorded worker: "+(active==true?"still connected; archive disabled.":active==false?"disconnected; receipt may be archived after review.":"unknown in this older receipt; archive disabled.");
            if(observed.Definition is not null)Feedback.Text+="\n"+observed.Definition;
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=ex.Message+" Pending receipt retained.";}
        finally{busy=false;cancellation=null;Update();}
    }
    private async void Archive(object sender,RoutedEventArgs e)=>await ArchiveAsync();
    public async Task ArchiveAsync()
    {
        if(!ArchiveButton.IsEnabled)return;
        bool confirmed=MessageBox.Show(this,"Archive this inspected receipt? This only ends local tracking. It does not retry the command or change the database; any invalid index remains for explicit maintenance.","Archive Index Receipt",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
        await ArchiveReviewedAsync(confirmed);
    }
    internal async Task ArchiveReviewedAsync(bool confirmed)
    {
        if(!confirmed || !ArchiveButton.IsEnabled || pending is null)return;
        busy=true;Update();
        try
        {
            if(await service.IsWorkerActiveAsync(pending)!=false)throw new InvalidOperationException("The recorded worker is active or unknown. Receipt retained.");
            store.Archive(pending);pending=null;attempted=true;Feedback.Text="Receipt archived locally. No database operation was executed. Close and inspect before preparing maintenance.";
        }
        catch(Exception ex)when(Handled(ex)){Feedback.Text=ex.Message;}
        finally{busy=false;Update();}
    }
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    public void CancelCurrentOperation(){cancellation?.Cancel();CancelButton.IsEnabled=false;}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
}
