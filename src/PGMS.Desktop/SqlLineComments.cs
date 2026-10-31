using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace PGMS.Desktop;

internal static class SqlLineComments
{
    internal const int MaximumLines=10000;
    internal static int Apply(TextEditor editor,bool uncomment)
    {
        if(editor.IsReadOnly)throw new InvalidOperationException("The SQL editor is read-only.");
        var document=editor.Document;
        int first=document.GetLineByOffset(editor.SelectionStart).LineNumber;
        int selectionEnd=editor.SelectionStart+editor.SelectionLength;
        var endLine=document.GetLineByOffset(selectionEnd);
        int last=endLine.LineNumber;
        bool selected=editor.SelectionLength>0;
        if(selected && selectionEnd==endLine.Offset)last--;
        if(last-first+1>MaximumLines)throw new ArgumentException("Select at most 10,000 lines to comment or uncomment at once.");
        var changes=new List<(int Offset,int Remove)>();
        for(int lineNumber=first;lineNumber<=last;lineNumber++)
        {
            var line=document.GetLineByNumber(lineNumber);int offset=line.Offset;
            while(offset<line.EndOffset && document.GetCharAt(offset) is ' ' or '\t')offset++;
            if(!uncomment)changes.Add((offset,0));
            else if(offset+1<line.EndOffset && document.GetCharAt(offset)=='-' && document.GetCharAt(offset+1)=='-')changes.Add((offset,2));
        }
        if(changes.Count==0)return 0;
        var caret=document.CreateAnchor(editor.CaretOffset);caret.MovementType=AnchorMovementType.AfterInsertion;caret.SurviveDeletion=true;
        using(document.RunUpdate())
            foreach(var change in changes.AsEnumerable().Reverse())document.Replace(change.Offset,change.Remove,uncomment?"":"--");
        if(selected)
        {
            int start=document.GetLineByNumber(first).Offset;
            editor.Select(start,document.GetLineByNumber(last).EndOffset-start);
        }
        else{editor.Select(caret.Offset,0);editor.CaretOffset=caret.Offset;}
        return changes.Count;
    }
}
