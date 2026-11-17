using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Tools ▸ External Tools… (PGMS-072, M1106), after the Visual Studio dialog SSMS 22.11 opens: Menu contents, Add, Delete, Move Up,
/// Move Down, Title, Command, Arguments, Initial directory and Prompt for arguments. Nothing runs here: a tool runs only when it is picked in the
/// Tools menu. OK writes the list.</summary>
public sealed class ExternalToolsDialog : Window
{
    private readonly List<ExternalTool> tools;
    private readonly ListBox list = new() { Height = 150 };
    private readonly TextBox title = new(), command = new(), arguments = new(), directory = new();
    private readonly CheckBox prompt = new() { Content = "_Prompt for arguments", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button delete = Side("_Delete"), up = Side("Move _Up"), down = Side("Move Do_wn"), browse = new() { Content = "…", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose the program" };
    private bool filling;

    /// <summary>Raised by OK with the tools in menu order (entries without title or command dropped).</summary>
    public event Action<IReadOnlyList<ExternalTool>>? Saved;

    public ExternalToolsDialog(IReadOnlyList<ExternalTool> current)
    {
        tools = [.. current];
        Title = "External Tools"; Width = 500; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        AutomationProperties.SetName(list, "Menu contents");
        var add = Side("_Add"); add.Click += (_, _) => Add();
        delete.Click += (_, _) => DeleteSelected(); up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) command.Text = dialog.FileName;
        };
        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { add, delete, up, down } };
        var top = new DockPanel(); DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons); top.Children.Add(list);

        var fields = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); fields.ColumnDefinitions.Add(new ColumnDefinition());
        var commandRow = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); commandRow.Children.Add(browse); commandRow.Children.Add(command);
        Row(fields, 0, "_Title:", title, title); Row(fields, 1, "_Command:", commandRow, command); Row(fields, 2, "A_rguments:", arguments, arguments); Row(fields, 3, "_Initial directory:", directory, directory);
        foreach (var box in new[] { title, command, arguments, directory }) box.TextChanged += (_, _) => Store();
        prompt.Click += (_, _) => Store();

        var macros = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Arguments and initial directory can use " + string.Join(", ", ExternalTools.Macros.Select(m => m.Macro)) + " (active query file, caret and selection). A tool runs only when you pick it in the Tools menu; PGMS never passes passwords to it." };
        macros.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.SecondaryText");
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.Click += (_, _) => Accept();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };
        Content = new StackPanel { Margin = new Thickness(12), Children = { new Label { Content = "_Menu contents:", Target = list, Padding = new Thickness(0, 0, 0, 3) }, top, fields, prompt, macros, bottom } };
        list.SelectionChanged += (_, _) => Fill();
        Refresh(tools.Count > 0 ? 0 : -1);
    }

    private static Button Side(string text) => new() { Content = text, Width = 90, Height = 24, Margin = new Thickness(0, 0, 0, 6) };

    private static void Row(Grid grid, int row, string label, UIElement element, Control target)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new Label { Content = label, Target = target, Padding = new Thickness(0, 4, 8, 4) };
        AutomationProperties.SetName(target, label.Replace("_", "").TrimEnd(':'));
        Grid.SetRow(text, row); Grid.SetRow(element, row); Grid.SetColumn(element, 1);
        if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 2, 0, 2);
        grid.Children.Add(text); grid.Children.Add(element);
    }

    internal IReadOnlyList<ExternalTool> Tools => tools;
    internal int SelectedIndex { get => list.SelectedIndex; set => list.SelectedIndex = value; }

    private void Refresh(int select)
    {
        filling = true;
        list.ItemsSource = tools.Select(t => t.Title.Length == 0 ? "[New Tool]" : t.Title).ToList();
        filling = false;
        list.SelectedIndex = select;
        Fill();
    }

    private void Fill()
    {
        if (filling) return;
        int index = list.SelectedIndex;
        bool has = index >= 0 && index < tools.Count;
        filling = true;
        var tool = has ? tools[index] : new ExternalTool("", "");
        title.Text = tool.Title; command.Text = tool.Command; arguments.Text = tool.Arguments; directory.Text = tool.InitialDirectory; prompt.IsChecked = tool.PromptForArguments;
        filling = false;
        foreach (var control in new Control[] { title, command, arguments, directory, prompt, browse, delete }) control.IsEnabled = has;
        up.IsEnabled = has && index > 0; down.IsEnabled = has && index < tools.Count - 1;
    }

    private void Store()
    {
        if (filling || list.SelectedIndex is not (>= 0 and var index) || index >= tools.Count) return;
        bool renamed = tools[index].Title != title.Text;
        tools[index] = new ExternalTool(title.Text, command.Text, arguments.Text, directory.Text, prompt.IsChecked == true);
        if (renamed) Refresh(index);
    }

    /// <summary>Add: a new "[New Tool]" entry at the end, selected, with the title box focused.</summary>
    internal void Add()
    {
        tools.Add(new ExternalTool("", ""));
        Refresh(tools.Count - 1);
        title.Focus();
    }

    /// <summary>Fills the fields of the selected tool as typing would (tests).</summary>
    internal void Edit(string toolTitle, string toolCommand, string toolArguments = "", string initialDirectory = "", bool promptForArguments = false)
    {
        title.Text = toolTitle; command.Text = toolCommand; arguments.Text = toolArguments; directory.Text = initialDirectory; prompt.IsChecked = promptForArguments; Store();
    }

    internal void DeleteSelected()
    {
        if (list.SelectedIndex is not (>= 0 and var index) || index >= tools.Count) return;
        tools.RemoveAt(index);
        Refresh(Math.Min(index, tools.Count - 1));
    }

    internal void Move(int step)
    {
        int index = list.SelectedIndex, target = index + step;
        if (index < 0 || target < 0 || target >= tools.Count) return;
        (tools[index], tools[target]) = (tools[target], tools[index]);
        Refresh(target);
    }

    /// <summary>OK: entries without a title or a command are dropped, the rest saved in order.</summary>
    internal void Accept()
    {
        Saved?.Invoke([.. tools.Where(t => !string.IsNullOrWhiteSpace(t.Title) && !string.IsNullOrWhiteSpace(t.Command)).Select(t => t with { Title = t.Title.Trim(), Command = t.Command.Trim() })]);
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
