using System.Windows;
using System.Windows.Threading;

namespace PGMS.Desktop;

public partial class MainWindow
{
    private QueryView? spareQuery;

    // The first editor in a process pays one-time XAML, template and AvalonEdit setup
    // (~300 ms on the UI thread, docs/en/validation.md). When startup leaves no
    // tab open, build one editor at idle, in two chunks, and hand it to the next new tab.
    private async Task WarmEditorAsync()
    {
        await RecoveryCompletion;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (spareQuery is not null || documents.Count > 0 || recoveryClosed || shuttingDown || closingWindow) return;
        var view = QueryView.CreateInstrumented(Guid.NewGuid(), UiPhaseMeasured);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (recoveryClosed || shuttingDown || closingWindow) { await view.DisposeAsync(); return; }
        view.Measure(new Size(Math.Max(ActualWidth, 400), Math.Max(ActualHeight, 300)));
        view.Arrange(new Rect(view.DesiredSize));
        spareQuery = view;
    }

    private QueryView TakeSpareOrCreate(Guid? recoveryId)
    {
        // Restored drafts keep their own identity; only fresh tabs reuse the prepared editor.
        if (recoveryId is null && spareQuery is { } spare) { spareQuery = null; return spare; }
        return QueryView.CreateInstrumented(recoveryId ?? Guid.NewGuid(), UiPhaseMeasured);
    }

    private void DiscardSpareQuery()
    {
        if (spareQuery is { } spare) { spareQuery = null; _ = spare.DisposeAsync(); }
    }
}
