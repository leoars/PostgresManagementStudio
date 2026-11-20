using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PGMS.Desktop;

/// <summary>
/// The message box of PGMS (B1 review, M1138): the layout of the Windows message box (icon and text over a button bar), drawn by WPF so the
/// buttons follow the interface language of PGMS and the theme. Same behavior as the native box: modal to its owner (or to the active window),
/// default button, Esc only when the box has Cancel or only OK, the window's close button only then, access keys on Yes and No, the system
/// sound of the icon, and Ctrl+C copying the caption, text and buttons as Windows does.
/// </summary>
public sealed class PgmsMessageBox : Window
{
    private readonly MessageBoxButton buttons;
    private readonly string text;
    private readonly List<(Button Button, MessageBoxResult Result, string Label)> choices = [];

    /// <summary>The answer; until a button is chosen it is the answer of the window's close button.</summary>
    public MessageBoxResult Result { get; private set; }
    /// <summary>The button that starts with the focus and answers Enter.</summary>
    public MessageBoxResult DefaultResult { get; }
    /// <summary>The message, already in the interface language.</summary>
    public string Message => text;
    /// <summary>The results of the buttons, in order (tests and diagnostics).</summary>
    public IReadOnlyList<MessageBoxResult> Choices => [.. choices.Select(c => c.Result)];
    /// <summary>The labels of the buttons, in the interface language and without the access key mark.</summary>
    public IReadOnlyList<string> ButtonLabels => [.. choices.Select(c => c.Label.Replace("_", ""))];

    private bool CanCancel => buttons is MessageBoxButton.OK or MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel;

    private PgmsMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        this.text = text; this.buttons = buttons;
        Title = caption;
        Localization.SetNoTranslate(this, true); // texts are already in the interface language; the message may quote SQL and names
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStyle = WindowStyle.SingleBorderWindow;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        MinWidth = 280; MaxWidth = Math.Max(420, SystemParameters.WorkArea.Width * 0.6);
        SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        SetResourceReference(ForegroundProperty, "Pgms.PrimaryText");
        AutomationProperties.SetHelpText(this, text);

        var results = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        // As in Windows: a default that is not one of the buttons means the first button.
        DefaultResult = results.Contains(defaultResult) ? defaultResult : results[0];
        Result = buttons switch { MessageBoxButton.OK => MessageBoxResult.OK, MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel, _ => MessageBoxResult.None };

        var body = new DockPanel { Margin = new Thickness(0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 11, 12, 11) };
        var barBorder = new Border { Child = bar };
        barBorder.SetResourceReference(Border.BackgroundProperty, "Pgms.DialogBackground");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        body.Children.Add(barBorder);
        foreach (var result in results)
        {
            string label = Localization.T(result switch { MessageBoxResult.Yes => "_Yes", MessageBoxResult.No => "_No", MessageBoxResult.Cancel => "Cancel", _ => "OK" });
            var button = new Button { Content = new AccessText { Text = label }, MinWidth = 88, MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            button.IsDefault = result == DefaultResult;
            button.IsCancel = CanCancel && result == (buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel);
            AutomationProperties.SetName(button, label.Replace("_", ""));
            var chosen = result;
            button.Click += (_, _) => Answer(chosen);
            choices.Add((button, result, label));
            bar.Children.Add(button);
        }

        var content = new DockPanel { Margin = new Thickness(0) };
        content.SetResourceReference(Panel.BackgroundProperty, "Pgms.ContentBackground");
        var inner = new DockPanel { Margin = new Thickness(24, 22, 28, 22) };
        if (IconImage(icon) is { } image)
        {
            var picture = new Image { Source = image, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
            AutomationProperties.SetName(picture, icon.ToString());
            DockPanel.SetDock(picture, Dock.Left);
            inner.Children.Add(picture);
        }
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = MaxWidth - 120 };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.PrimaryText");
        AutomationProperties.SetName(message, text);
        inner.Children.Add(message);
        content.Children.Add(inner);
        body.Children.Add(content);
        Content = body;

        sound = icon switch
        {
            MessageBoxImage.Error => SystemSounds.Hand,
            MessageBoxImage.Warning => SystemSounds.Exclamation,
            MessageBoxImage.Question => SystemSounds.Question,
            MessageBoxImage.Information => SystemSounds.Asterisk,
            _ => SystemSounds.Beep,
        };
        Loaded += (_, _) =>
        {
            choices.First(c => c.Result == DefaultResult).Button.Focus();
            if (PlaySounds) sound.Play();
        };
        PreviewKeyDown += OnKey;
        Closing += (_, e) => { if (!answered && !CanCancel) e.Cancel = true; }; // Yes/No without Cancel: the close button does nothing, as in Windows
        SourceInitialized += (_, _) => { if (!CanCancel) DisableCloseButton(); };
    }

