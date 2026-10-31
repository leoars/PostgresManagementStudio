using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

public partial class QueryView
{
    private readonly SqlLinkElementGenerator urlLinks = new(false);
    private readonly SqlLinkElementGenerator mailLinks = new(true);

    private void ConfigureSqlLinks()
    {
        // TextView processes its options before this subscriber (AvalonEdit 6.3.1).
        // Remove any recreated built-in matcher, preserving the public options.
        var generators = Editor.TextArea.TextView.ElementGenerators;
        foreach (var generator in generators.OfType<LinkElementGenerator>().ToArray())
            generators.Remove(generator);
        urlLinks.RequireControlModifierForClick = mailLinks.RequireControlModifierForClick = Editor.Options.RequireControlModifierForHyperlinkClick;
        if (Editor.Options.EnableHyperlinks) generators.Add(urlLinks);
        if (Editor.Options.EnableEmailHyperlinks) generators.Add(mailLinks);
    }
}
