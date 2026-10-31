using System.Globalization;
using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private sealed record AnalysisReview(string Sql,long Revision,int Timeout,QuerySession Session);
    private AnalysisReview? analysisReview;
    private void ClearAnalysisReview()
    {analysisReview=null;AnalysisPanel.Visibility=Visibility.Collapsed;AnalysisSql.Clear();}
    private void ReviewAnalyze(object sender,RoutedEventArgs e)=>ReviewAnalyzedPlan();
    public void ReviewAnalyzedPlan()
    {
        if(IsBusy || session?.IsConnected!=true)return;
        HideSessionSettings();ReplacementBar.Hide();NavigationBar.Visibility=Visibility.Collapsed;search.Close();
        analysisReview=null;AnalysisPanel.Visibility=Visibility.Collapsed;
        string sql=Editor.SelectionLength>0?Editor.SelectedText:Editor.Text;
        if(string.IsNullOrWhiteSpace(sql) || sql.Length>65536)
        {QueryStatus.Text="Select a nonempty statement of at most 64 Ki characters for analyzed-plan review.";return;}
        if(!int.TryParse(QueryTimeout.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int timeout) || timeout is <0 or >86400)
        {QueryStatus.Text="Enter a read timeout from 0 to 86400 seconds before review.";return;}
        analysisReview=new(sql,EditRevision,timeout,session);AnalysisSql.Text=sql;
        AnalysisPanel.Visibility=Visibility.Visible;UpdateExport();AnalysisClose.Focus();
    }
    private void CloseAnalysis(object sender,RoutedEventArgs e)
    {ClearAnalysisReview();Editor.Focus();}
    private void AnalysisKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {if(e.Key==System.Windows.Input.Key.Escape){ClearAnalysisReview();Editor.Focus();e.Handled=true;}}
    private async void RunAnalysis(object sender,RoutedEventArgs e)=>await ConfirmAnalyzedPlanAsync();
    public async Task ConfirmAnalyzedPlanAsync()
    {
        if(IsBusy)return;
        var review=analysisReview;
        string currentSql=Editor.SelectionLength>0?Editor.SelectedText:Editor.Text;
        if(review is null || review.Revision!=EditRevision || !ReferenceEquals(review.Session,session) || session?.IsConnected!=true ||
            currentSql!=review.Sql || !int.TryParse(QueryTimeout.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int timeout) || timeout!=review.Timeout)
        {ClearAnalysisReview();QueryStatus.Text="SQL or options changed. Review EXPLAIN ANALYZE again before running.";return;}
        ClearAnalysisReview();
        await LoadPlanAsync(true,review.Sql);
    }
}
