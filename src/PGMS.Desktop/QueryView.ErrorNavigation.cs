using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    private int? errorOffset;
    private long errorRevision;
    private void SetErrorPosition(string sql,int position,int selectionStart,long revision)
    {
        // Conservative: legacy multi-statement positions can be statement-relative.
        // Even semicolons inside literals disable navigation until a parser is available.
        if(position<1 || sql.Contains(';') || revision!=EditRevision)return;
        int offset=0;
        for(int character=1;character<position;character++)
        {
            if(offset>=sql.Length)return;
            offset+=char.IsHighSurrogate(sql[offset]) && offset+1<sql.Length && char.IsLowSurrogate(sql[offset+1])?2:1;
        }
        if(offset>sql.Length)return;
        errorOffset=selectionStart+offset;errorRevision=revision;
        ErrorLocation.IsEnabled=true;
    }
    private void MarkUnboundParameter(UnboundParameters unbound,int selectionStart,long revision)
    {
        if(revision!=EditRevision)return;
        errorOffset=selectionStart+unbound.Offset;errorRevision=revision;ErrorLocation.IsEnabled=true;
    }
    private void NavigateError(object sender,RoutedEventArgs e)
    {
        if(errorOffset is not int offset || errorRevision!=EditRevision || offset>Editor.Document.TextLength)
        {ErrorLocation.IsEnabled=false;return;}
        Editor.Select(offset,0);var location=Editor.Document.GetLocation(offset);
        Editor.ScrollTo(location.Line,location.Column);Editor.Focus();
    }
}
