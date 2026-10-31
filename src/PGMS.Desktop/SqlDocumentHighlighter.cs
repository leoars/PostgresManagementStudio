using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

public sealed class SqlTextEditor : TextEditor
{
    public SqlTextEditor() : base(new SqlTextArea()) { }
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()=>new SqlTextEditorAutomationPeer(this);
    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition definition)=>new SqlHighlightingColorizer(definition);
}

internal sealed class SqlHighlightingColorizer(IHighlightingDefinition definition) : HighlightingColorizer
{
    protected override IHighlighter CreateHighlighter(TextView view,TextDocument document)=>new SqlDocumentHighlighter(document,definition);
}

internal sealed class SqlDocumentHighlighter : IHighlighter
{
    private readonly IHighlightingDefinition definition;
    private const int CheckpointStride=128;
    private readonly List<SqlLexicalState> checkpoints=[default];
    private sealed record CachedLine(SqlLexicalState EndState,HighlightedSection[] Sections);
    private readonly Dictionary<int,CachedLine> highlighted=[];
    private readonly Queue<int> highlightedOrder=[];
    private int cachedSections;
    internal int CachedLineCount=>highlighted.Count;
    internal int CachedSectionCount=>cachedSections;
    internal int ScanCount{get;private set;}
    private int cursorLine;
    private SqlLexicalState cursorState;
    internal int CachedCheckpointCount=>checkpoints.Count;
    private int changedLine=1;
    private bool disposed;
    public IDocument Document=>document;
    private readonly TextDocument document;
    public event HighlightingStateChangedEventHandler? HighlightingStateChanged;
    public HighlightingColor DefaultTextColor=>definition.GetNamedColor("Default");

    internal SqlDocumentHighlighter(TextDocument document,IHighlightingDefinition definition)
    {
        this.document=document;this.definition=definition;
        if(PreparedLexicalStates.Get(document) is { } prepared){checkpoints.Clear();checkpoints.AddRange(prepared);}
        document.Changing+=Changing;document.Changed+=Changed;
    }
    private void Changing(object? sender,DocumentChangeEventArgs e)
    {
        changedLine=document.GetLineByOffset(e.Offset).LineNumber;
        highlighted.Clear();highlightedOrder.Clear();cachedSections=0;
        int keep=(changedLine-1)/CheckpointStride+1;
        if(checkpoints.Count>keep)checkpoints.RemoveRange(keep,checkpoints.Count-keep);
        if(cursorLine>=changedLine){cursorLine=(checkpoints.Count-1)*CheckpointStride;cursorState=checkpoints[^1];}
    }
    private void Changed(object? sender,DocumentChangeEventArgs e)=>HighlightingStateChanged?.Invoke(changedLine,document.LineCount);
    private bool Keyword(string text)
    {
        foreach(var rule in definition.MainRuleSet.Rules)
        {
            var match=rule.Regex.Match(text);
            if(match.Success && match.Index==0 && match.Length==text.Length && rule.Color?.Name=="Keyword")return true;
        }
        return false;
    }
    private void EnsureState(int lineNumber)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(lineNumber<0 || lineNumber>document.LineCount)throw new ArgumentOutOfRangeException(nameof(lineNumber));
        int checkpoint=Math.Min(lineNumber/CheckpointStride,checkpoints.Count-1);
        if(cursorLine>lineNumber || checkpoint*CheckpointStride>cursorLine)
        {cursorLine=checkpoint*CheckpointStride;cursorState=checkpoints[checkpoint];}
        while(cursorLine<lineNumber)
        {
            var line=document.GetLineByNumber(cursorLine+1);
            if(highlighted.TryGetValue(line.LineNumber,out var cached))Advance(cached.EndState);
            else{ScanCount++;Advance(SqlLexicalScanner.Scan(document.GetText(line),cursorState,null,Keyword));}
        }
    }
    private void Advance(SqlLexicalState state)
    {
        cursorLine++;cursorState=state;
        if(cursorLine%CheckpointStride==0 && checkpoints.Count==cursorLine/CheckpointStride)checkpoints.Add(state);
    }
    public HighlightedLine HighlightLine(int lineNumber)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var line=document.GetLineByNumber(lineNumber);var result=new HighlightedLine(document,line);
        if(highlighted.TryGetValue(lineNumber,out var cached))
        {
            cursorLine=lineNumber;cursorState=cached.EndState;
            foreach(var section in cached.Sections)result.Sections.Add(Clone(section));
            return result;
        }
        EnsureState(lineNumber-1);ScanCount++;
        var state=SqlLexicalScanner.Scan(document.GetText(line),cursorState,(offset,length,color)=>result.Sections.Add(new HighlightedSection{Offset=line.Offset+offset,Length=length,Color=GetNamedColor(color)}),Keyword);
        Advance(state);
        if(result.Sections.Count<=4096)
        {
            while(highlighted.Count>=64 || cachedSections+result.Sections.Count>4096)
            {int oldest=highlightedOrder.Dequeue();cachedSections-=highlighted[oldest].Sections.Length;highlighted.Remove(oldest);}
            var sections=result.Sections.Select(Clone).ToArray();highlighted.Add(lineNumber,new(state,sections));highlightedOrder.Enqueue(lineNumber);cachedSections+=sections.Length;
        }
        return result;
    }
    private static HighlightedSection Clone(HighlightedSection section)=>new(){Offset=section.Offset,Length=section.Length,Color=section.Color};
    public IEnumerable<HighlightingColor> GetColorStack(int lineNumber)
    {
        EnsureState(lineNumber);var state=cursorState;
        return state.Kind==SqlLexicalKind.Code?[]:[GetNamedColor(state.Kind==SqlLexicalKind.Comment?"Comment":"String")];
    }
    public void UpdateHighlightingState(int lineNumber)=>EnsureState(lineNumber);
    public HighlightingColor GetNamedColor(string name)=>definition.GetNamedColor(name);
    public void BeginHighlighting()=>ObjectDisposedException.ThrowIf(disposed,this);
    public void EndHighlighting(){ }
    public void Dispose(){if(disposed)return;disposed=true;document.Changing-=Changing;document.Changed-=Changed;checkpoints.Clear();highlighted.Clear();highlightedOrder.Clear();cachedSections=0;}
}
