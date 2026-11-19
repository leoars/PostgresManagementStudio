using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace PGMS.Desktop;

/// <summary>
/// Interface language (PGMS-085, M1125). The interface is written in English; a language is a table from the English text (with its access-key
/// underscore, as in XAML) to the translation, embedded as Resources/Strings/&lt;code&gt;.json. The language is chosen once, when PGMS starts, as in
/// SSMS (Environment ▸ International Settings asks for a restart): windows, menus and context menus are translated when they load or open, and
/// code asks <see cref="T"/> for the texts it sets. Texts without a translation stay in English. PostgreSQL terms, SQL and the names of the user's
/// objects are never translated: lists, grids and trees filled with data are skipped.
/// </summary>
public static class Localization
{
    /// <summary>The languages of the interface, in the order of the Options list: code and native name.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"), ("pt-BR", "Português (Brasil)"), ("es", "Español"), ("de", "Deutsch"), ("it", "Italiano"), ("fr", "Français"), ("ko", "한국어"), ("ja", "日本語"),
    ];

    private static IReadOnlyDictionary<string, string> table = new Dictionary<string, string>();

    /// <summary>The language in use ("en" until <see cref="Initialize"/> runs, so the tests and anything before startup stay in English).</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>The language a saved choice gives: a known code, or for none ("Same as Microsoft Windows") the Windows display language when PGMS
    /// has it, English otherwise.</summary>
    public static string Resolve(string? saved, CultureInfo windows)
    {
        if (saved is not null && Languages.Any(l => l.Code == saved)) return saved;
        if (windows.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        string two = windows.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    /// <summary>At startup: reads the saved choice (PGMS_LANGUAGE overrides it, for checks and support), loads the table and starts translating windows.</summary>
    public static void Initialize(Application app)
    {
        string? saved = Environment.GetEnvironmentVariable("PGMS_LANGUAGE") is { Length: > 0 } forced ? forced : new EnvironmentOptions(EnvironmentOptions.DefaultPath).Load().Language;
        Use(Resolve(saved, CultureInfo.CurrentUICulture));
        if (Current == "en") return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Translate((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Translate((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => Translate((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((sender, _) => Translate((DependencyObject)sender)));
        foreach (Window window in app.Windows) Translate(window);
    }

    /// <summary>Switches the table (startup, and the checks of the translations).</summary>
    public static void Use(string code)
    {
        Current = Languages.Any(l => l.Code == code) ? code : "en";
        table = Current == "en" ? new Dictionary<string, string>() : Load(Current);
        BuildMessages();
    }

    /// <summary>The translation table of a language, from the embedded resource; empty when it is missing or unreadable.</summary>
    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/PGMS;component/Resources/Strings/{code}.json"));
            if (info is null) return new Dictionary<string, string>();
            using var stream = info.Stream;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UriFormatException) { return new Dictionary<string, string>(); }
    }

    /// <summary>The text in the interface language; the English text when there is no translation.</summary>
    public static string T(string english) => table.TryGetValue(english, out var text) && text.Length > 0 ? text : english;

    private static List<(System.Text.RegularExpressions.Regex Pattern, string Translation)> patterns = [];
    private static List<(string English, string Translation)> prefixes = [];

    /// <summary>A message the program composed (M1126): the whole text when the table has it; else a pattern of the table with {0}, {1}…
    /// ("{0} was not found next to PGMS."), its parts kept; else the longest prefix of the table that ends with a space or a colon ("Save failed;
    /// query remains open: " + the error), the rest kept. Otherwise the text as it came.</summary>
    public static string Message(string text)
    {
        if (table.Count == 0 || text.Length == 0) return text;
        if (table.TryGetValue(text, out var whole) && whole.Length > 0) return whole;
        foreach (var (pattern, translation) in patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            return System.Text.RegularExpressions.Regex.Replace(translation, @"\{(\d+)\}", m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1 is int g && g < match.Groups.Count ? match.Groups[g].Value : m.Value);
        }
        foreach (var (english, translation) in prefixes)
            if (text.Length > english.Length && text.StartsWith(english, StringComparison.Ordinal)) return translation + text[english.Length..];
        return text;
    }

    private static void BuildMessages()
    {
        patterns = [.. table.Where(p => p.Value.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"\{\d+\}"))
            .OrderByDescending(p => System.Text.RegularExpressions.Regex.Replace(p.Key, @"\{\d+\}", "").Length)
            .Select(p => (new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Escape(p.Key), @"\\\{\d+}|\{\d+}", "(.+?)") + "$", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant), p.Value))];
        prefixes = [.. table.Where(p => p.Value.Length > 0 && (p.Key.EndsWith(": ") || p.Key.Length >= 10 && (p.Key.EndsWith(' ') || p.Key.EndsWith(':')))).OrderByDescending(p => p.Key.Length).Select(p => (p.Key, p.Value))];
    }

    /// <summary>Translates the fixed texts of an element and its logical children. Data is left alone: bound values, and the items of lists, grids
    /// and trees (object names, rows, files).</summary>
    public static void Translate(DependencyObject root)
    {
        if (table.Count == 0) return;
        Visit(root, 0);
    }

    private static void Visit(DependencyObject element, int depth)
    {
        if (depth > 200) return;
        // Canvases hold drawn data (the tables and columns of a diagram) and the stacked results panel holds result grids (M1129).
        if (GetNoTranslate(element) || element is Canvas or StackedResultsPanel) return;
        if (element is TextBoxBase or PasswordBox or ICSharpCode.AvalonEdit.TextEditor or DataGrid or TreeView or ListBox)
        {
            // The text is the user's; the accessible name and the tooltip of the box are interface text (M1129).
            Set(element, System.Windows.Automation.AutomationProperties.NameProperty);
            Set(element, FrameworkElement.ToolTipProperty);
            if (element is TextBoxBase or PasswordBox or ICSharpCode.AvalonEdit.TextEditor) return;
        }
        if (element is DataGrid or TreeView or ListBox or ListView && !GetTranslateItems(element)) return;
        switch (element)
        {
            case Window window: Set(window, Window.TitleProperty); break;
            case HeaderedItemsControl headered: Set(headered, HeaderedItemsControl.HeaderProperty); break;
            case HeaderedContentControl headered: Set(headered, HeaderedContentControl.HeaderProperty); break;
            case TextBlock: Set(element, TextBlock.TextProperty); break;
            case AccessText: Set(element, AccessText.TextProperty); break;
        }
        if (element is ContentControl and not Window) Set(element, ContentControl.ContentProperty);
        if (element is FrameworkElement framework)
        {
            if (framework.Name.Length > 0 || framework is Window) Watch(framework);
            Set(framework, FrameworkElement.ToolTipProperty);
            Set(framework, System.Windows.Automation.AutomationProperties.NameProperty);
            if (framework.ContextMenu is { } menu) Visit(menu, depth + 1);
        }
        if (element is ItemsControl items && items is not (Menu or MenuItem or ContextMenu or ComboBox or TabControl or StatusBar or ToolBar or TreeViewItem) && !GetTranslateItems(element)) return;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().ToList()) Visit(child, depth + 1);
    }

    private static void Set(DependencyObject element, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(element, property)) return;
        if (element.ReadLocalValue(property) is string text && text.Length > 0 && Message(text) is var translated && translated != text)
        {
            translating = true;
            try
            {
                if (property == HeaderedItemsControl.HeaderProperty || property == HeaderedContentControl.HeaderProperty) element.SetValue(EnglishHeaderProperty, text);
                element.SetValue(property, translated);
            }
            finally { translating = false; }
        }
    }

    /// <summary>The texts a named element (or a window) may get from code after it loaded: status lines, buttons whose caption changes, titles.
    /// Changes to them are translated with <see cref="Message"/> while the element is loaded (M1126).</summary>
    private static DependencyProperty[] WatchedProperties(FrameworkElement element) => element switch
    {
        Window => [Window.TitleProperty],
        TextBlock => [TextBlock.TextProperty, FrameworkElement.ToolTipProperty],
        HeaderedItemsControl => [HeaderedItemsControl.HeaderProperty, FrameworkElement.ToolTipProperty],
        HeaderedContentControl => [HeaderedContentControl.HeaderProperty, ContentControl.ContentProperty, FrameworkElement.ToolTipProperty],
        ContentControl => [ContentControl.ContentProperty, FrameworkElement.ToolTipProperty],
        _ => [FrameworkElement.ToolTipProperty],
    };

    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(Localization), new PropertyMetadata(false));
    private static readonly Dictionary<DependencyProperty, EventHandler> handlers = [];
    private static bool translating;

    private static void Watch(FrameworkElement element)
    {
        if ((bool)element.GetValue(WatchedProperty)) return;
        element.SetValue(WatchedProperty, true);
        element.Loaded += (_, _) => Hook(element, add: true);
        element.Unloaded += (_, _) => Hook(element, add: false);
        if (element.IsLoaded) Hook(element, add: true);
    }

    private static void Hook(FrameworkElement element, bool add)
    {
        foreach (var property in WatchedProperties(element))
        {
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, element.GetType());
            if (descriptor is null) continue;
            if (!handlers.TryGetValue(property, out var handler)) handlers[property] = handler = (sender, _) => Changed((DependencyObject)sender!, property);
            descriptor.RemoveValueChanged(element, handler);
            if (add) descriptor.AddValueChanged(element, handler);
        }
    }

    private static void Changed(DependencyObject element, DependencyProperty property)
    {
        if (translating || table.Count == 0 || BindingOperations.IsDataBound(element, property)) return;
        if (element.ReadLocalValue(property) is not string text || text.Length == 0) return;
        string translated = Message(text);
        bool header = property == HeaderedItemsControl.HeaderProperty || property == HeaderedContentControl.HeaderProperty;
        if (translated == text) { if (header) element.ClearValue(EnglishHeaderProperty); return; }
        translating = true;
        try
        {
            if (header) element.SetValue(EnglishHeaderProperty, text);
            element.SetValue(property, translated);
        }
        finally { translating = false; }
    }

    /// <summary>Marks an element whose texts are values, not interface text (the SSL modes of Connect, read back by the code): never translated.</summary>
    public static readonly DependencyProperty NoTranslateProperty = DependencyProperty.RegisterAttached("NoTranslate", typeof(bool), typeof(Localization), new PropertyMetadata(false));
    public static bool GetNoTranslate(DependencyObject element) => (bool)element.GetValue(NoTranslateProperty);
    public static void SetNoTranslate(DependencyObject element, bool value) => element.SetValue(NoTranslateProperty, value);

    /// <summary>Marks a list or tree whose items are fixed interface texts (the pages of Options), so they are translated; other lists hold data.</summary>
    public static readonly DependencyProperty TranslateItemsProperty = DependencyProperty.RegisterAttached("TranslateItems", typeof(bool), typeof(Localization), new PropertyMetadata(false));
    public static bool GetTranslateItems(DependencyObject element) => (bool)element.GetValue(TranslateItemsProperty);
    public static void SetTranslateItems(DependencyObject element, bool value) => element.SetValue(TranslateItemsProperty, value);

    /// <summary>The English header of a translated menu item or page: command names (Keyboard shortcuts, M1106) and page lookups use it, so they
    /// do not change with the language.</summary>
    public static readonly DependencyProperty EnglishHeaderProperty = DependencyProperty.RegisterAttached("EnglishHeader", typeof(string), typeof(Localization));

    /// <summary>The header as written in English: the original of a translated header, otherwise the header itself.</summary>
    public static object? EnglishHeader(DependencyObject element, object? header) => element.GetValue(EnglishHeaderProperty) as string ?? header;
}
