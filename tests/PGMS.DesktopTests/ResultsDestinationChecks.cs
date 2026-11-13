using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>M1044: Query ▸ Results To (Text/Grid/File), the default destination in Options, Select Top n Rows and Edit Top n Rows.</summary>
internal static class ResultsDestinationChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await QueryOptionsChecks.RunAsync(connection, check);
        await QueryMenuChecks.RunAsync(connection, check);
        await ForeignKeyScanUiChecks.RunAsync(connection, check);
        await ForeignKeyIndexScanUiChecks.RunAsync(connection, check);
        await ResultPagingChecks.RunAsync(connection, check);
        await AboutChecks.RunAsync(check);
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static async Task Until(Func<bool> done) { for (int i = 0; i < 200 && !done(); i++) await Task.Delay(50); }
        string schema = "rdst_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.loterias (codigo integer PRIMARY KEY, nome varchar(50) NOT NULL, ativa char(1));
            INSERT INTO {schema}.loterias VALUES (102, 'Zoo', 'S'), (104, 'Loto', NULL);
            CREATE TABLE {schema}.semchave (a integer, b text);
            INSERT INTO {schema}.semchave VALUES (1, 'um'), (1, 'um'), (2, NULL);
            CREATE VIEW {schema}.v AS SELECT codigo, nome FROM {schema}.loterias;
            CREATE MATERIALIZED VIEW {schema}.m AS SELECT 1 AS x;
            """);
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        var original = window.EnvironmentSettings.Load();
        string file = Path.Combine(Path.GetTempPath(), "pgms-results-" + Guid.NewGuid().ToString("N") + ".rpt");
        try
        {
            await Idle();
            // Connect at startup (SSMS): only Object Explorer; Cancel connects nothing; with Object Explorer connected it is not asked.
            bool startupAsked = false;
            await window.ConnectAtStartupAsync(_ => { startupAsked = true; return false; });
            check(startupAsked && ((TreeView)window.FindName("ExplorerTree")).Items.Count == 0, "The startup Connect dialog can be cancelled and connects nothing");
            var textButton =(ToggleButton)window.FindName("ResultsToTextButton"); var gridButton = (ToggleButton)window.FindName("ResultsToGridButton"); var fileButton = (ToggleButton)window.FindName("ResultsToFileButton");
            var query = await window.OpenQueryTextAsync(connection, $"SELECT codigo, nome, ativa FROM {schema}.loterias ORDER BY codigo; DO $$BEGIN RAISE NOTICE 'aviso'; END$$; UPDATE {schema}.loterias SET ativa = ativa WHERE false;");
            await Idle();
            check(query.ResultsDestination == ResultsDestination.Grid && gridButton.IsChecked == true && textButton.IsChecked == false && fileButton.IsChecked == false && textButton.IsEnabled,
                "A new query starts in Results to Grid, and the toolbar shows it checked");

            // Results to Text
            textButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
            check(query.ResultsDestination == ResultsDestination.Text && textButton.IsChecked == true && gridButton.IsChecked == false, "The Results to Text button sets the destination of the active query");
            var menu = (MenuItem)window.FindName("ResultsToMenu");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent)); await Idle();
            check(((MenuItem)window.FindName("ResultsToTextMenu")).IsChecked && !((MenuItem)window.FindName("ResultsToGridMenu")).IsChecked
                && ((MenuItem)window.FindName("ResultsToFileMenu")).InputGestureText == "Ctrl+Shift+F", "Query ▸ Results To marks the current destination and shows the SSMS shortcuts");
            await query.ExecuteAsync(); await Idle();
            var text = (TextBox)query.FindName("ResultsText");
            var resultsTab = (TabItem)query.FindName("ResultsTab"); var messagesTab = (TabItem)query.FindName("MessagesTab");
            string shown = text.Text.Replace("\r\n", "\n");
            check(shown.StartsWith("codigo nome ativa\n------ ---- -----\n102    Zoo  S\n104    Loto NULL\n\n(2 rows affected)\n\nNOTICE: aviso\n\n(0 rows affected)\n\nCompletion time: ", StringComparison.Ordinal),
                "Results to Text shows the result, counts, the notice in order and the completion time");
            if (!shown.StartsWith("codigo", StringComparison.Ordinal)) Console.WriteLine(shown);
            check(text.IsVisible && resultsTab.IsSelected && messagesTab.Visibility == Visibility.Collapsed, "In text mode only the Results tab is shown, as in SSMS");

            // Results to File
            query.PickResultsFile = () => null;
            window.SetResultsDestination(ResultsDestination.File); await Idle();
            check(fileButton.IsChecked == true, "Results to File is checked on the toolbar");
            await query.ExecuteAsync(); await Idle();
            check(!File.Exists(file) && text.Text.Replace("\r\n", "\n") == shown, "Cancelling Save Results executes nothing and keeps what was shown");
            query.PickResultsFile = () => file;
            await query.ExecuteAsync(); await Idle();
            byte[] bytes = File.ReadAllBytes(file);
            string written = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF && written.StartsWith("codigo nome ativa\r\n------ ---- -----\r\n102    Zoo  S\r\n", StringComparison.Ordinal) && written.Contains("\r\nCompletion time: ", StringComparison.Ordinal),
                "Results to File writes UTF-8 with BOM and the same text, with CRLF");
            var messages = (TextBox)query.FindName("Messages");
            check(resultsTab.Visibility == Visibility.Collapsed && messagesTab.IsSelected && messages.Text.Contains("(2 rows affected)", StringComparison.Ordinal) && messages.Text.Contains("NOTICE: aviso", StringComparison.Ordinal)
                && !messages.Text.Contains("codigo", StringComparison.Ordinal) && query.LastResultsFile == file, "In file mode only Messages is shown, with counts and notices but no rows");

            var big = await window.OpenQueryTextAsync(connection, $"SELECT g, repeat('z', 400) AS t FROM generate_series(1, {QueryDisplayBudget.MaxRows + 100}) g");
            big.ResultsDestination = ResultsDestination.File; big.PickResultsFile = () => file;
            await big.ExecuteAsync();
            var lines = File.ReadAllLines(file);
            check(lines.Count(l => l.Length > 0 && char.IsDigit(l[0])) == QueryDisplayBudget.MaxRows + 100 && lines[2].EndsWith(new string('z', 256), StringComparison.Ordinal) && !lines[2].EndsWith(new string('z', 257), StringComparison.Ordinal),
                $"The file gets every row beyond the grid limit, each value cut at 256 characters ({QueryDisplayBudget.MaxRows + 100:N0} rows)");

            // Back to Grid (the active tab is now the big query; the menu acts on the active tab)
            check(big.ResultsDestination == ResultsDestination.File, "Each tab keeps its own destination");
            query.ResultsDestination = ResultsDestination.Grid;
            await query.ExecuteAsync(); await Idle();
            check(resultsTab.Visibility == Visibility.Visible && messagesTab.Visibility == Visibility.Visible && !text.IsVisible && ((TabControl)query.FindName("ResultSets")).Items.Count == 1,
                "Results to Grid shows the grid and Messages again");

            // SSMS look of the results grid (user's captures, 08/11/2026), checked on the rendered pixels.
            var resultGrid = (DataGrid)((TabItem)((TabControl)query.FindName("ResultSets")).Items[0]).Content;
            var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
            AvalonDock.Layout.Extensions.Descendents(dock.Layout).OfType<AvalonDock.Layout.LayoutDocument>().Single(d => d.Content == query).IsActive = true;
            ((TabControl)query.FindName("OutputTabs")).SelectedItem = resultsTab;
            await Idle(); await Idle(); resultGrid.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)resultGrid.ActualWidth, (int)resultGrid.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(resultGrid);
            string shots = Path.Combine("artifacts", "acceptance", "M1044-grid"); Directory.CreateDirectory(shots);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(shots, "results-grid.png"))) png.Save(stream);
            int width = bitmap.PixelWidth; var pixels = new int[width * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, width * 4, 0);
            string Pixel(double x, double y) => (pixels[(int)y * width + (int)x] & 0xFFFFFF).ToString("X6");
            var row0 = (DataGridRow)resultGrid.ItemContainerGenerator.ContainerFromIndex(0); var row1 = (DataGridRow)resultGrid.ItemContainerGenerator.ContainerFromIndex(1);
            double y0 = row0.TranslatePoint(new Point(0, 0), resultGrid).Y, y1 = row1.TranslatePoint(new Point(0, 0), resultGrid).Y;
            double left = row0.TranslatePoint(new Point(0, 0), resultGrid).X + resultGrid.RowHeaderWidth;
            double ColumnX(int index) => left + resultGrid.Columns.Take(index).Sum(c => c.ActualWidth);
            check(row0.ActualHeight == 19 && y1 - y0 == 19 && Pixel(ColumnX(0) + 5, y0 + 18) == "CCCEDB" && Pixel(ColumnX(1) - 1, y0 + 5) == "CCCEDB",
                $"Rows are 19 px with #CCCEDB grid lines, as in SSMS (row {row0.ActualHeight}, line {Pixel(ColumnX(0) + 5, y0 + 18)})");
            check(Pixel(ColumnX(2) + resultGrid.Columns[2].ActualWidth - 3, y1 + 3) == "FDFBAC" && Pixel(ColumnX(1) + 3, y0 + 3) == "F5F5F5",
                $"NULL cells are #FDFBAC and the others #F5F5F5 ({Pixel(ColumnX(2) + resultGrid.Columns[2].ActualWidth - 3, y1 + 3)})");
            check(Pixel(ColumnX(3) + 20, y0 + 18) == "F5F5F5" && (string)row0.Header == "1" && (string)row1.Header == "2", "Grid lines end at the last column and rows are numbered");

            // Default destination from Tools ▸ Options
            window.EnvironmentSettings.Update(d => d with { DefaultResultsDestination = ResultsDestination.Text });
            var next = await window.OpenQueryTextAsync(connection, "SELECT 1");
            check(next.ResultsDestination == ResultsDestination.Text && query.ResultsDestination == ResultsDestination.Grid, "New queries use the default destination from Options; open ones keep theirs");
            var options = new OptionsDialog(window.EnvironmentSettings) { ShowInTaskbar = false };
            options.Show(); await Idle();
            ((TreeViewItem)options.FindName("QueryResultsPage")).IsSelected = true; await Idle();
            var defaultBox = (ComboBox)options.FindName("DefaultResultsBox");
            check(((FrameworkElement)options.FindName("QueryResultsPanel")).IsVisible && defaultBox.SelectedIndex == 1, "Options ▸ Query Results ▸ General shows Default destination for results");
            defaultBox.SelectedIndex = 2; options.Accept(options, new RoutedEventArgs());
            check(window.EnvironmentSettings.Load().DefaultResults == ResultsDestination.File, "Choosing Results to file there is saved");

            // Object Explorer: Select Top n Rows / Edit Top n Rows
            await window.ConnectExplorerAsync(connection);
            await ObjectScriptUiChecks.RunAsync(window, connection, check);
            bool askedAgain = false;
            await window.ConnectAtStartupAsync(_ => { askedAgain = true; return false; });
            check(!askedAgain, "With Object Explorer already connected the startup dialog is not shown");
            var tree = (TreeView)window.FindName("ExplorerTree");
            var catalog = new CatalogService(connection);
            async Task<MenuItem[]> Menu(CatalogEntry entry)
            {
                tree.Items.Clear(); var node = new CatalogNode(entry, catalog); tree.Items.Add(node); node.IsSelected = true;
                await Idle();
                var context = tree.ContextMenu; context.PlacementTarget = tree; context.IsOpen = true;
                await Idle();
                var items = context.Items.OfType<MenuItem>().ToArray(); context.IsOpen = false;
                return items;
            }
            window.EnvironmentSettings.Update(d => d with { SelectTopRows = 1000, EditTopRows = 200 });
            var tableMenu = await Menu(new CatalogEntry(CatalogKind.Relation, "loterias", "postgres", schema) { RelationType = "r" });
            check(tableMenu.Select(m => m.Name).Take(4).SequenceEqual(["ExplorerNewTable", "ExplorerDesign", "ExplorerSelectTop", "ExplorerEditTop"])
                && (string)tableMenu[2].Header == "Select Top 1000 Rows" && (string)tableMenu[3].Header == "Edit Top 200 Rows", "Table menu: Design, Select Top 1000 Rows, Edit Top 200 Rows, as in SSMS");
            window.EnvironmentSettings.Update(d => d with { SelectTopRows = 50, EditTopRows = 7 });
            tableMenu = await Menu(new CatalogEntry(CatalogKind.Relation, "loterias", "postgres", schema) { RelationType = "r" });
            check((string)tableMenu[2].Header == "Select Top 50 Rows" && (string)tableMenu[3].Header == "Edit Top 7 Rows", "The numbers in the menu come from Options ▸ Object Explorer ▸ Commands");
            var viewMenu = await Menu(new CatalogEntry(CatalogKind.Relation, "v", "postgres", schema) { RelationType = "v" });
            var matviewMenu = await Menu(new CatalogEntry(CatalogKind.Relation, "m", "postgres", schema) { RelationType = "m" });
            check(viewMenu.Any(m => m.Name == "ExplorerEditTop") && matviewMenu.Any(m => m.Name == "ExplorerSelectTop") && matviewMenu.All(m => m.Name != "ExplorerEditTop"),
                "Views offer both commands; materialized views only Select Top (they cannot be edited)");

            window.EnvironmentSettings.Update(d => d with { DefaultResultsDestination = ResultsDestination.Grid });
            var selectTop = (await window.SelectTopAsync(connection, schema, "loterias"))!;
            await Idle();
            check(selectTop.Editor.Text == TableRowsService.SelectTopSql(schema, "loterias", ["codigo", "nome", "ativa"], 50) && !selectTop.IsDirty
                && ((TabControl)selectTop.FindName("ResultSets")).Items.Count == 1, "Select Top opens a new unchanged query with the SSMS-style SELECT and runs it");

            // Edit Top: save on leaving the row
            var edit = await window.OpenEditRowsAsync(connection, schema, "loterias");
            await Idle();
            var grid = (DataGrid)edit.FindName("Rows");
            check(edit.Title == "127.0.0.1.postgres - " + schema + ".loterias" && edit.Top == 7 && edit.RowCount == 2 && grid.Columns.Count == 3, "Edit Top opens host.database - schema.table with the rows");
            check(((TextBlock)edit.FindName("CountText")).Text == "of 2", "The footer shows the row count");
            async Task EditCell(DataRowView row, int column, string value)
            {
                grid.Focus(); grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[column]);
                grid.ScrollIntoView(row); await Idle();
                grid.BeginEdit(); await Idle();
                ((TextBox)grid.Columns[column].GetCellContent(row)).Text = value;
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                await Idle(); await Until(() => !edit.IsBusy); await Idle();
            }
            var zoo = grid.Items.OfType<DataRowView>().Single(r => (string)r[0] == "102");
            await EditCell(zoo, 1, "Zoo editado");
            check((string?)await Scalar($"SELECT nome FROM {schema}.loterias WHERE codigo = 102") == "Zoo editado" && !zoo.IsEdit && zoo.Row.RowState == DataRowState.Unchanged, "Leaving an edited row saves it by primary key");
            await EditCell(zoo, 2, "NULL");
            check(await Scalar($"SELECT ativa FROM {schema}.loterias WHERE codigo = 102") is DBNull && zoo[2] is DBNull, "Typing NULL stores NULL");

            string? error = null; edit.ShowSaveError = m => error = m;
            await EditCell(zoo, 1, new string('x', 60));
            check(error?.StartsWith("No row was updated.\n\nThe data in row 1 was not committed.\nError Source: PostgreSQL.", StringComparison.Ordinal) == true && zoo.IsEdit
                && (string?)await Scalar($"SELECT nome FROM {schema}.loterias WHERE codigo = 102") == "Zoo editado", "A refused value shows the SSMS message and the row stays in edit");
            grid.CancelEdit(DataGridEditingUnit.Row); grid.CancelEdit(DataGridEditingUnit.Row); await Idle();
            check(!zoo.IsEdit && (string)zoo[1] == "Zoo editado", "Esc (cancel) restores the stored values");

            // Arrows confirm like Enter/Tab (pedido do usuário): Right at the end of the text moves to the next cell of the same row; Down saves the row and moves.
            grid.Focus(); grid.CurrentCell = new DataGridCellInfo(zoo, grid.Columns[1]); await Idle();
            grid.BeginEdit(); await Idle();
            var cellBox = (TextBox)grid.Columns[1].GetCellContent(zoo); cellBox.Text = "Zoo setas"; cellBox.CaretIndex = cellBox.Text.Length;
            await edit.MoveFromEditAsync(System.Windows.Input.Key.Right); await Idle();
            check(grid.CurrentColumn == grid.Columns[2] && grid.CurrentItem == zoo && (string)zoo[1] == "Zoo setas" && (string?)await Scalar($"SELECT nome FROM {schema}.loterias WHERE codigo = 102") == "Zoo editado",
                "Right at the end of the text confirms the cell and moves right, without saving the row yet");
            await edit.MoveFromEditAsync(System.Windows.Input.Key.Down); await Idle(); await Until(() => !edit.IsBusy); await Idle();
            check((string?)await Scalar($"SELECT nome FROM {schema}.loterias WHERE codigo = 102") == "Zoo setas" && grid.CurrentItem != zoo && !zoo.IsEdit,
                "Down confirms, saves the row and moves to the next one");

            // New row
            var placeholder = grid.Items[^1];
            grid.Focus(); grid.CurrentCell = new DataGridCellInfo(placeholder, grid.Columns[0]); await Idle();
            grid.BeginEdit(); await Idle();
            ((TextBox)grid.Columns[0].GetCellContent(grid.CurrentItem)).Text = "200"; grid.CommitEdit(DataGridEditingUnit.Cell, true); await Idle();
            grid.CurrentCell = new DataGridCellInfo(grid.CurrentItem, grid.Columns[1]); grid.BeginEdit(); await Idle();
            ((TextBox)grid.Columns[1].GetCellContent(grid.CurrentItem)).Text = "Nova";
            grid.CommitEdit(DataGridEditingUnit.Row, true); await Idle(); await Until(() => !edit.IsBusy); await Idle();
            check((string?)await Scalar($"SELECT nome FROM {schema}.loterias WHERE codigo = 200") == "Nova" && edit.RowCount == 3, "Typing in the new row (*) and leaving it inserts the row");

            // Delete
            string? asked = null; edit.ConfirmDelete = q => { asked = q; return MessageBoxResult.Yes; };
            grid.SelectedCells.Clear(); var added = grid.Items.OfType<DataRowView>().Single(r => (string)r[0] == "200");
            foreach (var column in grid.Columns) grid.SelectedCells.Add(new DataGridCellInfo(added, column));
            check(await edit.DeleteSelectedAsync() && asked == "Number of rows to delete: 1 \n\nClick Yes to permanently delete these rows. You won't be able to undo those changes."
                && (long)(await Scalar($"SELECT count(*) FROM {schema}.loterias"))! == 2 && edit.RowCount == 2, "Delete asks with the SSMS text and deletes the selected row");

            // No key: identical rows are refused
            var nokey = await window.OpenEditRowsAsync(connection, schema, "semchave");
            await Idle();
            grid = (DataGrid)nokey.FindName("Rows"); edit = nokey; error = null; nokey.ShowSaveError = m => error = m;
            var twin = grid.Items.OfType<DataRowView>().First(r => (string)r[0] == "1");
            await EditCell(twin, 1, "dup");
            check(error?.Contains("they alter multiple rows (2 rows)", StringComparison.Ordinal) == true && (long)(await Scalar($"SELECT count(*) FROM {schema}.semchave WHERE b = 'um'"))! == 2,
                "Without a key, changing one of two identical rows is refused and nothing changes");
            grid.CancelEdit(DataGridEditingUnit.Row); grid.CancelEdit(DataGridEditingUnit.Row);
            check(window.RowEditors.Count == 2 && (await window.OpenEditRowsAsync(connection, schema, "semchave")) == nokey && window.RowEditors.Count == 2, "Edit Top on a table already open activates its tab");
        }
        finally
        {
            window.EnvironmentSettings.Update(_ => original);
            window.Close();
            try { File.Delete(file); } catch (IOException) { }
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
