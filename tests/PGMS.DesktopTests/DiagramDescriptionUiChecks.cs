using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>Descriptions in the diagram tab and Export for AI (PGMS-045, M1062): the marks and tooltips, Description Property, the review and save of
/// COMMENT ON, the copy that differs from the database, a user who does not own a table, Export for AI, the AI Export Function and Install Diagram Support
/// with the function. Runs in the throwaway database of <see cref="DatabaseDiagramChecks"/>.</summary>
internal static class DiagramDescriptionUiChecks
{
    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string editorConnection = new NpgsqlConnectionStringBuilder(connection) { Username = "pgms_dsui_editor", Password = null }.ConnectionString;
        await Sql("""
            CREATE TABLE public.dsui_parent (id integer PRIMARY KEY, email text NOT NULL, name text);
            COMMENT ON TABLE public.dsui_parent IS 'Customers.';
            COMMENT ON COLUMN public.dsui_parent.email IS 'Login e-mail.';
            CREATE TABLE public.dsui_child (id integer PRIMARY KEY, parent_id integer CONSTRAINT dsui_child_parent_fk REFERENCES public.dsui_parent(id));
            COMMENT ON CONSTRAINT dsui_child_parent_fk ON public.dsui_child IS 'Each child has one parent.';
            CREATE ROLE pgms_dsui_owner NOLOGIN;
            CREATE ROLE pgms_dsui_editor LOGIN;
            CREATE TABLE public.dsui_theirs (id integer PRIMARY KEY, label text);
            ALTER TABLE public.dsui_theirs OWNER TO pgms_dsui_owner;
            CREATE TABLE public.dsui_mine (id integer PRIMARY KEY);
            ALTER TABLE public.dsui_mine OWNER TO pgms_dsui_editor;
            GRANT USAGE ON SCHEMA pgms TO pgms_dsui_editor;
            GRANT SELECT, INSERT, UPDATE, DELETE ON pgms.diagrams TO pgms_dsui_editor;
            GRANT SELECT ON public.dsui_theirs TO pgms_dsui_editor;
            """);
        var opened = new List<DatabaseDiagramView>();
        string? scratch = null;
        try
        {
            window.DiagramConfirmPrompt = _ => true;
            var view = (await window.NewDiagramAsync(connection, showAddTable: false))!;
            opened.Add(view);
            await Idle();
            view.ConfirmPrompt = _ => true;
            await view.AddTablesAsync((await view.CandidateTablesAsync()).Where(c => c.Name is "dsui_parent" or "dsui_child").ToArray());
            view.Tables.Single(t => t.Name == "dsui_child").X = 360;
            view.SetZoom(1); await Idle(); view.UpdateLayout();
            DiagramTableModel T(DatabaseDiagramView v, string name) => v.Tables.Single(t => t.Name == name);
            IEnumerable<FrameworkElement> Marks(DependencyObject root) => Descendants(root).OfType<FrameworkElement>().Where(e => e.Tag is DiagramDescriptionHit && e.IsVisible);
            FrameworkElement? TitleMark(DatabaseDiagramView v, string table) => Marks(v.TitleOf(T(v, table))).SingleOrDefault();
            FrameworkElement? RowMark(DatabaseDiagramView v, string table, string column) => Marks(v.RowOf(T(v, table), column)).SingleOrDefault();
            FrameworkElement? LineMark(DatabaseDiagramView v) => v.Lines.Children.OfType<FrameworkElement>().SingleOrDefault(e => e.Tag is DiagramDescriptionHit);
            Point Center(DatabaseDiagramView v, FrameworkElement e) => e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), v.DiagramSurface);
            void Click(DatabaseDiagramView v, FrameworkElement e) { var at = Center(v, e); v.Press(at); v.Release(at); }
            MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
            bool Has(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Any(i => i.Name == name);
            void Raise(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // Marks and tooltips of what the catalog describes.
            var parentMark = TitleMark(view, "dsui_parent");
            check(parentMark is not null && AutomationProperties.GetName(parentMark) == "Description" && parentMark.ToolTip is string parentTip && parentTip.StartsWith("Customers.", StringComparison.Ordinal) && TitleMark(view, "dsui_child") is null,
                "A described table has a mark in its title with the description in the tooltip; a table without one has none");
            check(RowMark(view, "dsui_parent", "email") is { ToolTip: string emailTip } && emailTip.StartsWith("Login e-mail.", StringComparison.Ordinal) && RowMark(view, "dsui_parent", "name") is null
                && view.RowOf(T(view, "dsui_parent"), "email").ToolTip is string rowTip && rowTip.StartsWith("Login e-mail.", StringComparison.Ordinal), "A described column has a mark at the end of its row and its description in the row tooltip");
            var relationMark = LineMark(view);
            var hitLine = view.Lines.Children.OfType<System.Windows.Shapes.Polyline>().First(p => p.Tag is DiagramRelation);
            check(relationMark is { ToolTip: string lineTip } && lineTip.StartsWith("Each child has one parent.", StringComparison.Ordinal) && hitLine.ToolTip is string relationTip && relationTip.EndsWith("\n\nEach child has one parent.", StringComparison.Ordinal),
                "A described relationship has a mark on its line and its description in the line tooltip");
            if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } folder)
            {
                double right = view.Tables.Max(t => t.X + view.BoxOf(t).ActualWidth) + 20, bottom = view.Tables.Max(t => t.Y + view.BoxOf(t).ActualHeight) + 20;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)right * 2, (int)bottom * 2, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(view.DiagramSurface);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                System.IO.Directory.CreateDirectory(folder);
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, "descriptions.png")); encoder.Save(file);
            }
            var relation = view.Relations.Single();
            var tableMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T(view, "dsui_parent"), null, DiagramHitPart.Title));
            var columnMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T(view, "dsui_parent"), "email", DiagramHitPart.Column));
            var area = view.FillMenu(new ContextMenu(), null);
            check(Has(tableMenu, "DiagramTableDescription") && !Has(tableMenu, "DiagramColumnDescription") && Has(columnMenu, "DiagramColumnDescription") && !Has(tableMenu, "DiagramUnappliedDescriptions")
                && Has(view.FillMenu(new ContextMenu(), relation), "DiagramRelationshipDescription") && Has(area, "DiagramExportForAi") && Has(area, "DiagramMarkdownFunction"),
                "The menus have Table Description, Column Description on a column, Relationship Description, Export for AI and AI Export Function");

            // Description Property, as SSMS opens it: OK only after a change; the limit is 4 KiB of UTF-8.
            DescriptionDialog? asked = null;
            view.DescriptionPrompt = d =>
            {
                asked = d;
                bool untouched = !d.OkButton.IsEnabled && d.Box.Text == "Customers." && d.Notice.Visibility != Visibility.Visible;
                d.Box.Text = new string('ç', 2049);
                bool limited = !d.OkButton.IsEnabled && d.Counter.Text.Contains("4096");
                d.Box.Text = "Customers of the store.\r\nOne row per person.";
                check(untouched && limited && d.OkButton.IsEnabled && d.Title == "Description Property", "Description Property starts with the description, refuses more than 4096 bytes and enables OK after a change");
                return true;
            };
            Item(tableMenu, "DiagramTableDescription").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle(); view.UpdateLayout();
            check(asked is not null && T(view, "dsui_parent").Comment == "Customers of the store.\nOne row per person." && view.TableTitle(T(view, "dsui_parent")).EndsWith('*') && view.IsDirty,
                "OK puts the description in the diagram as a change: the table shows * until Save");
            check(TitleMark(view, "dsui_parent")?.ToolTip is string pendingTip && pendingTip.StartsWith("Customers of the store.", StringComparison.Ordinal), "The mark shows the description being edited");

            // A click on the mark of a column opens its description; clearing the box removes it.
            asked = null;
            view.DescriptionPrompt = d => { asked = d; d.Box.Text = "   "; return true; };
            Click(view, RowMark(view, "dsui_parent", "email")!);
            await Idle(); await Idle(); view.UpdateLayout();
            check(asked is not null && T(view, "dsui_parent").Column("email")!.Comment is null && RowMark(view, "dsui_parent", "email") is null, "A click on the mark of a column opens its description; an empty box removes it");
            view.DescriptionPrompt = d => { d.Box.Text = "Child → parent."; return true; };
            Item(view.FillMenu(new ContextMenu(), view.Relations.Single()), "DiagramRelationshipDescription").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            view.DescriptionPrompt = d => { d.Box.Text = "Display name."; return true; };
            view.EditDescription(T(view, "dsui_parent"), DiagramDescription.ColumnObject, "name");
            await Idle(); view.UpdateLayout();
            check(LineMark(view)?.ToolTip is string newLineTip && newLineTip.StartsWith("Child → parent.", StringComparison.Ordinal) && RowMark(view, "dsui_parent", "name") is not null, "Relationship and column descriptions change the same way");
            var changes = view.BuildChanges()!;
            check(changes.Edits.All(e => e is DescriptionEdit) && changes.Script.Sql.Contains("COMMENT ON TABLE \"public\".\"dsui_parent\" IS E'Customers of the store.\nOne row per person.';")
                && changes.Script.Sql.Contains("COMMENT ON COLUMN \"public\".\"dsui_parent\".\"email\" IS NULL;") && changes.Script.Sql.Contains("COMMENT ON CONSTRAINT \"dsui_child_parent_fk\" ON \"public\".\"dsui_child\" IS E'Child → parent.';"),
                "Generate Change Script shows the COMMENT ON statements");

            // Save: the review, then the catalog and the copy in the diagram.
            TableDesignChange? reviewed = null;
            view.ReviewPrompt = c => { reviewed = c; return true; };
            view.NamePromptOverride = _ => "DescUi";
            check(await view.SaveAsync() && !view.IsDirty && reviewed is not null && reviewed.Sql.Contains("COMMENT ON TABLE"), "Save asks for the review of the COMMENT ON statements and saves");
            check((string?)await Scalar("SELECT obj_description('public.dsui_parent'::regclass, 'pg_class')") == "Customers of the store.\nOne row per person." && await Scalar("SELECT col_description('public.dsui_parent'::regclass, 2)") is DBNull
                && (string?)await Scalar("SELECT obj_description(oid, 'pg_constraint') FROM pg_constraint WHERE conname = 'dsui_child_parent_fk'") == "Child → parent.", "The descriptions are in the catalog");
            int id = view.Info!.Id;
            using (var saved = JsonDocument.Parse((string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={id}"))!))
            {
                var parent = saved.RootElement.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "dsui_parent");
                check(saved.RootElement.GetProperty("format").GetInt32() == 3 && parent.GetProperty("descriptions").EnumerateArray().Select(d => d.GetProperty("text").GetString()).SequenceEqual(["Customers of the store.\nOne row per person.", "Display name."]),
                    "The saved diagram keeps a copy of the descriptions");
            }

            // Changed in the database since the diagram was saved: the catalog wins, the mark says so, and the next save updates the copy.
            await Sql("COMMENT ON TABLE public.dsui_parent IS 'Changed elsewhere.'");
            await view.RefreshAsync(); await Idle(); view.UpdateLayout();
            var differs = TitleMark(view, "dsui_parent");
            check(differs is not null && AutomationProperties.GetName(differs) == "Description changed in the database" && differs.ToolTip is string differsTip && differsTip.StartsWith("Changed elsewhere.", StringComparison.Ordinal)
                && differsTip.Contains("Customers of the store.") && !view.IsDirty && T(view, "dsui_parent").Comment == "Changed elsewhere.", "Refresh shows the description of the database and marks that the copy in the diagram differs");
            check(view.MarkOf(T(view, "dsui_child"), DiagramDescription.ForeignKeyObject, "dsui_child_parent_fk").Mark == DatabaseDiagramView.DescriptionMark.Described, "What did not change keeps the plain mark");
            view.DescriptionPrompt = d => { check(d.Notice.Visibility == Visibility.Visible && d.Notice.Text.Contains("changed this description") && d.Box.Text == "Changed elsewhere.", "Description Property says the database changed it and shows the text of the database"); return false; };
            view.EditDescription(T(view, "dsui_parent"), DiagramDescription.TableObject, null);
            check(await view.SaveAsync() && TitleMark(view, "dsui_parent") is { } updated && AutomationProperties.GetName(updated) == "Description", "The next save updates the copy and the mark is plain again");

            // Export for AI: the Markdown of the catalog, copied or saved.
            string? copied = null, exported = null;
            scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pgms-export-" + Guid.NewGuid().ToString("N") + ".md");
            DiagramExportDialog? exportDialog = null;
            view.ExportPrompt = d =>
            {
                exportDialog = d; exported = d.Text.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                d.ClipboardOverride = text => copied = text; Raise(d.CopyButton);
                d.SavePathOverride = () => scratch; Raise(d.SaveButton);
                return false;
            };
            await view.ExportForAiAsync();
            string expected = await DiagramMarkdown.RenderAsync(connection, "DescUi", new DiagramLayout { Tables = view.Tables.Select(t => new DiagramTablePlacement(t.Schema, t.Name, t.X, t.Y)).ToArray() });
            check(exported == expected && exported!.StartsWith("# Database model: DescUi\n", StringComparison.Ordinal) && exported.Contains("| `public.dsui_parent` | Changed elsewhere. |") && exported.Contains("Child → parent."),
                "Export for AI shows the Markdown of the diagram read from the catalog");
            check(copied == expected && System.IO.File.ReadAllText(scratch) == expected && exportDialog!.Note.Visibility != Visibility.Visible, "Copy and Save As give the same text; with nothing pending there is no notice");
            view.DescriptionPrompt = d => { d.Box.Text = "Not saved yet."; return true; };
            view.EditDescription(T(view, "dsui_child"), DiagramDescription.TableObject, null);
            await view.ExportForAiAsync();
            check(exportDialog!.Note.Visibility == Visibility.Visible && exportDialog.Note.Text.Contains("unsaved changes") && !exported!.Contains("Not saved yet."), "With unsaved changes the export says they are not in it");
            view.SetDescription(T(view, "dsui_child"), DiagramDescription.TableObject, null, null);

            // The optional function: reviewed, created, removed.
            MarkdownFunctionDialog? functionDialog = null;
            view.FunctionPrompt = d => { functionDialog = d; Raise(d.CreateButton); return true; };
            await view.ManageMarkdownFunctionAsync();
            check(functionDialog!.Status.Text.Contains("is not installed") && functionDialog.Script.Text.Contains("CREATE OR REPLACE FUNCTION pgms.diagram_markdown") && !functionDialog.RemoveButton.IsEnabled
                && (string?)await Scalar("SELECT pgms.diagram_markdown('DescUi')") == expected && view.FeedbackText.Contains("SELECT pgms.diagram_markdown('DescUi');"), "AI Export Function shows the script and creates the function, which returns the same Markdown");
            view.FunctionPrompt = d =>
            {
                functionDialog = d; Raise(d.RemoveButton);
                bool shown = d.Script.Text == DiagramMarkdown.DropFunctionSql && !d.CreateButton.IsEnabled && d.Chosen == MarkdownFunctionDialog.Choice.None;
                Raise(d.RemoveButton);
                check(shown && d.Status.Text.Contains("is installed"), "Remove shows the DROP FUNCTION first and runs on the second click");
                return true;
            };
            await view.ManageMarkdownFunctionAsync();
            check(await Scalar("SELECT to_regprocedure('pgms.diagram_markdown(text)') IS NULL") is true, "The function is removed");
            window.DiagramClosePrompt = _ => MessageBoxResult.No;
            await window.CloseDiagramAsync(view); opened.Remove(view);

            // A user who does not own a table: kept in the diagram, not applied, with the script for the owner.
            var asEditor = (await window.NewDiagramAsync(editorConnection, showAddTable: false))!;
            opened.Add(asEditor);
            await Idle();
            await asEditor.AddTablesAsync((await asEditor.CandidateTablesAsync()).Where(c => c.Name is "dsui_theirs" or "dsui_mine").ToArray());
            await Idle(); asEditor.UpdateLayout();
            check(!T(asEditor, "dsui_theirs").CanComment && T(asEditor, "dsui_mine").CanComment, "The diagram knows which tables the user owns");
            asEditor.DescriptionPrompt = d => { check(d.Notice.Visibility == Visibility.Visible && d.Notice.Text.Contains("You do not own public.dsui_theirs"), "Description Property warns that the description will not reach the database"); d.Box.Text = "Labels."; return true; };
            asEditor.EditDescription(T(asEditor, "dsui_theirs"), DiagramDescription.TableObject, null);
            asEditor.DescriptionPrompt = d => { d.Box.Text = "Mine."; return true; };
            asEditor.EditDescription(T(asEditor, "dsui_mine"), DiagramDescription.TableObject, null);
            TableDesignChange? editorReview = null; IReadOnlyList<string>? toOwner = null;
            asEditor.ReviewPrompt = c => { editorReview = c; return true; };
            asEditor.UnappliedScriptPrompt = (_, statements) => toOwner = statements;
            asEditor.NamePromptOverride = _ => "DescEditor";
            check(await asEditor.SaveAsync() && !asEditor.IsDirty, "The diagram of the user saves");
            check(editorReview!.Sql.Contains("-- COMMENT ON TABLE \"public\".\"dsui_theirs\"") && editorReview.Warnings.Any(w => w.Contains("You do not own public.dsui_theirs"))
                && toOwner!.SequenceEqual(["COMMENT ON TABLE \"public\".\"dsui_theirs\" IS E'Labels.';"]), "The review shows the description left out, and after the save the script for the owner");
            check(await Scalar("SELECT obj_description('public.dsui_theirs'::regclass, 'pg_class')") is DBNull && (string?)await Scalar("SELECT obj_description('public.dsui_mine'::regclass, 'pg_class')") == "Mine.",
                "Only the description of the table the user owns reaches the database");
            await Idle(); asEditor.UpdateLayout();
            var notApplied = TitleMark(asEditor, "dsui_theirs");
            check(notApplied is not null && AutomationProperties.GetName(notApplied) == "Description not applied" && notApplied.ToolTip is string notAppliedTip && notAppliedTip.Contains("Not applied in the database") && notAppliedTip.Contains("“Labels.”")
                && Has(asEditor.FillMenu(new ContextMenu(), new DiagramHit(T(asEditor, "dsui_theirs"), null, DiagramHitPart.Title)), "DiagramUnappliedDescriptions"), "The table shows the description not applied, and the menu offers its script");
            toOwner = null;
            Item(asEditor.FillMenu(new ContextMenu(), new DiagramHit(T(asEditor, "dsui_theirs"), null, DiagramHitPart.Title)), "DiagramUnappliedDescriptions").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(toOwner!.SequenceEqual(["COMMENT ON TABLE \"public\".\"dsui_theirs\" IS E'Labels.';"]), "Script Unapplied Descriptions gives the same script later");
            int editorId = asEditor.Info!.Id;
            await window.CloseDiagramAsync(asEditor); opened.Remove(asEditor);
            async Task<DatabaseDiagramView> Open(string target, int diagram, string diagramName)
            {
                var o = window.OpenDiagram(target, diagram, diagramName);
                opened.Add(o);
                for (int i = 0; i < 100 && (o.IsBusy || o.Tables.Count == 0); i++) await Task.Delay(50);
                await Idle(); o.UpdateLayout();
                return o;
            }
            var reopened = await Open(editorConnection, editorId, "DescEditor");
            check(!reopened.IsDirty && T(reopened, "dsui_theirs").Unapplied.Single().Text == "Labels." && TitleMark(reopened, "dsui_theirs") is { } stillPending && AutomationProperties.GetName(stillPending) == "Description not applied",
                "Reopened, the description not applied is still there and the diagram is unchanged");
            reopened.DescriptionPrompt = d => { Raise(d.DiscardButton); check(d.OkButton.IsEnabled && d.DiscardUnapplied, "Description Property can discard the text kept in the diagram"); return true; };
            reopened.EditDescription(T(reopened, "dsui_theirs"), DiagramDescription.TableObject, null);
            await Idle(); reopened.UpdateLayout();
            check(T(reopened, "dsui_theirs").Unapplied.Count == 0 && TitleMark(reopened, "dsui_theirs") is null && reopened.IsDirty, "Discarding it removes the mark and marks the diagram changed");
            await window.CloseDiagramAsync(reopened); opened.Remove(reopened);
            await Sql("COMMENT ON TABLE public.dsui_theirs IS 'Labels.'");
            var applied = await Open(editorConnection, editorId, "DescEditor");
            check(T(applied, "dsui_theirs").Unapplied.Count == 0 && TitleMark(applied, "dsui_theirs") is { } plain && AutomationProperties.GetName(plain) == "Description", "Once the owner runs the script, the description is simply applied");
            await window.CloseDiagramAsync(applied); opened.Remove(applied);

            // Install Diagram Support with the optional function, unchecked by default.
            var dialog = new InstallDiagramSupportDialog("Question?");
            bool plainScript = dialog.FunctionBox.IsChecked == false && dialog.Script.Text.Contains("CREATE TABLE IF NOT EXISTS pgms.diagrams") && !dialog.Script.Text.Contains("diagram_markdown");
            dialog.FunctionBox.IsChecked = true;
            check(plainScript && dialog.Script.Text.Contains("CREATE OR REPLACE FUNCTION pgms.diagram_markdown"), "Install Diagram Support shows its script, with the function only when its box is checked");
            string other = "pgms_dsui_" + Guid.NewGuid().ToString("N")[..8];
            await Sql($"CREATE DATABASE {other}");
            string otherConnection = new NpgsqlConnectionStringBuilder(connection) { Database = other }.ConnectionString;
            try
            {
                window.DiagramInstallPrompt = d => { d.FunctionBox.IsChecked = true; return true; };
                check(await window.EnsureDiagramSupportAsync(otherConnection) && await DiagramMarkdown.IsFunctionInstalledAsync(otherConnection) && await new DiagramService(otherConnection).IsInstalledAsync(), "With the box checked, Install Diagram Support creates the table and the function");
            }
            finally
            {
                window.DiagramInstallPrompt = null;
                NpgsqlConnection.ClearAllPools();
                await Sql($"DROP DATABASE IF EXISTS {other} WITH (FORCE)");
            }
            await Sql($"DELETE FROM pgms.diagrams WHERE diagram_id IN ({id}, {editorId})");
        }
        finally
        {
            window.DiagramClosePrompt = _ => MessageBoxResult.No;
            foreach (var view in opened.ToArray()) await window.CloseDiagramAsync(view);
            window.DiagramClosePrompt = null;
            if (scratch is not null) System.IO.File.Delete(scratch);
            NpgsqlConnection.ClearAllPools();
            await Sql("DROP FUNCTION IF EXISTS pgms.diagram_markdown(text); DROP TABLE IF EXISTS public.dsui_child, public.dsui_parent, public.dsui_theirs, public.dsui_mine; REVOKE ALL ON pgms.diagrams FROM pgms_dsui_editor; REVOKE ALL ON SCHEMA pgms FROM pgms_dsui_editor; DROP OWNED BY pgms_dsui_editor, pgms_dsui_owner; DROP ROLE pgms_dsui_editor; DROP ROLE pgms_dsui_owner");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
