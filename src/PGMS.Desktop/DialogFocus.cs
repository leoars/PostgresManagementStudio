using System.Windows;
using System.Windows.Input;

namespace PGMS.Desktop;

// One instance per dialog; no global handlers or retained windows.
internal sealed class DialogFocus
{
    private UIElement? previous;

    public void Capture() => previous = Keyboard.FocusedElement as UIElement;

    public void Restore(Window window, UIElement temporary)
    {
        var current = Keyboard.FocusedElement as UIElement;
        if (window.IsVisible && (current is null || current == window || current == temporary || !current.IsEnabled)
            && previous is { IsEnabled: true, IsVisible: true } target && window.IsAncestorOf(target))
            target.Focus();
        previous = null;
    }
}
