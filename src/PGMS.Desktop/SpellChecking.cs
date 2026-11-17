using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

/// <summary>
/// Edit ▸ Advanced ▸ Toggle Spell Checker (PGMS-080, M1109), off by default as in SSMS 22.11. Like the Visual Studio spell checker, it only looks
/// at comments and string literals, never at SQL words. The words go to the spell checker of Windows (Windows Spell Checking API, spellcheck.dll,
/// Windows 8 and later), in the language of the Windows user interface or English; nothing is downloaded and no text leaves the computer.
/// </summary>
public static class SqlSpellChecker
{
    /// <summary>The comment and string ranges of the SQL: -- to the end of the line, /* */ (nested, as PostgreSQL), '…' and E'…' with '' inside.
    /// Dollar-quoted bodies and quoted identifiers are left out: they hold code and names.</summary>
    public static IEnumerable<(int Start, int Length)> TextRanges(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i); if (end < 0) end = sql.Length;
                yield return (i + 2, end - i - 2); i = end;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int depth = 1, j = i + 2;
                while (j < sql.Length && depth > 0)
                {
                    if (sql[j] == '/' && j + 1 < sql.Length && sql[j + 1] == '*') { depth++; j += 2; }
                    else if (sql[j] == '*' && j + 1 < sql.Length && sql[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                }
                int inner = depth == 0 ? j - 2 : j;
                yield return (i + 2, Math.Max(0, inner - i - 2)); i = j;
            }
            else if (c == '\'')
            {
                int j = i + 1;
                while (j < sql.Length) { if (sql[j] == '\'') { if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; } break; } j++; }
                yield return (i + 1, j - i - 1); i = Math.Min(j + 1, sql.Length);
            }
            else if (c == '"') { int j = sql.IndexOf('"', i + 1); i = j < 0 ? sql.Length : j + 1; }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                int j = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal); i = j < 0 ? sql.Length : j + tag.Length;
            }
            else i++;
        }
    }

    private static string? DollarTag(string sql, int at)
    {
        int j = at + 1;
        while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
        return j < sql.Length && sql[j] == '$' && (j == at + 1 || !char.IsDigit(sql[at + 1])) ? sql[at..(j + 1)] : null;
    }

    /// <summary>The misspelled words of the comments and strings, as offsets in <paramref name="sql"/>. Empty when no checker is available.</summary>
    public static IReadOnlyList<(int Start, int Length)> Misspellings(string sql, Func<string, IEnumerable<(int Start, int Length)>>? check = null)
    {
        check ??= Check;
        var result = new List<(int, int)>();
        foreach (var (start, length) in TextRanges(sql))
            if (length > 0) foreach (var (s, l) in check(sql.Substring(start, length))) result.Add((start + s, l));
        return result;
    }

    /// <summary>The language used: the Windows display language when the checker supports it, else en-US; null when Windows has no checker.</summary>
    public static string? Language => checker.Value.Language;

    /// <summary>Errors that Windows reports for a text; words with digits or underscores (identifiers) are skipped.</summary>
    public static IEnumerable<(int Start, int Length)> Check(string text)
    {
        if (checker.Value.Checker is not { } spell) return [];
        var found = new List<(int, int)>();
        lock (checker.Value)
        {
            try
            {
                spell.Check(text, out var errors);
                while (errors.Next(out var error) == 0 && error is not null)
                {
                    int start = (int)error.StartIndex, length = (int)error.Length;
                    if (start + length <= text.Length && !text.AsSpan(start, length).ContainsAny("0123456789_")) found.Add((start, length));
                    Marshal.ReleaseComObject(error);
                }
                Marshal.ReleaseComObject(errors);
            }
            catch (COMException) { }
        }
        return found;
    }

    private sealed record Engine(ISpellChecker? Checker, string? Language);
    private static readonly Lazy<Engine> checker = new(CreateChecker);

    private static Engine CreateChecker()
    {
        try
        {
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), true)!)!;
            foreach (var language in new[] { CultureInfo.CurrentUICulture.Name, "en-US" })
            {
                if (string.IsNullOrEmpty(language)) continue;
                factory.IsSupported(language, out int supported);
                if (supported == 0) continue;
                factory.CreateSpellChecker(language, out var spell);
                return new(spell, language);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or ArgumentException or NotSupportedException) { }
        return new(null, null);
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void get_SupportedLanguages(out IEnumString languages);
        void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        void get_LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string value);
        void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
    }
}

public partial class QueryView
{
    private SpellRenderer? spellRenderer;
    private IReadOnlyList<(int Start, int Length)> misspellings = [];
    private System.Windows.Threading.DispatcherTimer? spellTimer;

    /// <summary>Toggle Spell Checker for this editor; off by default.</summary>
    internal bool SpellCheck
    {
        get => spellRenderer is not null;
        set
        {
            if (value == SpellCheck) return;
            if (value)
            {
                spellRenderer = new SpellRenderer(this);
                Editor.TextArea.TextView.BackgroundRenderers.Add(spellRenderer);
                spellTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                spellTimer.Tick += (_, _) => { spellTimer!.Stop(); RefreshSpelling(); };
                Editor.TextChanged += SpellTextChanged;
                RefreshSpelling();
            }
            else
            {
                Editor.TextArea.TextView.BackgroundRenderers.Remove(spellRenderer!);
                Editor.TextChanged -= SpellTextChanged;
                spellTimer?.Stop(); spellTimer = null; spellRenderer = null; misspellings = [];
                Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            }
        }
    }

    /// <summary>The underlined words; tests replace the checker.</summary>
    internal IReadOnlyList<(int Start, int Length)> Misspellings => misspellings;
    internal Func<string, IEnumerable<(int Start, int Length)>>? SpellCheckOverride { get; set; }

    private void SpellTextChanged(object? sender, EventArgs e) { spellTimer?.Stop(); spellTimer?.Start(); }

    /// <summary>Checks the whole text again (after a pause in typing, or at once from tests).</summary>
    internal void RefreshSpelling()
    {
        if (!SpellCheck) return;
        misspellings = SqlSpellChecker.Misspellings(Editor.Text, SpellCheckOverride);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private sealed class SpellRenderer(QueryView view) : IBackgroundRenderer
    {
        private static readonly Pen Wave = CreatePen();
        private static Pen CreatePen() { var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x91, 0xAF)), 1); pen.Freeze(); return pen; }
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            foreach (var (start, length) in view.misspellings)
            {
                if (start + length > textView.Document.TextLength) continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, Length = length }))
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        double y = rect.Bottom - 1, x = rect.Left;
                        context.BeginFigure(new Point(x, y), false, false);
                        for (bool up = true; x < rect.Right; up = !up) { x = Math.Min(x + 2, rect.Right); context.LineTo(new Point(x, up ? y - 2 : y), true, false); }
                    }
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, Wave, geometry);
                }
            }
        }
    }
}

public partial class MainWindow
{
    /// <summary>Edit ▸ Advanced ▸ Toggle Spell Checker for the active query editor.</summary>
    private void ToggleSpellChecker(object sender, RoutedEventArgs e)
    {
        if (ActiveQuery is not { } query) return;
        query.SpellCheck = !query.SpellCheck;
        if (sender is MenuItem item) item.IsChecked = query.SpellCheck;
        Status.Text = !query.SpellCheck ? "Spell checker off." : SqlSpellChecker.Language is { } language ? $"Spell checker on ({language}): comments and strings are checked." : "Spell checker on, but Windows has no spell checker for this language.";
    }
}
