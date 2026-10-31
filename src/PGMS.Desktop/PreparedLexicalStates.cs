using System.Runtime.CompilerServices;
using ICSharpCode.AvalonEdit.Document;

namespace PGMS.Desktop;

internal static class PreparedLexicalStates
{
    private sealed record Seed(ITextSourceVersion Version,SqlLexicalState[] States);
    private static readonly ConditionalWeakTable<TextDocument,Seed> seeds=new();
    internal static void Prepare(TextDocument document,string text,CancellationToken token)
    {
        var states=new List<SqlLexicalState>{default};SqlLexicalState state=default;
        int start=0,line=0;
        while(start<text.Length)
        {
            token.ThrowIfCancellationRequested();int end=text.AsSpan(start).IndexOfAny('\r','\n');end=end<0?text.Length:start+end;
            state=SqlLexicalScanner.Scan(text.AsSpan(start,end-start),state,null,static _=>false);
            if(++line%128==0)states.Add(state);
            start=end;
            if(start<text.Length && text[start++]=='\r' && start<text.Length && text[start]=='\n')start++;
        }
        token.ThrowIfCancellationRequested();seeds.Add(document,new(document.Version,states.ToArray()));
        document.Changing+=Invalidate;
    }
    internal static bool HasSeed(TextDocument document)=>seeds.TryGetValue(document,out _);
    private static void Invalidate(object? sender,DocumentChangeEventArgs args)
    {
        if(sender is not TextDocument document)return;
        seeds.Remove(document);
        document.Changing-=Invalidate;
    }
    internal static SqlLexicalState[]? Get(TextDocument document)
    {
        if(!seeds.TryGetValue(document,out var seed))return null;
        if(seed.Version.CompareAge(document.Version)!=0){seeds.Remove(document);return null;}
        return seed.States;
    }
}
