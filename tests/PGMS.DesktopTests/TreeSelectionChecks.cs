using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// As in SSMS 22.11, the selection of a tree node covers the label and leaves the icon on the panel background.
internal static class TreeSelectionChecks
{
    internal static async Task RunAsync(Action<bool, string> check, TreeView tree, TreeViewItem item, TextBlock text)
    {
        var label = (Border)text.Parent;
        var header = (FrameworkElement)item.Template.FindName("Bd", item);
        tree.UpdateLayout();
        double labelLeft = label.TranslatePoint(new(), header).X;
        check(label.Style is not null && labelLeft >= 19 && Math.Abs(label.ActualHeight - 18) < 0.5, $"The selection box starts after the icon and is 18 px tall as in SSMS (left {labelLeft}, height {label.ActualHeight})");

        item.Focus();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(Selector.GetIsSelectionActive(item) && label.BorderBrush is DrawingBrush, "A focused tree draws the dotted focus rectangle");
        ExplorerExperienceChecks.Capture(Window.GetWindow(tree), "tree-selection");
        var focused = ((SolidColorBrush)label.Background).Color;
        var pixels = Render(header);
        check(pixels.Colors(0, (int)labelLeft).All(c => c != focused), "The icon stays out of the selection");
        check(pixels.Colors((int)labelLeft + 1, (int)header.ActualWidth - 1).Contains(focused), "The label shows the selection color");

        var icon = header.TranslatePoint(new(9, header.ActualHeight / 2), tree);
        check(ItemsControl.ContainerFromElement(null, (DependencyObject)tree.InputHitTest(icon)) == item, "A click on the icon still reaches its node");

        Keyboard.ClearFocus();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(!Selector.GetIsSelectionActive(item) && label.BorderBrush is SolidColorBrush { Color.A: 0 }, "Without focus the tree keeps the selection and hides the dotted rectangle");
        // User's choice (08/11/2026): a visible light blue instead of the SSMS #F0F0F0, which looked like the selection had disappeared.
        check(((SolidColorBrush)label.Background).Color == Color.FromRgb(0xCC, 0xE4, 0xF7) && item.Foreground is SolidColorBrush { Color: { R: 0x1E, G: 0x1E, B: 0x1E } },
            "Without focus the label stays visible in light blue with dark text");
    }

    private static Bitmap Render(FrameworkElement element)
    {
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bytes = new byte[width * height * 4];
        bitmap.CopyPixels(bytes, width * 4, 0);
        return new(bytes, width, height);
    }

    private sealed record Bitmap(byte[] Bytes, int Width, int Height)
    {
        internal IEnumerable<Color> Colors(int fromColumn, int toColumn)
        {
            for (int y = 0; y < Height; y++)
                for (int x = fromColumn; x < Math.Min(toColumn, Width); x++)
                {
                    int i = (y * Width + x) * 4;
                    yield return Color.FromArgb(Bytes[i + 3], Bytes[i + 2], Bytes[i + 1], Bytes[i]);
                }
        }
    }
}
