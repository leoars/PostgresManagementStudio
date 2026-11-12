using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Npgsql;
using PGMS.PostgreSql;
using Path = System.Windows.Shapes.Path;

namespace PGMS.Desktop;

/// <summary>The glyph of a description in the diagram: what it describes. Object is one of the DiagramDescription kinds; Name the column or foreign key.</summary>
public sealed record DiagramDescriptionHit(DiagramTableModel Table, string Object, string? Name);

/// <summary>Descriptions in the diagram and Export for AI (PGMS-045, docs/MARCO-M1062.md). In SSMS 22.11 the description of a table, column or relationship is
/// edited in the Properties window ("Description Property") and saved with the diagram as MS_Description; the diagram itself shows no mark and its tooltips
/// only names (pilotagem de 11/11/2026). In PostgreSQL the description is COMMENT ON, saved by Save after the script review, like every change in the diagram.
/// The mark next to a described item, its tooltip, the copy kept in the diagram, Export for AI and the optional function are PGMS additions.</summary>
public partial class DatabaseDiagramView
{
    private static readonly Brush NoteInk = Frozen("#6A6A6A"), NoteChangedInk = Frozen("#B85300"), NoteChangedFill = Frozen("#FFF1E0");

    public enum DescriptionMark { None, Described, Differs, NotApplied }

    internal Func<DescriptionDialog, bool>? DescriptionPrompt { get; set; }
    internal Func<DiagramExportDialog, bool>? ExportPrompt { get; set; }
    internal Func<MarkdownFunctionDialog, bool>? FunctionPrompt { get; set; }
    /// <summary>Replaces the window with the script of descriptions not applied (intro, statements).</summary>
    internal Action<string, IReadOnlyList<string>>? UnappliedScriptPrompt { get; set; }

    private IReadOnlySet<uint> NotOwned() => tables.Where(t => !t.CanComment && !t.IsNew).Select(t => t.Original.Oid).ToHashSet();

    /// <summary>The description an object of a table has in the diagram now (pending edits included).</summary>
    public static string? DescriptionOf(DiagramTableModel table, string obj, string? name) => obj switch
    {
        DiagramDescription.TableObject => table.Comment,
        DiagramDescription.ColumnObject => table.Column(name ?? "")?.Comment,
        _ => table.ForeignKeys.FirstOrDefault(k => k.Name == name)?.Description is { Length: > 0 } text ? text : null
    };

    /// <summary>What the mark of an object says: described; not applied (kept in the diagram, the owner has to apply it); or changed in the database since
    /// the diagram saved its copy (the diagram shows the catalog, the next save updates the copy).</summary>
    public (DescriptionMark Mark, string Tip) MarkOf(DiagramTableModel table, string obj, string? name)
    {
        string? now = DescriptionOf(table, obj, name);
        string Quote(string? text) => text is null ? "(no description)" : "“" + text + "”";
        if (table.Unapplied.FirstOrDefault(u => u.Same(obj, name)) is { } pending)
            return (DescriptionMark.NotApplied, (now ?? "No description in the database.") + $"\n\nNot applied in the database: only the owner of {table.Key} can change its descriptions. Kept in the diagram: "
                + (pending.Text.Length == 0 ? "remove the description." : Quote(pending.Text)) + "\nUse Script Unapplied Descriptions… on the table to send the script to the owner.");
        string? catalog = obj == DiagramDescription.ColumnObject ? table.Original.Columns.FirstOrDefault(c => c.Name == name)?.Comment : DiagramDescriptions.Text(table.Original, obj, name);
        if (table.SavedDescriptions is { } saved && !table.IsNew)
        {
            var item = saved.FirstOrDefault(d => d.Same(obj, name));
            // A copy not applied whose text the catalog now has was applied by the owner since: nothing differs.
            string? copy = item is null ? null : item.Applied ? item.Text : (item.Text.Length == 0 ? null : item.Text) == catalog ? catalog : item.DatabaseText;
            if (copy != catalog)
                return (DescriptionMark.Differs, (now ?? "No description.") + "\n\nChanged in the database since the diagram was saved. The copy in the diagram had " + Quote(copy)
                    + ". The diagram shows the database; the next save updates its copy.");
        }
        return now is null ? (DescriptionMark.None, "") : (DescriptionMark.Described, now);
    }

