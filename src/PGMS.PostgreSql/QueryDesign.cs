using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum DesignJoinKind { Inner, Left, Right, Full }
public enum DesignSort { None, Ascending, Descending }
/// <summary>The kinds of statement the Query Designer writes (Change Type in SSMS 22.11, docs/MARCO-M1047.md).</summary>
public enum DesignStatement { Select, InsertResults, InsertValues, Update, Delete, MakeTable }

/// <summary>A table or view in the FROM clause. Schema is what the SQL wrote (null when unqualified); Reference is how the columns name it.</summary>
public sealed record DesignSource(string? Schema, string Name, string? Alias)
{
    public string Reference => Alias ?? Name;
}

/// <summary>One column pair of a join between two sources, by Reference. Several pairs with the same sources are ANDed.</summary>
public sealed record DesignJoin(string Left, string LeftColumn, string Right, string RightColumn, DesignJoinKind Kind);

/// <summary>A row of the Criteria pane. With Source set, Expression is a column name (or *); without it, Expression is SQL as written.
/// Filters[0] is the Filter column and the others the Or… columns; each is an operator and a value, such as "> 10".</summary>
public sealed class DesignCriterion
{
    public string Expression { get; set; } = "";
    public string Source { get; set; } = "";
    public string Alias { get; set; } = "";
    public bool Output { get; set; } = true;
    public DesignSort Sort { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Group By, Sum, Avg, Min, Max, Count, Expression or Where; empty when the query is not grouped.</summary>
    public string Grouping { get; set; } = "";
    public List<string> Filters { get; set; } = [];
    /// <summary>The value of SET or of the VALUES list. In Update, Output says whether the column is set.</summary>
    public string NewValue { get; set; } = "";
    /// <summary>The target column of Insert Results.</summary>
    public string Append { get; set; } = "";
}

/// <summary>What the Diagram and Criteria panes show. The SQL pane is generated from it, and read back into it when the SQL can be represented.</summary>
public sealed class QueryDesign
{
    public const string GroupBy = "Group By", Where = "Where", Expression = "Expression";
    public static readonly string[] Aggregates = ["Sum", "Avg", "Min", "Max", "Count"];
    public List<DesignSource> Sources { get; } = [];
    public List<DesignJoin> Joins { get; } = [];
    public List<DesignCriterion> Criteria { get; } = [];
    public bool Distinct { get; set; }
    public bool Grouped { get; set; }
    public DesignStatement Statement { get; set; }
    /// <summary>The table written to by Insert Results and Make Table; the other statements write to their only source.</summary>
    public DesignSource? Target { get; set; }
}

/// <summary>Reads SELECT statements into a QueryDesign and writes them back. Strict on purpose: what it does not fully understand is reported, never dropped,
/// and the panes then show the "cannot be represented" state of SSMS 22.11 (docs/MARCO-M1046.md).</summary>
public static partial class QueryDesignSql
{
    private enum Kind { Word, Quoted, Number, Text, Symbol }
    private readonly record struct Token(Kind Kind, string Text, int Start, int End, int Depth)
    {
        public bool Is(string word) => Kind == Kind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Kind == Kind.Symbol && Text == symbol;
    }
    private sealed class Unrepresentable(string message) : Exception(message);

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$", RegexOptions.CultureInvariant)] private static partial Regex Plain();
    [GeneratedRegex(@"\n[ \t\r]*\n", RegexOptions.CultureInvariant)] private static partial Regex BlankLine();
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "all","analyse","analyze","and","any","array","as","asc","asymmetric","both","case","cast","check","collate","column","constraint","create","current_catalog","current_date","current_role","current_time","current_timestamp","current_user","default","deferrable","desc","distinct","do","else","end","except","false","fetch","for","foreign","from","grant","group","having","in","initially","intersect","into","lateral","leading","limit","localtime","localtimestamp","not","null","offset","on","only","or","order","placing","primary","references","returning","select","session_user","some","symmetric","system_user","table","then","to","trailing","true","union","unique","user","using","variadic","when","where","window","with"
    };
    private static readonly string[] Comparators = ["=", "<>", "!=", "<", ">", "<=", ">=", "~", "~*", "!~", "!~*"];
    private static readonly string[] JoinWords = ["join", "inner", "left", "right", "full", "cross", "natural", "on", "using"];
    private static readonly string[] Unsupported = ["with", "union", "intersect", "except", "window", "fetch", "for", "limit", "offset"];

    /// <summary>The name as SQL needs it: quoted only when it would otherwise mean something else.</summary>
    public static string Identifier(string name) => Plain().IsMatch(name) && !Reserved.Contains(name) ? name : SqlIdentifier.Quote(name);

    private static List<Token> Tokenize(string sql, out bool commented, out bool strange)
    {
        var tokens = new List<Token>();
        commented = strange = false;
        int i = 0, depth = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;
            if (c == '-' && i + 1 < n && sql[i + 1] == '-') { commented = true; while (i < n && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                commented = true; int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2; continue;
            }
            if (c == '$') { strange = true; i++; continue; }
            if (c == '\'' || (char.IsLetter(c) && i + 1 < n && sql[i + 1] == '\'' && "eEbBxXnN".Contains(c)))
            {
                i += c == '\'' ? 1 : 2;
                while (i < n)
                {
                    if (sql[i] == '\\' && c is 'e' or 'E' && i + 1 < n) { i += 2; continue; }
                    if (sql[i] == '\'') { if (i + 1 < n && sql[i + 1] == '\'') { i += 2; continue; } break; }
                    i++;
                }
                if (i >= n) { strange = true; break; }
                i++;
                tokens.Add(new(Kind.Text, sql[start..i], start, i, depth));
                continue;
            }
            if (c == '"')
            {
                var name = new StringBuilder(); i++;
                while (i < n)
                {
                    if (sql[i] == '"') { if (i + 1 < n && sql[i + 1] == '"') { name.Append('"'); i += 2; continue; } break; }
                    name.Append(sql[i++]);
                }
                if (i >= n) { strange = true; break; }
                i++;
                tokens.Add(new(Kind.Quoted, name.ToString(), start, i, depth));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                tokens.Add(new(Kind.Word, sql[start..i], start, i, depth));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < n && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                if (i < n && sql[i] is 'e' or 'E') { i++; if (i < n && sql[i] is '+' or '-') i++; while (i < n && char.IsDigit(sql[i])) i++; }
                tokens.Add(new(Kind.Number, sql[start..i], start, i, depth));
                continue;
            }
            string symbol = new[] { "!~*", "::", "<=", ">=", "<>", "!=", "!~", "~*", "||" }.FirstOrDefault(s => string.CompareOrdinal(sql, i, s, 0, s.Length) == 0) ?? c.ToString();
            i += symbol.Length;
            if (symbol is ")" or "]") depth--;
            tokens.Add(new(Kind.Symbol, symbol, start, i, depth));
            if (symbol is "(" or "[") depth++;
        }
        return tokens;
    }

