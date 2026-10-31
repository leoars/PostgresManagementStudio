using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PGMS.Desktop;

internal static class UnicodePasswordDialogChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var dialog = new RolePasswordDialog(connection.Replace("SSL Mode=Disable", "SSL Mode=Require", StringComparison.OrdinalIgnoreCase), "synthetic_unicode_role") { ShowInTaskbar = false, Opacity = 0 };
        T Find<T>(string name) where T : FrameworkElement => (T)dialog.FindName(name);
        try
        {
            dialog.Show();
            foreach (string value in new[] { "Café_密碼", "🔑🙂_42", "كلمةالسر" })
            {
                Find<PasswordBox>("Password").Password = value;
                Find<PasswordBox>("ConfirmPassword").Password = value;
                check(Find<Button>("ApplyButton").IsEnabled, "Matching Unicode password enables the existing Apply action");
            }
            Find<PasswordBox>("Password").Password = "I\u00ADX";
            Find<PasswordBox>("ConfirmPassword").Password = "I\u00ADX";
            await dialog.ApplyAsync(); // Must return before the native confirmation or any connection.
            check(Find<TextBlock>("Feedback").Text.Contains("No password change was sent", StringComparison.Ordinal) && !dialog.WasApplied, "Incompatible Unicode gives a recoverable error before confirmation or SQL");
            check(!Find<TextBlock>("Feedback").Text.Contains("I\u00ADX", StringComparison.Ordinal) && Find<PasswordBox>("Password").Password.Length > 0, "Refusal retains masked input and never echoes the secret in feedback");
            foreach (bool small in new[] { false, true })
            {
                dialog.Width = small ? 620 : 680; dialog.Height = small ? 460 : 520; dialog.UpdateLayout();
                var element = (FrameworkElement)dialog.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen()) { var rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight); dc.DrawRectangle(dialog.Background, null, rect); dc.DrawRectangle(new VisualBrush(element), null, rect); }
                bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory("artifacts/review"); using var file = File.Create("artifacts/review/m724-unicode-" + (small ? "small" : "normal") + ".png"); encoder.Save(file);
                check(Find<Button>("ApplyButton").ActualHeight > 0 && Find<TextBlock>("Feedback").ActualHeight > 0, "Unicode error and actions remain laid out at " + (small ? "minimum" : "normal") + " size");
            }
            Find<PasswordBox>("Password").Password = "密碼"; Find<PasswordBox>("ConfirmPassword").Password = "密碼x";
            check(!Find<Button>("ApplyButton").IsEnabled, "Unicode mismatch disables Apply");
        }
        finally { dialog.Close(); }
        check(Find<PasswordBox>("Password").Password.Length == 0 && Find<PasswordBox>("ConfirmPassword").Password.Length == 0, "Closing Unicode password dialog clears both inputs");
    }
}
