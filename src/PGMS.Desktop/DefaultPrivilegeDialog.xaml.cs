using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class DefaultPrivilegeDialog : Window
{
    private readonly string connection;
    private bool ready,busy,restoring,cancelRequested;
    private CancellationTokenSource? operation;
    private DefaultPrivilegeSnapshot? snapshot;
    private DefaultPrivilegeIdentity? boundIdentity;
    private PrivilegeRecipientIdentity? recipient;
    public bool HasUnconfirmedCommit { get; private set; }
    public string? ScriptSql { get; private set; }
    internal DefaultPrivilegeSnapshot? Snapshot => snapshot;
    /// <summary>With a schema (the one selected in Object Explorer), the scope starts on it instead of global; a restored transaction still wins.</summary>
    public DefaultPrivilegeDialog(string connection, string? schema = null)
    {
        this.connection=connection;InitializeComponent();
        Creator.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,new TextChangedEventHandler(ScopeChanged));
        var settings=new NpgsqlConnectionStringBuilder(connection);
        Target.Text=$"{settings.Host}:{settings.Port} / {settings.Database} \u2014 {settings.Username}";
        Creator.Text=settings.Username;ObjectClass.ItemsSource=Enum.GetValues<DefaultPrivilegeTarget>();ObjectClass.SelectedItem=DefaultPrivilegeTarget.Tables;
        Privilege.ItemsSource=DefaultPrivilegeRequest.Allowed(DefaultPrivilegeTarget.Tables);Privilege.SelectedIndex=0;
        if(schema is not null){GlobalScope.IsChecked=false;SchemaName.Text=schema;}
        ready=true;RestorePending();Refresh();
    }
    private DefaultPrivilegeRequest Request => new((DefaultPrivilegeTarget)ObjectClass.SelectedItem,Creator.Text,RoleName.Text,(string?)Privilege.SelectedItem??"",GlobalScope.IsChecked==true?null:SchemaName.Text,Revoke.IsChecked==true,PublicRecipient.IsChecked==true,GrantOption.IsChecked==true);
    private void ClassChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || restoring)return;
        Privilege.ItemsSource=DefaultPrivilegeRequest.Allowed((DefaultPrivilegeTarget)ObjectClass.SelectedItem);Privilege.SelectedIndex=0;
        if(ObjectClass.SelectedItem is DefaultPrivilegeTarget.Schemas)GlobalScope.IsChecked=true;
        ScopeChanged(sender,e);
    }
    private void ScopeChanged(object sender,RoutedEventArgs e){if(!ready || restoring)return;boundIdentity=null;Changed(sender,e);}
    private void Changed(object sender,RoutedEventArgs e)
    {
        if(!ready || restoring)return;if(PublicRecipient.IsChecked==true && GrantOption.IsChecked==true)GrantOption.IsChecked=false;snapshot=null;recipient=null;PolicyRows.ItemsSource=null;Details.Text="Inspect to load policy origins.";Feedback.Text="Inspect the proposal before applying or scripting.";Refresh();
    }
    private void Refresh()
    {
        if(!ready)return;
        bool valid=true;try{Preview.Text=Request.ToSql();}catch(ArgumentException ex){Preview.Text=ex.Message;valid=false;}
        Fields.IsEnabled=!busy && !HasUnconfirmedCommit;
        GlobalScope.IsEnabled=ObjectClass.SelectedItem is not DefaultPrivilegeTarget.Schemas;
        SchemaName.IsEnabled=GlobalScope.IsChecked!=true;RoleName.IsEnabled=PublicRecipient.IsChecked!=true;
        GrantOption.IsEnabled=PublicRecipient.IsChecked!=true;GrantOption.Content=Revoke.IsChecked==true?"Revoke grant _option only":"With grant _option";
        InspectButton.Content=HasUnconfirmedCommit?"Check _transaction":"_Inspect";
        InspectButton.IsEnabled=!busy && (HasUnconfirmedCommit || valid);
        ScriptButton.IsEnabled=!busy && !HasUnconfirmedCommit && valid && snapshot is not null && recipient is not null;
        ApplyButton.IsEnabled=ScriptButton.IsEnabled && snapshot?.CreatorPrivilegesAvailable==true;
        CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;CancelButton.IsEnabled=busy && !cancelRequested;
    }
    private void RestorePending()
    {
        try
        {
            var pending=new DefaultPrivilegeCommitStore(connection).Read();HasUnconfirmedCommit=pending is not null;
            if(pending is null)return;
            restoring=true;var r=pending.Request;Creator.Text=r.Creator;ObjectClass.SelectedItem=r.Target;
            Privilege.ItemsSource=DefaultPrivilegeRequest.Allowed(r.Target);Privilege.SelectedItem=r.Privilege;
            GlobalScope.IsChecked=r.Schema is null;SchemaName.Text=r.Schema??"public";RoleName.Text=r.Role;PublicRecipient.IsChecked=r.RecipientIsPublic;Revoke.IsChecked=r.Revoke;GrantOption.IsChecked=r.GrantOption;
            boundIdentity=pending.Identity;snapshot=null;recipient=null;
            Feedback.Text="An unconfirmed transaction was restored. Check transaction; its command will not be repeated.";
        }
        catch(Exception ex)when(ex is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException)
        {HasUnconfirmedCommit=true;Feedback.Text="Pending record could not be read and was preserved. "+ex.Message;}
        finally{restoring=false;}
    }
    internal async Task LoadCreatorsAsync()
    {
        if(busy || HasUnconfirmedCommit)return;
        string proposal=Creator.Text;busy=true;cancelRequested=false;operation=new();snapshot=null;recipient=null;Refresh();
        try
        {
            var choices=await new DefaultPrivilegeCreators(connection).ListAsync(operation.Token);
            restoring=true;Creator.ItemsSource=choices;Creator.SelectedIndex=-1;Creator.Text=proposal;restoring=false;
            Feedback.Text="Roles loaded. Choose a creator or enter its exact name, then inspect the policy.";
        }
        catch(OperationCanceledException){Feedback.Text="Loading creators canceled. The proposal was preserved.";}
        catch(Exception ex)when(ex is NpgsqlException or IOException or ArgumentException){Feedback.Text=ex.Message;}
        finally{restoring=false;operation.Dispose();operation=null;busy=false;Refresh();}
    }
    private async void LoadCreators(object sender,RoutedEventArgs e)=>await LoadCreatorsAsync();
    internal async Task InspectAsync()
    {
        if(busy || !InspectButton.IsEnabled)return;
        busy=true;cancelRequested=false;operation=new();Refresh();
        try
        {
            if(HasUnconfirmedCommit)
            {
                var store=new DefaultPrivilegeCommitStore(connection);using var lease=store.AcquireLease();var pending=store.Read();
                if(pending is null){HasUnconfirmedCommit=false;Feedback.Text="Pending record was cleared. Inspect before applying.";return;}
                var service=new DefaultPrivilegeService(connection);var receipt=service.ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
                var status=await service.ReconcileCommitAsync(receipt,operation.Token);
                if(status is PrivilegeCommitStatus.Committed or PrivilegeCommitStatus.Aborted)
                {store.Remove(receipt);HasUnconfirmedCommit=false;Feedback.Text=$"Transaction {status.ToString().ToLowerInvariant()}. Inspect before another operation.";}
                else Feedback.Text="Transaction outcome remains unknown. Check again; no SQL was repeated.";
                return;
            }
            var r=Request;var observed=await new DefaultPrivilegeCatalog(connection).InspectAsync(r.Target,r.Creator,r.Schema,operation.Token);
            if(Creator.SelectedItem is DefaultPrivilegeCreator choice && choice.Name==r.Creator && (choice.Oid!=observed.Identity.CreatorOid || choice.DatabaseOid!=observed.Identity.DatabaseOid))
                throw new InvalidOperationException("Selected creator was replaced. Load roles and choose the creator again.");
            if(boundIdentity is not null && observed.Identity!=boundIdentity)throw new InvalidOperationException("Creator or schema was replaced. Change the selection explicitly before inspecting again.");
            var selected=await new PrivilegeService(connection).InspectRecipientAsync(r.Role,r.RecipientIsPublic,operation.Token);
            if(selected is null)throw new InvalidOperationException("Recipient role does not exist.");
            boundIdentity=observed.Identity;snapshot=observed;recipient=selected;PolicyRows.ItemsSource=observed.Entries;
            Details.Text=$"Database OID {observed.Identity.DatabaseOid}; creator OID {observed.Identity.CreatorOid}; schema OID {observed.Identity.NamespaceOid}. Policies apply to future objects only.";
            Feedback.Text=observed.CreatorPrivilegesAvailable?"Policy inspected. Review the SQL before Script or Apply.":"Policy inspected. This connection cannot manage defaults for that creator. Script is available; connect as an authorized role.";
        }
        catch(OperationCanceledException){snapshot=null;Feedback.Text="Inspection canceled. Inspect again when ready.";}
        catch(Exception ex)when(ex is NpgsqlException or IOException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {snapshot=null;Feedback.Text=(HasUnconfirmedCommit?"Transaction remains unconfirmed; pending record preserved. ":"")+ex.Message;}
        finally{operation.Dispose();operation=null;busy=false;Refresh();}
    }
    internal async Task ApplyAsync()
    {
        if(busy || !ApplyButton.IsEnabled || snapshot is null || recipient is null)return;
        var r=Request;var captured=snapshot;var selected=recipient;busy=true;cancelRequested=false;operation=new();Refresh();
        try
        {
            var store=new DefaultPrivilegeCommitStore(connection,r,captured.Identity);using var lease=store.AcquireLease();
            if(store.Read() is not null){RestorePending();return;}
            var service=new DefaultPrivilegeService(connection,store);
            bool applied=await service.ApplyInspectedAsync(r,captured,selected,operation.Token);
            if(store.Recorded is { } recorded)store.Remove(service.ResumeCommitCheck(recorded.TransactionId,recorded.DatabaseOid));
            Feedback.Text=applied?"Default permission committed. Inspect before another operation.":"No policy change was committed. Inspect before another operation.";
        }
        catch(OperationCanceledException){Feedback.Text="Apply canceled. Inspect before another operation.";RestoreIfPresent();}
        catch(Exception ex)when(ex is NpgsqlException or IOException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {Feedback.Text=ex.Message;RestoreIfPresent();}
        finally{snapshot=null;recipient=null;operation.Dispose();operation=null;busy=false;Refresh();}
    }
    private void RestoreIfPresent(){var previous=Feedback.Text;RestorePending();if(!HasUnconfirmedCommit)Feedback.Text=previous;}
    private async void Inspect(object sender,RoutedEventArgs e)=>await InspectAsync();
    private async void Apply(object sender,RoutedEventArgs e)=>await ApplyAsync();
    internal void CancelCurrentOperation()
    {
        if(!busy || cancelRequested)return;cancelRequested=true;operation?.Cancel();
        Feedback.Text="Cancel requested. Wait for the operation to finish; then inspect or check its transaction.";Refresh();
    }
    private void Cancel(object sender,RoutedEventArgs e)=>CancelCurrentOperation();
    private void Script(object sender,RoutedEventArgs e){if(!ScriptButton.IsEnabled)return;ScriptSql=Request.ToSql();DialogResult=true;}
    private void RowChanged(object sender,SelectionChangedEventArgs e){if(PolicyRows.SelectedItem is DefaultPrivilegeAclEntry row)Details.Text=$"{row.Origin}: {row.Privilege}; recipient {row.Recipient} (OID {row.RecipientOid}); grantor {row.Grantor} (OID {row.GrantorOid}); grant option {row.Grantable}.";}
    private void CloseClick(object sender,RoutedEventArgs e)=>Close();
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;CancelCurrentOperation();}}
    private async void OnKey(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;if(busy)CancelCurrentOperation();else Close();}else if(e.Key==Key.F5){e.Handled=true;await InspectAsync();}}
}
