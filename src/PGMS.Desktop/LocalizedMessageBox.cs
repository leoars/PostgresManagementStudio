using System.Windows;

namespace PGMS.Desktop;

/// <summary>
/// The message boxes of PGMS (PGMS-085, M1128): <see cref="MessageBox"/> with the text and caption in the interface language. The text is
/// translated line by line with <see cref="Localization.Message"/>, so the fixed sentences of a confirmation change and the SQL, names and
/// errors it quotes stay as they are. Since the B1 review (M1138) the box is <see cref="PgmsMessageBox"/>, so the Yes/No/OK/Cancel buttons
/// follow the interface language of PGMS (and the theme) instead of the Windows language.
/// </summary>
public static class LocalizedMessageBox
{
    /// <summary>The text of a message box in the interface language: each line on its own.</summary>
    public static string Text(string text) => Localization.Current == "en" || text.Length == 0 ? text : string.Join("\n", text.Split('\n').Select(line => line.EndsWith('\r') ? Localization.Message(line[..^1]) + "\r" : Localization.Message(line)));

    public static MessageBoxResult Show(string text) => Show(null, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => Show(null, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(null, text, caption, button, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) => Show(null, text, caption, button, icon, defaultResult);
    public static MessageBoxResult Show(Window owner, string text) => Show(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button) => Show(owner, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(owner, text, caption, button, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        PgmsMessageBox.Show(owner, Text(text), caption.Length == 0 ? "" : Localization.Message(caption), button, icon, defaultResult);
}
