using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Editor Appearance of SSMS 22.11 (inventory M1063): Match Theme, Light, Dark, Light Extra Contrast and Dark Extra Contrast.
/// Stored by name in environment.json.</summary>
public enum EditorAppearanceChoice { MatchTheme = 0, Light = 1, Dark = 2, LightExtraContrast = 3, DarkExtraContrast = 4 }

/// <summary>View ▸ Presenter Mode sizes, in points (M1113). The defaults are the agent's choice: SSMS was not switched to Presenter Mode.</summary>
public sealed record PresenterModeSettings(double EditorSize = PresenterModeSettings.DefaultEditorSize, double GridSize = PresenterModeSettings.DefaultGridSize,
    double TextResultsSize = PresenterModeSettings.DefaultTextResultsSize, double EnvironmentSize = PresenterModeSettings.DefaultEnvironmentSize)
{
    public const double DefaultEditorSize = 16, DefaultGridSize = 14, DefaultTextResultsSize = 14, DefaultEnvironmentSize = 12;
    /// <summary>The sizes to use, each between 6 and 72 points; anything else falls back to its default.</summary>
    public PresenterModeSettings Valid() => new(Clamp(EditorSize, DefaultEditorSize), Clamp(GridSize, DefaultGridSize), Clamp(TextResultsSize, DefaultTextResultsSize), Clamp(EnvironmentSize, DefaultEnvironmentSize));
    private static double Clamp(double size, double fallback) => size is >= FontsAndColorsSettings.MinimumSize and <= FontsAndColorsSettings.MaximumSize ? size : fallback;
}

public partial class MainWindow
{
    /// <summary>View ▸ Presenter Mode ▸ Toggle Presenter Mode, Ctrl+Shift+F5 as in SSMS 22.11.</summary>
    public static RoutedUICommand PresenterModeCommand { get; } = new("Toggle Presenter Mode", "TogglePresenterMode", typeof(MainWindow), [new KeyGesture(Key.F5, ModifierKeys.Control | ModifierKeys.Shift)]);

    /// <summary>Presenter Mode is on for this session only; PGMS starts with it off.</summary>
    internal bool PresenterModeOn { get; private set; }

    private void EditorAppearanceMenuOpened(object sender, RoutedEventArgs e)
    {
        var current = EnvironmentSettings.Load().EditorAppearanceChoice;
        foreach (var item in EditorAppearanceMenu.Items.OfType<MenuItem>()) item.IsChecked = (string)item.Tag == current.ToString();
    }

    private void EditorAppearanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<EditorAppearanceChoice>(tag, out var choice)) SetEditorAppearance(choice);
    }

    /// <summary>Saves the choice and repaints the open editors; colors chosen in Fonts and Colors stay as chosen.</summary>
    internal void SetEditorAppearance(EditorAppearanceChoice choice)
    {
        EnvironmentSettings.Update(d => d with { EditorAppearance = choice == EditorAppearanceChoice.MatchTheme ? null : choice.ToString() });
        ApplyFontsAndColors();
    }

    private void PresenterModeMenuOpened(object sender, RoutedEventArgs e) => TogglePresenterModeMenu.IsChecked = PresenterModeOn;

    private void TogglePresenterModeExecuted(object sender, ExecutedRoutedEventArgs e) => SetPresenterMode(!PresenterModeOn);

    /// <summary>Larger fonts for the editor, the results and the main window, for showing PGMS on a projector or a shared screen.</summary>
    internal void SetPresenterMode(bool on)
    {
        PresenterModeOn = on;
        ApplyFontsAndColors();
        if (on) FontSize = FontsAndColorsSettings.Pixels(EnvironmentSettings.Load().Presenter.Valid().EnvironmentSize);
        else ClearValue(FontSizeProperty);
        Status.Text = on ? "Presenter Mode is on (Ctrl+Shift+F5 turns it off)." : "Presenter Mode is off.";
    }

    /// <summary>The Fonts and Colors settings in use: the saved ones, with the Presenter Mode sizes while it is on.</summary>
    private FontsAndColorsSettings EffectiveFontsAndColors(EnvironmentOptions.Document document)
    {
        var settings = document.Appearance;
        if (!PresenterModeOn) return settings;
        var presenter = document.Presenter.Valid();
        return settings with { EditorSize = presenter.EditorSize, GridSize = presenter.GridSize, TextResultsSize = presenter.TextResultsSize };
    }

    private void EditPresenterModeSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new PresenterModeDialog(EnvironmentSettings.Load().Presenter.Valid()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        EnvironmentSettings.Update(d => d with { PresenterMode = dialog.Result });
        if (PresenterModeOn) SetPresenterMode(true);
    }

    private void RestoreDefaultFontSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Restore the default fonts and sizes of Text Editor, Grid Results and Text Results? Colors and the Presenter Mode sizes are kept.",
            "Restore Default Font Settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) RestoreDefaultFontSettings();
    }

    /// <summary>Fonts and Colors back to the PGMS fonts and sizes; the colors the user chose stay.</summary>
    internal void RestoreDefaultFontSettings()
    {
        EnvironmentSettings.Update(d => d with { FontsAndColors = d.Appearance with { EditorFont = null, EditorSize = 0, GridFont = null, GridSize = 0, TextResultsFont = null, TextResultsSize = 0 } });
        ApplyFontsAndColors();
        Status.Text = "Default font settings restored.";
    }
}

