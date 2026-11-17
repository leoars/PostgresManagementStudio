using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using PGMS.Desktop;

internal static class LongLineDisplayChecks
{
    private static int Hidden(SqlTextEditor editor, int lineNumber)
    {
        var view = editor.TextArea.TextView; view.EnsureVisualLines();
        var visual = view.GetVisualLine(lineNumber);
        return visual?.Elements.Where(e => e.GetType().Name == "LongLineElement").Sum(e => e.DocumentLength) ?? -1;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var view = new QueryView(); var editor = (SqlTextEditor)view.FindName("Editor"); var window = new Window { Content = view, ShowInTaskbar = false, Width = 900, Height = 600 };
        async Task Idle() { view.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        try
        {
            window.Show();
            const int limit = LongLineElementGenerator.Limit;
            string exact = "--" + new string('x', limit - 2), over = exact + "y", huge = "--" + new string('z', 262142);
            string text = "SELECT 1;\n" + exact + "\n" + over + "\n" + huge + "\nSELECT 2;";
            editor.Document = new TextDocument(text); view.IsDirty = false; await Idle();
            check(Hidden(editor, 1) == 0 && Hidden(editor, 2) == 0, "A line of exactly 10,000 characters is drawn in full");
            check(Hidden(editor, 3) == 1, "One character over the limit hides exactly that character");
            check(Hidden(editor, 4) == huge.Length - limit, "A 262,144-character line shows 10,000 characters and hides the rest behind one marker");
            check(editor.Text == text && !view.IsDirty && !editor.CanUndo, "Cutting the display changes neither the text, the dirty state nor Undo");
            var hugeLine = editor.Document.GetLineByNumber(4);
            editor.Select(hugeLine.Offset, hugeLine.Length);
            check(editor.SelectedText == huge, "Selecting a cut line copies the complete line");
            editor.CaretOffset = hugeLine.EndOffset; editor.Document.Insert(editor.CaretOffset, "!"); await Idle();
            check(editor.Document.GetText(editor.Document.GetLineByNumber(4)) == huge + "!" && Hidden(editor, 4) == huge.Length + 1 - limit, "Typing at the end of a cut line edits the document and keeps the cut");
            editor.Undo(); await Idle();
            check(editor.Text == text, "Undo restores the cut line exactly");

            var watch = Stopwatch.StartNew(); editor.Document.Insert(editor.Document.GetLineByNumber(4).EndOffset, "?"); await Idle(); long cutMs = watch.ElapsedMilliseconds; editor.Undo(); await Idle();
            check(!editor.ShowLongLinesInFull, "Long lines start cut");
            editor.Select(0, 0); editor.CaretOffset = editor.Document.GetLineByNumber(4).EndOffset; editor.TextArea.Caret.BringCaretToView(); await Idle();
            string shots = Path.GetFullPath(Path.Combine("artifacts", "review")); Directory.CreateDirectory(shots);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(shots, "long-line-cut.png"))) encoder.Save(file);
            check(editor.HorizontalOffset > 0 && editor.ExtentWidth < 200000, "The horizontal extent of a cut line stays near 10,000 characters");
            editor.CaretOffset = 0; editor.ScrollToHorizontalOffset(0); await Idle();

            // A surrogate pair straddling the limit stays whole on the hidden side.
            string pair = new string('a', limit - 1) + "😀" + new string('b', 50);
            editor.Document = new TextDocument(pair); await Idle();
            check(Hidden(editor, 1) == pair.Length - (limit - 1), "The cut never splits a surrogate pair");

            editor.Document = new TextDocument(text); view.IsDirty = false; await Idle();
            var textView = editor.TextArea.TextView; var visual = textView.GetVisualLine(4);
            var marker = visual.Elements.Single(e => e.GetType().Name == "LongLineElement");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent, Source = textView };
            marker.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(marker, [down]);
            await Idle();
            check(down.Handled && editor.ShowLongLinesInFull && Hidden(editor, 4) == 0, "Clicking the marker draws the long line in full in this editor");
            check(editor.Text == text && !view.IsDirty, "Showing the line in full does not change the document");
            watch.Restart(); editor.Document.Insert(editor.Document.GetLineByNumber(4).EndOffset, "?"); await Idle(); long fullMs = watch.ElapsedMilliseconds; editor.Undo(); await Idle();
            Console.WriteLine($"INFO: one keystroke on a 262,144-character line: cut {cutMs} ms, in full {fullMs} ms");
            check(cutMs < fullMs, "A keystroke on a cut line is cheaper than on the same line drawn in full");

            editor.ShowLongLinesInFull = false; await Idle();
            check(Hidden(editor, 4) == huge.Length - limit, "Long lines can be cut again");
            int inside = editor.Document.GetLineByNumber(4).Offset + limit + 5000;
            editor.Select(inside, 3); await Idle();
            check(editor.ShowLongLinesInFull && Hidden(editor, 4) == 0 && editor.SelectedText == "zzz", "A selection that lands in the hidden part, as Find does, shows the line in full");
            var other = new QueryView();
            try { check(!((SqlTextEditor)other.FindName("Editor")).ShowLongLinesInFull, "Showing long lines in full is per editor"); }
            finally { await other.DisposeAsync(); }
        }
        finally { window.Close(); window.Content = null; await view.DisposeAsync(); }
    }
}
