namespace PGMS.Desktop;

internal enum SqlLexicalKind { Code, Comment, String, EscapeString, Identifier, DollarString }
internal readonly record struct SqlLexicalState(SqlLexicalKind Kind=SqlLexicalKind.Code,int Depth=0,string? Delimiter=null);

// Lexical coloring only. Never splits, rewrites or validates executed SQL.
internal static class SqlLexicalScanner
{
    private static bool IdentifierStart(char c)=>char.IsLetter(c) || c=='_' || c>=128;
    private static bool IdentifierPart(char c)=>IdentifierStart(c) || char.IsDigit(c) || c=='$';

    internal static SqlLexicalState Scan(ReadOnlySpan<char> text,SqlLexicalState state,Action<int,int,string>? emit,Func<string,bool> keyword)
    {
        int i=0,start=0;
        void Emit(int end,string color){if(end>start)emit?.Invoke(start,end-start,color);}
        while(i<text.Length)
        {
            if(state.Kind==SqlLexicalKind.Comment)
            {
                if(i+1<text.Length && text[i]=='/' && text[i+1]=='*'){state=state with{Depth=state.Depth+1};i+=2;}
                else if(i+1<text.Length && text[i]=='*' && text[i+1]=='/')
                {
                    i+=2;state=state with{Depth=state.Depth-1};
                    if(state.Depth==0){Emit(i,"Comment");state=default;}
                }
                else i++;
                continue;
            }
            if(state.Kind==SqlLexicalKind.DollarString)
            {
                int found=text[i..].IndexOf(state.Delimiter.AsSpan(),StringComparison.Ordinal);
                int end=found<0?-1:i+found;
                if(end<0){i=text.Length;continue;}
                i=end+state.Delimiter!.Length;Emit(i,"String");state=default;continue;
            }
            if(state.Kind is SqlLexicalKind.String or SqlLexicalKind.EscapeString or SqlLexicalKind.Identifier)
            {
                char quote=state.Kind==SqlLexicalKind.Identifier?'"':'\'';
                if(state.Kind==SqlLexicalKind.EscapeString && text[i]=='\\'){i=Math.Min(i+2,text.Length);continue;}
                if(text[i]==quote)
                {
                    if(i+1<text.Length && text[i+1]==quote){i+=2;continue;}
                    i++;Emit(i,"String");state=default;
                }
                else i++;
                continue;
            }
            start=i;
            if(i+1<text.Length && text[i]=='-' && text[i+1]=='-'){Emit(text.Length,"Comment");return default;}
            if(i+1<text.Length && text[i]=='/' && text[i+1]=='*'){state=new(SqlLexicalKind.Comment,1);i+=2;continue;}
            if((text[i]=='E' || text[i]=='e') && i+1<text.Length && text[i+1]=='\''){state=new(SqlLexicalKind.EscapeString);i+=2;continue;}
            if(text[i]=='\''){state=new(SqlLexicalKind.String);i++;continue;}
            if(text[i]=='"'){state=new(SqlLexicalKind.Identifier);i++;continue;}
            if(text[i]=='$')
            {
                int end=i+1;
                if(end<text.Length && text[end]!='$' && IdentifierStart(text[end]))
                    while(end<text.Length && (IdentifierStart(text[end]) || char.IsDigit(text[end])))end++;
                if(end<text.Length && text[end]=='$')
                {
                    state=new(SqlLexicalKind.DollarString,0,text.Slice(i,end-i+1).ToString());i=end+1;continue;
                }
            }
            if(IdentifierStart(text[i]))
            {
                i++;while(i<text.Length && IdentifierPart(text[i]))i++;
                if(emit is not null && i-start<=63 && keyword(text.Slice(start,i-start).ToString()))Emit(i,"Keyword");
                continue;
            }
            i++;
        }
        if(state.Kind!=SqlLexicalKind.Code)Emit(i,state.Kind==SqlLexicalKind.Comment?"Comment":"String");
        return state;
    }
}