/// <summary>Edit Presenter Mode Settings: the four sizes, in points.</summary>
internal sealed class PresenterModeDialog : Window
{
    private readonly TextBox editor = new(), grid = new(), text = new(), environment = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    public PresenterModeSettings Result { get; private set; }

    public PresenterModeDialog(PresenterModeSettings settings)
    {
        Result = settings;
        Title = "Presenter Mode Settings"; Width = 360; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        error.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.ErrorText");
        var form = new Grid { Margin = new Thickness(12) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        var intro = new TextBlock { Text = "Font sizes, in points, while Presenter Mode is on. Font names and colors come from Fonts and Colors.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumnSpan(intro, 2); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.Children.Add(intro);
        void Row(string label, TextBox box, double value)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new Label { Content = label, Target = box, Padding = new Thickness(0, 3, 8, 3) };
            box.Text = value.ToString(System.Globalization.CultureInfo.CurrentCulture); box.Margin = new Thickness(0, 2, 0, 2); box.Width = 70; box.HorizontalAlignment = HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetName(box, label.Replace("_", "", StringComparison.Ordinal).TrimEnd(':'));
            Grid.SetRow(caption, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            form.Children.Add(caption); form.Children.Add(box);
        }
        Row("_Text Editor:", editor, settings.EditorSize);
        Row("_Grid Results:", grid, settings.GridSize);
        Row("Te_xt Results:", text, settings.TextResultsSize);
        Row("_Environment:", environment, settings.EnvironmentSize);
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(error, form.RowDefinitions.Count - 1); Grid.SetColumnSpan(error, 2); form.Children.Add(error);
        var defaults = new Button { Content = "_Defaults", Width = 75, Margin = new Thickness(0, 0, 6, 0) };
        var ok = new Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Width = 75, IsCancel = true };
        defaults.Click += (_, _) => { var d = new PresenterModeSettings(); editor.Text = d.EditorSize.ToString(System.Globalization.CultureInfo.CurrentCulture); grid.Text = d.GridSize.ToString(System.Globalization.CultureInfo.CurrentCulture); text.Text = d.TextResultsSize.ToString(System.Globalization.CultureInfo.CurrentCulture); environment.Text = d.EnvironmentSize.ToString(System.Globalization.CultureInfo.CurrentCulture); };
        ok.Click += (_, _) => { if (Accept()) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12), Children = { defaults, ok, cancel } };
        Content = new DockPanel { Children = { WithDock(buttons, Dock.Bottom), form } };
    }

    private static UIElement WithDock(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }

    /// <summary>Every size must be a number from 6 to 72 points.</summary>
    internal bool Accept()
    {
        double?[] sizes = [FontsAndColorsSettings.ParseSize(editor.Text), FontsAndColorsSettings.ParseSize(grid.Text), FontsAndColorsSettings.ParseSize(text.Text), FontsAndColorsSettings.ParseSize(environment.Text)];
        if (sizes.Any(s => s is null)) { error.Text = $"Each size must be a number from {FontsAndColorsSettings.MinimumSize} to {FontsAndColorsSettings.MaximumSize} points."; return false; }
        Result = new(sizes[0]!.Value, sizes[1]!.Value, sizes[2]!.Value, sizes[3]!.Value);
        error.Text = "";
        return true;
    }

    internal void SetSizes(string editorSize, string gridSize, string textSize, string environmentSize) { editor.Text = editorSize; grid.Text = gridSize; text.Text = textSize; environment.Text = environmentSize; }
}
