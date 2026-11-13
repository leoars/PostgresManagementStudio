using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// Query Designer (PGMS-032, docs/MARCO-M1047.md): Design Query in Editor on the active query tab.
public partial class MainWindow
{
    internal const string QueryDesignerRefusal = "The text that you have selected cannot be represented graphically using the Query Designer. Please select just the text necessary to design a SELECT, DELETE, INSERT or UPDATE statement.";
    internal const string QueryDesignerStatementRefusal = "The statement at the cursor cannot be represented graphically using the Query Designer. Move the cursor to a SELECT, DELETE, INSERT or UPDATE statement, or to an empty line to design a new one.";
    /// <summary>Shows the dialog; the tests replace it to drive the dialog without a modal loop. True means OK.</summary>
    internal Func<QueryDesignerDialog, Task<bool>>? QueryDesignerHost { get; set; }
    internal Action<string>? QueryDesignerRefused { get; set; }

    /// <summary>Selected text opens in the designer and OK replaces it. With no selection, the statement under the caret does the same (a deliberate change from SSMS, which
    /// opens empty, asked for by the user); on an empty line the designer starts empty and OK inserts at the caret. Nothing runs.</summary>
    internal async Task OpenQueryDesignerAsync()
    {
        if (ActiveQuery is not { IsConnected: true, IsBusy: false, ConnectionString: { } connection } query) return;
        string? selected = query.SelectedSql;
        if (selected is { Length: > 0 } text && text.Trim().Length == 0) selected = null;
        (int Start, int Length)? statement = null;
        if (selected is null && QueryDesignSql.StatementAt(query.EditorText, query.EditorCaret) is { } found) { statement = found; selected = query.EditorText.Substring(found.Start, found.Length); }
        if (selected is not null && !QueryDesignSql.CanOpenDesigner(selected))
        {
            string refusal = statement is null ? QueryDesignerRefusal : QueryDesignerStatementRefusal;
            if (QueryDesignerRefused is { } report) report(refusal);
            else MessageBox.Show(this, refusal, "PGMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new QueryDesignerDialog(connection, selected) { Owner = this };
        dialog.Designer.ExecutionObserved = query.RecordDesignerExecution;
        bool accepted = QueryDesignerHost is { } host ? await host(dialog) : dialog.ShowDialog() == true;
        if (!accepted || dialog.ResultSql.Length == 0) return;
        string sql = dialog.ResultSql;
        // A selection that ended a statement keeps its semicolon, so the script around it still runs.
        if (selected?.TrimEnd().EndsWith(';') == true && !sql.TrimEnd().EndsWith(';')) sql += ";";
        query.InsertDesignedSql(sql, statement);
    }
}
