using ICSharpCode.AvalonEdit.Document;

namespace PGMS.Desktop;

// Holds only the immutable rope snapshot, never the editor or its live document.
internal sealed class QueryTextSnapshot
{
    private readonly ITextSource source;
    private QueryTextSnapshot(ITextSource source) => this.source = source;
    public static QueryTextSnapshot Capture(TextDocument document) => new(document.CreateSnapshot());
    public int Length => source.TextLength;
    public string Materialize() => source.Text;
}
