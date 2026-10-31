using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using PGMS.Desktop;

internal static class HighlightingCacheChecks
{
    internal static void Run(TextEditor editor,Action<bool,string> check)
    {
        var document=new TextDocument("$body$\n"+string.Concat(Enumerable.Repeat("inside\n",65536))+"$body$; SELECT 1;");
        using var highlighter=new SqlDocumentHighlighter(document,editor.SyntaxHighlighting);
        check(HighlightingChecks.ColorAt(highlighter,document.TextLength-9)=="Keyword","Sparse cache scans a 65K-line dollar string through its closing tag");
        check(highlighter.CachedCheckpointCount<=document.LineCount/128+1,"Lexical cache stores one checkpoint per 128 lines, not one state per line");
        foreach(int line in new[]{32769,2,65537,129,128,257})
        {
            var target=document.GetLineByNumber(line);
            check(HighlightingChecks.ColorAt(highlighter,target.Offset)=="String","Random backward/forward access keeps dollar state at line "+line);
        }
        document.Remove(0,6);
        check(HighlightingChecks.ColorAt(highlighter,document.TextLength-9)=="String","Editing first delimiter invalidates later checkpoints");
        document.UndoStack.Undo();
        check(HighlightingChecks.ColorAt(highlighter,document.TextLength-9)=="Keyword","Undo rebuilds sparse checkpoints from restored text");
        document.Text="SELECT "+new string('x',1024*1024)+";";
        long before=GC.GetAllocatedBytesForCurrentThread();var result=highlighter.HighlightLine(1);long allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        check(result.Sections.Count==1 && result.Sections[0].Color.Name=="Keyword","Long identifier is not split into keywords");
        Console.WriteLine("INFO: full highlighted-line allocation including AvalonEdit text extraction: "+allocation);
        int scans=highlighter.ScanCount;before=GC.GetAllocatedBytesForCurrentThread();var cached=highlighter.HighlightLine(1);allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        check(highlighter.ScanCount==scans && allocation<65536,"Repeated megacharacter line reuses bounded sections without extracting text again");
        cached.Sections[0].Length=0;check(highlighter.HighlightLine(1).Sections[0].Length==6,"Cached highlights are isolated from consumer section mutation");
        document.Text=string.Concat(Enumerable.Repeat("SELECT 1;\n",100));
        for(int line=1;line<=100;line++)highlighter.HighlightLine(line);
        check(highlighter.CachedLineCount<=64 && highlighter.CachedSectionCount<=4096,"Highlighted cache has explicit line and section budgets");
        document.Replace(0,6,"--xxxx");check(highlighter.CachedLineCount==0 && highlighter.HighlightLine(1).Sections[0].Color.Name=="Comment","Editing clears cached colors and rebuilds lexical state");
        document.Text="SELECT "+new string('x',1024*1024)+";";
        string scanText=document.Text;before=GC.GetAllocatedBytesForCurrentThread();
        SqlLexicalScanner.Scan(scanText,default,(_,_,_)=>{},value=>value=="SELECT");allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        check(allocation<65536,"Lexical scan avoids allocating a megacharacter token on top of supplied text");
        foreach(string newline in new[]{"\r\n","\n","\r"})
        {
            string preparedText="$tag$"+newline+string.Concat(Enumerable.Repeat("inside"+newline,65536))+"$tag$; SELECT 1;";
            var prepared=Task.Run(()=>PreparedQueryDocument.Create(preparedText)).GetAwaiter().GetResult();var owned=prepared.TakeOwnership();
            using var seeded=new SqlDocumentHighlighter(owned,editor.SyntaxHighlighting);
            check(HighlightingChecks.ColorAt(seeded,owned.TextLength-9)=="Keyword" && seeded.ScanCount<=128,"Worker lexical checkpoints bound distant first access for newline length "+newline.Length);
            check(PreparedLexicalStates.HasSeed(owned),"Unchanged prepared document retains reusable seed");
            owned.Remove(0,5);
            check(!PreparedLexicalStates.HasSeed(owned),"First edit releases seed version immediately without constructing another highlighter");
            using var changed=new SqlDocumentHighlighter(owned,editor.SyntaxHighlighting);
            check(HighlightingChecks.ColorAt(changed,owned.TextLength-9)=="String","Changed document never reuses stale prepared lexical seed");
        }
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;try{PreparedQueryDocument.Create("SELECT 1",canceled.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped,"Canceled preparation does not expose a lexical seed");
        string backing="prefixSELECT $$inside$$;suffix";var emitted=new List<(int Offset,int Length,string Color)>();
        var state=SqlLexicalScanner.Scan(backing.AsSpan(6,18),default,(offset,length,color)=>emitted.Add((offset,length,color)),value=>value=="SELECT");
        check(state.Kind==SqlLexicalKind.Code && emitted.First()==(0,6,"Keyword") && emitted.Last()==(7,10,"String"),"Span scan uses slice-relative offsets and exact dollar boundary");
        string manyLines=string.Concat(Enumerable.Repeat("SELECT 1;",4096));before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<manyLines.Length;i+=9)SqlLexicalScanner.Scan(manyLines.AsSpan(i,9),default,null,_=>throw new InvalidOperationException("State-only scan must not query keywords"));
        allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        check(allocation<65536,"State-only scanning of thousands of slices avoids substring allocations");
    }
}
