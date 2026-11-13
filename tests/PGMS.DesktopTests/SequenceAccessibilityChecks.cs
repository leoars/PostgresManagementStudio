using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceAccessibilityChecks
{
    public static Task RunAsync(string connection, Action<bool, string> check)
    {
        var dialog = new SequenceDialog(connection, "public", "accessibility_probe") { ShowInTaskbar = false };
        Exception? error = null;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                check(((ComboBox)dialog.FindName("Operation")).IsKeyboardFocused, "sequence opens with operation keyboard focus");
                ((ComboBox)dialog.FindName("Operation")).Focus();
                ((UIElement)dialog.FindName("Operation")).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                check(((TextBox)dialog.FindName("SchemaName")).IsKeyboardFocused, "Tab moves from operation to schema");
                ((UIElement)dialog.FindName("SchemaName")).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                check(((TextBox)dialog.FindName("SequenceName")).IsKeyboardFocused, "Tab moves from schema to sequence name");
                ((UIElement)dialog.FindName("SequenceName")).MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                check(((TextBox)dialog.FindName("SchemaName")).IsKeyboardFocused, "Shift Tab returns to schema");
                AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "n", false);
                check(((TextBox)dialog.FindName("SequenceName")).IsKeyboardFocused, "sequence name mnemonic focuses its field");
                var feedback = (TextBlock)dialog.FindName("Feedback");
                check(new TextBlockAutomationPeer(feedback).GetName() == feedback.Text, "sequence feedback exposes the actual status text to automation");
                check(!((Button)dialog.FindName("ApplyButton")).IsDefault, "Enter is not a default Apply action");
                check(new ButtonAutomationPeer((Button)dialog.FindName("InspectButton")).GetAccessKey().EndsWith("I", StringComparison.OrdinalIgnoreCase), "inspection exposes its mnemonic to automation");
                foreach (var operation in Enum.GetValues<SequenceOperation>())
                {
                    ((ComboBox)dialog.FindName("Operation")).SelectedItem = operation;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    foreach (string name in new[] { "Operation", "SchemaName", "SequenceName", "DataType", "Increment", "StartValue", "Minimum", "Maximum", "Cache", "ParameterValue", "ParameterMaximum", "NewName", "CommentText" })
                    {
                        var field = (Control)dialog.FindName(name);
                        if (!field.IsVisible) continue;
                        var peer = UIElementAutomationPeer.CreatePeerForElement(field);
                        check(!string.IsNullOrWhiteSpace(peer?.GetName()), $"sequence {operation} exposes accessible name for {name}");
                    }
                    if (operation == SequenceOperation.SetBounds)
                        check(UIElementAutomationPeer.CreatePeerForElement((TextBox)dialog.FindName("ParameterValue"))!.GetName().Contains("minimum"), "sequence parameter accessible name follows operation");
                }
                ((ComboBox)dialog.FindName("Operation")).SelectedItem = SequenceOperation.Create;
                foreach (double fontSize in new[] { 12d, 18d, 24d })
                {
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; dialog.FontSize = fontSize;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var root = (FrameworkElement)dialog.Content;
                    var scroller = (ScrollViewer)dialog.FindName("ContentScroller");
                    var close = (Button)dialog.FindName("CloseButton");
                    Point bottom = close.TranslatePoint(new Point(close.ActualWidth, close.ActualHeight), root);
                    check(bottom.X <= root.ActualWidth + .5 && bottom.Y <= root.ActualHeight + .5, $"sequence actions remain inside small viewport at font {fontSize}");
                    check(scroller.ScrollableHeight > 0, $"sequence form scrolls at font {fontSize}");
                    var cache = (TextBox)dialog.FindName("Cache"); cache.Focus(); cache.BringIntoView();
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Point position = cache.TranslatePoint(new Point(0, 0), scroller);
                    check(cache.IsKeyboardFocused && position.Y >= 0 && position.Y + cache.ActualHeight <= scroller.ActualHeight + .5, $"focused field scrolls into small viewport at font {fontSize}");
                    TableEditChecks.Capture(dialog, $"sequence-small-font-{fontSize}");
                }
                dialog.FontSize = 12; dialog.Width = 780; dialog.Height = 820;
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                TableEditChecks.Capture(dialog, "sequence-accessible-normal");
            }
            catch (Exception ex) { error = ex; }
            finally { dialog.Close(); }
        };
        dialog.ShowDialog();
        if (error is not null) throw error;
        return Task.CompletedTask;
    }
}