    /// <summary>A small page with lines: grey for a description, orange when the diagram and the database differ or the description is not applied.</summary>
    internal FrameworkElement NoteGlyph(DiagramDescriptionHit hit, DescriptionMark mark, string tip)
    {
        bool warn = mark is DescriptionMark.Differs or DescriptionMark.NotApplied;
        var ink = warn ? NoteChangedInk : NoteInk;
        var canvas = new Canvas { Width = 11, Height = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = hit };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M0.5,0.5 H7 L10.5,4 V11.5 H0.5 Z"), Fill = warn ? NoteChangedFill : Brushes.White, Stroke = ink, StrokeThickness = 1, IsHitTestVisible = false });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M7,0.5 V4 H10.5"), Stroke = ink, StrokeThickness = 1, IsHitTestVisible = false });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M2.5,6 H8.5 M2.5,8 H8.5 M2.5,10 H6.5"), Stroke = ink, StrokeThickness = 1, IsHitTestVisible = false });
        canvas.ToolTip = tip + "\n\nClick to edit the description.";
        ToolTipService.SetShowDuration(canvas, 60000);
        AutomationProperties.SetName(canvas, mark switch { DescriptionMark.Differs => "Description changed in the database", DescriptionMark.NotApplied => "Description not applied", _ => "Description" });
        AutomationProperties.SetHelpText(canvas, tip);
        return canvas;
    }

    private FrameworkElement? MarkFor(DiagramTableModel table, string obj, string? name)
    {
        if (table.View == DiagramTableView.NameOnly && obj == DiagramDescription.ColumnObject) return null;
        var (mark, tip) = MarkOf(table, obj, name);
        return mark == DescriptionMark.None ? null : NoteGlyph(new(table, obj, name), mark, tip);
    }

    /// <summary>The tooltip of a relationship line, with its description when it has one.</summary>
    private string RelationToolTip(DiagramRelation relation) => $"Relationship '{relation.Key.Name}' between '{relation.Primary.Caption}' and '{relation.Foreign.Caption}'"
        + (DescriptionOf(relation.Foreign, DiagramDescription.ForeignKeyObject, relation.Key.Name) is { } text ? "\n\n" + text : "");

    /// <summary>The mark of a relationship, in the middle of its line, above the label when labels are shown.</summary>
    private void DrawRelationMark(DiagramRelation relation, DiagramRouting.Route route)
    {
        if (MarkFor(relation.Foreign, DiagramDescription.ForeignKeyObject, relation.Key.Name) is not { } glyph) return;
        int middle = route.Points.Count / 2;
        var at = new Point((route.Points[middle - 1].X + route.Points[middle].X) / 2, (route.Points[middle - 1].Y + route.Points[middle].Y) / 2);
        glyph.Margin = new Thickness(0);
        Canvas.SetLeft(glyph, at.X + 4); Canvas.SetTop(glyph, at.Y - (showLabels ? 30 : 16));
        LinesLayer.Children.Add(glyph);
    }

    // ---- Editing ----
    public void EditDescription(DiagramTableModel table, string obj, string? name)
    {
        if (IsBusy || !tables.Contains(table)) return;
        CommitEdit();
        string subject = obj switch
        {
            DiagramDescription.TableObject => "Table " + table.Key,
            DiagramDescription.ColumnObject => $"Column {table.Key}.{name}",
            _ => $"Relationship {name} of {table.Key}"
        };
        var (mark, _) = MarkOf(table, obj, name);
        var notes = new List<string>();
        if (!table.CanComment && !table.IsNew)
            notes.Add($"You do not own {table.Key}, and PostgreSQL lets only the owner change its descriptions. Save keeps this description only in the diagram, marked as not applied, and shows the script to send to the owner.");
        if (mark == DescriptionMark.Differs) notes.Add("The database changed this description since the diagram was saved; the box shows the text of the database.");
        var unapplied = table.Unapplied.FirstOrDefault(u => u.Same(obj, name));
        var dialog = new DescriptionDialog(subject, DescriptionOf(table, obj, name), notes.Count == 0 ? null : string.Join("\n\n", notes), unapplied) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        if (!(DescriptionPrompt ?? (d => d.ShowDialog() == true))(dialog)) return;
        if (dialog.DiscardUnapplied || dialog.Changed) table.Unapplied.RemoveAll(u => u.Same(obj, name));
        if (dialog.Changed) SetDescription(table, obj, name, dialog.Value);
        Rebuild(); Touch();
    }

    /// <summary>Puts a description in the diagram as a pending change; Save writes it with COMMENT ON after the review.</summary>
    public void SetDescription(DiagramTableModel table, string obj, string? name, string? text)
    {
        switch (obj)
        {
            case DiagramDescription.TableObject: table.Comment = text; break;
            case DiagramDescription.ColumnObject:
                int at = table.Columns.FindIndex(c => c.Name == name);
                if (at >= 0) table.Columns[at] = table.Columns[at] with { Comment = text };
                break;
            default:
                if (table.ForeignKeys.FirstOrDefault(k => k.Name == name) is { } key) key.Description = text ?? "";
                break;
        }
    }

    private void EditDescription(DiagramDescriptionHit hit) => EditDescription(hit.Table, hit.Object, hit.Name);

    /// <summary>The statements that would apply the descriptions kept in the diagram but not applied, for the owner of the table.</summary>
    public static IReadOnlyList<string> UnappliedStatements(DiagramTableModel table) => table.Unapplied.Select(u => DiagramDescriptions.CommentSql(table.Schema, table.Name, u)).ToArray();

    public void ShowUnappliedScript(DiagramTableModel table) =>
        ShowUnappliedScript($"Descriptions of {table.Key} kept in the diagram but not applied: only the owner of the table can change them. Send this script to the owner, or to someone who can run it.", UnappliedStatements(table));

    private void ShowUnappliedScript(string intro, IReadOnlyList<string> statements)
    {
        if (statements.Count == 0) return;
        if (UnappliedScriptPrompt is { } prompt) { prompt(intro, statements); return; }
        var dialog = new ChangeScriptDialog(new TableDesignChange(statements, []), false, intro) { Owner = OwnerWindow, Title = "Unapplied Descriptions" };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
    }

    // ---- Export for AI ----
    /// <summary>The layout the export reads: the saved tables of the diagram; a table not saved yet is not in the database.</summary>
    internal DiagramLayout ExportLayout() { var layout = CurrentLayout(); return layout with { Tables = layout.Tables.Where(t => t.Oid != 0).ToArray() }; }

    public async Task ExportForAiAsync()
    {
        if (IsBusy) return;
        string markdown;
        using (var cancellation = operation = new CancellationTokenSource())
        {
            Say("Reading the catalog…", cancellable: true); RefreshState();
            try { markdown = await DiagramMarkdown.RenderAsync(connection, name, ExportLayout(), cancellation.Token); Say(""); }
            catch (OperationCanceledException) { Say("Export canceled."); return; }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The diagram could not be exported: " + Message(ex)); return; }
            finally { operation = null; Touch(); }
        }
        string? note = HasSchemaChanges ? "The export shows the database as it is now: the unsaved changes of this diagram are not in it. Save the diagram first to include them." : null;
        var dialog = new DiagramExportDialog(markdown, name, note) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        (ExportPrompt ?? (d => d.ShowDialog() == true))(dialog);
    }

    /// <summary>AI Export Function… of the diagram menu: reviews and creates, updates or removes pgms.diagram_markdown.</summary>
    public async Task ManageMarkdownFunctionAsync()
    {
        if (IsBusy) return;
        bool installed;
        try { installed = await DiagramMarkdown.IsFunctionInstalledAsync(connection); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Say("The database could not be read: " + Message(ex)); return; }
        var dialog = new MarkdownFunctionDialog(installed, name) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        if (!(FunctionPrompt ?? (d => d.ShowDialog() == true))(dialog) || dialog.Chosen == MarkdownFunctionDialog.Choice.None) return;
        bool create = dialog.Chosen == MarkdownFunctionDialog.Choice.Create;
        using var cancellation = operation = new CancellationTokenSource();
        Say(create ? "Creating the function…" : "Removing the function…"); RefreshState();
        try
        {
            await DiagramMarkdown.ApplyAsync(connection, create, cancellation.Token);
            Say(create ? $"The function pgms.diagram_markdown is ready: an agent connected to this database can run SELECT pgms.diagram_markdown('{name.Replace("'", "''", StringComparison.Ordinal)}');" : "The function pgms.diagram_markdown was removed.");
        }
        catch (OperationCanceledException) { Say("Canceled. Nothing was changed."); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say((create ? "The function was not created: " : "The function was not removed: ") + Message(ex)); }
        finally { operation = null; Touch(); }
    }
}
