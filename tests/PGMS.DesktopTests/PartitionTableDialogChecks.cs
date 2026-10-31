using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PartitionTableDialogChecks
{
    private static T Find<T>(Window w, string name) where T : FrameworkElement => (T)w.FindName(name);
    private static void Click(Window w, string name) => Find<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Modal(Window w, Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Loaded += async (_, _) => { try { await body(); done.SetResult(); } catch (Exception e) { done.SetException(e); } finally { if (w.IsVisible) w.Close(); } };
        w.ShowDialog(); await done.Task;
    }
    private static void Column(Window w, string name)
    {
        Find<TextBox>(w, "ColumnName").Text = name; Click(w, "AddButton");
    }
    private static void Key(Window w, int index)
    {
        Find<ComboBox>(w, "PartitionColumn").SelectedIndex = index; Click(w, "AddKeyButton");
    }
    private static async Task Capture(Window w, string name, double width, double height, Action<bool,string> check)
    {
        w.Width = width; w.Height = height;
        Find<ScrollViewer>(w,"ColumnFields").ScrollToBottom();
        await w.Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        w.UpdateLayout(); var root = (FrameworkElement)w.Content;
        foreach (string id in new[] { "ScriptButton", "ApplyButton", "CloseButton" })
        {
            var b = Find<Button>(w,id); var p = b.TranslatePoint(new Point(),root);
            check(p.Y >= 0 && p.Y + b.ActualHeight <= root.ActualHeight + .1, "Partition action fits " + name + "/" + id);
        }
        check(Find<TextBox>(w,"Preview").ActualHeight >= 45, "Partition SQL remains readable: " + name);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using(var dc=drawing.RenderOpen()){var rect=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(w.Background,null,rect);dc.DrawRectangle(new VisualBrush(root),null,rect);}bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/review");
        using var file = File.Create("artifacts/review/m795-"+name+".png"); encoder.Save(file);
    }
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="partition_ui_"+suffix, owner="partition_owner_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo}");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            foreach(int strategy in new[]{1,2,3})
            {
                var w=new CreateTableDialog(limited,schema){ShowInTaskbar=false};
                await Modal(w,async()=>
                {
                    Find<TextBox>(w,"TableName").Text="p"+strategy; Column(w,"é\"key"); Column(w,"second");
                    check(Find<ComboBox>(w,"PartitionStrategySelector").SelectedIndex==0 && !Find<TextBox>(w,"Preview").Text.Contains("PARTITION BY"),"New Table defaults to ordinary table: "+strategy);
                    Find<Expander>(w,"PartitionOptions").IsExpanded=true;
                    Find<ComboBox>(w,"PartitionStrategySelector").SelectedIndex=strategy;
                    check(!Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBox>(w,"Preview").Text.Length==0,"Selecting partitioning requires keys: "+strategy);
                    Key(w,0); Key(w,0);
                    check(Find<ListBox>(w,"PartitionKeys").Items.Count==1,"Duplicate key is not added: "+strategy);
                    Key(w,1);
                    if(strategy==2)
                    {
                        check(!Find<Button>(w,"ApplyButton").IsEnabled,"LIST refuses two keys in UI");
                        Find<ListBox>(w,"PartitionKeys").SelectedIndex=1; Click(w,"RemoveKeyButton");
                    }
                    else
                    {
                        Find<ListBox>(w,"PartitionKeys").SelectedIndex=1; Click(w,"MoveKeyButton");
                        check(Find<TextBox>(w,"Preview").Text.Contains("(\"second\", \"é\"\"key\")"),"Key order changes exact preview: "+strategy);
                    }
                    check(Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBlock>(w,"Feedback").Text.Contains("parent only"),"Valid partition definition explains parent-only creation: "+strategy);
                    if(strategy==1)
                    {
                        foreach(string id in new[]{"PartitionStrategySelector","PartitionColumn","PartitionKeys"})
                            check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(w,id))),"Partition control has accessible name: "+id);
                        await Capture(w,"normal",800,780,check);await Capture(w,"minimum",710,710,check);
                    }
                    await w.ApplyAsync();check(w.WasApplied,"WPF creates partitioned parent: "+strategy);
                });
                check((string)(await Scalar($"SELECT relkind::text FROM pg_class WHERE oid='{qs}.p{strategy}'::regclass"))! == "p","WPF creation is partitioned in PostgreSQL: "+strategy);
            }
            var script=new CreateTableDialog(limited,schema){ShowInTaskbar=false};
            await Modal(script,()=>
            {
                Find<TextBox>(script,"TableName").Text="script_only";Column(script,"id");
                Find<ComboBox>(script,"PartitionStrategySelector").SelectedIndex=1;Key(script,0);
                Find<DataGrid>(script,"ColumnGrid").SelectedIndex=0;Click(script,"RemoveButton"); Column(script,"replacement");
                check(!Find<Button>(script,"ApplyButton").IsEnabled && Find<TextBox>(script,"Preview").Text.Length==0,"Removing declared key invalidates preview and Apply");
                Find<ListBox>(script,"PartitionKeys").SelectedIndex=0;Click(script,"RemoveKeyButton");Key(script,0);
                check(Find<Button>(script,"ApplyButton").IsEnabled,"Explicit key repair restores valid proposal");
                Click(script,"ScriptButton");return Task.CompletedTask;
            });
            check(script.ScriptSql!.Contains("PARTITION BY RANGE (\"replacement\")") && !script.WasApplied && (bool)(await Scalar($"SELECT to_regclass('{qs}.script_only') IS NULL"))!,"Partition Script exports exact SQL without creating table");
            var conflict=new CreateTableDialog(limited,schema){ShowInTaskbar=false};
            await Modal(conflict,async()=>
            {
                Find<TextBox>(conflict,"TableName").Text="p1";Column(conflict,"id");Find<ComboBox>(conflict,"PartitionStrategySelector").SelectedIndex=3;Key(conflict,0);
                await conflict.ApplyAsync();
                check(!conflict.WasApplied && Find<TextBlock>(conflict,"Feedback").Text.Contains("42P07") && Find<ComboBox>(conflict,"PartitionStrategySelector").IsEnabled && Find<ListBox>(conflict,"PartitionKeys").Items.Count==1,"Duplicate parent preserves choices and recovers fields");
            });
        }
        finally {await Scalar($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE {qo}");}
    }
}
