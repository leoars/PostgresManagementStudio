using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class TableEditChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_ui_edit_" + Guid.NewGuid().ToString("N");
        string destination = table + "_dest";
        string renamed = table + "_renamed";
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE TABLE public.{SqlIdentifier.Quote(table)}(id int);");
        try
        {
            var dialog = new TableEditDialog(connection, "public", table) { ShowInTaskbar = false };
            dialog.ContentRendered += async (_, _) =>
            {
                ((TextBox)dialog.FindName("NewName")).Text = renamed;
                Capture(dialog, "m23-table-rename");
                await dialog.ApplyAsync();
            };
            dialog.ShowDialog();
            check(dialog.WasApplied && (await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", "public"))).Any(t => t.Name == renamed), "table rename Apply updates actual catalog from WPF");
            var script = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            script.ContentRendered += (_, _) =>
            {
                ((TextBox)script.FindName("NewName")).Text = table;
                ((Button)script.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            script.ShowDialog();
            check(script.ScriptSql == $"ALTER TABLE \"public\".{SqlIdentifier.Quote(renamed)} RENAME TO {SqlIdentifier.Quote(table)};" && !script.WasApplied, "table rename Script returns SQL without applying");
            var notNull = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            notNull.ContentRendered += async (_, _) =>
            {
                ((ComboBox)notNull.FindName("Operation")).SelectedItem = TableOperation.SetNotNull;
                ((TextBox)notNull.FindName("ColumnName")).Text = "id";
                Capture(notNull, "m28-set-not-null");
                await notNull.ApplyAsync();
            };
            notNull.ShowDialog();
            check(notNull.WasApplied, "Set NOT NULL Apply succeeds from WPF");
            var nullable = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            nullable.ContentRendered += async (_, _) =>
            {
                ((ComboBox)nullable.FindName("Operation")).SelectedItem = TableOperation.DropNotNull;
                ((TextBox)nullable.FindName("ColumnName")).Text = "id";
                Capture(nullable, "m29-operation");
                await nullable.ApplyAsync();
            };
            nullable.ShowDialog();
            await session.ExecuteAsync($"INSERT INTO public.{SqlIdentifier.Quote(renamed)} VALUES (NULL); DELETE FROM public.{SqlIdentifier.Quote(renamed)};");
            check(nullable.WasApplied, "Drop NOT NULL WPF allows NULL writes");
            var defaultDialog = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            defaultDialog.ContentRendered += async (_, _) =>
            {
                ((ComboBox)defaultDialog.FindName("Operation")).SelectedItem = TableOperation.SetDefault;
                ((TextBox)defaultDialog.FindName("ColumnName")).Text = "id";
                ((TextBox)defaultDialog.FindName("DefaultValue")).Text = "invalid integer";
                await defaultDialog.ApplyAsync();
                check(!defaultDialog.WasApplied && ((TextBlock)defaultDialog.FindName("Feedback")).Text.StartsWith("22P02", StringComparison.Ordinal), "default conversion failure preserves SQLSTATE in WPF");
                ((TextBox)defaultDialog.FindName("DefaultValue")).Text = "17";
                Capture(defaultDialog, "m30-operation");
                defaultDialog.Width = defaultDialog.MinWidth; defaultDialog.Height = defaultDialog.MinHeight;
                Capture(defaultDialog, "m30-operation-min");
                await defaultDialog.ApplyAsync();
            };
            defaultDialog.ShowDialog();
            check(defaultDialog.WasApplied && (string)(await session.ExecuteAsync($"INSERT INTO public.{SqlIdentifier.Quote(renamed)} DEFAULT VALUES RETURNING id;")).Tables[0].Rows[0][0] == "17", "Set Default Apply supplies next inserted value from WPF");
            await session.ExecuteAsync($"DELETE FROM public.{SqlIdentifier.Quote(renamed)};");
            var dropDefault = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            dropDefault.ContentRendered += async (_, _) =>
            {
                ((ComboBox)dropDefault.FindName("Operation")).SelectedItem = TableOperation.DropDefault;
                ((TextBox)dropDefault.FindName("ColumnName")).Text = "id";
                check(((FrameworkElement)dropDefault.FindName("DefaultFields")).Visibility == Visibility.Collapsed, "Drop Default hides literal input");
                Capture(dropDefault, "m31-operation");
                await dropDefault.ApplyAsync();
            };
            dropDefault.ShowDialog();
            check(dropDefault.WasApplied && (await session.ExecuteAsync($"INSERT INTO public.{SqlIdentifier.Quote(renamed)} DEFAULT VALUES RETURNING id;")).Tables[0].Rows[0][0] is DBNull, "Drop Default Apply removes default from WPF");
            await session.ExecuteAsync($"DELETE FROM public.{SqlIdentifier.Quote(renamed)};");
            await session.ExecuteAsync($"CREATE SCHEMA {SqlIdentifier.Quote(destination)};");
            var move = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            move.ContentRendered += async (_, _) =>
            {
                ((ComboBox)move.FindName("Operation")).SelectedItem = TableOperation.SetSchema;
                ((TextBox)move.FindName("NewName")).Text = destination;
                check(((FrameworkElement)move.FindName("ColumnFields")).Visibility == Visibility.Collapsed && (string)((Label)move.FindName("NewNameLabel")).Content == "Destination schema:", "Move to Schema shows destination label and hides column input");
                Capture(move, "m32-operation");
                move.Width = move.MinWidth; move.Height = move.MinHeight;
                Capture(move, "m32-operation-min");
                await move.ApplyAsync();
            };
            move.ShowDialog();
            check(move.WasApplied && (await session.ExecuteAsync($"SELECT * FROM {SqlIdentifier.Quote(destination)}.{SqlIdentifier.Quote(renamed)};")).Tables.Count == 1, "Move to Schema Apply changes actual catalog from WPF");
            await new AdministrativeEditService(connection).ApplyAsync(new TableEdit(destination, renamed, TableOperation.SetSchema, "public"));
            var comment = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            comment.ContentRendered += async (_, _) =>
            {
                ((ComboBox)comment.FindName("Operation")).SelectedItem = TableOperation.CommentTable;
                check(!((Button)comment.FindName("ApplyButton")).IsEnabled, "empty table comment cannot silently remove metadata");
                ((TextBox)comment.FindName("CommentText")).Text = "Table documentation\nReviewed in properties";
                Capture(comment, "m36-table-comment");
                await comment.ApplyAsync();
            };
            comment.ShowDialog();
            check(comment.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", renamed)).Comment!.StartsWith("Table documentation", StringComparison.Ordinal), "table comment Apply updates catalog from WPF");
            var removeComment = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            removeComment.ContentRendered += (_, _) =>
            {
                ((ComboBox)removeComment.FindName("Operation")).SelectedItem = TableOperation.CommentTable;
                ((CheckBox)removeComment.FindName("RemoveComment")).IsChecked = true;
                ((Button)removeComment.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            removeComment.ShowDialog();
            check(removeComment.ScriptSql!.EndsWith(" IS NULL;", StringComparison.Ordinal) && (await new TablePropertiesService(connection).InspectAsync("public", renamed)).Comment is not null, "comment removal Script leaves actual comment intact");
            var columnComment = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            columnComment.ContentRendered += async (_, _) =>
            {
                ((ComboBox)columnComment.FindName("Operation")).SelectedItem = TableOperation.CommentColumn;
                ((TextBox)columnComment.FindName("ColumnName")).Text = "absent";
                ((TextBox)columnComment.FindName("CommentText")).Text = "Column documentation\nVisible in Table Properties";
                await columnComment.ApplyAsync();
                check(!columnComment.WasApplied && ((TextBlock)columnComment.FindName("Feedback")).Text.StartsWith("42703", StringComparison.Ordinal), "column comment failure keeps actionable SQLSTATE in WPF");
                ((TextBox)columnComment.FindName("ColumnName")).Text = "id";
                Capture(columnComment, "m37-column-comment");
                columnComment.Width = columnComment.MinWidth; columnComment.Height = columnComment.MinHeight;
                Capture(columnComment, "m37-column-comment-min");
                await columnComment.ApplyAsync();
            };
            columnComment.ShowDialog();
            check(columnComment.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", renamed)).Columns.Single(c => c.Name == "id").Comment!.StartsWith("Column documentation", StringComparison.Ordinal), "column comment Apply updates catalog from WPF");
            var removeColumnComment = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            removeColumnComment.ContentRendered += async (_, _) =>
            {
                ((ComboBox)removeColumnComment.FindName("Operation")).SelectedItem = TableOperation.CommentColumn;
                ((TextBox)removeColumnComment.FindName("ColumnName")).Text = "id";
                ((CheckBox)removeColumnComment.FindName("RemoveComment")).IsChecked = true;
                await removeColumnComment.ApplyAsync();
            };
            removeColumnComment.ShowDialog();
            check(removeColumnComment.WasApplied && (await new TablePropertiesService(connection).InspectAsync("public", renamed)).Columns.Single(c => c.Name == "id").Comment is null, "explicit column comment removal Apply clears metadata in WPF");
            // More dialog checks are added by subsequent milestones.
            var renameColumn = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            renameColumn.ContentRendered += async (_, _) =>
            {
                ((ComboBox)renameColumn.FindName("Operation")).SelectedItem = TableOperation.RenameColumn;
                ((TextBox)renameColumn.FindName("ColumnName")).Text = "id";
                ((TextBox)renameColumn.FindName("NewName")).Text = "key_id";
                Capture(renameColumn, "m26-rename-column");
                await renameColumn.ApplyAsync();
            };
            renameColumn.ShowDialog();
            check(renameColumn.WasApplied && (await session.ExecuteAsync($"SELECT key_id FROM public.{SqlIdentifier.Quote(renamed)};")).Tables[0].Columns.Count == 1, "Rename Column Apply changes catalog from WPF");
            await new AdministrativeEditService(connection).ApplyAsync(new TableEdit("public", renamed, TableOperation.RenameColumn, "id", "key_id"));
            var add = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            add.ContentRendered += async (_, _) =>
            {
                ((ComboBox)add.FindName("Operation")).SelectedItem = TableOperation.AddColumn;
                ((TextBox)add.FindName("ColumnName")).Text = "added_text";
                Capture(add, "m25-add-column");
                await add.ApplyAsync();
            };
            add.ShowDialog();
            check(add.WasApplied && (await session.ExecuteAsync($"SELECT added_text FROM public.{SqlIdentifier.Quote(renamed)};")).Tables[0].Columns.Count == 1, "Add Column Apply creates typed column from WPF");
            await session.ExecuteAsync($"INSERT INTO public.{SqlIdentifier.Quote(renamed)}(id) VALUES (1);");
            var required = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            required.ContentRendered += async (_, _) =>
            {
                ((ComboBox)required.FindName("Operation")).SelectedItem = TableOperation.AddColumn;
                ((TextBox)required.FindName("ColumnName")).Text = "required";
                ((CheckBox)required.FindName("AllowNull")).IsChecked = false;
                await required.ApplyAsync();
                check(!required.WasApplied && ((TextBlock)required.FindName("Feedback")).Text.StartsWith("23502", StringComparison.Ordinal) && ((Button)required.FindName("ApplyButton")).IsEnabled, "Add Column failure preserves SQLSTATE and re-enables controls");
                required.Close();
            };
            required.ShowDialog();
            var dropColumn = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            dropColumn.ContentRendered += (_, _) =>
            {
                ((ComboBox)dropColumn.FindName("Operation")).SelectedItem = TableOperation.DropColumn;
                ((TextBox)dropColumn.FindName("ColumnName")).Text = "added_text";
                check(((FrameworkElement)dropColumn.FindName("NewNameFields")).Visibility == Visibility.Collapsed && ((FrameworkElement)dropColumn.FindName("TypeFields")).Visibility == Visibility.Collapsed, "Drop Column hides unrelated fields");
                Capture(dropColumn, "m27-drop-column");
                dropColumn.Width = dropColumn.MinWidth; dropColumn.Height = dropColumn.MinHeight;
                Capture(dropColumn, "m27-drop-column-min");
                ((Button)dropColumn.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            dropColumn.ShowDialog();
            check(dropColumn.ScriptSql == $"ALTER TABLE ONLY \"public\".{SqlIdentifier.Quote(renamed)} DROP COLUMN \"added_text\" RESTRICT;" && (await session.ExecuteAsync($"SELECT added_text FROM public.{SqlIdentifier.Quote(renamed)};")).Tables[0].Columns.Count == 1, "Drop Column Script leaves the column intact");
            var drop = new TableEditDialog(connection, "public", renamed) { ShowInTaskbar = false };
            drop.ContentRendered += (_, _) =>
            {
                ((ComboBox)drop.FindName("Operation")).SelectedItem = TableOperation.Drop;
                check(((FrameworkElement)drop.FindName("NewNameFields")).Visibility == Visibility.Collapsed, "Drop Table hides irrelevant new-name field");
                Capture(drop, "m24-table-drop");
                ((Button)drop.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            drop.ShowDialog();
            check(drop.ScriptSql == $"DROP TABLE \"public\".{SqlIdentifier.Quote(renamed)} RESTRICT;" && (await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", "public"))).Any(t => t.Name == renamed), "Drop Table Script uses RESTRICT and leaves table intact");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(table)}; DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(renamed)}; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(destination)} CASCADE;"); }
    }
    internal static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(window.Background, null, bounds); dc.DrawRectangle(new VisualBrush(surface) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(VisualTreeHelper.GetOffset(surface).X, VisualTreeHelper.GetOffset(surface).Y, bounds.Width, bounds.Height), Stretch = Stretch.Fill }, null, bounds); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create($"artifacts/review/{name}.png"); encoder.Save(file);
    }
}
