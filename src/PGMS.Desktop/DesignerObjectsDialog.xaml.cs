using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PGMS.Desktop;

/// <summary>What one kind of designer object offers to the shared dialog: Foreign Key Relationships, Indexes/Keys and Check Constraints have the same layout in SSMS 22.11.</summary>
public sealed record DesignerObjectKind(string Title, string ListCaption, string EmptyHelp, Func<object, string> Help, Func<object> Add, Action<object> Remove, Func<object, bool> CanDelete, Func<object, IReadOnlyList<DesignerProperty>> Properties, Func<IReadOnlyList<object>> Rows);

/// <summary>Changes stay in the designer until Save, as in SSMS: Close keeps them and there is no Cancel.</summary>
public partial class DesignerObjectsDialog : Window
{
    private const int StyleIndex = -16;
    private const nint MinimizeBox = 0x20000, MaximizeBox = 0x10000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    private readonly DesignerObjectKind kind;
    private readonly bool readOnly;

    /// <summary>Select picks the item shown first.</summary>
    public DesignerObjectsDialog(DesignerObjectKind kind, bool readOnly, object? select = null)
    {
        InitializeComponent();
        this.kind = kind; this.readOnly = readOnly;
        Title = kind.Title; ListCaption.Text = kind.ListCaption; Properties.IsReadOnly = readOnly; AddButton.IsEnabled = !readOnly;
        // The SSMS dialogs have no minimize or maximize button; WPF offers both on every resizable window.
        SourceInitialized += (_, _) => { nint handle = new WindowInteropHelper(this).Handle; SetWindowLongPtr(handle, StyleIndex, GetWindowLongPtr(handle, StyleIndex) & ~(MinimizeBox | MaximizeBox)); };
        Properties.ShowDescription = false;
        Properties.ValueChanged += () => { var selected = Items.SelectedItem; Items.Items.Refresh(); Items.SelectedItem = selected; };
        // Focus on the selected item shows it in the active selection color, as SSMS opens these dialogs.
        Loaded += (_, _) => (Items.ItemContainerGenerator.ContainerFromItem(Items.SelectedItem ?? new object()) as UIElement ?? Items).Focus();
        Reload(select);
    }

    private void Reload(object? select)
    {
        Items.ItemsSource = kind.Rows();
        Items.SelectedItem = select ?? (Items.Items.Count > 0 ? Items.Items[0] : null);
        if (Items.SelectedItem is null) ShowSelection();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelection();
    private void ShowSelection()
    {
        var item = Items.SelectedItem;
        DeleteButton.IsEnabled = !readOnly && item is not null && kind.CanDelete(item);
        Help.Text = item is null ? kind.EmptyHelp : kind.Help(item);
        Properties.Show(item is null ? [] : kind.Properties(item));
    }
    private void AddItem(object sender, RoutedEventArgs e) => Reload(kind.Add());
    private void DeleteItem(object sender, RoutedEventArgs e) { if (Items.SelectedItem is { } item) { kind.Remove(item); Reload(null); } }
    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
}