    /// <summary>Null when the text is one statement that can return rows; otherwise the reason. A second statement is refused, so Execute, Verify and Save always run one.</summary>
    public static string? CheckSingleSelect(string sql) => CheckSingle(sql, "select", "with", "values");

    /// <summary>The same check for Verify in the Query Designer, which also writes INSERT, UPDATE and DELETE. Verify never runs them.</summary>
    public static string? CheckVerifiable(string sql) => CheckSingle(sql, "select", "with", "values", "insert", "update", "delete");

    private static string? CheckSingle(string sql, params string[] starts)
    {
        var tokens = Tokenize(sql, out _, out bool strange);
        if (tokens.Count == 0) return "Enter a SELECT statement.";
        int semicolon = tokens.FindIndex(t => t.IsSymbol(";"));
        if (semicolon >= 0 && semicolon != tokens.Count - 1) return "Enter only one statement.";
        if (!strange && !starts.Any(tokens[0].Is)) return "Enter a SELECT statement.";
        return null;
    }

    /// <summary>The statement under the caret, from its first word to its semicolon, for opening the designer with no selection (decision of the user; SSMS opens empty).
    /// A statement ends at a semicolon or at a blank line outside parentheses. Null when the caret is on no statement: a blank line, or a line with only comments.</summary>
    public static (int Start, int Length)? StatementAt(string text, int caret)
    {
        var tokens = Tokenize(text, out _, out _);
        var statements = new List<(int Start, int End)>();
        int first = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            bool last = i == tokens.Count - 1, semicolon = tokens[i].IsSymbol(";");
            bool gap = !last && tokens[i].Depth <= 0 && tokens[i + 1].Depth <= 0 && BlankLine().IsMatch(text[tokens[i].End..tokens[i + 1].Start]);
            if (!last && !semicolon && !gap) continue;
            statements.Add((tokens[first].Start, tokens[i].End));
            first = i + 1;
        }
        foreach (var (start, end) in statements) if (start <= caret && caret <= end) return (start, end - start);
        // On the same line as a statement, with only spaces or a comment between: that statement.
        static bool OneLine(string value) => !value.Contains('\n', StringComparison.Ordinal);
        foreach (var (start, end) in statements) if (caret > end ? OneLine(text[end..caret]) : caret < start && OneLine(text[caret..start])) return (start, end - start);
        return null;
    }

    /// <summary>False when selected text is not one statement of a kind the designer writes: SSMS refuses it with a message instead of opening.
    /// A statement the panes cannot show still opens, in the SQL pane only.</summary>
    public static bool CanOpenDesigner(string sql)
    {
        var tokens = Tokenize(sql, out _, out bool strange);
        if (tokens.Count == 0 || !new[] { "select", "with", "insert", "update", "delete" }.Any(tokens[0].Is)) return false;
        int semicolon = tokens.FindIndex(t => t.IsSymbol(";"));
        if (semicolon >= 0 && semicolon != tokens.Count - 1) return false;
        if (strange) return true;
        bool insert = tokens[0].Is("insert"), with = tokens[0].Is("with");
        for (int i = 1; i < tokens.Count; i++)
        {
            var (token, before) = (tokens[i], tokens[i - 1]);
            if (token.Depth != 0) continue;
            if (token.Is("insert")) insert = true;
            if (token.Is("select") && !(insert || before.IsSymbol(")") || before.Is("union") || before.Is("intersect") || before.Is("except") || before.Is("all") || before.Is("distinct"))) return false;
            if ((token.Is("insert") || token.Is("update") || token.Is("delete")) && !(with && before.IsSymbol(")") || before.Is("do") || before.Is("for") || before.Is("key"))) return false;
        }
        return true;
    }

    /// <summary>Reads a SELECT. Null, with the reason in error, when it cannot be represented in the Diagram and Criteria panes.</summary>
    public static QueryDesign? Parse(string sql, out string? error)
    {
        error = null;
        try
        {
            var design = new Reader(sql).Read();
            string? bare = design.Statement is DesignStatement.Update or DesignStatement.Delete && design.Sources[0].Alias is null ? design.Sources[0].Name : null;
            string written = string.Join(" ", Canonical(sql, bare)), generated = string.Join(" ", Canonical(Generate(design), bare));
            if (written != generated) throw new Unrepresentable("Some of this query has no place in the Diagram and Criteria panes.");
            return design;
        }
        catch (Unrepresentable ex) { error = ex.Message; return null; }
        catch (ArgumentOutOfRangeException) { error = "Unable to parse query text."; return null; }
    }

    /// <summary>The words of a statement, sorted, without what the panes write differently (parentheses, AS, INNER, OUTER, ASC, the last semicolon).</summary>
    private static IEnumerable<string> Canonical(string sql, string? bareTable = null)
    {
        var tokens = Tokenize(sql, out _, out _);
        // UPDATE and DELETE may name their only table in front of a column, or not; the panes write the shorter form.
        if (bareTable is not null)
        {
            bool Table(int i) => i >= 0 && i < tokens.Count && tokens[i].Kind is Kind.Word or Kind.Quoted && (tokens[i].Kind == Kind.Word ? tokens[i].Text.ToLowerInvariant() : tokens[i].Text) == bareTable;
            bool Dot(int i) => i >= 0 && i < tokens.Count && tokens[i].IsSymbol(".");
            tokens = tokens.Where((_, i) => !(Table(i) && Dot(i + 1)) && !(Dot(i) && Table(i - 1))).ToList();
        }
        return tokens.Where(t => !(t.Kind == Kind.Symbol && t.Text is "(" or ")" or ";") && !t.Is("as") && !t.Is("inner") && !t.Is("outer") && !t.Is("asc"))
            .Select(t => t.Kind switch { Kind.Word => t.Text.ToLowerInvariant(), Kind.Quoted => Plain().IsMatch(t.Text) ? t.Text : "\"" + t.Text + "\"", _ => t.Text })
            .OrderBy(t => t, StringComparer.Ordinal);
    }

    private sealed class Reader(string sql)
    {
        private readonly List<Token> t = Tokenize(sql, out bool comment, out bool odd) is var list && (comment || odd) ? throw new Unrepresentable(comment ? "Comments have no place in the Diagram and Criteria panes." : "This query uses syntax the Diagram and Criteria panes do not read.") : list;
        private readonly QueryDesign design = new();

        private string Text(int from, int to) => from >= to ? "" : sql[t[from].Start..t[to - 1].End].Trim();
        private static string Name(Token token) => token.Kind == Kind.Word ? token.Text.ToLowerInvariant() : token.Text;
        private int At(int from, int to, params string[] words)
        {
            for (int i = from; i < to; i++) if (t[i].Depth == 0 && t[i].Kind == Kind.Word && words.Any(t[i].Is)) return i;
            return -1;
        }

        public QueryDesign Read()
        {
            int end = t.Count;
            if (end > 0 && t[end - 1].IsSymbol(";")) end--;
            if (end > 0 && At(0, end, Unsupported) is >= 0 and var other) throw new Unrepresentable($"{t[other].Text.ToUpperInvariant()} has no place in the Diagram and Criteria panes.");
            if (end > 0 && t[0].Is("update")) ReadUpdate(end);
            else if (end > 0 && t[0].Is("delete")) ReadDelete(end);
            else if (end > 0 && t[0].Is("insert")) ReadInsert(end);
            else if (end > 0 && t[0].Is("select")) ReadSelect(0, end);
            else throw new Unrepresentable("Only SELECT, INSERT, UPDATE and DELETE statements can be shown in the Diagram and Criteria panes.");
            return design;
        }

        private void ReadUpdate(int end)
        {
            design.Statement = DesignStatement.Update;
            if (At(1, end, "from", "returning") >= 0) throw new Unrepresentable("This UPDATE has no place in the Diagram and Criteria panes.");
            int set = At(1, end, "set"), where = set < 0 ? -1 : At(set, end, "where"), stop = where < 0 ? end : where;
            if (set < 0) throw new Unrepresentable("UPDATE without SET.");
            var target = ReadSource(1, set);
            if (target.Next != set) throw new Unrepresentable("This UPDATE has no place in the Diagram pane.");
            design.Sources.Add(target.Source);
            foreach (var (a, b) in Split(set + 1, stop, ","))
            {
                if (b - a < 2 || t[a].Kind is not (Kind.Word or Kind.Quoted) || !t[a + 1].IsSymbol("=")) throw new Unrepresentable("This SET has no place in the Criteria pane.");
                design.Criteria.Add(new() { Expression = Written(a), NewValue = Text(a + 2, b) });
            }
            if (where >= 0) ReadFilter(where + 1, end, "");
        }

        private void ReadDelete(int end)
        {
            design.Statement = DesignStatement.Delete;
            if (!t[1].Is("from") || At(2, end, "using", "returning") >= 0) throw new Unrepresentable("This DELETE has no place in the Diagram and Criteria panes.");
            int where = At(2, end, "where"), stop = where < 0 ? end : where;
            var target = ReadSource(2, stop);
            if (target.Next != stop) throw new Unrepresentable("This DELETE has no place in the Diagram pane.");
            design.Sources.Add(target.Source);
            if (where >= 0) ReadFilter(where + 1, end, "");
        }

        private void ReadInsert(int end)
        {
            if (!t[1].Is("into") || At(2, end, "conflict", "returning", "overriding") >= 0) throw new Unrepresentable("This INSERT has no place in the Diagram and Criteria panes.");
            var (schema, name, p) = ReadName(2, end);
            var target = new DesignSource(schema, name, null);
            List<int>? columns = null;
            if (p < end && t[p].IsSymbol("("))
            {
                int close = Close(p);
                columns = [];
                foreach (var (a, b) in Split(p + 1, close, ",", t[p].Depth + 1))
                {
                    if (b != a + 1 || t[a].Kind is not (Kind.Word or Kind.Quoted)) throw new Unrepresentable("This column list has no place in the Criteria pane.");
                    columns.Add(a);
                }
                p = close + 1;
            }
            if (p < end && t[p].Is("values"))
            {
                if (columns is null || p + 1 >= end || !t[p + 1].IsSymbol("(") || Close(p + 1) != end - 1) throw new Unrepresentable("Only one row of VALUES with a column list has a place in the Criteria pane.");
                var values = Split(p + 2, end - 1, ",", t[p + 1].Depth + 1).ToArray();
                if (values.Length != columns.Count) throw new Unrepresentable("The columns and the values do not match.");
                design.Statement = DesignStatement.InsertValues;
                design.Sources.Add(target);
                for (int i = 0; i < values.Length; i++) design.Criteria.Add(new() { Expression = Written(columns[i]), NewValue = Text(values[i].From, values[i].To) });
            }
            else if (p < end && t[p].Is("select"))
            {
                ReadSelect(p, end);
                if (design.Statement != DesignStatement.Select) throw new Unrepresentable("INSERT with SELECT INTO has no place in the Criteria pane.");
                design.Statement = DesignStatement.InsertResults;
                design.Target = target;
                if (columns is not null)
                {
                    var outputs = design.Criteria.Where(c => c.Output).ToArray();
                    if (outputs.Length != columns.Count) throw new Unrepresentable("The columns and the query do not match.");
                    for (int i = 0; i < outputs.Length; i++) outputs[i].Append = Name(t[columns[i]]);
                }
            }
            else throw new Unrepresentable("This INSERT has no place in the Diagram and Criteria panes.");
        }

        private string Written(int at) => t[at].Kind == Kind.Word ? Name(t[at]) : sql[t[at].Start..t[at].End];

        private int Close(int open)
        {
            for (int i = open + 1; i < t.Count; i++) if (t[i].IsSymbol(")") && t[i].Depth == t[open].Depth) return i;
            throw new Unrepresentable("Unable to parse query text.");
        }

        private (string? Schema, string Name, int Next) ReadName(int at, int to)
        {
            if (at >= to || t[at].Kind is not (Kind.Word or Kind.Quoted)) throw new Unrepresentable("This statement names no table.");
            string? schema = null; string name = Name(t[at]); at++;
            if (at + 1 < to && t[at].IsSymbol(".")) { schema = name; name = Name(t[at + 1]); at += 2; }
            return (schema, name, at);
        }

        private void ReadSelect(int first, int end)
        {
            int p = first + 1;
            if (p >= end) throw new Unrepresentable("A query without FROM has no place in the Diagram pane.");
            if (At(p, end, "select") >= 0) throw new Unrepresentable("Only one statement has a place in the Diagram and Criteria panes.");
            if (t[p].Is("distinct")) { if (t[p + 1].Is("on")) throw new Unrepresentable("DISTINCT ON has no place in the Diagram and Criteria panes."); design.Distinct = true; p++; }
            else if (t[p].Is("all")) throw new Unrepresentable("ALL has no place in the Diagram and Criteria panes.");
            int from = At(p, end, "from");
            if (from < 0) throw new Unrepresentable("A query without FROM has no place in the Diagram pane.");
            int into = At(p, end, "into");
            if (into >= 0)
            {
                if (into > from || t[into + 1].Is("temp") || t[into + 1].Is("temporary") || t[into + 1].Is("unlogged") || t[into + 1].Is("table")) throw new Unrepresentable("This INTO has no place in the Diagram and Criteria panes.");
                var (schema, name, next) = ReadName(into + 1, from);
                if (next != from) throw new Unrepresentable("This INTO has no place in the Diagram and Criteria panes.");
                design.Statement = DesignStatement.MakeTable;
                design.Target = new(schema, name, null);
            }
            int where = At(from, end, "where"), group = At(from, end, "group"), having = At(from, end, "having"), order = At(from, end, "order");
            int Next(params int[] marks) => marks.Where(m => m >= 0).DefaultIfEmpty(end).Min();
            ReadFrom(from + 1, Next(where, group, having, order));
            ReadItems(p, into >= 0 ? into : from);
            if (group >= 0)
            {
                if (!t[group + 1].Is("by")) throw new Unrepresentable("GROUP without BY.");
                design.Grouped = true;
                foreach (var c in design.Criteria.Where(c => c.Grouping.Length == 0)) c.Grouping = QueryDesign.Expression;
                foreach (var (a, b) in Split(group + 2, Next(having, order), ","))
                {
                    var c = FindOrAdd(a, b, QueryDesign.GroupBy);
                    c.Grouping = QueryDesign.GroupBy;
                }
            }
            if (where >= 0) ReadFilter(where + 1, Next(group, having, order), design.Grouped ? QueryDesign.Where : "");
            if (having >= 0)
            {
                if (group < 0) throw new Unrepresentable("HAVING without GROUP BY has no place in the Criteria pane.");
                ReadFilter(having + 1, Next(order), "");
            }
            if (order >= 0)
            {
                if (!t[order + 1].Is("by")) throw new Unrepresentable("ORDER without BY.");
                int rank = 1;
                foreach (var (a, b) in Split(order + 2, end, ","))
                {
                    int last = b; var sort = DesignSort.Ascending;
                    if (t[last - 1].Is("asc")) last--; else if (t[last - 1].Is("desc")) { sort = DesignSort.Descending; last--; }
                    if (At(a, last, "nulls", "using") >= 0 || (last == a + 1 && t[a].Kind == Kind.Number)) throw new Unrepresentable("This ORDER BY has no place in the Criteria pane.");
                    var c = last == a + 1 && design.Criteria.FirstOrDefault(x => x.Alias.Length > 0 && x.Alias == Name(t[a])) is { } byAlias ? byAlias : FindOrAdd(a, last, QueryDesign.GroupBy);
                    c.Sort = sort; c.SortOrder = rank++;
                }
            }
        }

        private IEnumerable<(int From, int To)> Split(int from, int to, string symbol, int depth = 0)
        {
            int a = from;
            for (int i = from; i < to; i++)
                if (t[i].Depth == depth && t[i].IsSymbol(symbol)) { yield return (a, i); a = i + 1; }
            yield return (a, to);
        }

        private (DesignSource Source, int Next) ReadSource(int at, int to)
        {
            if (at >= to || t[at].Kind is not (Kind.Word or Kind.Quoted) || t[at].Is("lateral") || t[at].Is("only")) throw new Unrepresentable("This FROM clause has no place in the Diagram pane.");
            string? schema = null; string name = Name(t[at]); at++;
            if (at + 1 < to && t[at].IsSymbol(".")) { schema = name; name = Name(t[at + 1]); at += 2; }
            if (at < to && t[at].IsSymbol("(")) throw new Unrepresentable("Functions in FROM have no place in the Diagram pane.");
            string? alias = null;
            if (at < to && t[at].Is("as")) { alias = Name(t[at + 1]); at += 2; }
            else if (at < to && t[at].Kind is Kind.Word or Kind.Quoted && !JoinWords.Any(t[at].Is)) { alias = Name(t[at]); at++; }
            if (at < to && t[at].IsSymbol("(")) throw new Unrepresentable("Column aliases in FROM have no place in the Diagram pane.");
            return (new(schema, name, alias), at);
        }

        private void ReadFrom(int from, int to)
        {
            int p = from;
            var first = ReadSource(p, to);
            design.Sources.Add(first.Source); p = first.Next;
            while (p < to)
            {
                var kind = DesignJoinKind.Inner; bool cross = false;
                if (t[p].Is("inner")) p++;
                else if (t[p].Is("left")) { kind = DesignJoinKind.Left; p++; }
                else if (t[p].Is("right")) { kind = DesignJoinKind.Right; p++; }
                else if (t[p].Is("full")) { kind = DesignJoinKind.Full; p++; }
                else if (t[p].Is("cross")) { cross = true; p++; }
                if (p < to && t[p].Is("outer")) p++;
                if (p >= to || !t[p].Is("join")) throw new Unrepresentable("This FROM clause has no place in the Diagram pane.");
                var next = ReadSource(p + 1, to);
                design.Sources.Add(next.Source); p = next.Next;
                if (cross) continue;
                if (p >= to || !t[p].Is("on")) throw new Unrepresentable("USING and NATURAL joins have no place in the Diagram pane.");
                int stop = At(p + 1, to, "join", "inner", "left", "right", "full", "cross");
                if (stop < 0) stop = to;
                foreach (var (a0, b0) in SplitAnd(p + 1, stop))
                {
                    var (a, b) = Strip(a0, b0);
                    int equal = -1;
                    for (int i = a; i < b; i++) if (t[i].IsSymbol("=") && t[i].Depth == t[a].Depth) equal = i;
                    if (equal < 0 || Column(a, equal) is not { } l || Column(equal + 1, b) is not { } r) throw new Unrepresentable("Only joins that compare columns have a place in the Diagram pane.");
                    string me = next.Source.Reference;
                    if (l.Source == me && r.Source != me) design.Joins.Add(new(r.Source, r.Name, l.Source, l.Name, kind));
                    else if (r.Source == me && l.Source != me) design.Joins.Add(new(l.Source, l.Name, r.Source, r.Name, kind));
                    else throw new Unrepresentable("Only joins between the tables being joined have a place in the Diagram pane.");
                }
                p = stop;
            }
        }

        /// <summary>A column written with the reference of a source, as alias.column.</summary>
        private (string Source, string Name)? Column(int from, int to)
        {
            (from, to) = Strip(from, to);
            if (to - from != 3 || t[from].Kind is not (Kind.Word or Kind.Quoted) || !t[from + 1].IsSymbol(".") || t[from + 2].Kind is not (Kind.Word or Kind.Quoted)) return null;
            string source = Name(t[from]);
            return design.Sources.Any(s => s.Reference == source) ? (source, Name(t[from + 2])) : null;
        }

        private (int From, int To) Strip(int from, int to)
        {
            while (to - from >= 2 && t[from].IsSymbol("(") && t[to - 1].IsSymbol(")") && t[from].Depth == t[to - 1].Depth && !Enumerable.Range(from + 1, to - from - 2).Any(i => t[i].Depth == t[from].Depth)) { from++; to--; }
            return (from, to);
        }

        private IEnumerable<(int From, int To)> SplitAnd(int from, int to) => SplitWord(from, to, "and");
        private IEnumerable<(int From, int To)> SplitWord(int from, int to, string word)
        {
            (from, to) = Strip(from, to);
            int depth = from < to ? t[from].Depth : 0, a = from; bool between = false;
            for (int i = from; i < to; i++)
            {
                if (t[i].Depth != depth) continue;
                if (t[i].Is("between")) { between = true; continue; }
                if (t[i].Is("and") && between) { between = false; continue; }
                if (t[i].Is(word)) { yield return (a, i); a = i + 1; }
            }
            yield return (a, to);
        }

        private void ReadItems(int from, int to)
        {
            foreach (var (a, b0) in Split(from, to, ","))
            {
                int b = b0; string alias = "";
                if (b - a >= 3 && t[b - 2].Is("as") && t[b - 2].Depth == 0) { alias = Name(t[b - 1]); b -= 2; }
                else if (b - a >= 2 && t[b - 1].Depth == 0 && t[b - 1].Kind is Kind.Word or Kind.Quoted && BareAliasFits(a, b - 1)) { alias = Name(t[b - 1]); b--; }
                var c = Item(a, b);
                c.Alias = alias;
                design.Criteria.Add(c);
            }
        }

        private bool BareAliasFits(int a, int b)
        {
            if (b - a == 1) return t[a].Kind is Kind.Word or Kind.Quoted && !t[a].Is("distinct");
            if (b - a == 3 && t[a + 1].IsSymbol(".")) return true;
            return t[b - 1].IsSymbol(")") && t[a].Kind == Kind.Word && t[a + 1].IsSymbol("(") && !Enumerable.Range(a + 2, b - a - 3).Any(i => t[i].Depth == t[a + 1].Depth + 0 && t[i].IsSymbol(")"));
        }

        private DesignCriterion Item(int a, int b)
        {
            if (b - a == 1 && t[a].IsSymbol("*")) return new() { Expression = "*" };
            if (b - a == 3 && t[a + 1].IsSymbol(".") && t[a + 2].IsSymbol("*") && design.Sources.Any(s => s.Reference == Name(t[a]))) return new() { Expression = "*", Source = Name(t[a]) };
            if (Column(a, b) is { } column) return new() { Expression = column.Name, Source = column.Source };
            if (b - a >= 4 && t[a].Kind == Kind.Word && t[a + 1].IsSymbol("(") && t[b - 1].IsSymbol(")") && QueryDesign.Aggregates.FirstOrDefault(t[a].Is) is { } aggregate
                && !Enumerable.Range(a + 2, b - a - 3).Any(i => t[i].Depth == t[a + 1].Depth))
            {
                var inner = b - a == 4 && t[a + 2].IsSymbol("*") ? new DesignCriterion { Expression = "*" } : Item(a + 2, b - 1);
                if (inner.Grouping.Length > 0 || At2(a + 2, b - 1, "distinct")) return new() { Expression = Text(a, b) };
                inner.Grouping = aggregate;
                return inner;
            }
            return new() { Expression = Text(a, b) };
        }
        private bool At2(int from, int to, string word) { for (int i = from; i < to; i++) if (t[i].Is(word)) return true; return false; }

        private static bool Same(DesignCriterion a, DesignCriterion b) => a.Expression == b.Expression && a.Source == b.Source && (a.Grouping == b.Grouping || !QueryDesign.Aggregates.Contains(b.Grouping) && !QueryDesign.Aggregates.Contains(a.Grouping));
        private DesignCriterion FindOrAdd(int a, int b, string grouping)
        {
            var wanted = Item(a, b);
            if (design.Criteria.FirstOrDefault(c => Same(c, wanted)) is { } found) return found;
            wanted.Output = false;
            if (wanted.Grouping.Length == 0 && design.Grouped) wanted.Grouping = grouping;
            design.Criteria.Add(wanted);
            return wanted;
        }

        private void ReadFilter(int from, int to, string forced)
        {
            int branch = 0;
            foreach (var (oa, ob) in SplitWord(from, to, "or"))
            {
                foreach (var (a0, b0) in SplitAnd(oa, ob))
                {
                    var (a, b) = Strip(a0, b0);
                    int depth = a < b ? t[a].Depth : 0, op = -1;
                    for (int i = a; i < b && op < 0; i++)
                        if (t[i].Depth == depth && (t[i].Kind == Kind.Symbol && Comparators.Contains(t[i].Text) || t[i].Is("like") || t[i].Is("ilike") || t[i].Is("in") || t[i].Is("is") || t[i].Is("between") || t[i].Is("not") && i > a)) op = i;
                    if (op <= a) throw new Unrepresentable("This filter has no place in the Criteria pane.");
                    var c = forced.Length > 0 ? AddWhere(a, op, forced) : FindOrAdd(a, op, QueryDesign.Expression);
                    while (c.Filters.Count <= branch) c.Filters.Add("");
                    if (c.Filters[branch].Length > 0)
                    {
                        // A second condition on the same column in one branch takes a row of its own.
                        var more = new DesignCriterion { Expression = c.Expression, Source = c.Source, Grouping = c.Grouping, Output = false };
                        design.Criteria.Add(more); c = more;
                        while (c.Filters.Count <= branch) c.Filters.Add("");
                    }
                    c.Filters[branch] = Text(op, b);
                }
                branch++;
            }
        }

        private DesignCriterion AddWhere(int a, int b, string grouping)
        {
            var row = Item(a, b);
            row.Output = false; row.Grouping = grouping;
            design.Criteria.Add(row);
            return row;
        }
    }

    private static string ColumnSql(DesignCriterion c) => c.Source.Length == 0 ? c.Expression : Identifier(c.Source) + "." + (c.Expression == "*" ? "*" : Identifier(c.Expression));
    private static string Value(DesignCriterion c) => QueryDesign.Aggregates.Contains(c.Grouping) ? c.Grouping.ToUpperInvariant() + "(" + ColumnSql(c) + ")" : ColumnSql(c);
    private static readonly string[] FilterStarts = ["=", "<", ">", "!", "~"];
    private static string Condition(string left, string filter)
    {
        string text = filter.Trim();
        bool hasOperator = FilterStarts.Any(s => text.StartsWith(s, StringComparison.Ordinal)) || Regex.IsMatch(text, @"^(like|ilike|in|is|between|not)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return "(" + left + " " + (hasOperator ? "" : "= ") + text + ")";
    }

    private static string Filters(IReadOnlyList<DesignCriterion> rows, Func<DesignCriterion, string>? column = null)
    {
        column ??= Value;
        int columns = rows.Select(r => r.Filters.Count).DefaultIfEmpty(0).Max();
        var branches = new List<string>();
        for (int j = 0; j < columns; j++)
        {
            var terms = rows.Where(r => j < r.Filters.Count && r.Filters[j].Trim().Length > 0).Select(r => Condition(column(r), r.Filters[j])).ToArray();
            if (terms.Length > 0) branches.Add(terms.Length == 1 || columns == 1 ? string.Join(" AND ", terms) : "(" + string.Join(" AND ", terms) + ")");
        }
        return string.Join(" OR ", branches);
    }

    /// <summary>The SELECT for a design, laid out as the SQL pane of SSMS lays it out.</summary>
    public static string Generate(QueryDesign design) => design.Statement switch
    {
        DesignStatement.Update => GenerateUpdate(design),
        DesignStatement.Delete => GenerateDelete(design),
        DesignStatement.InsertValues => GenerateInsertValues(design),
        DesignStatement.InsertResults => GenerateInsertResults(design),
        _ => GenerateSelect(design)
    };

    private static DesignCriterion[] Active(QueryDesign design) => design.Criteria.Where(c => c.Expression.Trim().Length > 0).ToArray();
    private static string TargetSql(DesignSource? target) => target is null ? "" : SourceSql(target);
    private static string WrittenColumn(DesignCriterion c) => c.Source.Length == 0 ? c.Expression : Identifier(c.Expression);

    /// <summary>A column of the only table of UPDATE or DELETE: bare, as SSMS writes it, unless the table has an alias.</summary>
    private static string DmlColumn(QueryDesign design, DesignCriterion c) => design.Sources.FirstOrDefault() is { Alias: null } only && c.Source == only.Name ? Identifier(c.Expression) : ColumnSql(c);

    private static string GenerateUpdate(QueryDesign design)
    {
        var active = Active(design);
        var text = new StringBuilder("UPDATE      ").Append(TargetSql(design.Sources.FirstOrDefault()));
        text.Append("\nSET         ").Append(string.Join(", ", active.Where(c => c.Output).Select(c => (WrittenColumn(c) + " = " + c.NewValue).TrimEnd())));
        if (Filters(active, c => DmlColumn(design, c)) is { Length: > 0 } condition) text.Append("\nWHERE       ").Append(condition);
        return text.ToString();
    }

    private static string GenerateDelete(QueryDesign design)
    {
        var text = new StringBuilder("DELETE FROM ").Append(TargetSql(design.Sources.FirstOrDefault()));
        if (Filters(Active(design), c => DmlColumn(design, c)) is { Length: > 0 } condition) text.Append("\nWHERE       ").Append(condition);
        return text.ToString();
    }

    private static string GenerateInsertValues(QueryDesign design)
    {
        var active = Active(design);
        return "INSERT INTO " + TargetSql(design.Sources.FirstOrDefault()) + "\n            (" + string.Join(", ", active.Select(WrittenColumn)) + ")\nVALUES      (" + string.Join(", ", active.Select(c => c.NewValue)) + ")";
    }

    private static string GenerateInsertResults(QueryDesign design)
    {
        var appended = Active(design).Where(c => c.Output && c.Append.Trim().Length > 0).Select(c => Identifier(c.Append.Trim())).ToArray();
        return "INSERT INTO " + TargetSql(design.Target) + (appended.Length > 0 ? "\n            (" + string.Join(", ", appended) + ")" : "") + "\n" + GenerateSelect(design);
    }

    private static string GenerateSelect(QueryDesign design)
    {
        var active = Active(design);
        var outputs = active.Where(c => c.Output).Select(c => Value(c) + (c.Alias.Length > 0 ? " AS " + Identifier(c.Alias) : "")).ToArray();
        var text = new StringBuilder("SELECT      ");
        if (design.Distinct) text.Append("DISTINCT ");
        text.Append(string.Join(", ", outputs));
        if (design.Statement == DesignStatement.MakeTable) text.Append("\nINTO        ").Append(TargetSql(design.Target));
        text.Append("\nFROM        ").Append(From(design));
        var where = design.Grouped ? active.Where(c => c.Grouping == QueryDesign.Where).ToArray() : active;
        if (Filters(where) is { Length: > 0 } condition) text.Append("\nWHERE       ").Append(condition);
        if (design.Grouped)
        {
            var groups = active.Where(c => c.Grouping == QueryDesign.GroupBy).Select(ColumnSql).Distinct().ToArray();
            if (groups.Length > 0) text.Append("\nGROUP BY ").Append(string.Join(", ", groups));
            if (Filters(active.Where(c => c.Grouping != QueryDesign.Where).ToArray()) is { Length: > 0 } having) text.Append("\nHAVING      ").Append(having);
        }
        var sorted = active.Where(c => c.Sort != DesignSort.None).OrderBy(c => c.SortOrder).Select(c => Value(c) + (c.Sort == DesignSort.Descending ? " DESC" : "")).ToArray();
        if (sorted.Length > 0) text.Append("\nORDER BY ").Append(string.Join(", ", sorted));
        return text.ToString();
    }

    private static string SourceSql(DesignSource s) => (s.Schema is null ? "" : Identifier(s.Schema) + ".") + Identifier(s.Name) + (s.Alias is null ? "" : " AS " + Identifier(s.Alias));
    private static string From(QueryDesign design)
    {
        if (design.Sources.Count == 0) return "";
        var text = new StringBuilder(SourceSql(design.Sources[0]));
        var earlier = new HashSet<string>(StringComparer.Ordinal) { design.Sources[0].Reference };
        foreach (var source in design.Sources.Skip(1))
        {
            string me = source.Reference;
            var pairs = design.Joins.Where(j => j.Left == me && earlier.Contains(j.Right) || j.Right == me && earlier.Contains(j.Left)).ToArray();
            if (pairs.Length == 0) text.Append(" CROSS JOIN\n            ").Append(SourceSql(source));
            else
            {
                var kind = pairs[0].Right == me ? pairs[0].Kind : Flip(pairs[0].Kind);
                string word = kind switch { DesignJoinKind.Left => "LEFT OUTER", DesignJoinKind.Right => "RIGHT OUTER", DesignJoinKind.Full => "FULL OUTER", _ => "INNER" };
                text.Append(' ').Append(word).Append(" JOIN\n            ").Append(SourceSql(source)).Append(" ON ");
                text.Append(string.Join(" AND ", pairs.Select(j => $"{Identifier(j.Left)}.{Identifier(j.LeftColumn)} = {Identifier(j.Right)}.{Identifier(j.RightColumn)}")));
            }
            earlier.Add(me);
        }
        return text.ToString();
    }
    private static DesignJoinKind Flip(DesignJoinKind kind) => kind switch { DesignJoinKind.Left => DesignJoinKind.Right, DesignJoinKind.Right => DesignJoinKind.Left, _ => kind };
}

