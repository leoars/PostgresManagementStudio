using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ColumnTypeDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    private static void Click(Window w,string name)=>Find<Button>(w,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Capture(Window w,string name,double width,double height,Action<bool,string> check,string milestone)
    {
        w.Width=width;w.Height=height;await w.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);w.UpdateLayout();
        foreach(string id in w is ColumnTypePicker?new[]{"ChooseButton","InspectButton","CancelButton","CloseButton"}:new[]{"ApplyButton","ScriptButton","CloseButton"})
        {
            var button=Find<Button>(w,id);var root=(FrameworkElement)w.Content;var point=button.TranslatePoint(new Point(),root);
            check(point.X>=0 && point.Y>=0 && point.X+button.ActualWidth<=root.ActualWidth+.1 && point.Y+button.ActualHeight<=root.ActualHeight+.1,"Type action fits "+name+"/"+id);
        }
        TableEditChecks.Capture(w,milestone+"-"+name);
    }
    private static async Task Modal(Window w,Func<Task> body)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Loaded+=async(_,_)=>{try{await body();done.SetResult();}catch(Exception ex){done.SetException(ex);}finally{if(w.IsVisible)w.Close();}};
        w.ShowDialog();await done.Task;
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="type_ui_"+suffix,owner="type_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TYPE {qs}.mood AS ENUM('ação','漢字');RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            var picker=new ColumnTypePicker(limited){ShowInTaskbar=false};
            await Modal(picker,async()=>
            {
                check(!Find<Button>(picker,"ChooseButton").IsEnabled,"Type picker requires catalog selection");
                await picker.LoadAsync();var types=Find<ComboBox>(picker,"Types");
                types.SelectedItem=types.Items.Cast<InspectedDataType>().Single(x=>x.Oid==1700);
                Find<TextBox>(picker,"Precision").Text="9";Find<TextBox>(picker,"Scale").Text="3";
                check(Find<TextBox>(picker,"Preview").Text=="\"pg_catalog\".\"numeric\"(9, 3)","Type picker previews exact numeric modifiers");
                Find<TextBox>(picker,"Scale").Text="10";check(!Find<Button>(picker,"ChooseButton").IsEnabled,"Invalid numeric scale blocks choice");
                Find<TextBox>(picker,"Scale").Text="3";
                foreach(string id in new[]{"Types","Length","Precision","Scale","Preview"})check(!string.IsNullOrEmpty(UIElementAutomationPeer.CreatePeerForElement(Find<FrameworkElement>(picker,id))?.GetName()),"Type picker accessible field: "+id);
                await Capture(picker,"type-picker-normal",720,490,check,"m825");
                await Capture(picker,"type-picker-minimum",620,440,check,"m825");
                Click(picker,"ChooseButton");
            });
            check(picker.SelectedType?.Precision==9 && picker.SelectedType.Scale==3,"Type picker returns reviewed immutable selection");
            var create=new CreateTableDialog(limited,schema){ShowInTaskbar=false};
            await Modal(create,async()=>
            {
                Find<TextBox>(create,"TableName").Text="typed";
                Exception? nestedError=null;
                _ = create.Dispatcher.BeginInvoke(new Action(async()=>
                {
                    var nested=Application.Current.Windows.OfType<ColumnTypePicker>().Single();
                    try{await nested.LoadAsync();var items=Find<ComboBox>(nested,"Types");items.SelectedItem=items.Items.Cast<InspectedDataType>().Single(x=>x.Schema==schema && x.Name=="mood");Click(nested,"ChooseButton");}
                    catch(Exception ex){nestedError=ex;nested.Close();}
                }));
                Click(create,"CatalogTypeButton");if(nestedError is not null)throw nestedError;
                check(!Find<ComboBox>(create,"DataType").IsEnabled && !Find<ComboBox>(create,"IdentitySelector").IsEnabled,"Catalog type makes its precedence explicit and prevents conflicting identity choice");
                Find<TextBox>(create,"ColumnName").Text="state";Click(create,"AddButton");
                check(Find<TextBox>(create,"Preview").Text.Contains(qs+".\"mood\""),"New Table receives enum through actual picker dialog");
                check(((TableColumn)Find<DataGrid>(create,"ColumnGrid").Items[0]).TypeDisplay.Contains("mood"),"Column list shows selected catalog type instead of fallback built-in");
                Click(create,"ClearTypeButton");check(Find<ComboBox>(create,"DataType").IsEnabled,"Use built-in restores standard type selection");
                check(Find<TextBox>(create,"Preview").Text.Contains(qs+".\"mood\""),"Changing next-column choice preserves previously added enum");
                create.SetInspectedType(picker.SelectedType!);Find<TextBox>(create,"ColumnName").Text="amount";Click(create,"AddButton");
                await Capture(create,"new-table-types-normal",800,780,check,"m825");
                await Capture(create,"new-table-types-minimum",710,710,check,"m825");
                await create.ApplyAsync();
            });
            check(create.WasApplied,"New Table applies inspected enum and numeric types as ordinary owner");
            await Scalar($"INSERT INTO {qs}.typed VALUES('漢字',12.345)");
            check(Convert.ToString(await Scalar($"SELECT state::text FROM {qs}.typed"))=="漢字","WPF-created enum preserves actual Unicode label");
            var stale=new CreateTableDialog(limited,schema){ShowInTaskbar=false};
            await Modal(stale,async()=>
            {
                var mood=(await new DataTypeCatalog(limited).InspectAsync()).Single(x=>x.Schema==schema && x.Name=="mood");
                stale.SetInspectedType(new(mood));Find<TextBox>(stale,"TableName").Text="stale";Find<TextBox>(stale,"ColumnName").Text="state";Click(stale,"AddButton");
                await Scalar($"ALTER TYPE {qs}.mood ADD VALUE 'new'");await stale.ApplyAsync();
                check(!stale.WasApplied && Find<TextBlock>(stale,"Feedback").Text.Contains("changed"),"WPF refuses catalog definition changed after review");
                check(Find<Button>(stale,"CatalogTypeButton").IsEnabled && Find<TextBox>(stale,"Preview").Text.Contains("mood"),"Type conflict preserves proposal and permits a new selection");
            });
            await Scalar($"SET ROLE {qo};CREATE TABLE {qs}.conversion(value text,other int);INSERT INTO {qs}.conversion VALUES(' 12 ',7);RESET ROLE");
            var tableSnapshot=await new TablePropertiesService(limited).InspectAsync(schema,"conversion");
            var typesNow=await new DataTypeCatalog(limited).InspectAsync();var integer=typesNow.Single(x=>x.Oid==23);var textType=typesNow.Single(x=>x.Oid==25);
            var operation=new ColumnTypeDialog(limited,tableSnapshot,tableSnapshot.Columns.Single(x=>x.Name=="value")){ShowInTaskbar=false};operation.Show();
            try
            {
                check(!Find<Button>(operation,"ApplyButton").IsEnabled,"Column conversion requires current inspection and reviewed proposal");
                await operation.InspectAsync();operation.SetType(new(integer));Find<ComboBox>(operation,"Conversion").SelectedValue=ColumnTypeConversion.TrimText;
                check(Find<TextBox>(operation,"Preview").Text.Contains("pg_catalog.btrim") && !Find<Button>(operation,"ApplyButton").IsEnabled,"Conversion SQL exposes USING and requires value-change review");
                Find<CheckBox>(operation,"Reviewed").IsChecked=true;
                await CascadePrivilegeDialogChecks.Capture(operation,"column-type-normal",820,660,check,"m826");
                await CascadePrivilegeDialogChecks.Capture(operation,"column-type-minimum",700,560,check,"m826");
                await SequenceConfirmationChecks.RespondAsync(operation,operation.ApplyAsync,false,check);
                check(!operation.WasApplied && Find<TextBlock>(operation,"Feedback").Text.Contains("Nothing was sent"),"Native No preserves stored values and conversion proposal");
                await SequenceConfirmationChecks.RespondAsync(operation,operation.ApplyAsync,true,check);
                check(operation.WasApplied && operation.SnapshotIsStale && operation.RequiresParentRefresh,"Confirmed conversion succeeds and requires fresh inspection");
                check(Convert.ToInt32(await Scalar($"SELECT value FROM {qs}.conversion"))==12 && Convert.ToInt32(await Scalar($"SELECT other FROM {qs}.conversion"))==7,"WPF conversion changes real target values and preserves neighboring data");
                await operation.InspectAsync();operation.SetType(new(textType));
                check(Find<CheckBox>(operation,"Reviewed").IsChecked!=true && !Find<Button>(operation,"ApplyButton").IsEnabled,"Changing destination invalidates previous value-change review");
                foreach(string cancelMode in new[]{"button","escape","close"})
                {
                    await operation.InspectAsync();Find<CheckBox>(operation,"Reviewed").IsChecked=true;
                    await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
                    await using(var command=new NpgsqlCommand($"LOCK TABLE {qs}.conversion IN ACCESS EXCLUSIVE MODE",blocker,tx))await command.ExecuteNonQueryAsync();
                    Task? pending=null;
                    try
                    {
                        pending=SequenceConfirmationChecks.RespondAsync(operation,operation.ApplyAsync,true,check);
                        await SequenceApplyCancellationChecks.WaitForLock(connection,schema);
                        check(!Find<FrameworkElement>(operation,"Fields").IsEnabled && Find<Button>(operation,"CancelButton").IsEnabled,"Blocked conversion protects proposal and exposes cancellation: "+cancelMode);
                        await operation.ApplyAsync();
                        if(cancelMode=="close")operation.Close();else if(cancelMode=="escape")operation.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(operation),0,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});else Click(operation,"CancelButton");
                        await pending.WaitAsync(TimeSpan.FromSeconds(8));
                        check(operation.IsVisible && operation.SnapshotIsStale && Find<TextBlock>(operation,"Feedback").Text.Contains("Result not confirmed"),"Interrupted conversion retains dialog and requires inspection: "+cancelMode);
                    }
                    finally{operation.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                    check((await new ExpressionIndexCatalog(limited).InspectAsync(schema,"conversion")).Columns.Single(x=>x.Name=="value").TypeOid==23,"Canceled blocked conversion leaves actual column integer: "+cancelMode);
                }
                await operation.InspectAsync();Find<CheckBox>(operation,"Reviewed").IsChecked=true;await SequenceConfirmationChecks.RespondAsync(operation,operation.ApplyAsync,true,check);
                check((await new ExpressionIndexCatalog(limited).InspectAsync(schema,"conversion")).Columns.Single(x=>x.Name=="value").TypeOid==25,"Explicit reinspected retry converts after cancellation");
            }
            finally{operation.CancelCurrentOperation();operation.Close();}
            var parentSnapshot=await new TablePropertiesService(limited).InspectAsync(schema,"conversion");
            var parent=new TableEditDialog(limited,inspectedTable:parentSnapshot,column:"value"){ShowInTaskbar=false};
            await Modal(parent,async()=>
            {
                check(Find<Button>(parent,"TypeOperationsButton").IsEnabled,"Inspected table column exposes type conversion navigation");
                Exception? nestedError=null;
                _=parent.Dispatcher.BeginInvoke(new Action(async()=>
                {
                    var child=Application.Current.Windows.OfType<ColumnTypeDialog>().Single();
                    try{await child.InspectAsync();child.SetType(new(integer));Click(child,"ScriptButton");}catch(Exception ex){nestedError=ex;child.Close();}
                }));
                await parent.OpenTypeOperationsAsync();if(nestedError is not null)throw nestedError;
            });
            check(parent.ScriptSql?.Contains("ALTER COLUMN \"value\" TYPE \"pg_catalog\".\"int4\"")==true && !parent.WasApplied,"Parent propagates exact conversion Script without executing it");
            var replaced=new ColumnTypeDialog(limited,parentSnapshot,parentSnapshot.Columns.Single(x=>x.Name=="value")){ShowInTaskbar=false};
            await Scalar($"ALTER TABLE {qs}.conversion DROP COLUMN value;ALTER TABLE {qs}.conversion ADD COLUMN value text");replaced.Show();
            try{await replaced.InspectAsync();check(replaced.SnapshotIsStale && Find<TextBlock>(replaced,"Feedback").Text.Contains("replaced"),"Type dialog refuses same-name column replaced after parent inspection");}finally{replaced.Close();}
            await Scalar($"SET ROLE {qo};CREATE DOMAIN {qs}.amount_default AS numeric(8,2) DEFAULT 4.25 CHECK(VALUE>0);RESET ROLE");
            var domain=(await new DataTypeCatalog(limited).InspectAsync()).Single(x=>x.Schema==schema && x.Name=="amount_default");
            var addParent=new TableEditDialog(limited,inspectedTable:await new TablePropertiesService(limited).InspectAsync(schema,"conversion")){ShowInTaskbar=false};
            await Modal(addParent,async()=>
            {
                Find<ComboBox>(addParent,"Operation").SelectedItem=TableOperation.AddColumn;Find<TextBox>(addParent,"ColumnName").Text="amount";
                check(Find<Button>(addParent,"AddCatalogColumnButton").IsEnabled,"Inspected Add Column exposes catalog addition");
                Exception? nestedError=null;
                _=addParent.Dispatcher.BeginInvoke(new Action(async()=>
                {
                    var child=Application.Current.Windows.OfType<ColumnTypeDialog>().Single();
                    try
                    {
                        check(Find<TextBox>(child,"NewColumnName").Text=="amount" && !Find<StackPanel>(child,"ConversionFields").IsVisible,"Catalog addition receives proposed name and hides USING conversion");
                        await child.InspectAsync();child.SetType(new(domain));Find<CheckBox>(child,"AllowNull").IsChecked=false;Find<CheckBox>(child,"Reviewed").IsChecked=true;
                        check(Find<TextBox>(child,"Preview").Text.Contains("ADD COLUMN \"amount\"") && Find<TextBox>(child,"Preview").Text.Contains("NOT NULL"),"Catalog addition previews explicit type and null restriction");
                        await CascadePrivilegeDialogChecks.Capture(child,"add-catalog-normal",820,660,check,"m827");
                        await CascadePrivilegeDialogChecks.Capture(child,"add-catalog-minimum",700,560,check,"m827");
                        await SequenceConfirmationChecks.RespondAsync(child,child.ApplyAsync,false,check);
                        check(!child.WasApplied && !child.RequiresParentRefresh,"Native No does not add the catalog column");
                        await SequenceConfirmationChecks.RespondAsync(child,child.ApplyAsync,true,check);check(child.WasApplied,"Confirmed catalog addition succeeds with domain default");child.Close();
                    }
                    catch(Exception ex){nestedError=ex;child.CancelCurrentOperation();child.Close();}
                }));
                await addParent.OpenCatalogAdditionAsync();if(nestedError is not null)throw nestedError;
                check(addParent.WasApplied && addParent.InspectedTable!.Columns.Any(x=>x.Name=="amount"),"Parent records catalog addition and refreshes actual column list");
            });
            check(Convert.ToDecimal(await Scalar($"SELECT amount FROM {qs}.conversion"))==4.25m,"Domain default populates the actual existing row during ADD COLUMN");
            var missingDefault=new ColumnTypeDialog(limited,await new TablePropertiesService(limited).InspectAsync(schema,"conversion"),null,"required"){ShowInTaskbar=false};missingDefault.Show();
            try
            {
                await missingDefault.InspectAsync();missingDefault.SetType(new(integer));Find<CheckBox>(missingDefault,"AllowNull").IsChecked=false;Find<CheckBox>(missingDefault,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(missingDefault,missingDefault.ApplyAsync,true,check);
                check(!missingDefault.WasApplied && missingDefault.SnapshotIsStale && Find<TextBlock>(missingDefault,"Feedback").Text.Contains("23502"),"NOT NULL without a default fails on existing rows and preserves SQLSTATE");
                check(!(await new ExpressionIndexCatalog(limited).InspectAsync(schema,"conversion")).Columns.Any(x=>x.Name=="required"),"Failed catalog addition leaves no column behind");
                await missingDefault.InspectAsync();Find<CheckBox>(missingDefault,"AllowNull").IsChecked=true;Find<CheckBox>(missingDefault,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(missingDefault,missingDefault.ApplyAsync,true,check);
                check(missingDefault.WasApplied && await Scalar($"SELECT required FROM {qs}.conversion") is DBNull,"Explicit retry permits nullable addition and preserves existing row as NULL");
            }
            finally{missingDefault.Close();}
        }
        finally{await Scalar($"DROP SCHEMA {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE {qo}");}
    }
}
