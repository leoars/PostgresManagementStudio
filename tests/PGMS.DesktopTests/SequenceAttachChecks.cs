using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceAttachChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string name="pgms_attach_ui_"+Guid.NewGuid().ToString("N"), seq=ObjectSql.Relation("public",name), table=ObjectSql.Relation("public",name+"_t");
        await using var session=new QuerySession(connection); await session.OpenAsync(); var service=new SequenceService(connection);
        async Task Prepare(SequenceDialog w)
        {
            ((ComboBox)w.FindName("Operation")).SelectedItem=SequenceOperation.SetOwnedBy;
            ((TextBox)w.FindName("OwnerTable")).Text=name+"_t";
            await w.ReadOwnerColumnsAsync(); ((ComboBox)w.FindName("OwnerColumn")).SelectedIndex=0; await w.InspectAsync();
        }
        try
        {
            await session.ExecuteAsync($"CREATE SEQUENCE {seq}; CREATE TABLE {table}(id bigint,other bigint,ident bigint GENERATED ALWAYS AS IDENTITY);");
            SequenceRestartChecks.Show(new SequenceDialog(connection,"public",name){ShowInTaskbar=false},async w=>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem=SequenceOperation.SetOwnedBy;
                check(!((Button)w.FindName("ApplyButton")).IsEnabled,"Association UI requires both inspected endpoints");
                await Prepare(w);
                check(((ComboBox)w.FindName("OwnerColumn")).Items.Count==2,"Association UI lists only eligible inspected columns");
                check(((TextBlock)w.FindName("OwnerSummary")).Text.Contains("Table OID:"),"Association UI shows destination identity");
                ((Button)w.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(w.ScriptSql?.Contains("OWNED BY") == true && !w.WasApplied,"Association Script returns SQL without mutation");
            });
            check((await service.InspectAsync("public",name)).Dependency is null,"Association Script leaves source unassociated");
            SequenceRestartChecks.Show(new SequenceDialog(connection,"public",name){ShowInTaskbar=false},async w=>
            {
                await Prepare(w);
                var ownerTable=(TextBox)w.FindName("OwnerTable"); var ownerColumn=(ComboBox)w.FindName("OwnerColumn");
                check(new System.Windows.Automation.Peers.TextBoxAutomationPeer(ownerTable).GetName()=="Destination table","Destination table exposes accessible name");
                check(new System.Windows.Automation.Peers.ComboBoxAutomationPeer(ownerColumn).GetName()=="Destination column","Destination column exposes accessible name");
                System.Windows.Input.AccessKeyManager.ProcessKey(null,"d",false);
                check(ownerTable.IsKeyboardFocusWithin,"Destination table mnemonic focuses field");
                System.Windows.Input.AccessKeyManager.ProcessKey(null,"u",false);
                check(ownerColumn.IsKeyboardFocusWithin,"Destination column mnemonic focuses selector");
                ((Button)w.FindName("ReadOwnerButton")).Focus(); await w.ReadOwnerColumnsAsync();
                check(((Button)w.FindName("ReadOwnerButton")).IsKeyboardFocusWithin,"Destination inspection restores initiating focus");
                await SequenceRestartChecks.Capture(w,"sequence-owner",check);
                await SequenceConfirmationChecks.RespondAsync(w,false,check);
                check(!w.WasApplied && (await service.InspectAsync("public",name)).Dependency is null,"Association No preserves source and dialog");
                await session.ExecuteAsync($"ALTER TABLE {table} ALTER COLUMN id TYPE integer;");
                await SequenceConfirmationChecks.RespondAsync(w,true,check);
                check(!w.WasApplied && ((TextBlock)w.FindName("Feedback")).Text.Contains("Destination identity"),"Association Yes still rejects destination conflict");
                await w.ReadOwnerColumnsAsync();
                check(((ComboBox)w.FindName("OwnerColumn")).SelectedItem is SequenceOwnerTarget t && t.Column=="id","Destination refresh preserves selected column name with fresh metadata");
                await SequenceConfirmationChecks.RespondAsync(w,true,check);
                check(w.WasApplied,"Association Apply succeeds after destination refresh");
            });
            check((await service.InspectAsync("public",name)).Dependency?.ColumnNumber==1,"Association WPF persists correct column dependency");
            SequenceRestartChecks.Show(new SequenceDialog(connection,"public",name){ShowInTaskbar=false},async w=>
            {
                await Prepare(w); check(!((Button)w.FindName("ApplyButton")).IsEnabled,"Already associated destination disables Apply");
                ((TextBox)w.FindName("OwnerTable")).Text=name+"_missing";
                check(((ComboBox)w.FindName("OwnerColumn")).SelectedItem is null,"Changing destination clears stale column selection");
                await w.ReadOwnerColumnsAsync();
                check(((TextBlock)w.FindName("Feedback")).Text.Contains("No eligible columns") && !((Button)w.FindName("ApplyButton")).IsEnabled,"Empty destination list retains actionable explanation");
            });
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {seq};"); }
    }
}