public sealed record DesignColumnInfo(string Name, string Type, bool IsPrimaryKey);
public sealed record DesignSourceInfo(uint Oid, string Schema, string Name, IReadOnlyList<DesignColumnInfo> Columns);
public sealed record DesignForeignKeyLink(uint From, IReadOnlyList<string> Columns, uint To, IReadOnlyList<string> ReferencedColumns);
public sealed record DesignResult(DataTable Table, bool Truncated);

/// <summary>Catalog reads, Execute SQL, Verify SQL and saving a view for the query designers. Execute and Verify run in READ ONLY transactions.</summary>
public sealed class QueryDesignService(string connectionString)
{
    public const int MaxRows = 1000;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Tables, partitioned tables, views and materialized views outside the system schemas, without the diagram table.</summary>
    public async Task<IReadOnlyList<DiagramTableRef>> ListRelationsAsync(CancellationToken token = default)
    {
        const string sql = """
            SELECT n.nspname, c.relname, c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind IN ('r','p','v','m') AND NOT c.relispartition AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
              AND NOT (n.nspname='pgms' AND c.relname='diagrams')
            ORDER BY n.nspname, c.relname
            """;
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<DiagramTableRef>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<uint>(2)));
        return result;
    }

    /// <summary>Finds a relation as the session would (search_path for an unqualified name). Null when there is none.</summary>
    public async Task<DesignSourceInfo?> ReadSourceAsync(string? schema, string name, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var find = new NpgsqlCommand("SELECT c.oid, n.nspname, c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE c.oid = pg_catalog.to_regclass(@name)", connection) { CommandTimeout = 15 };
        find.Parameters.AddWithValue("name", (schema is null ? "" : SqlIdentifier.Quote(schema) + ".") + SqlIdentifier.Quote(name));
        uint oid; string foundSchema, foundName;
        await using (var reader = await find.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            oid = reader.GetFieldValue<uint>(0); foundSchema = reader.GetString(1); foundName = reader.GetString(2);
        }
        const string columns = """
            SELECT a.attname, pg_catalog.format_type(a.atttypid,a.atttypmod),
                EXISTS (SELECT 1 FROM pg_catalog.pg_constraint k WHERE k.conrelid=a.attrelid AND k.contype='p' AND a.attnum = ANY(k.conkey))
            FROM pg_catalog.pg_attribute a WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """;
        await using var read = new NpgsqlCommand(columns, connection) { CommandTimeout = 15 };
        read.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
        await using var rows = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
        var list = new List<DesignColumnInfo>();
        while (await rows.ReadAsync(token).ConfigureAwait(false)) list.Add(new(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2)));
        return new(oid, foundSchema, foundName, list);
    }

    /// <summary>Foreign keys between the given relations, for the joins Add Table creates by itself.</summary>
    public async Task<IReadOnlyList<DesignForeignKeyLink>> ForeignKeysAsync(IReadOnlyList<uint> relations, CancellationToken token = default)
    {
        const string sql = """
            SELECT k.conrelid, k.confrelid,
                ARRAY(SELECT a.attname::text FROM unnest(k.conkey) WITH ORDINALITY o(n,i) JOIN pg_catalog.pg_attribute a ON a.attrelid=k.conrelid AND a.attnum=o.n ORDER BY o.i),
                ARRAY(SELECT a.attname::text FROM unnest(k.confkey) WITH ORDINALITY o(n,i) JOIN pg_catalog.pg_attribute a ON a.attrelid=k.confrelid AND a.attnum=o.n ORDER BY o.i)
            FROM pg_catalog.pg_constraint k WHERE k.contype='f' AND k.conrelid = ANY(@oids) AND k.confrelid = ANY(@oids) ORDER BY k.conname
            """;
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oids", NpgsqlDbType.Array | NpgsqlDbType.Oid, relations.ToArray());
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<DesignForeignKeyLink>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetFieldValue<uint>(0), reader.GetFieldValue<string[]>(2), reader.GetFieldValue<uint>(1), reader.GetFieldValue<string[]>(3)));
        return result;
    }

    /// <summary>Tables linked to a table by a foreign key, in either direction, without itself and without partitions.</summary>
    public async Task<IReadOnlyList<DiagramTableRef>> RelatedTablesAsync(uint table, CancellationToken token = default)
    {
        const string sql = """
            SELECT DISTINCT n.nspname, c.relname, c.oid
            FROM pg_catalog.pg_constraint k
            JOIN pg_catalog.pg_class c ON c.oid = CASE WHEN k.conrelid=@oid THEN k.confrelid ELSE k.conrelid END
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE k.contype='f' AND (k.conrelid=@oid OR k.confrelid=@oid) AND c.oid<>@oid AND NOT c.relispartition
            ORDER BY 1, 2
            """;
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, table);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<DiagramTableRef>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<uint>(2)));
        return result;
    }

    public async Task<string> ViewDefinitionAsync(string schema, string name, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        return await DefinitionAsync(connection, null, schema, name, token).ConfigureAwait(false);
    }

    private static async Task<string> DefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_get_viewdef(c.oid, true) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='v'", connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("schema", schema); command.Parameters.AddWithValue("name", name);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string definition) throw new InvalidOperationException("The view was not found. Refresh Object Explorer.");
        return definition.Trim().TrimEnd(';').TrimEnd();
    }

    private static string Check(string sql) => QueryDesignSql.CheckSingleSelect(sql) is { } problem ? throw new ArgumentException(problem) : sql.Trim().TrimEnd(';').TrimEnd();

    /// <summary>Verify SQL Syntax: EXPLAIN in a READ ONLY transaction that is rolled back. Throws what PostgreSQL reports.</summary>
    public async Task VerifyAsync(string sql, CancellationToken token = default, bool allowWrites = false)
    {
        string statement = !allowWrites ? Check(sql) : QueryDesignSql.CheckVerifiable(sql) is { } problem ? throw new ArgumentException(problem) : sql.Trim().TrimEnd(';').TrimEnd();
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("EXPLAIN " + statement, connection, transaction) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.RollbackAsync(token).ConfigureAwait(false);
    }

    /// <summary>Execute SQL for the Results pane: at most MaxRows rows, as text, in a READ ONLY transaction, so a designer can never change data.</summary>
    public async Task<DesignResult> ExecuteAsync(string sql, CancellationToken token = default)
    {
        string statement = Check(sql);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(statement, connection, transaction) { CommandTimeout = 0, AllResultTypesAreUnknown = true };
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        bool truncated = false;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i), unique = name;
                for (int n = 1; table.Columns.Contains(unique); n++) unique = name + "_" + n;
                table.Columns.Add(new DataColumn(unique, typeof(string)) { Caption = name });
            }
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (table.Rows.Count >= MaxRows) { truncated = true; break; }
                var row = table.NewRow();
                for (int i = 0; i < reader.FieldCount; i++) row[i] = await reader.IsDBNullAsync(i, token).ConfigureAwait(false) ? DBNull.Value : reader.GetString(i);
                table.Rows.Add(row);
            }
        }
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return new(table, truncated);
    }

    /// <summary>The statement that saves a view. An existing view keeps its OID, privileges and dependents with CREATE OR REPLACE.</summary>
    public static string ViewSql(string schema, string name, string sql, bool replace) => $"CREATE {(replace ? "OR REPLACE " : "")}VIEW {ObjectSql.Relation(schema, name)} AS\n{Check(sql)};";

    /// <summary>Saves in one transaction. With expected (the definition the designer read) a view changed elsewhere is refused; without it, a new view never replaces another.</summary>
    public async Task SaveViewAsync(string schema, string name, string sql, string? expected, CancellationToken token = default)
    {
        string statement = ViewSql(schema, name, sql, expected is not null);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        if (expected is not null)
        {
            await using (var guard = new NpgsqlCommand($"LOCK TABLE {ObjectSql.Relation(schema, name)} IN ACCESS EXCLUSIVE MODE", connection, transaction) { CommandTimeout = 30 }) await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (await DefinitionAsync(connection, transaction, schema, name, token).ConfigureAwait(false) != expected)
                throw new InvalidOperationException("The view changed since the designer read it. Nothing was saved.");
        }
        try
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction) { CommandTimeout = 30 };
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P16")
        {
            throw new InvalidOperationException("PostgreSQL can only add columns at the end of an existing view; it cannot remove, rename, reorder or change the type of the ones it has. Nothing was saved. To make this change, drop and create the view in a query window. (" + ex.MessageText + ")", ex);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
