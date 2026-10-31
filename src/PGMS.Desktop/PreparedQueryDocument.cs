using ICSharpCode.AvalonEdit.Document;

namespace PGMS.Desktop;

// Build on a worker, release ownership, and claim exactly once on the UI thread.
internal sealed class PreparedQueryDocument
{
    private TextDocument? document;
    private PreparedQueryDocument(TextDocument document) => this.document = document;

    public static PreparedQueryDocument Create(string text, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var document = new TextDocument(text);
        PreparedLexicalStates.Prepare(document,text,token);
        token.ThrowIfCancellationRequested();
        document.SetOwnerThread(null);
        return new(document);
    }

    public TextDocument TakeOwnership()
    {
        var result = Interlocked.Exchange(ref document, null)
            ?? throw new InvalidOperationException("The prepared document has already been attached.");
        result.SetOwnerThread(Thread.CurrentThread);
        return result;
    }
}
