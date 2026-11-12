using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class PrivilegeDialog : Window
{
    private PrivilegeGrantorSnapshot? grantorSnapshot;
    private PrivilegeRequest? grantorContext;
    private PrivilegeGrantorIdentity? selectedGrantor;
    private PrivilegeRevokePreview? revokePreview;
    private bool configuringDelegation, explicitGrantorRequested;
    private readonly string connection;
    private readonly PrivilegeCommitStore commitStore;
    private bool ready, busy;
    private uint? selectedRoutineOid;
    private string? selectedRoutineSchema;
    private RoutinePrivilegeSelection? selectedRoutine;
    private bool routineNeedsInspection = true;
    private CancellationTokenSource? inspection, application;
    private PrivilegeService? pendingCommitService;
    private PrivilegeCommitReceipt? pendingCommit;
    private bool unreadableCommit;
    public bool HasUnconfirmedCommit => pendingCommit is not null || unreadableCommit;
    private readonly DialogFocus focus=new();
    public string? ScriptSql { get; private set; }
    public PrivilegeDialog(string connection, CatalogEntry? routineEntry = null, CatalogEntry? selected = null)
    {
        this.connection = connection;
        commitStore=new PrivilegeCommitStore(connection);
        InitializeComponent();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Object Permissions\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        ObjectType.ItemsSource = Enum.GetValues<PrivilegeTarget>().Where(t => t != PrivilegeTarget.Routine).ToArray();
        ready = true; ObjectType.SelectedIndex = 0;
        ObjectName.Text = c.Database;
        if (routineEntry is not null)
        {
            if (!routineEntry.CanManageRoutinePermissions) throw new ArgumentException("Select a routine in Object Explorer.");
            selectedRoutineOid = routineEntry.Oid; selectedRoutineSchema = routineEntry.Schema;
            ObjectType.ItemsSource = new[] { PrivilegeTarget.Routine };
            ObjectType.SelectedItem = PrivilegeTarget.Routine;
            SchemaName.Text = routineEntry.Schema; ObjectName.Text = routineEntry.Name;
            Update();
        }
        else if (selected is not null && Preselection(selected) is { } choice)
        {
            ObjectType.SelectedItem = choice.Target;
            if (choice.Schema is not null) SchemaName.Text = choice.Schema;
            ObjectName.Text = choice.Name;
            Update();
        }
        RestorePendingCommit();
        Loaded += (_,_) => {if(HasUnconfirmedCommit)InspectButton.Focus();else ObjectType.Focus();};
    }
    /// <summary>The target of an Object Explorer selection: its database, a schema, a table, view or materialized view, or a sequence.</summary>
    internal static (PrivilegeTarget Target, string? Schema, string Name)? Preselection(CatalogEntry entry) => entry switch
    {
        { Kind: CatalogKind.Database } => (PrivilegeTarget.Database, null, entry.Name),
        { Kind: CatalogKind.Schema } => (PrivilegeTarget.Schema, null, entry.Name),
        { Kind: CatalogKind.Relation, RelationType: "S" } => (PrivilegeTarget.Sequence, entry.Schema, entry.Name),
        { Kind: CatalogKind.Relation } => (PrivilegeTarget.Table, entry.Schema, entry.Name),
        _ => null
    };

    private void RestorePendingCommit()
    {
        try
        {
            if(commitStore.Read() is not { } saved)return;
            pendingCommitService=new PrivilegeService(connection);
            pendingCommit=pendingCommitService.ResumeCommitCheck(saved.TransactionId,saved.DatabaseOid);
            unreadableCommit=false;
            var r=saved.Request;
            selectedRoutine=r.Routine; selectedRoutineOid=r.Routine?.ObjectOid; selectedRoutineSchema=r.Routine?.Schema;
            routineNeedsInspection=true;
            ObjectType.ItemsSource=r.Target==PrivilegeTarget.Routine ? new[]{PrivilegeTarget.Routine} : Enum.GetValues<PrivilegeTarget>().Where(t=>t!=PrivilegeTarget.Routine).ToArray();
            ObjectType.SelectedItem=r.Target;SchemaName.Text=r.Schema;ObjectName.Text=r.Name;RoleName.Text=r.Role;
            ColumnScope.IsChecked=r.Column is not null;ColumnName.Text=r.Column??"";PublicRecipient.IsChecked=r.RecipientIsPublic;
            Revoke.IsChecked=r.Revoke;GrantOption.IsChecked=r.GrantOption;Privilege.SelectedItem=r.Privilege;
            selectedGrantor=r.Grantor;explicitGrantorRequested=r.Grantor is not null;
            CascadeRevoke.IsChecked=r.Cascade;
            GrantorInfo.Text=r.Grantor is null ? "Session grant origin" : $"Restored grantor: {r.Grantor.Name} (OID {r.Grantor.Oid})";
            Feedback.Text=$"Pending transaction {saved.TransactionId} restored from protected local storage. No command was repeated. Check its result on the same unchanged cluster before applying again.";
        }
        catch(Exception ex)when(ex is System.IO.IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            unreadableCommit=true;
            Feedback.Text="The pending permission record could not be read. It was preserved. Apply and Script remain blocked. Restore access to protected local state and check again.";
        }
        Update(true);
    }
    private PrivilegeRequest Request => new((PrivilegeTarget)ObjectType.SelectedItem, ObjectName.Text, RoleName.Text, Privilege.SelectedItem as string ?? "", Revoke.IsChecked == true, SchemaName.Text, PublicRecipient.IsChecked == true, GrantOption.IsChecked == true, Column:ColumnScopeActive ? ColumnName.Text : null) { Routine = ObjectType.SelectedItem is PrivilegeTarget.Routine ? selectedRoutine : null, Grantor = selectedGrantor, Cascade = CascadeRevoke.IsChecked == true };
    private PrivilegeRequest DelegationContext => Request with { Grantor = null, Cascade = false };
    private async void LoadGrantors(object sender, RoutedEventArgs e) => await LoadGrantorsAsync();
    private async void ReviewRevoke(object sender, RoutedEventArgs e) => await ReviewRevokeAsync();
    private void ClearGrantor(object sender, RoutedEventArgs e)
    {
        explicitGrantorRequested=false;GrantorRoles.SelectedIndex=-1; selectedGrantor=null; CascadeRevoke.IsChecked=false; revokePreview=null; Update();
    }
    private void GrantorChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || configuringDelegation) return;
        selectedGrantor=GrantorRoles.SelectedItem is PrivilegeGrantorCandidate candidate && grantorSnapshot is { } snapshot
            ? new(snapshot.Target.DatabaseOid,candidate.Oid,candidate.Name) : null;
        if(selectedGrantor is not null)explicitGrantorRequested=true;
        revokePreview=null; Update();
    }
    private void CascadeChanged(object sender, RoutedEventArgs e) { if(ready) { revokePreview=null; Update(); } }
    internal async Task LoadGrantorsAsync() => await InspectDelegationAsync(false);
    internal async Task ReviewRevokeAsync() => await InspectDelegationAsync(true);
    private async Task InspectDelegationAsync(bool dependencies)
    {
        if(busy || HasUnconfirmedCommit) return;
        using var cancellation=new CancellationTokenSource(); inspection=cancellation;
        busy=true; Fields.IsEnabled=CloseButton.IsEnabled=false;
        CancelInspectionButton.Visibility=Visibility.Visible; CancelInspectionButton.IsEnabled=true;
        Update(true);
        try
        {
            revokePreview=null;
            if(dependencies)
            {
                revokePreview=await new PrivilegeRevokeInspector(connection).InspectAsync(Request,cancellation.Token);
                if(grantorSnapshot?.Target != revokePreview.Target) {revokePreview=null;throw new InvalidOperationException("Target changed since grantor inspection. Load grantors again.");}
                Feedback.Text="Dependency review loaded. No changes made. Apply requires confirmation.";
            }
            else
            {
                var context=DelegationContext;
                var snapshot=await new PrivilegeGrantorCatalog(connection).InspectAsync(context,cancellation.Token);
                configuringDelegation=true;
                selectedGrantor=null; grantorSnapshot=snapshot; grantorContext=context;
                GrantorRoles.ItemsSource=snapshot.Candidates; GrantorRoles.SelectedIndex=-1;
                configuringDelegation=false;
                Feedback.Text=snapshot.Truncated ? "Grantor list is incomplete. Narrow the selection; no explicit choice is authorized." : "Grantors loaded. Select a role explicitly; no changes made.";
            }
        }
        catch(Exception ex) when(ex is NpgsqlException or InvalidOperationException or ArgumentException or OperationCanceledException or System.IO.IOException or TimeoutException)
        { revokePreview=null; if(!dependencies) {grantorSnapshot=null;grantorContext=null;selectedGrantor=null;} Feedback.Text=ex.Message+" Review again before applying."; }
        finally { configuringDelegation=false; inspection=null;busy=false; Fields.IsEnabled=CloseButton.IsEnabled=true; CancelInspectionButton.Visibility=Visibility.Collapsed;CancelInspectionButton.IsEnabled=false;Update(true); }
    }
    private bool ColumnScopeActive => ObjectType.SelectedItem is PrivilegeTarget.Table && ColumnScope.IsChecked == true;
    private void TypeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || ObjectType.SelectedItem is not PrivilegeTarget) return;
        ConfigureColumnScope();
        SchemaName.IsEnabled = (PrivilegeTarget)ObjectType.SelectedItem is PrivilegeTarget.Table or PrivilegeTarget.Sequence;
        SequencePrivilegeHelp.Visibility = (PrivilegeTarget)ObjectType.SelectedItem == PrivilegeTarget.Sequence ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }
    private void ConfigureColumnScope()
    {
        string? selected=Privilege.SelectedItem as string;
        var choices=ColumnScopeActive ? PrivilegeRequest.AllowedForColumn : PrivilegeRequest.Allowed((PrivilegeTarget)ObjectType.SelectedItem);
        Privilege.ItemsSource=choices;
        Privilege.SelectedItem=selected is not null && choices.Contains(selected) ? selected : choices[0];
        TableColumnScope.Visibility=ObjectType.SelectedItem is PrivilegeTarget.Table ? Visibility.Visible : Visibility.Collapsed;
        ColumnFields.Visibility=ColumnScopeActive ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ColumnScopeChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return;
        ConfigureColumnScope();Update();
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    public bool IsBusy => busy;
    public bool AccessSnapshotIsStale { get; private set; }
    private void Update(bool preserveFeedback = false)
    {
        if(ObjectType.SelectedItem is not PrivilegeTarget)return;
        if(grantorContext is not null && grantorContext != DelegationContext)
        {
            configuringDelegation=true;grantorSnapshot=null;grantorContext=null;selectedGrantor=null;revokePreview=null;
            GrantorRoles.ItemsSource=null;configuringDelegation=false;
        }
        if(revokePreview is not null && revokePreview.Request != Request) revokePreview=null;
        bool canChooseGrantor=grantorSnapshot is {Truncated:false} && GrantorRoles.SelectedItem is PrivilegeGrantorCandidate {CanSetRole:true};
        if(!HasUnconfirmedCommit) GrantorInfo.Text=selectedGrantor is null ? "No explicit grantor selected. PostgreSQL resolves the grant origin."
             : GrantorRoles.SelectedItem is PrivilegeGrantorCandidate authority
                ? $"{selectedGrantor.Name} (OID {selectedGrantor.Oid}) | SET allowed: {authority.CanSetRole} | Role available without SET: {authority.AvailableWithoutSet}\nObject owner: {authority.IsOwner} | Direct grant option: {authority.DirectGrantOption} | Effective grant option: {authority.EffectiveGrantOption} | Recorded ACL grantor: {authority.AppearsAsGrantor}\nThese flags describe this role and privilege, not every membership path. Apply requires SET and revalidates the actual grant origin."
                : $"{selectedGrantor.Name} (OID {selectedGrantor.Oid}). Load grantors and select again before applying.";
        CascadeRevoke.IsEnabled=Revoke.IsChecked==true && selectedGrantor is not null;
        ReviewRevokeButton.IsEnabled=!busy && !HasUnconfirmedCommit && CascadeRevoke.IsChecked==true && canChooseGrantor;
        LoadGrantorsButton.IsEnabled=!busy && !HasUnconfirmedCommit && (ObjectType.SelectedItem is not PrivilegeTarget.Routine || !routineNeedsInspection);
        RevokeImpact.Text=revokePreview is null ? "No current dependency review. Review again after changing the request." :
            $"{revokePreview.PotentialChanges.Count} potentially affected grant(s).\n"+string.Join("\n",revokePreview.PotentialChanges.Select(x=>$"{(x.Direct ? "Direct" : "Dependent")}: {x.Grantor} (OID {x.GrantorOid}) → {x.Recipient} (OID {x.RecipientOid}) | {x.Privilege} | Column: {x.Column ?? "all"} | Grant option: {x.GrantOption}"));
        InspectButton.Content=HasUnconfirmedCommit?"_Check transaction":"_Inspect access";
        System.Windows.Automation.AutomationProperties.SetName(InspectButton,HasUnconfirmedCommit?"Check unconfirmed permission transaction":"Inspect connected role access");
        if(!busy)Fields.IsEnabled=!HasUnconfirmedCommit;
        bool routineContext=ObjectType.SelectedItem is PrivilegeTarget.Routine;
        ObjectType.IsEnabled=!routineContext;
        ObjectName.IsReadOnly=routineContext;
        if(routineContext)SchemaName.IsEnabled=false;
        RoutineHelp.Visibility=routineContext?Visibility.Visible:Visibility.Collapsed;
        RoutineHelp.Text=selectedRoutine is null
            ? "Inspect access to load this selected overload. To change the selection, close this window and select a routine in Object Explorer."
            : selectedRoutine.QualifiedSignature+"\nEXECUTE applies only to this overload. PUBLIC has EXECUTE by default. To select another overload, close and use Object Explorer.";
        RoleName.IsEnabled = PublicRecipient.IsChecked != true;
        GrantOption.IsEnabled = PublicRecipient.IsChecked != true;
        if (PublicRecipient.IsChecked == true && GrantOption.IsChecked == true) GrantOption.IsChecked = false;
        GrantOption.Content = Revoke.IsChecked == true ? "Revoke _grant option only" : "With _grant option";
        System.Windows.Automation.AutomationProperties.SetName(GrantOption, Revoke.IsChecked == true ? "Revoke grant option only" : "With grant option");
        if (AccessSnapshot is { } prior && (prior.Target != (PrivilegeTarget)ObjectType.SelectedItem || prior.Name != ObjectName.Text || ((prior.Target is PrivilegeTarget.Table or PrivilegeTarget.Sequence) && prior.Schema != SchemaName.Text)))
        {
            AccessSnapshot=null; AccessRows.ItemsSource=null; AclRows.ItemsSource=null; AclNotice.Text="Target changed. Inspect to load ACL entries."; AccessSnapshotIsStale=false;
            AccessSummary.Text="Target changed. Inspect to load effective access.";
        }
        try
        {
            Preview.Text = Request.ToSql();
            if (!preserveFeedback)
            {
                Feedback.Text = PublicRecipient.IsChecked == true
                    ? "PUBLIC includes all current and future roles. Revoke removes its grant only; other access may remain."
                    : GrantOption.IsChecked == true
                        ? Revoke.IsChecked == true
                            ? "Removes delegation only; the base privilege remains. RESTRICT refuses dependent grants."
                            : "Allows this recipient to grant the privilege to other roles. Review the SQL before applying."
                        : "Revoke removes a direct grant with RESTRICT. Access through PUBLIC or other memberships may remain.";
                if(ColumnScopeActive) Feedback.Text+=" Column scope does not deny access from table grants, PUBLIC or memberships. Inspect to review effective access.";
            }
            ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy && !HasUnconfirmedCommit && (!routineContext || !routineNeedsInspection);
            if(explicitGrantorRequested && selectedGrantor is null)
            {
                ScriptButton.IsEnabled=ApplyButton.IsEnabled=false;
                if(!preserveFeedback)Feedback.Text="Grantor selection is outdated. Load grantors and select again, or explicitly choose Use session origin.";
            }
            if(selectedGrantor is not null && !canChooseGrantor) ApplyButton.IsEnabled=false;
            if(Request.Cascade) ApplyButton.IsEnabled &= revokePreview is not null;
            if(Request.Cascade && !preserveFeedback) Feedback.Text="CASCADE may remove dependent grants. Review the potential changes, then Apply to confirm.";
        }
        catch (ArgumentException ex) { Preview.Clear(); if (!preserveFeedback) Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
        try
        {
            if (routineContext)
            {
                if (selectedRoutineOid is null or 0) throw new ArgumentException("Select a routine in Object Explorer.");
            }
            else _=SqlIdentifier.Quote(ObjectName.Text);
            if ((PrivilegeTarget)ObjectType.SelectedItem is PrivilegeTarget.Table or PrivilegeTarget.Sequence) _=SqlIdentifier.Quote(SchemaName.Text);
            InspectButton.IsEnabled=!busy;
        }
        catch(ArgumentException) { InspectButton.IsEnabled=false; }
        if(HasUnconfirmedCommit)InspectButton.IsEnabled=!busy;
    }
    private void AclSelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(AclDetails is null) return;
        AclDetails.Text=AclRows.SelectedItem is PrivilegeAclEntry entry
            ? $"{entry.Scope} | Column: {entry.Column ?? "N/A"} | attnum: {entry.ColumnNumber?.ToString() ?? "N/A"}\nGrantor: {entry.Grantor} (OID {entry.GrantorOid})\nGrantee: {entry.Grantee} (OID {entry.GranteeOid})\n{entry.Privilege} | ACL grant option: {entry.GrantOption} | {entry.SourceText} | Role available: {entry.RoleAvailable}"
            : "Select an ACL row to read complete names and OIDs.";
    }
    private void UpdateAclNotice()
    {
        if (AccessSnapshot is not { } snapshot) {AclNotice.Text="No ACL snapshot loaded.";return;}
        AclNotice.Text=snapshot.AclEntries.Count==0 ? "No ACL entries in the inspected scope; effective authority may still exist." : $"{snapshot.AclEntries.Count} retained ACL row(s).";
        if(snapshot.AclTruncated) AclNotice.Text+=$" Entry limit reached: first {EffectivePrivilegeService.MaximumAclEntries} rows only.";
        if(snapshot.ColumnsTruncated) AclNotice.Text+=$" Column scope limited to the first {EffectivePrivilegeService.MaximumColumns} columns.";
        if(AccessSnapshotIsStale) AclNotice.Text="Previous ACL observation may be outdated. "+AclNotice.Text;
        if(busy) AclNotice.Text="Operation in progress; previous observation remains visible. "+AclNotice.Text;
    }
    private void MarkAccessStale()
    {
        if (selectedRoutineOid is not null) routineNeedsInspection=true;
        if (AccessSnapshot is null || AccessSnapshotIsStale) return;
        AccessSnapshotIsStale=true;
        AccessSummary.Text="Outdated observation; inspect to refresh.\n"+AccessSummary.Text;
        UpdateAclNotice();
    }
    public EffectivePrivilegeSnapshot? AccessSnapshot { get; private set; }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        if (HasUnconfirmedCommit) { await CheckCommitAsync(); return; }
        focus.Capture();
        PermissionTabs.SelectedItem=EffectiveAccessTab;
        using var cancellation=new CancellationTokenSource(); inspection=cancellation;
        busy=true;
        CancelInspectionButton.Visibility=Visibility.Visible; CancelInspectionButton.IsEnabled=true; CancelInspectionButton.Focus();
        Fields.IsEnabled=CloseButton.IsEnabled=false;
        Update(true);
        Feedback.Text="Inspecting effective access…";
        UpdateAclNotice();
        try
        {
            if (ObjectType.SelectedItem is PrivilegeTarget.Routine && selectedRoutine is null)
            {
                if (selectedRoutineOid is not { } oid) throw new InvalidOperationException("Select a routine in Object Explorer.");
                var captured=await new RoutinePrivilegeCatalog(connection).InspectAsync(oid,cancellation.Token);
                if (captured.Schema!=selectedRoutineSchema) throw new InvalidOperationException("Routine selection changed. Refresh Object Explorer and select it again.");
                selectedRoutine=captured; ObjectName.Text=captured.Name; SchemaName.Text=captured.Schema;
            }
            var snapshot=await new EffectivePrivilegeService(connection).InspectAsync((PrivilegeTarget)ObjectType.SelectedItem,ObjectName.Text,SchemaName.Text,cancellation.Token,column:ColumnScopeActive && !string.IsNullOrWhiteSpace(ColumnName.Text)?ColumnName.Text:null,recipient:RoleName.Text,recipientIsPublic:PublicRecipient.IsChecked==true,routine:selectedRoutine);
            cancellation.Token.ThrowIfCancellationRequested();
            AccessSnapshot=snapshot; AccessSnapshotIsStale=false; routineNeedsInspection=false;
            AccessRows.ItemsSource=snapshot.Privileges;
            AclRows.ItemsSource=snapshot.AclEntries;
            UpdateAclNotice();
            static string Flag(bool? value) => value?.ToString() ?? "N/A";
            AccessSummary.Text=$"Connected role: {snapshot.Role} | Session: {snapshot.SessionUser}\n{snapshot.Target}: {snapshot.Schema} {snapshot.Name} | OID: {snapshot.Oid} | Owner: {snapshot.Owner}\nConnected database: {snapshot.Database} | Inspected UTC: {snapshot.ObservedAt:O}\nSchema USAGE: {Flag(snapshot.SchemaUsage)} | RLS enabled/forced/active: {Flag(snapshot.RowSecurityEnabled)}/{Flag(snapshot.RowSecurityForced)}/{Flag(snapshot.RowSecurityActive)}" +
                (snapshot.ColumnsTruncated ? "\nColumn details limited to the first 256 columns; remaining columns were not inspected." : "");

            Feedback.Text="Effective access snapshot loaded. No changes made." + (snapshot.ColumnsTruncated ? " Column details limited to the first 256 columns." : "");
        }
        catch(Exception ex) when(cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or System.IO.IOException or TimeoutException)
        { MarkAccessStale(); Feedback.Text="Inspection canceled. " + (AccessSnapshot is null ? "No snapshot loaded." : "Previous snapshot retained; it may be outdated."); }
        catch(Exception ex) when(ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException)
        { MarkAccessStale(); Feedback.Text=(ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message)+(AccessSnapshot is null ? " No snapshot loaded. Inspect to retry." : " Previous snapshot retained; it may be outdated."); }
        finally { inspection=null; busy=false; CancelInspectionButton.IsEnabled=false; CancelInspectionButton.Visibility=Visibility.Collapsed; Fields.IsEnabled=CloseButton.IsEnabled=true; Update(true); UpdateAclNotice(); focus.Restore(this,CancelInspectionButton); }
    }
    internal async Task CheckCommitAsync()
    {
        if(busy)return;
        if(unreadableCommit)RestorePendingCommit();
        if(pendingCommit is null || pendingCommitService is null)return;
        focus.Capture();
        using var cancellation=new CancellationTokenSource(); inspection=cancellation;
        busy=true;Fields.IsEnabled=CloseButton.IsEnabled=false;
        CancelInspectionButton.Visibility=Visibility.Visible;CancelInspectionButton.IsEnabled=true;CancelInspectionButton.Focus();
        Update(true);Feedback.Text="Checking the original transaction on the unchanged configured cluster… No command will be repeated.";
        PrivilegeCommitStatus? observedStatus=null;
        try
        {
            using var lease=commitStore.AcquireLease();
            var status=await pendingCommitService.ReconcileCommitAsync(pendingCommit,cancellation.Token);
            observedStatus=status;
            string id=pendingCommit.TransactionId;
            if(status is PrivilegeCommitStatus.Committed or PrivilegeCommitStatus.Aborted)
            {
                commitStore.Remove(pendingCommit);
                pendingCommit=null;pendingCommitService=null;
                Feedback.Text=status==PrivilegeCommitStatus.Committed
                    ?$"Transaction {id} committed. No command was repeated. Inspect access to review its current state."
                    :$"Transaction {id} aborted. No changes from this transaction committed. Inspect access before retrying.";
            }
            else Feedback.Text=$"Transaction {id}: result still {(status==PrivilegeCommitStatus.InProgress?"in progress":"unknown")}. No command was repeated. Keep the cluster configuration unchanged and check again.";
        }
        catch(Exception ex)when(ex is NpgsqlException or System.IO.IOException or TimeoutException or OperationCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {Feedback.Text=observedStatus is PrivilegeCommitStatus.Committed or PrivilegeCommitStatus.Aborted
            ?$"Transaction {observedStatus.ToString()!.ToLowerInvariant()}; its protected local record could not be cleared. No command was repeated. Restore local state access and check again."
            :"Transaction result remains unknown. No command was repeated. Restore connectivity to the same cluster and local state access, then check again.";}
        finally
        {
            inspection=null;busy=false;Fields.IsEnabled=CloseButton.IsEnabled=true;
            CancelInspectionButton.IsEnabled=false;CancelInspectionButton.Visibility=Visibility.Collapsed;
            MarkAccessStale();Update(true);UpdateAclNotice();focus.Restore(this,CancelInspectionButton);
        }
    }
    private void CancelInspection(object sender, RoutedEventArgs e) => CancelCurrentOperation();
    public void CancelCurrentOperation()
    {
        if (application is null) { CancelAccessInspection(); return; }
        if (application.IsCancellationRequested) return;
        CancelInspectionButton.IsEnabled=false;
        Feedback.Text="Canceling Apply… Waiting for the server. Inspect access before retrying.";
        application.Cancel();
    }
    public void CancelAccessInspection()
    {
        if (inspection is null) return;
        CancelInspectionButton.IsEnabled=false;
        Feedback.Text="Canceling inspection… Connection startup may wait for its timeout.";
        inspection.Cancel();
    }
    private void Script(object sender, RoutedEventArgs e) { if(busy || HasUnconfirmedCommit || !ScriptButton.IsEnabled) return; ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    internal async Task ApplyAsync()
    {
        if (busy || !ApplyButton.IsEnabled) return;
        var request = Request;
        var reviewed=revokePreview;
        if(request.Cascade)
        {
            if(reviewed is null || reviewed.Request!=request)return;
            if(MessageBox.Show(this,Target.Text+"\n\n"+request.ToSql()+$"\n\n{reviewed.PotentialChanges.Count} grant(s) are potentially affected. Independent access may remain. Revoke with CASCADE?", "Confirm Cascading Revoke", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)!=MessageBoxResult.Yes)
            { Feedback.Text="Cascading revoke canceled. Nothing was sent."; return; }
        }
        focus.Capture();
        using var cancellation = new CancellationTokenSource(); application = cancellation;
        CancelInspectionButton.Content="_Cancel Apply";
        System.Windows.Automation.AutomationProperties.SetName(CancelInspectionButton,"Cancel permission Apply");
        CancelInspectionButton.Visibility=Visibility.Visible; CancelInspectionButton.IsEnabled=true;
        busy = true; InspectButton.IsEnabled=false; Fields.IsEnabled = ScriptButton.IsEnabled = ApplyButton.IsEnabled = CloseButton.IsEnabled = false;
        CancelInspectionButton.Focus();
        Feedback.Text = "Applying permissions…";
        UpdateAclNotice();
        var journal=new PrivilegeCommitStore(connection,request);
        var service=new PrivilegeService(connection,journal);
        try
        {
            using var lease=journal.AcquireLease();
            if(journal.Read() is not null){RestorePendingCommit();return;}
            var identity=request.Grantor is null ? AccessSnapshot?.TargetIdentity : grantorSnapshot?.Target;
            if(identity is not null && identity.Column!=request.Column)
            {
                short? number=request.Column is null?null:AccessSnapshot!.Privileges.FirstOrDefault(p=>p.Scope=="Column" && p.Column==request.Column)?.ColumnNumber;
                identity=identity with{Column=request.Column,ColumnNumber=number};
            }
            bool committed=request.Cascade
                ?await service.ApplyReviewedRevokeAsync(reviewed!,cancellation.Token)
                :identity is null
                ?await service.ApplyCurrentAsync(request,cancellation.Token)
                :AccessSnapshot?.RecipientIdentity is { } recipient && recipient.IsPublic==request.RecipientIsPublic && (recipient.IsPublic || recipient.Name==request.Role)
                    ?await service.ApplyInspectedAsync(request,identity,recipient,cancellation.Token)
                    :await service.ApplyInspectedAsync(request,identity,cancellation.Token);
            Feedback.Text=committed
                ?"Command completed. Effective access can also come from PUBLIC, ownership and memberships."
                :"No ACL change was committed. Inspect access to refresh before retrying.";
            if(journal.Recorded is { } saved)
            {
                var receipt=service.ResumeCommitCheck(saved.TransactionId,saved.DatabaseOid);
                try{journal.Remove(receipt);}
                catch(Exception ex)when(ex is System.IO.IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    pendingCommitService=service;pendingCommit=receipt;
                    Feedback.Text="The server confirmed COMMIT, but its protected local record could not be cleared. Check transaction before another Apply; no command will be repeated.";
                }
            }
        }
        catch(PrivilegeCommitUncertainException ex)
        {
            pendingCommitService=service;pendingCommit=ex.Receipt;
            Feedback.Text=$"Transaction {ex.Receipt.TransactionId}: commit result unknown. Use Check transaction before applying again. Check only against the same unchanged cluster; a changed or failed-over cluster cannot confirm this receipt.";
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or NpgsqlException or System.IO.IOException or TimeoutException)
        { Feedback.Text="Apply interrupted. Result not confirmed; inspect access before retrying. A completed commit cannot be undone by canceling."; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Feedback.Text=(ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message)+" Inspect access before retrying; the result is not confirmed."; }
        finally { if(!HasUnconfirmedCommit)RestorePendingCommit();
            application=null; CancelInspectionButton.IsEnabled=false; CancelInspectionButton.Visibility=Visibility.Collapsed;
            CancelInspectionButton.Content="_Cancel inspection";
            System.Windows.Automation.AutomationProperties.SetName(CancelInspectionButton,"Cancel access inspection");
            revokePreview=null;
            MarkAccessStale();
            if(selectedRoutineOid is not null && !HasUnconfirmedCommit) Feedback.Text+=" Inspect access before another routine operation. If the routine changed, refresh Object Explorer and select it again.";
            busy = false; Fields.IsEnabled = CloseButton.IsEnabled = true; Update(true); UpdateAclNotice(); focus.Restore(this,CancelInspectionButton); }
    }
    private async void OnPreviewKeyDown(object sender,KeyEventArgs e)
    {
        if (e.Key==Key.F5) {e.Handled=true; await InspectAsync();}
        else if (busy && e.Key==Key.Escape) {e.Handled=true; CancelCurrentOperation();}
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; CancelCurrentOperation(); } }
}
