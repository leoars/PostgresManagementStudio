using System.IO;
using System.Text.Json;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Show Execution Plan JSON, Show Execution Plan as Text and Compare Showplan (PGMS-084, M1119), from the context menu of a plan in a
/// query tab or in a tab of its own. As SSMS opens the XML in a new document, the JSON and the text open in a new query tab without connection
/// and without highlighting; nothing is sent to the server. Compare asks for a second plan file and opens both side by side with the differences.</summary>
public partial class MainWindow
{
    private int planSourceSequence;
    /// <summary>The tab Show Execution Plan JSON or as Text opened last, with its title (tests).</summary>
    internal (QueryView View, string Title)? LastPlanSource { get; private set; }
    /// <summary>Asks for the plan file to compare with; null cancels. Replaced by tests.</summary>
    internal Func<string?>? PickComparePlanFile { get; set; }

    /// <param name="caption">Names the plan in a comparison (the tab it came from), read when the command runs.</param>
    private void AttachPlanCommands(ExecutionPlanView diagram, Func<string> caption) =>
        diagram.PlanCommandRequested += async command => await RunPlanCommandAsync(diagram, command, caption());

    internal async Task RunPlanCommandAsync(ExecutionPlanView diagram, PlanCommand command, string caption)
    {
        if ((diagram.SelectedStatement ?? diagram.Statements.FirstOrDefault()) is not { } statement) { Status.Text = "There is no plan to show."; return; }
        try
        {
            switch (command)
            {
                case PlanCommand.ShowJson: OpenPlanSource(QueryPlanText.Json(statement.Source), "json"); break;
                case PlanCommand.ShowText: OpenPlanSource(QueryPlanText.Text(statement.Source), "txt"); break;
                case PlanCommand.Compare: await CompareWithFileAsync(statement, caption); break;
                case PlanCommand.ComparePrevious: CompareWithPrevious(diagram, statement, caption); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { Status.Text = "The plan could not be read: " + ex.Message; }
    }

    internal int OpenQueryCount() => OpenQueries().Count();
    internal IReadOnlyList<QueryView> OpenQueryViews() => OpenQueries().ToList();

    private QueryView OpenPlanSource(string text, string extension)
    {
        var view = NewQuery(text);
        view.Editor.SyntaxHighlighting = null;
        view.IsDirty = false;
        documents[view].Title = $"ExecutionPlan{++planSourceSequence}.{extension}";
        LastPlanSource = (view, documents[view].Title);
        Status.Text = extension == "json" ? "Plan JSON opened in a new tab." : "Plan text opened in a new tab (rebuilt from the JSON, in the EXPLAIN text layout).";
        return view;
    }

    private async Task CompareWithFileAsync(ExecutionPlanStatement first, string caption)
    {
        string? path = PickComparePlanFile is { } pick ? pick() : AskPlanFile();
        if (path is null) return;
        QueryPlan second;
        try { second = await QueryPlanFiles.LoadAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) { Status.Text = "The plan file could not be opened: " + ex.Message; return; }
        OpenPlanComparison(first.Source, caption, second, Path.GetFileName(path));
    }

    /// <summary>B1 review (M1140): compares the plan with the one the same view showed before (the previous Estimated/Analyzed plan or actual
    /// plan of the tab): plan 1 is the previous one, plan 2 the current one, the statement at the same position (or the first).</summary>
    private void CompareWithPrevious(ExecutionPlanView diagram, ExecutionPlanStatement current, string caption)
    {
        if (diagram.PreviousStatements is not { Count: > 0 } previous) { Status.Text = "This plan has no previous plan in the same tab to compare with."; return; }
        int index = Math.Max(0, diagram.Statements.ToList().IndexOf(current));
        var before = index < previous.Count ? previous[index] : previous[0];
        OpenPlanComparison(before.Source, caption + " (previous)", current.Source, caption + " (current)");
    }

    private string? AskPlanFile()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Compare Showplan", Filter = "PostgreSQL plan JSON (*.json)|*.json|All files (*.*)|*.*", DefaultExt = ".json" };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private readonly List<ExecutionPlanComparisonView> planComparisons = [];
    internal IReadOnlyList<ExecutionPlanComparisonView> PlanComparisons => planComparisons;

    /// <summary>Both plans side by side, with the list of differences, in a document tab of its own.</summary>
    internal ExecutionPlanComparisonView OpenPlanComparison(QueryPlan first, string firstCaption, QueryPlan second, string secondCaption)
    {
        var warnings = EnvironmentSettings.Load().QueryOptionDefaults.PlanWarnings;
        var view = new ExecutionPlanComparisonView(ExecutionPlanBuilder.Build([first], warnings: warnings), firstCaption, ExecutionPlanBuilder.Build([second], warnings: warnings), secondCaption, QueryPlanComparison.Compare(first, second));
        var document = new AvalonDock.Layout.LayoutDocument { Title = "Showplan Comparison", ContentId = Guid.NewGuid().ToString(), Content = view };
        planComparisons.Add(view);
        document.Closed += (_, _) => { planComparisons.Remove(view); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        int different = view.Differences.Count(d => d.Difference.Length > 0);
        Status.Text = different == 0 ? "The plans have the same operators and values." : $"{different} difference(s) between the plans.";
        return view;
    }
}
