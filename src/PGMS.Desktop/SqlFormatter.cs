using System.Text;

namespace PGMS.Desktop;

internal enum SqlTokenKind { Word, QuotedIdentifier, String, Number, Parameter, Operator, OpenParen, CloseParen, OpenBracket, CloseBracket, Comma, Semicolon, Dot, LineComment, BlockComment, Other }

internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Start, bool NewlineBefore)
{
    internal bool IsComment => Kind is SqlTokenKind.LineComment or SqlTokenKind.BlockComment;
    internal bool IsWord(string word) => Kind == SqlTokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Edit ▸ Advanced ▸ Format SQL (PGMS-055). Layout modelled on SSMS 22.11 as piloted in M1077: keywords in
/// uppercase, one list item per line, clause bodies aligned after the longest clause keyword, AND/OR and joins on their
/// own lines, subqueries aligned inside their parenthesis, a blank line between statements. Only whitespace and the case
/// of unquoted keywords change; the result is re-read and compared token by token before it is accepted.</summary>
internal static class SqlFormatter
{
    internal const int MaximumCharacters = 1_000_000;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL","ALTER","ANALYZE","AND","ANY","ARRAY","AS","ASC","ASYMMETRIC","BEGIN","BETWEEN","BOTH","BY","CASCADE","CASE","CAST","CHECK","COLLATE","COLUMN","COMMIT",
        "CONFLICT","CONSTRAINT","CREATE","CROSS","CURRENT_DATE","CURRENT_TIME","CURRENT_TIMESTAMP","CURRENT_USER","DEFAULT","DEFERRABLE","DELETE","DESC","DISTINCT","DO","DROP",
        "ELSE","END","EXCEPT","EXISTS","EXPLAIN","FALSE","FETCH","FILTER","FIRST","FOLLOWING","FOR","FOREIGN","FROM","FULL","GRANT","GROUP","GROUPING","HAVING","IF","ILIKE","IN",
        "INDEX","INNER","INSERT","INTERSECT","INTERVAL","INTO","IS","ISNULL","JOIN","KEY","LAST","LATERAL","LEADING","LEFT","LIKE","LIMIT","LOCAL","LOCALTIME","LOCALTIMESTAMP",
        "MATERIALIZED","NATURAL","NEXT","NO","NOT","NOTHING","NOTNULL","NULL","NULLS","OF","OFFSET","ON","ONLY","OR","ORDER","OUTER","OVER","OVERLAPS","PARTITION","PRECEDING",
        "PRIMARY","RANGE","RECURSIVE","REFERENCES","REPLACE","RESTRICT","RETURNING","REVOKE","RIGHT","ROLLBACK","ROW","ROWS","SCHEMA","SELECT","SESSION_USER","SET","SETS",
        "SHARE","SIMILAR","SOME","SYMMETRIC","TABLE","TEMP","TEMPORARY","THEN","TIES","TO","TRAILING","TRUE","TRUNCATE","UNBOUNDED","UNION","UNIQUE","UNLOGGED","UPDATE",
        "USING","VALUES","VARIADIC","VERBOSE","VIEW","WHEN","WHERE","WINDOW","WITH","WITHIN","WITHOUT","ZONE",
        "BIGINT","BIGSERIAL","BIT","BOOL","BOOLEAN","BYTEA","CHAR","CHARACTER","DATE","DECIMAL","DOUBLE","FLOAT","INT","INTEGER","JSON","JSONB","NUMERIC","PRECISION","REAL",
        "SERIAL","SMALLINT","TEXT","TIME","TIMESTAMP","TIMESTAMPTZ","UUID","VARCHAR","VARYING",
    };
    /// <summary>Keywords that are also function names: before "(" they keep their case, like count or max in SSMS.</summary>
    private static readonly HashSet<string> FunctionLike = new(StringComparer.OrdinalIgnoreCase) { "LEFT","RIGHT","REPLACE","COALESCE","NULLIF","GREATEST","LEAST","EXTRACT","POSITION","SUBSTRING","TRIM","OVERLAY","FORMAT" };
    private static readonly HashSet<string> TypeNames = new(StringComparer.OrdinalIgnoreCase) { "BIGINT","BIT","CHAR","CHARACTER","DECIMAL","FLOAT","INTERVAL","NUMERIC","TIME","TIMESTAMP","TIMESTAMPTZ","VARCHAR","VARYING" };

    // ---- Tokenizer -------------------------------------------------------------------------------------------------

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c > 127 && !char.IsWhiteSpace(c);
    private static bool IsWordPart(char c) => IsWordStart(c) || char.IsDigit(c) || c == '$';
    private const string OperatorChars = "+-*/<>=~!@#%^&|`?:";

    /// <summary>Splits SQL into tokens; throws <see cref="FormatException"/> when a string, quoted name or comment is not closed.</summary>
    internal static List<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        int i = 0; bool newline = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { if (c is '\n' or '\r') newline = true; i++; continue; }
            int start = i; SqlTokenKind kind;
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '-' && next == '-') { while (i < text.Length && text[i] is not ('\n' or '\r')) i++; kind = SqlTokenKind.LineComment; }
            else if (c == '/' && next == '*')
            {
                int depth = 0;
                while (true)
                {
                    if (i + 1 >= text.Length) throw new FormatException("a /* comment is not closed");
                    if (text[i] == '/' && text[i + 1] == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && text[i + 1] == '/') { depth--; i += 2; if (depth == 0) break; }
                    else i++;
                }
                kind = SqlTokenKind.BlockComment;
            }
            else if (c == '\'' ) { i = QuotedEnd(text, i, '\'', backslash: false); kind = SqlTokenKind.String; }
            else if (c == '"') { i = QuotedEnd(text, i, '"', backslash: false); kind = SqlTokenKind.QuotedIdentifier; }
            else if ((c is 'e' or 'E') && next == '\'') { i = QuotedEnd(text, i + 1, '\'', backslash: true); kind = SqlTokenKind.String; }
            else if ((c is 'b' or 'B' or 'x' or 'X' or 'n' or 'N') && next == '\'') { i = QuotedEnd(text, i + 1, '\'', backslash: false); kind = SqlTokenKind.String; }
            else if ((c is 'u' or 'U') && next == '&' && i + 2 < text.Length && text[i + 2] is '\'' or '"')
            {
                bool name = text[i + 2] == '"';
                i = QuotedEnd(text, i + 2, text[i + 2], backslash: false); kind = name ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.String;
            }
            else if (c == '$' && char.IsDigit(next)) { i++; while (i < text.Length && char.IsDigit(text[i])) i++; kind = SqlTokenKind.Parameter; }
            else if (c == '$' && DollarTag(text, i) is { } tag)
            {
                int close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (close < 0) throw new FormatException($"the dollar-quoted string {tag} is not closed");
                i = close + tag.Length; kind = SqlTokenKind.String;
            }
            else if (IsWordStart(c)) { while (i < text.Length && IsWordPart(text[i])) i++; kind = SqlTokenKind.Word; }
            else if (char.IsDigit(c) || c == '.' && char.IsDigit(next))
            {
                bool hex = c == '0' && next is 'x' or 'X' or 'o' or 'O' or 'b' or 'B';
                i++;
                while (i < text.Length)
                {
                    char d = text[i];
                    if (!char.IsLetterOrDigit(d) && d is not ('_' or '.')) break;
                    if (d == '.' && i + 1 < text.Length && text[i + 1] == '.') break;
                    i++;
                    if (!hex && d is 'e' or 'E' && i < text.Length && text[i] is '+' or '-') i++;
                }
                kind = SqlTokenKind.Number;
            }
            else if (OperatorChars.Contains(c))
            {
                i++;
                while (i < text.Length && OperatorChars.Contains(text[i]) && !(text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-') && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')) i++;
                // As in PostgreSQL: a multi-character operator cannot end in + or - unless it has one of ~!@#%^&|`?.
                while (i - start > 1 && text[i - 1] is '+' or '-' && text.AsSpan(start, i - start).IndexOfAny("~!@#%^&|`?") < 0) i--;
                kind = SqlTokenKind.Operator;
            }
            else
            {
                i++;
                kind = c switch { '(' => SqlTokenKind.OpenParen, ')' => SqlTokenKind.CloseParen, '[' => SqlTokenKind.OpenBracket, ']' => SqlTokenKind.CloseBracket, ',' => SqlTokenKind.Comma, ';' => SqlTokenKind.Semicolon, '.' => SqlTokenKind.Dot, _ => SqlTokenKind.Other };
            }
            tokens.Add(new SqlToken(kind, text[start..i], start, newline));
            newline = false;
        }
        return tokens;
    }

    private static int QuotedEnd(string text, int open, char quote, bool backslash)
    {
        int i = open + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == quote) { if (i + 1 < text.Length && text[i + 1] == quote) { i += 2; continue; } return i + 1; }
            i++;
        }
        throw new FormatException(quote == '"' ? "a quoted name is not closed" : "a string is not closed");
    }

    private static string? DollarTag(string text, int i)
    {
        int j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) { if (j == i + 1 && char.IsDigit(text[j])) return null; j++; }
        return j < text.Length && text[j] == '$' ? text[i..(j + 1)] : null;
    }

    // ---- Writer ----------------------------------------------------------------------------------------------------

    private sealed class Writer(string newline)
    {
        private readonly List<StringBuilder> lines = [new()];
        private StringBuilder Current => lines[^1];
        private int pendingColumn = -1;
        internal SqlToken? Last;
        internal int Column { get { var s = Current; for (int i = s.Length - 1; i >= 0; i--) if (s[i] == '\n') return s.Length - i - 1; return s.Length; } }
        internal bool AtLineStart => Current.ToString().Trim().Length == 0 || Column == 0;
        internal void BreakBeforeNext(int column) => pendingColumn = column;
        internal void NewLine(int column)
        {
            pendingColumn = -1;
            TrimEnd(Current);
            lines.Add(new StringBuilder().Append(' ', column)); Last = null;
        }
        internal void BlankLine() { pendingColumn = -1; TrimEnd(Current); if (Current.Length == 0 && lines.Count == 1) return; lines.Add(new StringBuilder()); lines.Add(new StringBuilder()); Last = null; }
        internal void PadTo(int column) { int col = Column; Current.Append(' ', col < column ? column - col : 1); }
        internal void Write(SqlToken token, string text, bool space)
        {
            if (pendingColumn >= 0) NewLine(pendingColumn);
            if (space && Current.Length > 0 && Current[^1] != ' ' && Current[^1] != '\n') Current.Append(' ');
            Current.Append(text); Last = token;
        }
        internal void WriteRaw(string text) { if (pendingColumn >= 0) NewLine(pendingColumn); Current.Append(text); Last = null; }
        private static void TrimEnd(StringBuilder s) { int n = s.Length; while (n > 0 && s[n - 1] == ' ') n--; s.Length = n; }
        public override string ToString() { TrimEnd(Current); return string.Join(newline, lines.Select(l => l.ToString())); }
    }

    // ---- Formatter -------------------------------------------------------------------------------------------------

    /// <summary>Formats <paramref name="text"/>; throws <see cref="FormatException"/> with the reason when it cannot be read.</summary>
    internal static string Format(string text, string newline)
    {
        if (text.Length > MaximumCharacters) throw new FormatException("Format SQL handles at most 1,000,000 characters");
        var tokens = Tokenize(text);
        string result = Layout(tokens, text, newline);
        if (!SameTokens(tokens, Tokenize(result))) throw new InvalidOperationException("Format SQL stopped because the formatted text would not read the same as the original; nothing was changed.");
        return result;
    }

    /// <summary>The layout without the final comparison (used by <see cref="Format"/> and by the tests).</summary>
    internal static string Layout(List<SqlToken> tokens, string text, string newline)
    {
        var writer = new Writer(newline);
        new Formatter(tokens, text, writer).Run();
        string result = writer.ToString();
        if (text.EndsWith('\n') || text.EndsWith('\r')) result += newline;
        return result;
    }

    internal static bool SameTokens(List<SqlToken> a, List<SqlToken> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind) return false;
            if (!string.Equals(a[i].Text, b[i].Text, a[i].Kind == SqlTokenKind.Word ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private enum Body { Plain, List, Condition, From, Values, Insert }

    private sealed class Formatter(List<SqlToken> tokens, string source, Writer w)
    {
        internal void Run()
        {
            int i = 0; bool first = true;
            while (i < tokens.Count)
            {
                // Leading comments of the statement, each on its own line.
                int start = i;
                if (!first) w.BlankLine();
                while (i < tokens.Count && tokens[i].IsComment) { if (i > start) w.NewLine(0); w.WriteRaw(tokens[i].Text); i++; }
                if (i >= tokens.Count) break;
                if (i > start) w.NewLine(0);
                int end = StatementEnd(i);
                Statement(i, end);
                i = end;
                // A comment on the same line as the end of the statement stays there.
                while (i < tokens.Count && tokens[i].IsComment && !tokens[i].NewlineBefore) { w.Write(tokens[i], tokens[i].Text, true); if (tokens[i].Kind == SqlTokenKind.LineComment) w.BreakBeforeNext(0); i++; }
                first = false;
            }
        }

        private int StatementEnd(int i)
        {
            int depth = 0;
            for (; i < tokens.Count; i++)
            {
                var k = tokens[i].Kind;
                if (k is SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket) depth++;
                else if (k is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket) { if (--depth < 0) throw new FormatException("a closing parenthesis has no opening one"); }
                else if (k == SqlTokenKind.Semicolon && depth == 0) return i + 1;
            }
            if (depth != 0) throw new FormatException("a parenthesis is not closed");
            return i;
        }

        private int Next(int i, int end) { for (int j = i + 1; j < end; j++) if (!tokens[j].IsComment) return j; return -1; }
        private bool WordAt(int i, int end, string word) => i >= 0 && i < end && tokens[i].IsWord(word);
        private int Match(int open)
        {
            int depth = 0;
            for (int i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Kind is SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket) depth++;
                else if (tokens[i].Kind is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket && --depth == 0) return i;
            }
            throw new FormatException("a parenthesis is not closed");
        }
        private static bool StartsQuery(SqlToken t) => t.IsWord("SELECT") || t.IsWord("WITH") || t.IsWord("VALUES");

        private void Statement(int start, int end)
        {
            int last = end; bool semicolon = end > start && tokens[end - 1].Kind == SqlTokenKind.Semicolon;
            if (semicolon) last--;
            var head = tokens[start];
            if (start == last) { WriteToken(start, false); return; }
            if (StartsQuery(head) || head.IsWord("INSERT") || head.IsWord("UPDATE") || head.IsWord("DELETE")) Query(start, last, 0);
            else if (head.IsWord("EXPLAIN") || head.IsWord("CREATE") && QueryAfterAs(start, last) >= 0)
            {
                int q = head.IsWord("EXPLAIN") ? FirstQueryWord(start, last) : QueryAfterAs(start, last);
                if (q < 0) { Verbatim(start, last); }
                else { Inline(start, q, 0, Body.Plain); w.NewLine(0); Query(q, last, 0); }
            }
            else if (head.IsWord("CREATE") && CreateTableParen(start, last) is int open and >= 0) CreateTable(start, open, last);
            else Verbatim(start, last);
            if (semicolon) WriteToken(end - 1, false);
        }

        private int FirstQueryWord(int start, int end)
        {
            for (int i = start; i < end; i++) { if (tokens[i].Kind == SqlTokenKind.OpenParen) i = Match(i); else if (StartsQuery(tokens[i]) || tokens[i].IsWord("INSERT") || tokens[i].IsWord("UPDATE") || tokens[i].IsWord("DELETE")) return i; }
            return -1;
        }

        /// <summary>CREATE [OR REPLACE] [MATERIALIZED] VIEW … AS SELECT: index of the query, or -1.</summary>
        private int QueryAfterAs(int start, int end)
        {
            bool view = false;
            for (int i = start; i < end; i++)
            {
                if (tokens[i].Kind == SqlTokenKind.OpenParen) { i = Match(i); continue; }
                if (tokens[i].IsWord("VIEW")) view = true;
                if (view && tokens[i].IsWord("AS")) { int q = Next(i, end); return q >= 0 && StartsQuery(tokens[q]) ? q : -1; }
            }
            return -1;
        }

        private int CreateTableParen(int start, int end)
        {
            bool table = false;
            for (int i = start; i < end; i++)
            {
                if (tokens[i].IsWord("TABLE")) table = true;
                else if (tokens[i].IsWord("AS") || tokens[i].IsWord("OF") || tokens[i].IsWord("PARTITION")) return -1;
                else if (tokens[i].Kind == SqlTokenKind.OpenParen) return table ? i : -1;
            }
            return -1;
        }

        /// <summary>Keeps the source text of a statement the formatter does not lay out (functions, DO blocks, DDL, MERGE…).</summary>
        private void Verbatim(int start, int end)
        {
            var a = tokens[start]; var b = tokens[end - 1];
            w.WriteRaw(source[a.Start..(b.Start + b.Text.Length)]);
        }

        private void CreateTable(int start, int open, int end)
        {
            Inline(start, open, 0, Body.Plain);
            WriteToken(open, true);
            int close = Match(open), item = open + 1;
            for (int i = open + 1; i <= close; i++)
            {
                if (tokens[i].Kind == SqlTokenKind.OpenParen) { i = Match(i); continue; }
                if (tokens[i].Kind is SqlTokenKind.Comma || i == close)
                {
                    if (item < i) { w.NewLine(4); Inline(item, i, 4, Body.Plain); }
                    if (i < close) WriteToken(i, false);
                    item = i + 1;
                }
            }
            w.NewLine(0); WriteToken(close, false);
            if (close + 1 < end) Inline(close + 1, end, 0, Body.Plain);
        }

        private readonly record struct Clause(int Start, int KeywordEnd, int End, string Keyword, bool Separator, Body Body);

        /// <summary>Lays out a query (SELECT, WITH, VALUES, INSERT, UPDATE, DELETE) whose first line continues at the
        /// writer's position and whose other lines start at <paramref name="baseColumn"/>.</summary>
        private void Query(int start, int end, int baseColumn)
        {
            var clauses = Clauses(start, end);
            int width = clauses.Where(c => !c.Separator).Select(c => c.Keyword.Length).DefaultIfEmpty(0).Max() + 1;
            int bodyColumn = baseColumn + width;
            for (int n = 0; n < clauses.Count; n++)
            {
                var c = clauses[n];
                if (n > 0) w.NewLine(baseColumn);
                for (int k = c.Start; k < c.KeywordEnd; k++) if (tokens[k].IsComment) WriteToken(k, true); else w.Write(tokens[k], tokens[k].Text.ToUpperInvariant(), k > c.Start);
                if (c.Separator) { if (c.KeywordEnd < c.End) Inline(c.KeywordEnd, c.End, baseColumn, Body.Plain); continue; }
                if (c.KeywordEnd >= c.End) continue;
                w.PadTo(bodyColumn);
                Inline(c.KeywordEnd, c.End, bodyColumn, c.Body);
            }
        }

        private List<Clause> Clauses(int start, int end)
        {
            var result = new List<Clause>();
            string kind = tokens[start].Text.ToUpperInvariant();
            bool afterWith = false;
            int depth = 0, current = -1, keywordEnd = -1; string keyword = ""; bool separator = false; Body body = Body.Plain;
            void Close(int at) { if (current >= 0) result.Add(new Clause(current, keywordEnd, at, keyword, separator, body)); }
            for (int i = start; i < end; i++)
            {
                var t = tokens[i];
                if (t.Kind is SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket) { depth++; continue; }
                if (t.Kind is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket) { depth--; continue; }
                if (depth != 0 || t.Kind != SqlTokenKind.Word) continue;
                int n1 = Next(i, end);
                int prev = Previous(i, start);
                (int words, Body b, bool sep)? found = null;
                string u = t.Text.ToUpperInvariant();
                switch (u)
                {
                    case "SELECT": found = (1, Body.List, false); break;
                    case "WITH" when i == start: found = (1, Body.List, false); afterWith = true; break;
                    case "INSERT" when i == start || afterWith: found = (1, Body.Insert, false); kind = "INSERT"; break;
                    case "UPDATE" when i == start || afterWith && prev >= 0 && tokens[prev].Kind == SqlTokenKind.CloseParen: found = (1, Body.Plain, false); kind = "UPDATE"; break;
                    case "DELETE" when i == start || afterWith && prev >= 0 && tokens[prev].Kind == SqlTokenKind.CloseParen: found = (1, Body.Plain, false); kind = "DELETE"; break;
                    case "FROM" when !(kind == "DELETE" && current >= 0 && keyword == "DELETE" && keywordEnd == i) && !(prev >= 0 && tokens[prev].IsWord("DISTINCT")): found = (1, Body.From, false); break;
                    case "WHERE": found = (1, Body.Condition, false); break;
                    case "HAVING": found = (1, Body.Condition, false); break;
                    case "GROUP" or "ORDER" when WordAt(n1, end, "BY"): found = (2, Body.List, false); break;
                    case "LIMIT" or "OFFSET" or "FETCH": found = (1, Body.Plain, false); break;
                    case "WINDOW" or "RETURNING": found = (1, Body.List, false); break;
                    case "FOR" when WordAt(n1, end, "UPDATE") || WordAt(n1, end, "SHARE") || WordAt(n1, end, "NO") || WordAt(n1, end, "KEY"): found = (1, Body.Plain, false); break;
                    case "SET" when kind == "UPDATE" && keyword == "UPDATE" || prev >= 0 && tokens[prev].IsWord("UPDATE") && Previous(prev, start) is int p2 and >= 0 && tokens[p2].IsWord("DO"): found = (1, Body.List, false); break;
                    case "VALUES" when !(prev >= 0 && tokens[prev].IsWord("DEFAULT")): found = (1, Body.Values, false); break;
                    case "USING" when kind == "DELETE": found = (1, Body.From, false); break;
                    case "ON" when kind == "INSERT" && WordAt(n1, end, "CONFLICT"): found = (2, Body.Plain, false); break;
                    case "UNION" or "INTERSECT" or "EXCEPT": found = (WordAt(n1, end, "ALL") || WordAt(n1, end, "DISTINCT") ? 2 : 1, Body.Plain, true); break;
                }
                if (found is not { } f) continue;
                Close(i);
                current = i; separator = f.sep; body = f.b;
                int at = i; var words = new List<string> { tokens[i].Text.ToUpperInvariant() };
                for (int m = 1; m < f.words; m++) { at = Next(at, end); words.Add(tokens[at].Text.ToUpperInvariant()); }
                int kEnd = at + 1;
                keywordEnd = kEnd; keyword = string.Join(' ', words);
                i = kEnd - 1;
            }
            if (current < 0) { result.Add(new Clause(start, start, end, "", false, Body.Plain)); return result; }
            Close(end);
            if (result[0].Start > start) result.Insert(0, new Clause(start, start, result[0].Start, "", false, Body.Plain));
            return result;
        }

        private int Previous(int i, int start) { for (int j = i - 1; j >= start; j--) if (!tokens[j].IsComment) return j; return -1; }

        private static bool IsJoinWord(SqlToken t) => t.IsWord("JOIN") || t.IsWord("INNER") || t.IsWord("LEFT") || t.IsWord("RIGHT") || t.IsWord("FULL") || t.IsWord("CROSS") || t.IsWord("NATURAL") || t.IsWord("OUTER");

        /// <summary>Writes tokens on the current line, breaking at depth 0 according to <paramref name="body"/>; continuation lines start at <paramref name="column"/>.</summary>
        private void Inline(int start, int end, int column, Body body)
        {
            int depth = 0, caseDepth = 0; bool between = false;
            for (int i = start; i < end; i++)
            {
                var t = tokens[i];
                if (t.IsComment)
                {
                    if (t.NewlineBefore && !w.AtLineStart) w.NewLine(column);
                    WriteToken(i, true);
                    if (t.Kind == SqlTokenKind.LineComment) w.BreakBeforeNext(column);
                    continue;
                }
                if (t.Kind == SqlTokenKind.OpenParen)
                {
                    if (i + 1 < end && StartsQuery(tokens[i + 1]))
                    {
                        int close = Match(i);
                        WriteToken(i, SpaceBefore(i));
                        Query(i + 1, close, w.Column);
                        WriteToken(close, false);
                        i = close; continue;
                    }
                    WriteToken(i, SpaceBefore(i) || depth == 0 && body == Body.Insert); depth++; continue;
                }
                if (t.Kind is SqlTokenKind.OpenBracket) { WriteToken(i, false); depth++; continue; }
                if (t.Kind is SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket) { depth--; WriteToken(i, false); continue; }
                if (depth == 0)
                {
                    if (t.IsWord("CASE")) caseDepth++;
                    else if (t.IsWord("END") && caseDepth > 0) caseDepth--;
                    if (t.Kind == SqlTokenKind.Comma && caseDepth == 0 && body is Body.List or Body.Values or Body.From) { WriteToken(i, false); w.BreakBeforeNext(column); continue; }
                    if (t.IsWord("BETWEEN")) between = true;
                    if ((t.IsWord("AND") || t.IsWord("OR")) && caseDepth == 0 && body is Body.Condition or Body.From)
                    {
                        if (t.IsWord("AND") && between) between = false;
                        else { w.NewLine(column); WriteToken(i, false); continue; }
                    }
                    if (body == Body.From && caseDepth == 0 && IsJoinWord(t) && !(w.Last is { } last && IsJoinWord(last)))
                    {
                        int j = i; while (j < end && IsJoinWord(tokens[j])) j++;
                        if (j > i && tokens[j - 1].IsWord("JOIN"))
                        {
                            w.NewLine(column);
                            for (int k = i; k < j; k++) WriteToken(k, k > i);
                            w.BreakBeforeNext(column);
                            i = j - 1; continue;
                        }
                    }
                    if (body == Body.From && caseDepth == 0 && (t.IsWord("ON") || t.IsWord("USING"))) { w.NewLine(column); WriteToken(i, false); continue; }
                }
                WriteToken(i, SpaceBefore(i));
            }
        }

        private bool IsKeywordAt(int i)
        {
            var t = tokens[i];
            if (t.Kind != SqlTokenKind.Word || !Keywords.Contains(t.Text)) return false;
            if (i > 0 && tokens[i - 1].Kind == SqlTokenKind.Dot) return false;
            if (i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Dot) return false;
            if (i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.OpenParen && FunctionLike.Contains(t.Text)) return false;
            return true;
        }

        private void WriteToken(int i, bool space)
        {
            var t = tokens[i];
            w.Write(t, IsKeywordAt(i) ? t.Text.ToUpperInvariant() : t.Text, space);
        }

        private bool IsUnaryAt(int i)
        {
            var t = tokens[i];
            if (t.Kind != SqlTokenKind.Operator || t.Text is not ("-" or "+")) return false;
            int p = i - 1; while (p >= 0 && tokens[p].IsComment) p--;
            if (p < 0) return true;
            var prev = tokens[p];
            return prev.Kind is SqlTokenKind.Operator or SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket or SqlTokenKind.Comma or SqlTokenKind.Semicolon || prev.Kind == SqlTokenKind.Word && IsKeywordAt(p) && !TypeNames.Contains(prev.Text) && !prev.IsWord("NULL") && !prev.IsWord("TRUE") && !prev.IsWord("FALSE") && !prev.IsWord("END");
        }

        private bool SpaceBefore(int i)
        {
            var t = tokens[i];
            if (w.Last is not { } prev) return false;
            int p = i - 1; while (p >= 0 && tokens[p].IsComment) p--;
            if (prev.IsComment) return true;
            if (t.Kind is SqlTokenKind.Comma or SqlTokenKind.Semicolon or SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket or SqlTokenKind.Dot or SqlTokenKind.OpenBracket) return false;
            if (prev.Kind is SqlTokenKind.Dot or SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket) return false;
            if (t.Text == "::" || prev.Text == "::" || t.Text == ":" || prev.Text == ":") return false;
            if (p >= 0 && IsUnaryAt(p)) return false;
            if (t.Kind == SqlTokenKind.OpenParen)
            {
                if (prev.Kind is SqlTokenKind.QuotedIdentifier) return false;
                if (prev.Kind == SqlTokenKind.Word) return p >= 0 && IsKeywordAt(p) && !TypeNames.Contains(prev.Text);
                return true;
            }
            return true;
        }
    }
}
