using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using PGMS.Desktop;
using PGMS.PostgreSql;
using PGMS.Desktop.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--query-close-checks") { Environment.ExitCode = QueryCloseCommandChecks.RunIsolated(args[1]); return; }
        if (args.Length == 4 && args[0] == "--query-close-child") { Environment.ExitCode = QueryCloseCommandChecks.RunChild(args); return; }
        if (args.Length == 2 && args[0] == "--uia-isolated") { Environment.ExitCode = IsolatedTextAutomationChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--uia-isolated-desktop") { Environment.ExitCode = IsolatedTextAutomationChecks.Run(args[1], true); return; }
        if (args.Length == 2 && args[0] == "--uia-isolated-shell") { Environment.ExitCode = IsolatedTextAutomationChecks.Run(args[1], true, true); return; }
        if (args.Length == 4 && args[0] == "--uia-client-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunClient(args); return; }
        if (args.Length == 4 && args[0] == "--uia-fixture-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunFixture(args); return; }
        if (args.Length == 4 && args[0] == "--uia-desktop-client-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunClient(args); return; }
        if (args.Length == 4 && args[0] == "--uia-desktop-fixture-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunFixture(args); return; }
        if (args.Length == 4 && args[0] == "--uia-desktop-shell-client-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunClient(args); return; }
        if (args.Length == 4 && args[0] == "--uia-desktop-shell-fixture-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunFixture(args); return; }
        if (args.Length == 4 && args[0] is "--uia-desktop-fail-child" or "--uia-desktop-sleeper-child") { Environment.ExitCode = IsolatedTextAutomationChecks.RunContainmentChild(args); return; }
        if (args.Length == 2 && args[0] == "--automation-contract-probe") { Environment.ExitCode = AutomationContractProbe.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--automation-contract-checks") { Environment.ExitCode = SqlTextAutomationChecks.Run(args[1]); return; }
        if (args.Length == 6 && args[0] == "--default-commit-child") { Task.Run(()=>DefaultPrivilegeProcessChecks.RunChildAsync(args[1],args[2],args[3],args[4],args[5])).GetAwaiter().GetResult(); return; }
        if (args.Length == 6 && args[0] == "--routine-commit-child") { Task.Run(()=>PrivilegeCommitProcessChecks.RunChildAsync(args[1],args[2],args[3],args[4],args[5],true)).GetAwaiter().GetResult(); return; }
        if (args.Length == 6 && args[0] == "--permission-commit-child") { Task.Run(()=>PrivilegeCommitProcessChecks.RunChildAsync(args[1],args[2],args[3],args[4],args[5])).GetAwaiter().GetResult(); return; }
        if (args.Length == 2 && args[0] == "--seed-installed-recovery") { Environment.ExitCode = InstalledRecoveryFixture.Seed(args[1]); return; }
        if (args.Length == 4 && args[0] == "--installed-recovery") { Environment.ExitCode = Task.Run(() => InstalledRecoveryProbe.Run(args[1], args[2], args[3])).GetAwaiter().GetResult(); return; }
        if (args.Length == 4 && args[0] == "--installed-profile") { Environment.ExitCode = Task.Run(()=>InstalledProfileProbe.Run(args[1],args[2],args[3])).GetAwaiter().GetResult(); return; }
        if (args.Length == 1 && args[0] == "--clipboard-guards") { Environment.ExitCode = ClipboardGuardChecks.RunStandalone(); return; }
        if (args.Length == 1 && args[0] == "--clipboard-isolated") { Environment.ExitCode = ClipboardProcessChecks.Run(); return; }
        if (args.Length >= 1 && args[0] == "--clipboard-child") { Environment.ExitCode = ClipboardProcessChecks.RunChild(args); return; }
        if (args.Length == 2 && args[0] == "--recovery-child") { RecoveryProcessChecks.RunChild(args[1]); return; }
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.GetFullPath(Path.Combine("artifacts", "desktop-test-state", Guid.NewGuid().ToString("N"))));
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Shell.xaml") });
        if (args.SequenceEqual(new[] { "--preview-about" }) || args.SequenceEqual(new[] { "--capture-about" }))
        {
            var about = new AboutWindow();
            if (args[0] == "--capture-about") about.ContentRendered += async (_, _) =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Directory.CreateDirectory("artifacts/review");
                TableEditChecks.Capture(about, "m957-about-normal");
                about.Width = about.MinWidth; about.Height = about.MinHeight;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                TableEditChecks.Capture(about, "m957-about-small");
                about.Close();
            };
            app.Run(about); return;
        }
        if (args.SequenceEqual(new[] { "--preview-explorer" }))
        {
            var preview = new MainWindow { Title = "PGMS — Explorer preview (isolated test cluster)" };
            preview.Loaded += async (_, _) =>
            {
                try
                {
                    await preview.ConnectExplorerAsync($"Host=127.0.0.1;Port={TestClusterEndpoint.Port};Database=pgms_comparison_m952;Username=pgms_test;SSL Mode=Disable;Pooling=false");
                    var tree = (TreeView)preview.FindName("ExplorerTree");
                    var current = (CatalogNode)tree.Items[0];
                    foreach (string name in new[] { "Databases", "pgms_comparison_m952", "Schemas", "acceptance", "Tables" })
                    {
                        await current.LoadAsync(); current.IsExpanded = true;
                        current = current.Children.Single(n => n.Entry.Name == name);
                    }
                    await current.LoadAsync(); current.IsExpanded = true;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            };
            app.Run(preview); return;
        }
        if (args.Contains("--representative") || args.Contains("--sessions") || args.Contains("--plans") || args.Contains("--export-roundtrip") || args.Contains("--export") || args.Contains("--query-statistics") || args.Contains("--activity") || args.Contains("--highlighting") || args.Contains("--global-backup") || args.Contains("--column-types") || args.Contains("--index-maintenance") || args.Contains("--index-expressions") || args.Contains("--partition-tables") || args.Contains("--cascade-journal") || args.Contains("--default-process") || args.Contains("--default-cancel") || args.Contains("--default-ui") || args.Contains("--default-journal") || args.Contains("--routine-boundaries") || args.Contains("--routine-process") || args.Contains("--routine-privileges") || args.Contains("--backup-formats") || args.Contains("--identity-columns") || args.Contains("--restore-ownership") || args.Contains("--privilege-journal") || args.Contains("--maintain-privilege") || args.Contains("--privilege-commit") || args.Contains("--unicode-password") || args.Contains("--sql-links") || args.Contains("--editor-factors") || args.Contains("--snapshots") || args.Contains("--row-names") || args.Contains("--membership-options") || args.Contains("--column-privileges") || args.Contains("--privilege-cancellation") || args.Contains("--sequence-privileges") || args.Contains("--privilege-commands") || args.Contains("--effective-privileges") || args.Contains("--copy") || args.Contains("--sequence-owner") || args.Contains("--sequence-state") || args.Contains("--open-long-line") || args.Contains("--open-large") || args.Contains("--open-load") || args.Contains("--recovery-save") || args.Contains("--recovery-load") || args.Contains("--recovery") || args.Contains("--files") || args.Contains("--properties") || args.Contains("--explorer") || args.Contains("--advanced-index") || args.Contains("--sequences") || args.Contains("--accessibility") || args.Contains("--connection") || args.Contains("--streaming") || args.Contains("--replace"))
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Startup += async (_, _) =>
            {
                int count = 0;
                try
                {
                    async Task RunFocused(string testConnection, Action<bool, string> testCheck) { int rep=Array.IndexOf(args,"--representative"); if(rep>=0) await RepresentativeLoadChecks.RunAsync(testConnection,args[rep+1],int.Parse(args[rep+2],System.Globalization.CultureInfo.InvariantCulture),testCheck); else if(args.Contains("--sessions")) await QuerySessionUiChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--plans")) await QueryPlanUiChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--export-roundtrip") || args.Contains("--export")) await ResultExportUiChecks.RunAsync(testCheck); else if(args.Contains("--query-statistics"))await QueryStatisticsDialogChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--activity"))await ActivityDialogChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--highlighting"))await HighlightingChecks.RunAsync(testCheck); else if(args.Contains("--global-backup"))await GlobalBackupDialogChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--column-types")){await ColumnTypeDialogChecks.RunAsync(testConnection,testCheck);await EnumDialogChecks.RunAsync(testConnection,testCheck);await DomainDialogChecks.RunAsync(testConnection,testCheck);await CreateTypeDialogChecks.RunAsync(testConnection,testCheck);} else if(args.Contains("--index-maintenance")){await ConcurrentIndexStoreChecks.RunAsync(testConnection,testCheck);await ConcurrentIndexDialogChecks.RunAsync(testConnection,testCheck);} else if(args.Contains("--index-expressions")){await ExpressionIndexDialogChecks.RunAsync(testConnection,testCheck);await RangeExclusionDialogChecks.RunAsync(testConnection,testCheck);} else if(args.Contains("--partition-tables")){await PartitionTableDialogChecks.RunAsync(testConnection,testCheck);await PartitionOperationDialogChecks.RunAsync(testConnection,testCheck);} else if(args.Contains("--cascade-journal")) await CascadeJournalChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--default-process")) await DefaultPrivilegeProcessChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--default-cancel")) await DefaultPrivilegeCancellationChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--default-ui")) await DefaultPrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--default-journal")) await DefaultPrivilegeJournalChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--routine-boundaries")) await RoutineBoundaryChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--routine-process")) await PrivilegeCommitProcessChecks.RunAsync(testConnection,testCheck,true); else if(args.Contains("--routine-privileges")) await RoutinePrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if(args.Contains("--backup-formats")) await BackupFormatDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--identity-columns")) { await IdentityColumnDialogChecks.RunAsync(testConnection,testCheck); await IdentityColumnOperationDialogChecks.RunAsync(testConnection,testCheck); await IdentityColumnCancellationChecks.RunAsync(testConnection,testCheck); } else if (args.Contains("--restore-ownership")) await RestoreOwnershipDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--privilege-journal")) { await PrivilegeCommitStoreChecks.RunAsync(testConnection,testCheck); await PrivilegeCommitProcessChecks.RunAsync(testConnection,testCheck); } else if (args.Contains("--maintain-privilege")) await MaintainPrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--privilege-commit")) await PrivilegeCommitDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--unicode-password")) await UnicodePasswordDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--sql-links")) await SqlLinkChecks.RunAsync(testCheck); else if (args.Contains("--editor-factors")) await EditorFactorChecks.RunAsync(testCheck, args.Contains("unicode") ? "unicode" : args.Contains("links") ? "links" : args.Contains("short") ? "short" : "long-line"); else if (args.Contains("--snapshots")) await QueryTextSnapshotChecks.RunAsync(testCheck); else if (args.Contains("--row-names")) await DataGridRowNameChecks.RunAsync(testCheck); else if (args.Contains("--membership-options")) await MembershipOptionDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--column-privileges")) await ColumnPrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--privilege-cancellation")) await PrivilegeCancellationDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--sequence-privileges")) await SequencePrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--privilege-commands")) { await PrivilegeCommandDialogChecks.RunAsync(testConnection,testCheck); await CascadePrivilegeDialogChecks.RunAsync(testConnection,testCheck); await GrantorAuthorityDialogChecks.RunAsync(testConnection,testCheck); await PrivilegeIdentityDialogChecks.RunAsync(testConnection,testCheck); } else if (args.Contains("--effective-privileges")) await EffectivePrivilegeDialogChecks.RunAsync(testConnection,testCheck); else if (args.Contains("--copy")) { await ResultCopyChecks.RunAsync(testConnection,testCheck); await QueryResultCopyChecks.RunAsync(testConnection,testCheck); } else if (args.Contains("--sequence-owner")) { await SequenceAttachChecks.RunAsync(testConnection,testCheck); await SequenceApplyCancellationChecks.RunAsync(testConnection,testCheck); } else if (args.Contains("--sequence-state")) { await SequenceRestartChecks.RunAsync(testConnection, testCheck); await SequenceDetachChecks.RunAsync(testConnection, testCheck); } else if (args.Contains("--open-long-line")) await SqlOpenLoadChecks.RunAsync(testCheck, "long-line"); else if (args.Contains("--open-large")) await SqlOpenLoadChecks.RunAsync(testCheck, "near-16MiB"); else if (args.Contains("--open-load")) await SqlOpenLoadChecks.RunAsync(testCheck); else if (args.Contains("--recovery-save")) await RecoverySaveMeasurementChecks.RunAsync(testCheck); else if (args.Contains("--recovery-load")) await RecoveryLoadChecks.RunAsync(testCheck); else if (args.Contains("--recovery")) await QueryRecoveryChecks.RunAsync(testCheck); else if (args.Contains("--files")) await SqlFileChecks.RunAsync(testCheck); else if (args.Contains("--properties")) await ObjectPropertyChecks.RunAsync(testConnection, testCheck); else if (args.Contains("--explorer")) await ExplorerExperienceChecks.RunAsync(testConnection, testCheck); else if (args.Contains("--replace")) await ReplaceChecks.RunAsync(testCheck); else if (args.Contains("--streaming")) await QueryStreamingChecks.RunAsync(testConnection, testCheck); else if (args.Contains("--connection")) await ConnectionExperienceChecks.RunAsync(testConnection, testCheck); else if (args.Contains("--accessibility")) await AdministrativeAccessibilityChecks.RunAsync(testConnection, testCheck); else if (args.Contains("--sequences")) await SequenceChecks.RunAsync(testConnection, testCheck); else await AdvancedIndexChecks.RunAsync(testConnection, testCheck); }
                    await RunFocused($"Host=127.0.0.1;Port={TestClusterEndpoint.Port};Database=postgres;Username=pgms_test;SSL Mode=Disable;Pooling=false", (ok, text) =>
                    {
                        if (!ok) throw new Exception("FAILED: " + text);
                        Console.WriteLine("PASS: " + text); count++;
                    });
                    Console.WriteLine($"{count} focused desktop checks passed."); app.Shutdown(0);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
            };
            app.Run(); return;
        }
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        window.Loaded += async (_, _) =>
        {
            int passed = 0;
            void Check(bool condition, string name)
            {
                if (!condition) throw new Exception("FAILED: " + name);
                Console.WriteLine("PASS: " + name); passed++;
            }
            try
            {
                ApplicationCommands.New.Execute(null, window);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var manager = (DockingManager)window.FindName("DockManager");
                var document = manager.Layout.Descendents().OfType<LayoutDocument>().Single();
                var query = (QueryView)document.Content;
                var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
                Check(manager.ActiveContent == query, "New Query activates its document");
                Check(editor.IsKeyboardFocusWithin, "New Query moves keyboard focus into the editor");
                Check(ApplicationCommands.Save.CanExecute(null, window), "Save command enabled for active query");
                editor.Text = "SELECT 'PGMS' AS product, NULL::text AS missing; SELECT 17 AS version;";
                Check(query.IsDirty && document.Title.EndsWith('*'), "editing marks the document dirty");
                await query.ConnectAsync($"Host=127.0.0.1;Port={TestClusterEndpoint.Port};Database=postgres;Username=pgms_test;SSL Mode=Disable;Pooling=false;Timeout=3");
                await query.ExecuteAsync();
                var sets = (TabControl)query.FindName("ResultSets");
                Check(sets.Items.Count == 2, "query UI shows multiple result grids");
                var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
                Check(grid.Items.Count == 1 && grid.Columns.Count == 2, "result grid binds rows and columns");
                Check(((Button)query.FindName("ExportCsvButton")).IsEnabled, "CSV export enabled after result selection");
                string exported = Path.GetFullPath("artifacts/review/m8-result.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(exported)!);
                sets.SelectedIndex = 1;
                await query.ExportSelectedCsvAsync(exported, overwrite: true);
                Check(await File.ReadAllTextAsync(exported) == "\"version\"\r\n\"17\"\r\n", "WPF export writes only the selected result set");
                sets.SelectedIndex = 0;
                await query.ExportSelectedCsvAsync(exported, overwrite: true);
                Check((await File.ReadAllTextAsync(exported)).Contains("\"PGMS\",\\N"), "WPF export uses data values rather than visual NULL text");
                query.UpdateLayout();
                var exportSurface = (FrameworkElement)query.Content;
                var exportBounds = new Rect(0, 0, exportSurface.ActualWidth, exportSurface.ActualHeight);
                var exportDrawing = new DrawingVisual();
                using (var dc = exportDrawing.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, exportBounds); dc.DrawRectangle(new VisualBrush(exportSurface), null, exportBounds); }
                var exportBitmap = new RenderTargetBitmap((int)Math.Ceiling(exportBounds.Width), (int)Math.Ceiling(exportBounds.Height), 96, 96, PixelFormats.Pbgra32);
                exportBitmap.Render(exportDrawing);
                var exportEncoder = new PngBitmapEncoder(); exportEncoder.Frames.Add(BitmapFrame.Create(exportBitmap));
                using (var capture = File.Create("artifacts/review/m8-csv-results.png")) exportEncoder.Save(capture);
                editor.Select(0, editor.Text.IndexOf(';') + 1);
                await query.ExecuteAsync();
                Check(sets.Items.Count == 1, "Execute uses selected SQL only");
                editor.Select(0, 0);
                document.Float();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(document.IsFloating, "query document can float");
                document.DockAsDocument();
                Check(!document.IsFloating, "floating query can dock again");
                using var writer = new StringWriter();
                new XmlLayoutSerializer(manager).Serialize(writer);
                Check(writer.ToString().Contains("explorer"), "layout persistence includes Object Explorer");
                var serializer = new XmlLayoutSerializer(manager);
                serializer.LayoutSerializationCallback += (_, args) =>
                {
                    args.Content = args.Model.ContentId == "explorer" ? window.FindName("ExplorerContent") : query;
                };
                serializer.Deserialize(new StringReader(writer.ToString()));
                Check(manager.Layout.Descendents().OfType<LayoutDocument>().Count() == 1, "layout roundtrip restores query pane");
                query.IsDirty = false;
                await query.DisposeAsync();
                string fixtureConnection = $"Host=127.0.0.1;Port={TestClusterEndpoint.Port};Database=postgres;Username=pgms_test;SSL Mode=Require;Pooling=false;Timeout=3";
                var connectionTestDialog = new ConnectionDialog { ShowInTaskbar = false };
                connectionTestDialog.Loaded += async (_, _) =>
                {
                    ((TextBox)connectionTestDialog.FindName("Host")).Text = "127.0.0.1";
                    ((TextBox)connectionTestDialog.FindName("Port")).Text = TestClusterEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ((TextBox)connectionTestDialog.FindName("Database")).Text = "postgres";
                    ((TextBox)connectionTestDialog.FindName("Username")).Text = "pgms_test";
                    ((ComboBox)connectionTestDialog.FindName("Ssl")).SelectedItem = ((ComboBox)connectionTestDialog.FindName("Ssl")).Items.OfType<ComboBoxItem>().Single(i => (string)i.Content == "Require");
                    await connectionTestDialog.TestConnectionAsync();
                    Check(((TextBlock)connectionTestDialog.FindName("Error")).Text.Contains("Connection succeeded", StringComparison.Ordinal) && ((TextBlock)connectionTestDialog.FindName("Error")).Text.Contains("TLS: Yes", StringComparison.Ordinal) && ((TextBlock)connectionTestDialog.FindName("Error")).Foreground == Brushes.SeaGreen, "connection dialog Test shows server and TLS diagnostics with success styling");
                    CaptureWindow(connectionTestDialog, "artifacts/review/m21-connection-test.png");
                    ((TextBox)connectionTestDialog.FindName("Database")).Text = "pgms_missing_test_connection";
                    await connectionTestDialog.TestConnectionAsync();
                    Check(((TextBlock)connectionTestDialog.FindName("Error")).Text.Contains("3D000", StringComparison.Ordinal), "connection dialog Test displays PostgreSQL SQLSTATE on failure");
                    connectionTestDialog.Close();
                };
                connectionTestDialog.ShowDialog();
                var catalog = new CatalogService(fixtureConnection);
                var schemaNode = new CatalogNode(new(CatalogKind.Schemas, "Schemas", "postgres"), catalog);
                Check(schemaNode.Children.Single().Name == "Expand to load…", "catalog node starts unloaded");
                await schemaNode.LoadAsync();
                Check(schemaNode.Children.Any(n => n.Name == "public"), "expanding node loads actual schemas");
                var missing = new CatalogNode(new(CatalogKind.Schemas, "Schemas", "pgms_missing_" + Guid.NewGuid().ToString("N")), catalog);
                await missing.LoadAsync();
                Check(missing.Error?.Contains("3D000") == true, "catalog failure is reported without crashing UI");
                var createDialog = new CreateObjectDialog(CreateObjectKind.Schema, fixtureConnection) { ShowInTaskbar = false };
                createDialog.Loaded += (_, _) => createDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        var name = (TextBox)createDialog.FindName("ObjectName");
                        var preview = (TextBox)createDialog.FindName("SqlPreview");
                        var create = (Button)createDialog.FindName("CreateButton");
                        Check(!create.IsEnabled, "empty administrative name disables creation");
                        name.Text = "pgms_m2_script_only";
                        Check(preview.Text == "CREATE SCHEMA \"pgms_m2_script_only\";", "dialog previews exact SQL before action");
                        createDialog.UpdateLayout();
                        var surface = (FrameworkElement)createDialog.Content;
                        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                        var drawing = new DrawingVisual();
                        using (var dc = drawing.RenderOpen())
                        {
                            dc.DrawRectangle(createDialog.Background, null, bounds);
                            dc.DrawRectangle(new VisualBrush(surface), null, bounds);
                        }
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(drawing);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        Directory.CreateDirectory("artifacts/review");
                        using (var capture = File.Create("artifacts/review/m2-create-schema.png")) encoder.Save(capture);
                        ((Button)createDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch { createDialog.Close(); throw; }
                });
                createDialog.ShowDialog();
                Check(createDialog.ScriptSql == "CREATE SCHEMA \"pgms_m2_script_only\";" && !createDialog.WasCreated, "Script returns SQL without executing");
                var afterScript = await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
                Check(afterScript.All(s => s.Name != "pgms_m2_script_only"), "Script action creates no server object");
                foreach (bool restoreMode in new[] { false, true })
                {
                    var backupDialog = new BackupDialog(restoreMode, fixtureConnection) { ShowInTaskbar = false };
                    backupDialog.Loaded += (_, _) => backupDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        ((Button)backupDialog.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(((TextBox)backupDialog.FindName("Feedback")).Text.Contains("Select a backup path"), "backup/restore validates missing path before starting");
                        Check(((TextBlock)backupDialog.FindName("Target")).Text.Contains("Database: postgres"), "backup/restore shows the exact destination database");
                        backupDialog.UpdateLayout();
                        var surface = (FrameworkElement)backupDialog.Content;
                        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                        var drawing = new DrawingVisual();
                        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(backupDialog.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(drawing);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var capture = File.Create("artifacts/review/m3-" + (restoreMode ? "restore" : "backup") + ".png")) encoder.Save(capture);
                        backupDialog.Close();
                    });
                    backupDialog.ShowDialog();
                }
                var roleDialog = new CreateRoleDialog(fixtureConnection) { ShowInTaskbar = false };
                roleDialog.Loaded += (_, _) =>
                {
                    var memberships = (ListBox)roleDialog.FindName("Memberships");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    timer.Tick += (_, _) =>
                    {
                        if (memberships.Items.Count == 0) return;
                        timer.Stop();
                        try
                        {
                            var roleName = (TextBox)roleDialog.FindName("RoleName");
                            var preview = (TextBox)roleDialog.FindName("SqlPreview");
                            Check(!((Button)roleDialog.FindName("CreateButton")).IsEnabled, "empty role name disables creation");
                            roleName.Text = "pgms_m4_script_only";
                            ((CheckBox)roleDialog.FindName("CreateDatabase")).IsChecked = true;
                            ((TextBox)roleDialog.FindName("ConnectionLimit")).Text = "4";
                            var membership = memberships.Items.OfType<CheckBox>().First(c => (string)c.Content == "pgms_test");
                            membership.IsChecked = true;
                            memberships.ScrollIntoView(membership);
                            Check(preview.Text.Contains("CREATEDB") && preview.Text.Contains("CONNECTION LIMIT 4") && preview.Text.Contains("GRANT \"pgms_test\" TO \"pgms_m4_script_only\";"), "role dialog previews attributes and membership");
                            roleDialog.UpdateLayout();
                            var surface = (FrameworkElement)roleDialog.Content;
                            var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                            var drawing = new DrawingVisual();
                            using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(roleDialog.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(drawing);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using (var capture = File.Create("artifacts/review/m4-new-role.png")) encoder.Save(capture);
                            ((Button)roleDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch { roleDialog.Close(); throw; }
                    };
                    timer.Start();
                };
                roleDialog.ShowDialog();
                Check(roleDialog.ScriptSql?.Contains("CREATE ROLE \"pgms_m4_script_only\"") == true && !roleDialog.WasCreated, "role Script returns SQL without execution");
                var roleService = new RoleAdministrationService(fixtureConnection);
                Check(!(await roleService.ListRolesAsync()).Contains("pgms_m4_script_only"), "role Script creates no server role");
                var duplicateRoleDialog = new CreateRoleDialog(fixtureConnection) { ShowInTaskbar = false };
                duplicateRoleDialog.Loaded += (_, _) =>
                {
                    var memberships = (ListBox)duplicateRoleDialog.FindName("Memberships");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    timer.Tick += (_, _) =>
                    {
                        if (memberships.Items.Count == 0) return;
                        timer.Stop();
                        ((TextBox)duplicateRoleDialog.FindName("RoleName")).Text = "pgms_test";
                        ((Button)duplicateRoleDialog.FindName("CreateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var completion = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                        completion.Tick += (_, _) =>
                        {
                            if (((TextBlock)duplicateRoleDialog.FindName("Progress")).Text != "Creation did not complete normally") return;
                            completion.Stop();
                            Check(((TextBlock)duplicateRoleDialog.FindName("Feedback")).Text.Contains("42710"), "duplicate role keeps SQLSTATE visible after controls are restored");
                            Check(((Button)duplicateRoleDialog.FindName("CreateButton")).IsEnabled, "role controls re-enabled after server error");
                            duplicateRoleDialog.Close();
                        };
                        completion.Start();
                    };
                    timer.Start();
                };
                duplicateRoleDialog.ShowDialog();

                string missingDatabaseConnection = new Npgsql.NpgsqlConnectionStringBuilder(fixtureConnection) { Database = "pgms_missing_ui" }.ConnectionString;
                var loadFailureDialog = new CreateRoleDialog(missingDatabaseConnection) { ShowInTaskbar = false };
                loadFailureDialog.Loaded += (_, _) =>
                {
                    var completion = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    completion.Tick += (_, _) =>
                    {
                        if (((TextBlock)loadFailureDialog.FindName("Progress")).Text != "Membership list unavailable") return;
                        completion.Stop();
                        Check(((TextBlock)loadFailureDialog.FindName("Feedback")).Text.Contains("3D000"), "membership load failure remains visible alongside form validation");
                        loadFailureDialog.Close();
                    };
                    completion.Start();
                };
                loadFailureDialog.ShowDialog();
                var permissionDialog = new PrivilegeDialog(fixtureConnection) { ShowInTaskbar = false };
                permissionDialog.Loaded += (_, _) => permissionDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    ((ComboBox)permissionDialog.FindName("ObjectType")).SelectedItem = PrivilegeTarget.Table;
                    ((TextBox)permissionDialog.FindName("ObjectName")).Text = "sample";
                    ((TextBox)permissionDialog.FindName("RoleName")).Text = "pgms_test";
                    Check(((TextBox)permissionDialog.FindName("Preview")).Text == "GRANT SELECT ON TABLE \"public\".\"sample\" TO \"pgms_test\";", "permission dialog previews qualified and quoted SQL");
                    permissionDialog.UpdateLayout();
                    var surface = (FrameworkElement)permissionDialog.Content;
                    var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(permissionDialog.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var capture = File.Create("artifacts/review/m5-permissions.png")) encoder.Save(capture);
                    ((Button)permissionDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                permissionDialog.ShowDialog();
                Check(permissionDialog.ScriptSql is not null, "permission Script returns SQL for editor review");
                await using (var monitorFixture = new Npgsql.NpgsqlConnection(fixtureConnection + ";Application Name=PGMS.M6.fixture"))
                {
                    await monitorFixture.OpenAsync();
                    var monitor = new ActivityWindow(fixtureConnection) { ShowInTaskbar = false };
                    monitor.Show();
                    var sessions = (DataGrid)monitor.FindName("Sessions");
                    for (int attempt = 0; attempt < 100 && monitor.SnapshotVersion == 0; attempt++) await Task.Delay(25);
                    Check(sessions.Items.OfType<ActivitySession>().Any(s => s.Pid == monitorFixture.ProcessID), "monitor WPF displays actual backend session");
                    ((TextBox)monitor.FindName("DatabaseFilter")).Text = "POSTGRES";
                    ((TextBox)monitor.FindName("UserFilter")).Text = "pgms_test";
                    ((TextBox)monitor.FindName("ApplicationFilter")).Text = "PGMS.M6.fixture";
                    ((TextBox)monitor.FindName("PidFilter")).Text = monitorFixture.ProcessID.ToString();
                    await monitor.ApplyFiltersAsync();
                    Check(sessions.Items.OfType<ActivitySession>().Count() == 1 && ((ActivitySession)sessions.Items[0]).Pid == monitorFixture.ProcessID, "monitor applies database/user/application and PID filters");
                    ((TextBox)monitor.FindName("PidFilter")).Text = "bad";
                    await monitor.ApplyFiltersAsync();
                    Check(((TextBox)monitor.FindName("Status")).Text.Contains("positive integer", StringComparison.Ordinal), "monitor reports invalid PID filter inline");
                    ((TextBox)monitor.FindName("PidFilter")).Clear();
                    await monitor.ApplyFiltersAsync();
                    Check(!((Button)monitor.FindName("TerminateButton")).IsEnabled, "monitor cannot terminate without selection");
                    sessions.SelectedItem = sessions.Items.OfType<ActivitySession>().First(s => s.Pid == monitorFixture.ProcessID);
                    Check(((Button)monitor.FindName("CancelButton")).IsEnabled && ((Button)monitor.FindName("TerminateButton")).IsEnabled, "monitor exposes distinct cancel and terminate actions on selection");
                    var autoRefresh = (CheckBox)monitor.FindName("AutoRefreshCheck"); autoRefresh.IsChecked = true;
                    Check(monitor.IsAutoRefreshRunning && monitor.AutoRefreshInterval == TimeSpan.FromSeconds(5), "monitor auto-refresh starts at the selected interval");
                    int previousRows = monitor.SnapshotVersion;
                    for (int attempt = 0; attempt < 80 && previousRows == monitor.SnapshotVersion; attempt++) await Task.Delay(100);
                    Check(previousRows != monitor.SnapshotVersion, "monitor timer actually reloads sessions");
                    Check(sessions.SelectedItem is ActivitySession selectedSession && selectedSession.Pid == monitorFixture.ProcessID, "monitor refresh preserves selected backend identity");
                    var intervalSelector = (ComboBox)monitor.FindName("AutoRefreshIntervalSelector");
                    intervalSelector.SelectedIndex = 1;
                    Check(monitor.AutoRefreshInterval == TimeSpan.FromSeconds(10), "monitor applies changed auto-refresh interval");
                    autoRefresh.IsChecked = false;
                    Check(!monitor.IsAutoRefreshRunning, "monitor auto-refresh stops when unchecked");
                    monitor.UpdateLayout();
                    var surface = (FrameworkElement)monitor.Content;
                    var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(monitor.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var capture = File.Create("artifacts/review/m22-activity.png")) encoder.Save(capture);
                    monitor.Width = monitor.MinWidth; monitor.Height = monitor.MinHeight; monitor.UpdateLayout();
                    var filterFields = (FrameworkElement)monitor.FindName("FilterFields");
                    var selectorBounds = intervalSelector.TransformToAncestor(filterFields).TransformBounds(new Rect(intervalSelector.RenderSize));
                    Check(selectorBounds.Right <= filterFields.ActualWidth && selectorBounds.Bottom <= filterFields.ActualHeight, "monitor interval selector fits at minimum window size");
                    bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                    drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(monitor.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                    bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var capture = File.Create("artifacts/review/m22-activity-min.png")) encoder.Save(capture);
                    autoRefresh.IsChecked = true;
                    monitor.Close();
                    Check(!monitor.IsAutoRefreshRunning, "monitor stops auto-refresh when closed");
                }
                string profilePath = Path.GetFullPath("artifacts/desktop-test-state/profiles-" + Guid.NewGuid().ToString("N") + ".json");
                var profileStore = new ConnectionProfileStore(profilePath);
                var profile = new ConnectionProfile(Guid.NewGuid(), "Synthetic PG17", "127.0.0.1", TestClusterEndpoint.Port, "postgres", "pgms_test", "Disable");
                string syntheticSecret = "synthetic-" + Guid.NewGuid().ToString("N");
                profileStore.Save(profile.WithPassword(syntheticSecret, true));
                var persisted = new ConnectionProfileStore(profilePath).Load().Single();
                Check(persisted.RevealPassword() == syntheticSecret && !File.ReadAllText(profilePath).Contains(syntheticSecret), "DPAPI profile roundtrip persists no plaintext password");
                profileStore.Save(persisted.WithPassword("", false));
                Check(profileStore.Load().Single().ProtectedPassword is null, "saving without remember removes persisted secret");
                profileStore.Save(profile with { Name = "Updated" });
                Check(profileStore.Load().Count == 1 && profileStore.Load().Single().Name == "Updated", "profile update preserves identity without duplicates");
                profileStore.Delete(profile.Id);
                Check(profileStore.Load().Count == 0, "profile deletion removes saved settings");
                File.WriteAllText(profilePath, "invalid-json");
                bool corruptRejected = false;
                try { profileStore.Save(profile); } catch (System.Text.Json.JsonException) { corruptRejected = true; }
                Check(corruptRejected && File.ReadAllText(profilePath) == "invalid-json", "corrupt profile file is preserved when saving fails");
                var protectedProfile = profile.WithPassword(syntheticSecret, true);
                bool wrongIdentityRejected = false;
                try { (protectedProfile with { Id = Guid.NewGuid() }).RevealPassword(); } catch (System.Security.Cryptography.CryptographicException) { wrongIdentityRejected = true; }
                Check(wrongIdentityRejected, "DPAPI protected password is bound to profile identity");

                var sharedStore = new ConnectionProfileStore(ConnectionProfileStore.DefaultPath);
                sharedStore.Save(profile);
                var connectDialog = new ConnectionDialog { ShowInTaskbar = false };
                connectDialog.Loaded += (_, _) => connectDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var profiles = (ComboBox)connectDialog.FindName("Profiles");
                    profiles.SelectedItem = profiles.Items.OfType<ConnectionProfile>().Single(p => p.Id == profile.Id);
                    Check(((TextBox)connectDialog.FindName("Host")).Text == "127.0.0.1" && ((TextBox)connectDialog.FindName("Port")).Text == TestClusterEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "selecting profile fills connection fields");
                    Check(((CheckBox)connectDialog.FindName("RememberPassword")).IsChecked == false && ((PasswordBox)connectDialog.FindName("Password")).Password == "", "profile without saved secret requires password input");
                    connectDialog.UpdateLayout();
                    var surface = (FrameworkElement)connectDialog.Content;
                    var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(connectDialog.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var capture = File.Create("artifacts/review/m7-profiles.png")) encoder.Save(capture);
                    connectDialog.Close();
                });
                connectDialog.ShowDialog(); sharedStore.Delete(profile.Id);
                var roleEditDialog = new RoleAttributeDialog(fixtureConnection) { ShowInTaskbar = false };
                roleEditDialog.Loaded += async (_, _) =>
                {
                    Check(!((Button)roleEditDialog.FindName("ApplyButton")).IsEnabled, "role edit cannot apply invalid fields");
                    ((TextBox)roleEditDialog.FindName("RoleName")).Text = "pgms_test";
                    ((TextBox)roleEditDialog.FindName("AttributeValue")).Text = "false";
                    await roleEditDialog.InspectAsync();
                    Check(((TextBox)roleEditDialog.FindName("Inspection")).Text.Contains("LOGIN = True"), "role edit dialog inspects actual attributes");
                    CaptureWindow(roleEditDialog, "artifacts/review/m9-role-properties.png");
                    ((Button)roleEditDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                roleEditDialog.ShowDialog();
                Check(roleEditDialog.ScriptSql == "ALTER ROLE \"pgms_test\" WITH NOLOGIN;", "role edit Script returns one exact attribute change");
                Check((await new AdministrativeEditService(fixtureConnection).InspectRoleAsync("pgms_test")).Contains("LOGIN = True"), "role edit Script leaves server attributes unchanged");
                var renameRoleDialog = new RoleRenameDialog(fixtureConnection, "pgms_role_script") { ShowInTaskbar = false };
                renameRoleDialog.Loaded += (_, _) =>
                {
                    ((TextBox)renameRoleDialog.FindName("NewName")).Text = "pgms_role_script_new";
                    Check(((TextBox)renameRoleDialog.FindName("Preview")).Text == "ALTER ROLE \"pgms_role_script\" RENAME TO \"pgms_role_script_new\";", "role rename dialog previews exact SQL");
                    CaptureWindow(renameRoleDialog, "artifacts/review/m18-role-rename.png");
                    ((Button)renameRoleDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                renameRoleDialog.ShowDialog();
                Check(renameRoleDialog.ScriptSql == "ALTER ROLE \"pgms_role_script\" RENAME TO \"pgms_role_script_new\";" && !renameRoleDialog.WasApplied, "role rename Script returns SQL without changing server state");
                var dropRoleDialog = new RoleDropDialog(fixtureConnection, "pgms_drop_script") { ShowInTaskbar = false };
                dropRoleDialog.Loaded += (_, _) =>
                {
                    Check(((TextBox)dropRoleDialog.FindName("Preview")).Text == "DROP ROLE \"pgms_drop_script\";", "role drop dialog previews RESTRICT default SQL without CASCADE");
                    CaptureWindow(dropRoleDialog, "artifacts/review/m19-role-drop.png");
                    ((Button)dropRoleDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                dropRoleDialog.ShowDialog();
                Check(dropRoleDialog.ScriptSql == "DROP ROLE \"pgms_drop_script\";" && !dropRoleDialog.WasApplied, "role drop Script does not execute deletion");
                await BackupFormatDialogChecks.RunAsync(fixtureConnection, Check);
                await ActivityDialogChecks.RunAsync(fixtureConnection, Check);
                await QueryStatisticsDialogChecks.RunAsync(fixtureConnection, Check);
                await DataGridRowNameChecks.RunAsync(Check);
                await ResultExportUiChecks.RunAsync(Check);
                await GlobalBackupDialogChecks.RunAsync(fixtureConnection, Check);
                await ColumnTypeDialogChecks.RunAsync(fixtureConnection,Check);await EnumDialogChecks.RunAsync(fixtureConnection,Check);await DomainDialogChecks.RunAsync(fixtureConnection,Check);await CreateTypeDialogChecks.RunAsync(fixtureConnection,Check);await ConcurrentIndexStoreChecks.RunAsync(fixtureConnection,Check);await ConcurrentIndexDialogChecks.RunAsync(fixtureConnection,Check);await ExpressionIndexDialogChecks.RunAsync(fixtureConnection,Check);await RangeExclusionDialogChecks.RunAsync(fixtureConnection,Check);await PartitionTableDialogChecks.RunAsync(fixtureConnection, Check);await PartitionOperationDialogChecks.RunAsync(fixtureConnection, Check);
                await IdentityColumnDialogChecks.RunAsync(fixtureConnection, Check);
                await IdentityColumnOperationDialogChecks.RunAsync(fixtureConnection, Check);
                await IdentityColumnCancellationChecks.RunAsync(fixtureConnection, Check);
                await RestoreOwnershipDialogChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeCommitStoreChecks.RunAsync(fixtureConnection, Check);
                await DefaultPrivilegeProcessChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeCommitProcessChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeCommitProcessChecks.RunAsync(fixtureConnection, Check,true);
                await RoutineBoundaryChecks.RunAsync(fixtureConnection,Check);
                await DefaultPrivilegeJournalChecks.RunAsync(fixtureConnection,Check);
                await DefaultPrivilegeDialogChecks.RunAsync(fixtureConnection,Check);
                await DefaultPrivilegeCancellationChecks.RunAsync(fixtureConnection,Check);
                await MaintainPrivilegeDialogChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeCommitDialogChecks.RunAsync(fixtureConnection, Check);
                await UnicodePasswordDialogChecks.RunAsync(fixtureConnection, Check);
                var rolePasswordDialog = new RolePasswordDialog(fixtureConnection, "pgms_password_ui") { ShowInTaskbar = false };
                bool matchingPasswordEnabled = false, mismatchDisabled = false;
                rolePasswordDialog.Loaded += (_, _) =>
                {
                    string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    ((PasswordBox)rolePasswordDialog.FindName("Password")).Password = secret;
                    ((PasswordBox)rolePasswordDialog.FindName("ConfirmPassword")).Password = secret;
                    matchingPasswordEnabled = ((Button)rolePasswordDialog.FindName("ApplyButton")).IsEnabled;
                    CaptureWindow(rolePasswordDialog, "artifacts/review/m20-role-password.png");
                    ((PasswordBox)rolePasswordDialog.FindName("ConfirmPassword")).Password = secret + "x";
                    mismatchDisabled = !((Button)rolePasswordDialog.FindName("ApplyButton")).IsEnabled;
                    rolePasswordDialog.Close();
                };
                rolePasswordDialog.ShowDialog();
                Check(matchingPasswordEnabled && mismatchDisabled && !rolePasswordDialog.WasApplied && ((PasswordBox)rolePasswordDialog.FindName("Password")).Password.Length == 0, "password dialog requires match, keeps secret masked, and clears fields on close");
                var membershipDialog = new RoleMembershipDialog(fixtureConnection) { ShowInTaskbar = false };
                membershipDialog.Loaded += async (_, _) =>
                {
                    ((TextBox)membershipDialog.FindName("MemberRole")).Text = "pgms_test";
                    ((TextBox)membershipDialog.FindName("GrantedRole")).Text = "pgms_test";
                    Check(!((Button)membershipDialog.FindName("ApplyButton")).IsEnabled, "membership dialog rejects self membership");
                    ((TextBox)membershipDialog.FindName("GrantedRole")).Text = "pgms_missing_m10";
                    await membershipDialog.ApplyAsync();
                    Check(((TextBlock)membershipDialog.FindName("Feedback")).Text.Contains("42704") && ((Button)membershipDialog.FindName("ApplyButton")).IsEnabled, "membership server failure stays visible after controls re-enable");
                    ((TextBox)membershipDialog.FindName("GrantedRole")).Text = "pg_read_all_data";
                    ((CheckBox)membershipDialog.FindName("Revoke")).IsChecked = true;
                    await membershipDialog.InspectAsync();
                    Check(((TextBox)membershipDialog.FindName("Inspection")).Text.Length > 0, "membership dialog inspects current member");
                    CaptureWindow(membershipDialog, "artifacts/review/m10-memberships.png");
                    ((Button)membershipDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                membershipDialog.ShowDialog();
                Check(membershipDialog.ScriptSql == "REVOKE \"pg_read_all_data\" FROM \"pgms_test\" RESTRICT;", "membership Script returns exact restrictive revoke");
                string uiSchema = "pgms_ui_" + Guid.NewGuid().ToString("N");
                string uiRenamed = uiSchema + "_new";
                await new AdministrationService(fixtureConnection).CreateAsync(new(CreateObjectKind.Schema, uiSchema));
                try
                {
                    var schemaDialog = new SchemaEditDialog(fixtureConnection, uiSchema) { ShowInTaskbar = false };
                    schemaDialog.Loaded += async (_, _) =>
                    {
                        Check(!((Button)schemaDialog.FindName("ApplyButton")).IsEnabled, "schema rename requires valid new name");
                        ((TextBox)schemaDialog.FindName("NewName")).Text = uiRenamed;
                        Check(((TextBox)schemaDialog.FindName("Preview")).Text == $"ALTER SCHEMA \"{uiSchema}\" RENAME TO \"{uiRenamed}\";", "schema dialog previews exact quoted rename");
                        CaptureWindow(schemaDialog, "artifacts/review/m11-schema-rename.png");
                        await schemaDialog.ApplyAsync();
                    };
                    schemaDialog.ShowDialog();
                    var schemasAfterRename = await new CatalogService(fixtureConnection).ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
                    Check(schemaDialog.WasApplied && schemasAfterRename.Any(s => s.Name == uiRenamed), "schema Apply in WPF renames actual fixture schema");
                    var dropDialog = new SchemaEditDialog(fixtureConnection, uiRenamed) { ShowInTaskbar = false };
                    dropDialog.Loaded += (_, _) => dropDialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        ((ComboBox)dropDialog.FindName("Operation")).SelectedItem = SchemaOperation.Drop;
                        Check(!((TextBox)dropDialog.FindName("NewName")).IsEnabled && ((TextBox)dropDialog.FindName("Preview")).Text.EndsWith(" RESTRICT;"), "schema drop disables rename field and uses RESTRICT");
                        CaptureWindow(dropDialog, "artifacts/review/m11-schema-drop.png");
                        ((Button)dropDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    dropDialog.ShowDialog();
                    schemasAfterRename = await new CatalogService(fixtureConnection).ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
                    Check(dropDialog.ScriptSql == $"DROP SCHEMA \"{uiRenamed}\" RESTRICT;" && schemasAfterRename.Any(s => s.Name == uiRenamed), "schema DROP Script does not execute deletion");
                }
                finally
                {
                    await using var cleanup = new QuerySession(fixtureConnection); await cleanup.OpenAsync();
                    await cleanup.ExecuteAsync($"DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(uiSchema)} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(uiRenamed)} CASCADE;");
                }
                string uiTable = "pgms_table_" + Guid.NewGuid().ToString("N");
                var tableDialog = new CreateTableDialog(fixtureConnection) { ShowInTaskbar = false };
                tableDialog.Loaded += async (_, _) =>
                {
                    ((TextBox)tableDialog.FindName("TableName")).Text = uiTable;
                    Check(!((Button)tableDialog.FindName("ApplyButton")).IsEnabled, "table creation requires at least one column");
                    ((TextBox)tableDialog.FindName("ColumnName")).Text = "id";
                    ((CheckBox)tableDialog.FindName("Nullable")).IsChecked = false;
                    ((Button)tableDialog.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(((TextBox)tableDialog.FindName("Preview")).Text.Contains("\"id\" integer NOT NULL"), "table column Add updates exact SQL preview");
                    ((TextBox)tableDialog.FindName("ColumnName")).Text = "note";
                    ((ComboBox)tableDialog.FindName("DataType")).SelectedItem = ColumnType.Text;
                    ((CheckBox)tableDialog.FindName("Nullable")).IsChecked = true;
                    ((Button)tableDialog.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    CaptureWindow(tableDialog, "artifacts/review/m12-new-table.png");
                    await tableDialog.ApplyAsync();
                };
                try
                {
                    tableDialog.ShowDialog();
                    var createdTables = await new CatalogService(fixtureConnection).ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", "public"));
                    Check(tableDialog.WasApplied && createdTables.Any(t => t.Name == uiTable), "table dialog Create persists real table");
                    string uiIndex = "pgms_idx_" + Guid.NewGuid().ToString("N");
                    var indexDialog = new RelationEditDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    indexDialog.Loaded += async (_, _) =>
                    {
                        ((TextBox)indexDialog.FindName("ObjectName")).Text = uiIndex;
                        Check(!((Button)indexDialog.FindName("ApplyButton")).IsEnabled, "index creation requires column names");
                        ((TextBox)indexDialog.FindName("Columns")).Text = "id\nnote";
                        ((CheckBox)indexDialog.FindName("Unique")).IsChecked = true;
                        CaptureWindow(indexDialog, "artifacts/review/m13-index-create.png");
                        await indexDialog.ApplyAsync();
                    };
                    indexDialog.ShowDialog();
                    var uiEntry = createdTables.Single(t => t.Name == uiTable);
                    Check(indexDialog.WasApplied && (await new CatalogService(fixtureConnection).ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", "public", uiEntry.Oid))).Any(i => i.Name == uiIndex), "index Apply persists real index from WPF");
                    var dropIndexDialog = new RelationEditDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    dropIndexDialog.Loaded += (_, _) =>
                    {
                        ((TextBox)dropIndexDialog.FindName("ObjectName")).Text = uiIndex;
                        ((ComboBox)dropIndexDialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                        Check(!((TextBox)dropIndexDialog.FindName("Columns")).IsEnabled && ((TextBox)dropIndexDialog.FindName("TableName")).IsEnabled, "index drop disables columns and permits table selection for inspection");
                        CaptureWindow(dropIndexDialog, "artifacts/review/m13-index-drop.png");
                        ((Button)dropIndexDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                    dropIndexDialog.ShowDialog();
                    Check(dropIndexDialog.ScriptSql == $"DROP INDEX \"public\".\"{uiIndex}\" RESTRICT;" && (await new CatalogService(fixtureConnection).ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", "public", uiEntry.Oid))).Any(i => i.Name == uiIndex), "DROP INDEX Script returns exact SQL without execution");
                    string uiPrimaryKey = "pgms_pk_" + Guid.NewGuid().ToString("N");
                    var primaryKeyDialog = new TableConstraintDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    primaryKeyDialog.Loaded += async (_, _) =>
                    {
                        ((TextBox)primaryKeyDialog.FindName("ObjectName")).Text = uiPrimaryKey;
                        ((TextBox)primaryKeyDialog.FindName("Columns")).Text = "id";
                        Check(((TextBox)primaryKeyDialog.FindName("Preview")).Text == $"ALTER TABLE \"public\".\"{uiTable}\" ADD CONSTRAINT \"{uiPrimaryKey}\" PRIMARY KEY (\"id\");", "constraint dialog previews exact primary key SQL");
                        CaptureWindow(primaryKeyDialog, "artifacts/review/m14-constraint-create.png");
                        await primaryKeyDialog.ApplyAsync();
                    };
                    primaryKeyDialog.ShowDialog();
                    var constraintService = new TableConstraintService(fixtureConnection);
                    var uiConstraints = await constraintService.InspectAsync("public", uiTable);
                    Check(primaryKeyDialog.WasApplied && uiConstraints.Any(c => c.Name == uiPrimaryKey && c.Kind == "PRIMARY KEY"), "primary key Apply creates a real table constraint from WPF");
                    string uiUnique = "pgms_unique_" + Guid.NewGuid().ToString("N");
                    var uniqueConstraintDialog = new TableConstraintDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    uniqueConstraintDialog.Loaded += async (_, _) =>
                    {
                        ((ComboBox)uniqueConstraintDialog.FindName("Operation")).SelectedValue = TableConstraintOperation.AddUnique;
                        ((TextBox)uniqueConstraintDialog.FindName("ObjectName")).Text = uiUnique;
                        ((TextBox)uniqueConstraintDialog.FindName("Columns")).Text = "note";
                        await uniqueConstraintDialog.ApplyAsync();
                    };
                    uniqueConstraintDialog.ShowDialog();
                    uiConstraints = await constraintService.InspectAsync("public", uiTable);
                    Check(uniqueConstraintDialog.WasApplied && uiConstraints.Any(c => c.Name == uiUnique && c.Kind == "UNIQUE"), "unique constraint Apply creates a real table constraint from WPF");
                    string uiCheck = "pgms_check_" + Guid.NewGuid().ToString("N");
                    var checkDialog = new CheckConstraintDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    checkDialog.Loaded += async (_, _) =>
                    {
                        ((TextBox)checkDialog.FindName("ObjectName")).Text = uiCheck;
                        ((TextBox)checkDialog.FindName("Expression")).Text = "id > 0";
                        Check(((TextBox)checkDialog.FindName("Preview")).Text == $"ALTER TABLE \"public\".\"{uiTable}\" ADD CONSTRAINT \"{uiCheck}\" CHECK (id > 0);", "CHECK dialog previews exact SQL");
                        CaptureWindow(checkDialog, "artifacts/review/m15-check-create.png");
                        await checkDialog.ApplyAsync();
                    };
                    checkDialog.ShowDialog();
                    Check(checkDialog.WasApplied && (await new CheckConstraintService(fixtureConnection).InspectAsync("public", uiTable)).Any(c => c.Name == uiCheck), "CHECK Apply creates a real table constraint from WPF");
                    string uiForeignKey = "pgms_fk_" + Guid.NewGuid().ToString("N");
                    var foreignKeyDialog = new ForeignKeyDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    foreignKeyDialog.Loaded += async (_, _) =>
                    {
                        ((TextBox)foreignKeyDialog.FindName("ObjectName")).Text = uiForeignKey;
                        ((TextBox)foreignKeyDialog.FindName("Columns")).Text = "id";
                        ((TextBox)foreignKeyDialog.FindName("ReferencedSchema")).Text = "public";
                        ((TextBox)foreignKeyDialog.FindName("ReferencedTable")).Text = uiTable;
                        ((TextBox)foreignKeyDialog.FindName("ReferencedColumns")).Text = "id";
                        ((ComboBox)foreignKeyDialog.FindName("OnDelete")).SelectedItem = ReferentialAction.Cascade;
                        Check(((TextBox)foreignKeyDialog.FindName("Preview")).Text.Contains("ON DELETE CASCADE", StringComparison.Ordinal), "foreign key dialog previews configured referential action");
                        CaptureWindow(foreignKeyDialog, "artifacts/review/m16-foreign-key-create.png");
                        await foreignKeyDialog.ApplyAsync();
                    };
                    foreignKeyDialog.ShowDialog();
                    Check(foreignKeyDialog.WasApplied && (await new ForeignKeyService(fixtureConnection).InspectAsync("public", uiTable)).Any(c => c.Name == uiForeignKey), "FOREIGN KEY Apply creates a real self-reference from WPF");
                    var databaseEditDialog = new DatabaseEditDialog(fixtureConnection, "pgms_ui_script_db") { ShowInTaskbar = false };
                    databaseEditDialog.Loaded += (_, _) =>
                    {
                        ((TextBox)databaseEditDialog.FindName("NewName")).Text = "pgms_ui_script_db_renamed";
                        Check(((TextBox)databaseEditDialog.FindName("Preview")).Text == "ALTER DATABASE \"pgms_ui_script_db\" RENAME TO \"pgms_ui_script_db_renamed\";", "database dialog previews exact rename SQL");
                        CaptureWindow(databaseEditDialog, "artifacts/review/m17-database-rename.png");
                        ((Button)databaseEditDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                    databaseEditDialog.ShowDialog();
                    Check(databaseEditDialog.ScriptSql == "ALTER DATABASE \"pgms_ui_script_db\" RENAME TO \"pgms_ui_script_db_renamed\";" && !databaseEditDialog.WasApplied, "database Script returns SQL without applying the rename");
                    var dropConstraintDialog = new TableConstraintDialog(fixtureConnection, "public", uiTable) { ShowInTaskbar = false };
                    dropConstraintDialog.Loaded += async (_, _) =>
                    {
                        ((ComboBox)dropConstraintDialog.FindName("Operation")).SelectedValue = TableConstraintOperation.Drop;
                        await dropConstraintDialog.InspectAsync();
                        var list = (ListBox)dropConstraintDialog.FindName("ExistingConstraints");
                        Check(list.Items.Count == 2, "constraint inspection lists existing primary key and unique constraints");
                        list.SelectedItem = list.Items.Cast<TableConstraintInfo>().Single(c => c.Name == uiUnique);
                        Check(((TextBox)dropConstraintDialog.FindName("ObjectName")).Text == uiUnique, "selecting inspected constraint fills its exact name");
                        CaptureWindow(dropConstraintDialog, "artifacts/review/m14-constraint-drop.png");
                        ((Button)dropConstraintDialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                    dropConstraintDialog.ShowDialog();
                    Check(dropConstraintDialog.ScriptSql == $"ALTER TABLE \"public\".\"{uiTable}\" DROP CONSTRAINT \"{uiUnique}\" RESTRICT;" && (await constraintService.InspectAsync("public", uiTable)).Any(c => c.Name == uiUnique), "constraint DROP Script uses RESTRICT without executing");
                }
                finally
                {
                    await using var cleanup = new QuerySession(fixtureConnection); await cleanup.OpenAsync();
                    await cleanup.ExecuteAsync($"DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(uiTable)};");
                }
                await TableEditChecks.RunAsync(fixtureConnection, Check);
                await TablePropertyChecks.RunAsync(fixtureConnection, Check);
                await InspectionEditChecks.RunAsync(fixtureConnection, Check);
                await ExplorerContextChecks.RunAsync(window, fixtureConnection, Check);
                await ConstraintIdentityChecks.RunAsync(fixtureConnection, Check);
                await ConstraintCancellationChecks.RunAsync(fixtureConnection, Check);
                await ConstraintValidationChecks.RunAsync(fixtureConnection, Check);
                await IndexInspectionChecks.RunAsync(fixtureConnection, Check);
            await AdvancedIndexChecks.RunAsync(fixtureConnection, Check);
                await SequenceChecks.RunAsync(fixtureConnection, Check);
                await AdministrativeAccessibilityChecks.RunAsync(fixtureConnection, Check);
                await ConnectionExperienceChecks.RunAsync(fixtureConnection, Check);
                await QueryStreamingChecks.RunAsync(fixtureConnection, Check);
                await QueryPlanUiChecks.RunAsync(fixtureConnection, Check);
                await QuerySessionUiChecks.RunAsync(fixtureConnection, Check);
                await EffectivePrivilegeDialogChecks.RunAsync(fixtureConnection, Check);
                await CascadeJournalChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeCommandDialogChecks.RunAsync(fixtureConnection, Check);
                await CascadePrivilegeDialogChecks.RunAsync(fixtureConnection, Check);
                await GrantorAuthorityDialogChecks.RunAsync(fixtureConnection, Check);
                await PrivilegeIdentityDialogChecks.RunAsync(fixtureConnection, Check);
                await RoutinePrivilegeDialogChecks.RunAsync(fixtureConnection, Check);
                await MembershipOptionDialogChecks.RunAsync(fixtureConnection,Check);
                await ColumnPrivilegeDialogChecks.RunAsync(fixtureConnection,Check);
                await PrivilegeCancellationDialogChecks.RunAsync(fixtureConnection,Check);
                await SequencePrivilegeDialogChecks.RunAsync(fixtureConnection, Check);
                await ResultCopyChecks.RunAsync(fixtureConnection, Check);
                await QueryResultCopyChecks.RunAsync(fixtureConnection, Check);
                await ReplaceChecks.RunAsync(Check);
                await HighlightingChecks.RunAsync(Check);
                await SqlLinkChecks.RunAsync(Check);
                await QueryTextSnapshotChecks.RunAsync(Check);
                await SqlFileChecks.RunAsync(Check);
                await QueryRecoveryChecks.RunAsync(Check);
                await ExplorerExperienceChecks.RunAsync(fixtureConnection, Check);
                await ObjectPropertyChecks.RunAsync(fixtureConnection, Check);
                await IndexCancellationChecks.RunAsync(fixtureConnection, Check);
                Console.WriteLine($"{passed} desktop checks passed.");
                Environment.ExitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally
            {
                // Tests never prompt to save their synthetic document.
                var manager = (DockingManager)window.FindName("DockManager");
                foreach (var doc in manager.Layout.Descendents().OfType<LayoutDocument>())
                    if (doc.Content is QueryView q) q.IsDirty = false;
                app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run(window);
    }
    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(window.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface), null, bounds); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var capture = File.Create(path); encoder.Save(capture);
    }
}
