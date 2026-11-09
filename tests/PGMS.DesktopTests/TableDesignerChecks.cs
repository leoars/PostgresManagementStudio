using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class TableDesignerChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "dsg_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.parent (id integer PRIMARY KEY);
            CREATE TABLE {schema}.items (id integer PRIMARY KEY, label varchar(80) NOT NULL, note varchar(100), price numeric(10,2), parent_id integer REFERENCES {schema}.parent(id), CONSTRAINT items_price CHECK (price > 0));
            CREATE INDEX items_note ON {schema}.items (note);
            CREATE TABLE {schema}.sales (at date) PARTITION BY RANGE (at);
            INSERT INTO {schema}.items (id, label) VALUES (1, 'kept');
            """);
        var window = new MainWindow { ShowInTaskbar = false, Width = 1100, Height = 800 }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            var catalog = new CatalogService(connection);
            string database = new NpgsqlConnectionStringBuilder(connection).Database!;
            var designerMenu = (MenuItem)window.FindName("TableDesignerMenu");
            static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            static async Task<TableDesignerView> Ready(TableDesignerView view)
            {
                for (int i = 0; i < 200 && (!view.IsLoaded || view.IsBusy || view.Original is null && !view.IsNew); i++) await Task.Delay(50);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                for (int i = 0; i < 100 && view.IsBusy; i++) await Task.Delay(50);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                return view;
            }
            async Task<MenuItem[]> Menu(CatalogEntry entry)
            {
                tree.Items.Clear(); var node = new CatalogNode(entry, catalog); tree.Items.Add(node); node.IsSelected = true;
                await Idle();
                var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
                await Idle();
                var items = menu.Items.OfType<MenuItem>().ToArray(); menu.IsOpen = false;
                return items;
            }
            var entry = new CatalogEntry(CatalogKind.Relation, "items", database, schema, 1) { RelationType = "r" };

            check(designerMenu.Visibility == Visibility.Collapsed, "Table Designer menu is hidden without a designer tab");
            var tableMenu = await Menu(entry);
            check(tableMenu.Select(m => m.Name).Take(2).SequenceEqual(["ExplorerNewTable", "ExplorerDesign"]), "Table menu has New Table and Design at the top, like SSMS");
            tableMenu.Single(m => m.Name == "ExplorerDesign").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var view = await Ready(window.TableDesigners.Single());
            check(view.Title == $"{schema}.items" && !view.IsDirty && !view.IsReadOnly, "Design opens the table as a document tab with no pending changes");
            check(view.Columns.Select(c => c.Name).SequenceEqual(["id", "label", "note", "price", "parent_id"]) && view.Columns[0].IsPrimaryKey && !view.Columns[1].IsPrimaryKey, "The grid lists the columns and marks the primary key");
            check(view.Columns[1].DataType == "character varying(80)" && !view.Columns[1].AllowNulls && view.Columns[2].AllowNulls, "The grid shows data type and Allow Nulls");
            check(designerMenu.Visibility == Visibility.Visible, "Table Designer menu appears with an active designer");
            tableMenu.Single(m => m.Name == "ExplorerDesign").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle();
            check(window.TableDesigners.Count == 1, "Designing an open table activates its tab");

            var grid = (DataGrid)view.FindName("ColumnGrid");
            string render = Environment.GetEnvironmentVariable("PGMS_DESIGNER_RENDER") ?? "";
            void Save(string name, Visual visual, double width, double height)
            {
                if (render.Length == 0) return;
                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(render); using var file = File.Create(Path.Combine(render, name)); encoder.Save(file);
            }
            grid.CurrentCell = new(view.Columns[2], grid.Columns[0]); await Idle();
            check(view.Columns[2].IsCurrent && !view.Columns[0].IsCurrent, "The row pointer follows the current cell");
            check(grid.ItemContainerGenerator.ContainerFromItem(view.Columns[1]) is DataGridRow labelRow && System.Windows.Automation.AutomationProperties.GetName(labelRow) == "Column Name label, Data Type character varying(80), Allow Nulls No", "Grid rows have short screen-reader names");
            grid.SelectedItem = view.Columns[1]; await Idle();
            check(grid.ItemContainerGenerator.ContainerFromItem(view.Columns[1]) is DataGridRow { Background: SolidColorBrush { Color: { R: 0xA6, G: 0xD1, B: 0xF0 } } }, "A selected row is light blue, as in SSMS");
            grid.UnselectAllCells(); grid.CurrentCell = new(view.Columns[2], grid.Columns[0]); await Idle();
            Save("designer.png", view, view.ActualWidth, view.ActualHeight);
            Save("window.png", window, window.ActualWidth, window.ActualHeight);
            var columnProperties = (DesignerPropertyGrid)view.FindName("ColumnProperties");
            var lengthEditor = Editors(columnProperties).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Length");
            check(lengthEditor.Text == "100", "Column Properties shows Length of the selected column");
            var nullsEditor = Editors(columnProperties).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Allow Nulls");
            nullsEditor.IsDropDownOpen = true;
            for (int i = 0; i < 40 && nullsEditor.ItemContainerGenerator.ContainerFromIndex(1) is null; i++) await Task.Delay(50);
            check(nullsEditor.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem { IsVisible: true }, "A property choice still opens its list with the flat style");
            nullsEditor.IsDropDownOpen = false; await Idle();
            check(Editors(columnProperties).Contains(lengthEditor) && view.Columns[2].IsCurrent, "Focus in the panel keeps the column and its row pointer");
            lengthEditor.Focus(); await Idle();
            check(!grid.IsKeyboardFocusWithin && Editors(columnProperties).Contains(lengthEditor) && view.Columns[2].IsCurrent, "A property text box can take focus without emptying the panel");
            lengthEditor.Text = "200"; lengthEditor.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(lengthEditor), 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
            check(view.Columns[2].DataType == "character varying(200)" && view.IsDirty && view.Title.EndsWith('*'), "Editing Length changes the data type and marks the tab");

            view.Columns[1].Name = "caption";
            view.Columns[3].DefaultValue = "1";
            view.Columns[2].Description = "A note";
            TableDesignChange? reviewed = null;
            view.ReviewPrompt = change => { reviewed = change; return false; };
            check(!await view.SaveAsync() && reviewed is not null && view.IsDirty, "Refusing the script leaves the designer as it was");
            check((string?)await Scalar($"SELECT attname FROM pg_attribute WHERE attrelid='{schema}.items'::regclass AND attnum=2") == "label", "Nothing reaches the server before the script is accepted");
            check(reviewed!.Sql.Contains("RENAME COLUMN \"label\" TO \"caption\"") && reviewed.Sql.Contains("TYPE character varying(200)") && reviewed.Sql.Contains("SET DEFAULT 1") && reviewed.Sql.Contains("COMMENT ON COLUMN"), "The script shows every pending change");
            view.ReviewPrompt = _ => true;
            var treeRoot = tree.Items[0];
            check(await view.SaveAsync() && !view.IsDirty && view.Title == $"{schema}.items", "Saving applies the script and clears the mark");
            await Task.Delay(500); await Idle();
            check(ReferenceEquals(tree.Items[0], treeRoot), "Saving keeps the Object Explorer tree as it was");
            check((string?)await Scalar($"SELECT label FROM (SELECT caption AS label FROM {schema}.items) x") == "kept" && (int)(await Scalar($"SELECT character_maximum_length FROM information_schema.columns WHERE table_schema='{schema}' AND table_name='items' AND column_name='note'"))! == 200, "The saved table has the designed columns and keeps its rows");
            check(view.FeedbackText == "Table saved.", "The designer reports the save");

            // Categories.
            static System.Windows.Controls.Primitives.ToggleButton Category(DependencyObject root, string name)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is System.Windows.Controls.Primitives.ToggleButton toggle && System.Windows.Automation.AutomationProperties.GetName(toggle) == name) return toggle;
                    try { return Category(child, name); } catch (InvalidOperationException) { }
                }
                throw new InvalidOperationException("Category not found: " + name);
            }
            FrameworkElement Collation() => Editors(columnProperties).Single(e => System.Windows.Automation.AutomationProperties.GetName(e) == "Collation");
            check(Collation().IsVisible, "A category starts expanded");
            Category(columnProperties, "Table Designer").IsChecked = false; await Idle();
            check(!Collation().IsVisible, "Collapsing a category hides its rows");

            // Primary key.
            grid.CurrentCell = new(view.Columns[1], grid.Columns[0]); await Idle();
            check(!Collation().IsVisible && Category(columnProperties, "Table Designer").IsChecked == false, "A collapsed category stays collapsed on another column");
            Category(columnProperties, "Table Designer").IsChecked = true; await Idle();
            check(Collation().IsVisible, "Expanding a category shows its rows again");
            check(view.PrimaryKeyHeader == "Set Primary Key", "Menu offers Set Primary Key on an ordinary column");
            view.TogglePrimaryKey();
            check(view.Columns[1].IsPrimaryKey && !view.Columns[0].IsPrimaryKey && view.Columns[1].AllowNulls == false, "Set Primary Key moves the key to the selected row");
            check(view.PrimaryKeyHeader == "Remove Primary Key", "Menu offers Remove Primary Key on a key column");
            view.TogglePrimaryKey();
            check(!view.Columns.Any(c => c.IsPrimaryKey) && view.Indexes.All(i => i.Kind != DesignIndexKind.PrimaryKey), "Remove Primary Key leaves the table without a key");
            await view.LoadAsync(); await Ready(view);
            check(!view.IsDirty && view.Columns[0].IsPrimaryKey, "Reloading discards pending changes");

            // Delete and insert.
            view.ReviewPrompt = null;
            grid.CurrentCell = new(view.Columns[2], grid.Columns[0]); await Idle();
            view.DeleteColumn();
            check(!view.Columns.Any(c => c.Name == "note") && !view.Indexes.Any(i => i.Name == "items_note"), "Delete Column removes the column and its index");
            var deletion = view.BuildChange()!;
            check(deletion.Sql.Contains("DROP INDEX") && deletion.Sql.Contains("DROP COLUMN \"note\"") && deletion.Warnings.Count == 1, "The script drops the index first and warns about data");
            grid.CurrentCell = new(view.Columns[0], grid.Columns[0]); await Idle();
            view.InsertColumn(); await Idle();
            var inserted = view.Columns[0];
            grid.CancelEdit(DataGridEditingUnit.Cell); grid.CancelEdit(DataGridEditingUnit.Row); await Idle();
            check(inserted.Position is null && inserted.DataType == "text" && inserted.AllowNulls, "Insert Column adds a text row at the current position");
            check(view.FeedbackText.Contains("at the end of the table"), "Insert Column says PostgreSQL appends the column");
            check(view.BuildChange() is { } blank && !blank.Sql.Contains("ADD COLUMN"), "A row without a name is left out of the script");
            inserted.Name = "added"; inserted.Identity = ColumnIdentity.Always;
            check(!inserted.AllowNulls && !inserted.CanAllowNulls, "Identity turns Allow Nulls off");
            check(view.BuildChange() is null && view.FeedbackText.Contains("identity requires"), "An invalid column is reported in the designer");
            inserted.Identity = ColumnIdentity.None; inserted.DataType = "not a type;";
            check(view.BuildChange() is null, "An invalid type is reported in the designer");
            inserted.DataType = "integer"; inserted.AllowNulls = false;
            view.ReviewPrompt = _ => true;
            check(await view.SaveAsync() == false && view.FeedbackText.Contains("not saved") && view.IsDirty, "A server error keeps the changes and reports the reason");
            check((long)(await Scalar($"SELECT count(*) FROM pg_attribute WHERE attrelid='{schema}.items'::regclass AND attname='note' AND NOT attisdropped"))! == 1, "A failed save changes nothing on the server");
            inserted.AllowNulls = true;
            check(await view.SaveAsync() && view.Columns.Last().Name == "added", "After a fix the same changes save, and the new column comes last");

            // Dialog models.
            var relationships = view.RelationshipsKind();
            check(relationships.Title == "Foreign Key Relationships" && relationships.Rows().Count == 1, "Relationships lists the foreign key");
            var key = (DesignerForeignKeyRow)relationships.Add();
            check(key.Name == "fk_items" && key.DisplayName == "fk_items*" && relationships.Help(key).Contains("new relationship"), "Add creates a named relationship marked as new");
            var keyProperties = relationships.Properties(key);
            check(keyProperties.Select(p => p.Name).SequenceEqual(["Check Existing Data On Creation", "Foreign Key Columns", "Primary/Unique Key Base Table", "Primary/Unique Key Columns", "(Name)", "Description", "Delete Rule", "Update Rule"]), "Relationship properties follow the SSMS groups");
            keyProperties.Single(p => p.Name == "Foreign Key Columns").Set!("added");
            keyProperties.Single(p => p.Name == "Primary/Unique Key Base Table").Set!($"{schema}.parent");
            keyProperties.Single(p => p.Name == "Primary/Unique Key Columns").Set!("id");
            keyProperties.Single(p => p.Name == "Delete Rule").Set!("Set Null");
            var indexKind = view.IndexesKind();
            var index = (DesignerIndexRow)indexKind.Add();
            var indexProperties = indexKind.Properties(index);
            indexProperties.Single(p => p.Name == "Columns").Set!("caption (DESC), id");
            indexProperties.Single(p => p.Name == "Is Unique").Set!("Yes");
            check(index.ColumnsText == "caption (DESC), id (ASC)" && indexKind.Rows().Any(r => r is DesignerIndexRow { Kind: DesignIndexKind.PrimaryKey }), "Indexes/Keys lists the primary key and parses column order");
            var checkKind = view.ChecksKind();
            var ck = (DesignerCheckRow)checkKind.Add();
            checkKind.Properties(ck).Single(p => p.Name == "Expression").Set!("added > 0");
            checkKind.Properties(ck).Single(p => p.Name == "Check Existing Data On Creation").Set!("No");
            check(view.IsDirty, "Dialog additions mark the designer");
            check(await view.SaveAsync() && view.ForeignKeys.Any(f => f is { Name: "fk_items", OnDelete: ReferentialAction.SetNull }) && view.Indexes.Single(i => i.Name == index.Name) is { Unique: true } saved && saved.Columns[0].Descending && view.Checks.Single(c => c.Name == ck.Name).CheckExisting == false, "Relationship, index and check are saved together");
            var fixedIndex = "items_lower";
            await Sql($"CREATE INDEX {fixedIndex} ON {schema}.items (lower(caption))");
            await view.LoadAsync(); await Ready(view);
            var fixedRow = view.Indexes.Single(i => i.Name == fixedIndex);
            check(!indexKind.Properties(fixedRow).Single(p => p.Name == "Columns").IsEditable() && indexKind.Help(fixedRow).Contains("does not edit"), "A fixed index is shown without editing its definition");
            indexKind.Remove(view.Indexes.Single(i => i.Kind == DesignIndexKind.Index && i.Name == index.Name));
            check(view.BuildChange()!.Sql.Contains($"DROP INDEX \"{schema}\".\"{index.Name}\""), "Deleting in the dialog drops the index on save");

            // Closing with pending changes.
            MessageBoxResult answer = MessageBoxResult.Cancel;
            window.DesignerClosePrompt = _ => answer;
            check(!await window.CloseDesignerAsync(view) && window.TableDesigners.Count == 1, "Cancel keeps a tab with pending changes");
            answer = MessageBoxResult.Yes; view.ReviewPrompt = _ => false;
            check(!await window.CloseDesignerAsync(view) && window.TableDesigners.Count == 1, "A refused script keeps the tab open");
            answer = MessageBoxResult.No;
            check(await window.CloseDesignerAsync(view) && window.TableDesigners.Count == 0 && designerMenu.Visibility == Visibility.Collapsed, "No closes the tab, discards the changes and hides the menu");
            check((long)(await Scalar($"SELECT count(*) FROM pg_class WHERE relname='{index.Name}' AND relnamespace='{schema}'::regnamespace"))! == 1, "Discarded changes never reach the server");

            // New table.
            var created = window.OpenTableDesigner(connection, schema, null);
            check(created.IsNew && created.Title == $"{schema}.Table_1" && !created.IsDirty && created.Columns.Count == 0, "New Table opens an empty designer");
            created.Columns.Add(new DesignerColumnRow { Name = "id", DataType = "bigint" });
            created.Columns.Add(new DesignerColumnRow { Name = "name" });
            await Ready(created);
            check(created.Columns.Count == 2, "Columns typed while the designer loads are kept");
            var newGrid = (DataGrid)created.FindName("ColumnGrid"); await Idle();
            newGrid.CurrentCell = new(created.Columns[0], newGrid.Columns[0]); await Idle();
            created.TogglePrimaryKey();
            check(created.IsDirty && created.Columns[0].IsPrimaryKey && !created.Columns[0].AllowNulls, "A new table takes columns and a primary key");
            created.NamePrompt = _ => null;
            check(!await created.SaveAsync() && created.IsNew, "Cancelling Choose Name does not save");
            created.NamePrompt = _ => "fresh"; created.ReviewPrompt = _ => false;
            check(!await created.SaveAsync() && created.Title == $"{schema}.Table_1*" && created.Indexes.Single().Name == "Table_1_pkey", "Refusing the script keeps the provisional name");
            created.ReviewPrompt = c => c.Statements[0].StartsWith("CREATE TABLE", StringComparison.Ordinal);
            check(await created.SaveAsync() && !created.IsNew && created.Title == $"{schema}.fresh" && !created.IsDirty, "Saving a new table creates it and keeps the tab on it");
            check(created.Original!.Indexes.Single().Name == "fresh_pkey" && created.Columns[0].IsPrimaryKey, "The primary key of the new table follows the chosen name");
            check(created.Targets(connection, schema, "fresh") && !created.Targets(connection, schema, "other"), "The tab targets the created table");
            created.Columns[1].Name = "other";
            window.DesignerClosePrompt = _ => MessageBoxResult.No;
            await window.CloseDesignerAsync(created);

            // Read-only tables.
            var partitioned = window.OpenTableDesigner(connection, schema, "sales"); await Ready(partitioned);
            check(partitioned.IsReadOnly && !partitioned.CanEdit && ((FrameworkElement)partitioned.FindName("ReadOnlyBanner")).Visibility == Visibility.Visible, "A partitioned table opens read-only and says why");
            check(!await partitioned.SaveAsync(), "A read-only designer does not save");
            await window.CloseDesignerAsync(partitioned);
            var missing = window.OpenTableDesigner(connection, schema, "missing"); for (int i = 0; i < 100 && missing.FeedbackText.Length == 0 || missing.IsBusy; i++) await Task.Delay(30);
            check(missing.FeedbackText.Contains("could not be read"), "A missing table is reported in the tab");
            await window.CloseDesignerAsync(missing);
        }
        finally
        {
            window.DesignerClosePrompt = _ => MessageBoxResult.No;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static IEnumerable<FrameworkElement> Editors(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox or ComboBox) yield return (FrameworkElement)child;
            foreach (var nested in Editors(child)) yield return nested;
        }
    }
}