    private readonly SystemSound sound;
    private bool answered;

    /// <summary>Off in the test runner, so the checks do not beep.</summary>
    internal static bool PlaySounds { get; set; } = true;

    /// <summary>Raised when a box is shown (tests answer it like a person, without global input).</summary>
    internal static event Action<PgmsMessageBox>? Opened;

    /// <summary>Chooses a button, as a click on it does.</summary>
    public void Answer(MessageBoxResult result)
    {
        if (!choices.Any(c => c.Result == result)) throw new ArgumentException("The message box has no such button.", nameof(result));
        Result = result; answered = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try { Clipboard.SetText(CopyText()); } catch (System.Runtime.InteropServices.ExternalException) { }
            e.Handled = true;
            return;
        }
        // Access keys also work without Alt, as in the Windows message box (Y, N).
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Alt && key is >= Key.A and <= Key.Z)
        {
            char letter = (char)('a' + (key - Key.A));
            foreach (var choice in choices)
            {
                int mark = choice.Label.IndexOf('_');
                if (mark >= 0 && mark + 1 < choice.Label.Length && char.ToLowerInvariant(choice.Label[mark + 1]) == letter) { e.Handled = true; Answer(choice.Result); return; }
            }
        }
    }

    /// <summary>What Ctrl+C copies in a Windows message box: caption, text and buttons between dashed lines.</summary>
    public string CopyText()
    {
        const string line = "---------------------------";
        var copy = new StringBuilder();
        copy.Append(line).Append("\r\n").Append(Title).Append("\r\n").Append(line).Append("\r\n").Append(text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n").Append(line).Append("\r\n");
        foreach (var label in ButtonLabels) copy.Append(label).Append("   ");
        copy.Append("\r\n").Append(line).Append("\r\n");
        return copy.ToString();
    }

    private void DisableCloseButton()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        nint menu = GetSystemMenu(handle, false);
        if (menu != 0) EnableMenuItem(menu, 0xF060, 0x1); // SC_CLOSE, MF_GRAYED
    }

    private static BitmapSource? IconImage(MessageBoxImage icon)
    {
        // The stock icons of Windows (IDI_HAND, IDI_QUESTION, IDI_EXCLAMATION, IDI_ASTERISK), shared and never destroyed.
        int id = icon switch { MessageBoxImage.Error => 32513, MessageBoxImage.Question => 32514, MessageBoxImage.Warning => 32515, MessageBoxImage.Information => 32516, _ => 0 };
        if (id == 0) return null;
        nint handle = LoadIcon(0, id);
        if (handle == 0) return null;
        var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);

    /// <summary>Shows a PGMS message box, modal to <paramref name="owner"/> or to the active window of this thread, and returns the answer.
    /// Without a WPF dispatcher on this thread (never the case in PGMS today) it falls back to the Windows message box.</summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA || Application.Current is null || !Application.Current.Dispatcher.CheckAccess())
            return owner is null ? MessageBox.Show(text, caption, button, icon, defaultResult) : MessageBox.Show(owner, text, caption, button, icon, defaultResult);
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var box = new PgmsMessageBox(text, caption, button, icon, defaultResult);
        if (owner is not null && owner != box && owner.IsLoaded && new WindowInteropHelper(owner).Handle != 0 && owner.IsVisible)
        { box.Owner = owner; box.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        box.ContentRendered += (_, _) => Opened?.Invoke(box);
        box.ShowDialog();
        return box.Result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetSystemMenu(nint window, bool revert);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int EnableMenuItem(nint menu, uint item, uint flags);
}
