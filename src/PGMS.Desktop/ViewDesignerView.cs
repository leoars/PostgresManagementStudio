using System.IO;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>View Designer as a document tab, like View ▸ Design and New View in SSMS 22.11 (docs/MARCO-M1046.md). Save shows the script and uses CREATE OR REPLACE VIEW on an existing view.</summary>
public sealed class ViewDesignerView : UserControl
{
    private readonly string connection;
    private readonly NpgsqlConnectionStringBuilder target;
    private readonly DockPanel root = new();
    private readonly TextBlock feedback = new() { Margin = new Thickness(8, 3, 8, 3), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private string schema, name, baseline = "", definition = "";
    private bool existing, loadedOnce, busy;

    public event Action? TitleChanged;
    public event Action? Saved;
    internal bool AddTableOnOpen { get; set; } = true;
    internal Func<string, string?>? NamePromptOverride { get; set; }
    internal Func<TableDesignChange, bool>? ReviewPrompt { get; set; }

    public ViewDesignerView(string connection, string schema, string? view)
    {
        this.connection = connection; target = new(connection); this.schema = schema; name = view ?? "View_1"; existing = view is not null;
        Designer = new QueryDesignerControl(connection);
        Designer.Changed += Refresh;
        Designer.Feedback += Say;
        DockPanel.SetDock(feedback, Dock.Bottom);
        root.Children.Add(feedback); root.Children.Add(Designer);
        Content = root;
        ToolTip = $"{target.Host}:{target.Port} {target.Database}";
        Loaded += async (_, _) => await FirstLoadAsync();
    }

    public QueryDesignerControl Designer { get; }
    public string Schema => schema;
    public string ViewName => name;
    public string? Database => target.Database;
    public bool IsNew => !existing;
    public bool IsBusy => busy || Designer.IsBusy;
    public bool IsDirty => Designer.Sql != baseline;
    public string Title => schema + "." + name + (IsDirty ? "*" : "");
    public string FeedbackText => feedback.Text;

    public bool Targets(string other, string otherSchema, string? view)
    {
        if (view is null || IsNew || view != name || otherSchema != schema) return false;
        var candidate = new NpgsqlConnectionStringBuilder(other);
        return string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase) && candidate.Port == target.Port && candidate.Username == target.Username && candidate.Database == target.Database;
    }

    private void Say(string text) { feedback.Text = text; feedback.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void Refresh() => TitleChanged?.Invoke();
    private static string Message(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    public void Shutdown() => Designer.Shutdown();

    private async Task FirstLoadAsync()
    {
        if (loadedOnce) return;
        loadedOnce = true;
        await LoadAsync();
        if (IsNew && AddTableOnOpen) Designer.ShowAddTable();
    }

    /// <summary>Reads the view (a new one starts empty); pending changes are dropped.</summary>
    public async Task LoadAsync()
    {
        busy = true;
        try
        {
            definition = existing ? await new QueryDesignService(connection).ViewDefinitionAsync(schema, name) : "";
            await Designer.LoadAsync(definition);
            baseline = Designer.Sql;
            Say("");
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The view could not be read: " + Message(ex)); }
        finally { busy = false; Refresh(); }
    }

    /// <summary>Asks for a name on a new view, shows the script, then saves. False leaves the tab as it was.</summary>
    public async Task<bool> SaveAsync()
    {
        if (IsBusy) return false;
        await Designer.LeaveSqlFirstAsync();
        string sql = Designer.Sql;
        if (QueryDesignSql.CheckSingleSelect(sql) is { } problem) { Say(problem); return false; }
        string chosenSchema = schema, chosenName = name;
        if (IsNew)
        {
            string? typed = (NamePromptOverride ?? (s => NamePrompt.Show(Window.GetWindow(this), "Choose Name", "Enter a name for the view:", s)))(name);
            if (typed is null) return false;
            chosenName = typed.Trim();
            int dot = chosenName.IndexOf('.');
            if (dot > 0) { chosenSchema = chosenName[..dot]; chosenName = chosenName[(dot + 1)..]; }
        }
        string statement;
        try { statement = QueryDesignService.ViewSql(chosenSchema, chosenName, sql, existing); }
        catch (ArgumentException ex) { Say(ex.Message); return false; }
        var change = new TableDesignChange([statement], existing ? ["An existing view keeps its columns: PostgreSQL only lets CREATE OR REPLACE VIEW add columns at the end."] : []);
        if (!(ReviewPrompt ?? (c => new ChangeScriptDialog(c, true) { Owner = Window.GetWindow(this) }.ShowDialog() == true))(change)) return false;
        busy = true; Say("Saving…");
        try
        {
            var service = new QueryDesignService(connection);
            await service.SaveViewAsync(chosenSchema, chosenName, sql, existing ? definition : null);
            schema = chosenSchema; name = chosenName; existing = true;
            definition = await service.ViewDefinitionAsync(schema, name);
            baseline = sql;
            Say("View saved.");
            Saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Say("The view was not saved: " + Message(ex)); return false; }
        finally { busy = false; Refresh(); }
    }
}
