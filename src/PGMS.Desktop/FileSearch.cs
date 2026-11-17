using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PGMS.Desktop;

/// <summary>What to look for in Find in Files / Replace in Files (PGMS-066, M1091).</summary>
public sealed record FileSearchOptions(string Pattern, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false)
{
    /// <summary>The pattern as a regular expression; throws ArgumentException for an empty pattern or an invalid expression.</summary>
    public Regex ToRegex()
    {
        if (string.IsNullOrEmpty(Pattern)) throw new ArgumentException("Enter the text to find.");
        string body = UseRegex ? Pattern : Regex.Escape(Pattern);
        if (WholeWord) body = @"(?<![\p{L}\p{Nd}_])(?:" + body + @")(?![\p{L}\p{Nd}_])";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(body, options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { throw new ArgumentException("The regular expression is not valid: " + ex.Message, ex); }
    }
}

/// <summary>One match: 1-based line and column, the offset and length in the text, and the line it is on.</summary>
public sealed record FileMatch(string Path, string DisplayName, int Line, int Column, int Offset, int Length, string LineText)
{
    /// <summary>The query tab the text came from, when it was searched in an open document.</summary>
    public QueryView? Query { get; init; }
}

/// <summary>Search and replace over text, files and folders; files opened in a query tab are given by their editor text.</summary>
public static class FileSearch
{
    public const int MaximumMatches = 10_000;
    public const long MaximumFileBytes = SqlFileStore.MaximumBytes;

    /// <summary>The file types box: patterns separated by ";" or ","; empty means *.*.</summary>
    public static string[] ParseTypes(string? types)
    {
        var list = (types ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? ["*.*"] : list;
    }

    public static IEnumerable<string> EnumerateFiles(string folder, string? types, bool subfolders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = subfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var pattern in ParseTypes(types))
            foreach (var file in Directory.EnumerateFiles(folder, pattern, enumeration))
                if (seen.Add(file)) yield return file;
    }

    public static IEnumerable<FileMatch> Matches(string path, string displayName, string text, Regex regex)
    {
        int line = 1, lineStart = 0, scanned = 0;
        foreach (Match match in regex.Matches(text))
        {
            if (match.Length == 0) continue;
            for (; scanned < match.Index; scanned++)
                if (text[scanned] == '\n') { line++; lineStart = scanned + 1; }
            int end = text.IndexOf('\n', match.Index);
            string lineText = text[lineStart..(end < 0 ? text.Length : end)].TrimEnd('\r');
            yield return new FileMatch(path, displayName, line, match.Index - lineStart + 1, match.Index, match.Length, lineText);
        }
    }

    /// <summary>Text with every match replaced; without regular expressions the replacement is literal ($ is not special).</summary>
    public static (string Text, int Count) Replace(string text, Regex regex, string replacement, bool useRegex)
    {
        int count = 0;
        string result = regex.Replace(text, m => { if (m.Length == 0) return m.Value; count++; return useRegex ? m.Result(replacement) : replacement; });
        return (result, count);
    }

    /// <summary>A file read for searching: null when it is too big, not readable or looks binary (has a NUL in its first bytes).</summary>
    public static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes) return null;
            byte[] bytes = File.ReadAllBytes(path);
            bool unicodeBom = bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF);
            if (!unicodeBom && Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) return null;
            return SqlFileStore.Read(path).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException or ArgumentException) { return null; }
    }
}
