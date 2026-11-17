using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ICSharpCode.AvalonEdit.Snippets;

namespace PGMS.Desktop;

/// <summary>Insert Snippet (Ctrl+K, Ctrl+X), Surround With (Ctrl+K, Ctrl+S) and template parameters (Ctrl+Shift+M) in the editor (PGMS-065, M1083).</summary>
public partial class QueryView
{
    /// <summary>Snippets offered by the pickers; tests replace it.</summary>
    internal Func<IReadOnlyList<SqlSnippet>> SnippetSource { get; set; } = () => SqlTemplateCatalog.Snippets();
    internal ContextMenu? SnippetPicker { get; private set; }

    /// <summary>Opens the picker at the caret: folders, then snippets; Surround With lists only snippets that wrap the selection.</summary>
    internal ContextMenu? ShowSnippetPicker(bool surround)
    {
        if (Editor.IsReadOnly) return null;
        var snippets = SnippetSource().Where(s => surround ? s.IsSurroundsWith : s.IsExpansion).ToArray();
        if (snippets.Length == 0) { QueryStatus.Text = surround ? "No snippets surround text." : "No snippets are available."; return null; }
        var menu = new ContextMenu { PlacementTarget = Editor.TextArea, Placement = PlacementMode.Relative };
        menu.Items.Add(new MenuItem { Header = surround ? "Surround With:" : "Insert Snippet:", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var group in snippets.GroupBy(s => s.Folder).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = new MenuItem { Header = group.Key.Replace("_", "__") };
            foreach (var snippet in group.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new MenuItem { Header = snippet.Title.Replace("_", "__"), ToolTip = snippet.Description.Length == 0 ? null : snippet.Description, Tag = snippet };
                item.Click += (_, _) => InsertSnippet(snippet);
                folder.Items.Add(item);
            }
            menu.Items.Add(folder);
        }
        var caret = Editor.TextArea.TextView.GetVisualPosition(Editor.TextArea.Caret.Position, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom) - Editor.TextArea.TextView.ScrollOffset;
        menu.HorizontalOffset = Math.Max(0, caret.X); menu.VerticalOffset = Math.Max(0, caret.Y);
        menu.Closed += (_, _) => { if (ReferenceEquals(SnippetPicker, menu)) SnippetPicker = null; };
        SnippetPicker = menu;
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>Inserts the snippet at the caret (replacing or wrapping the selection); Tab moves between its fields, Enter or Esc leaves them, as in SSMS.</summary>
    internal void InsertSnippet(SqlSnippet snippet)
    {
        if (Editor.IsReadOnly) return;
        SnippetPicker?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var built = new Snippet();
        var first = new Dictionary<string, SnippetReplaceableTextElement>();
        foreach (var part in snippet.Parts())
            built.Elements.Add(part switch
            {
                SqlSnippet.TextPart t => new SnippetTextElement { Text = t.Text },
                SqlSnippet.LiteralPart l when first.TryGetValue(l.Literal.Id, out var target) => new SnippetBoundElement { TargetElement = target },
                SqlSnippet.LiteralPart l => first[l.Literal.Id] = new SnippetReplaceableTextElement { Text = l.Literal.Default },
                SqlSnippet.SelectedPart => new SnippetSelectionElement(),
                _ => new SnippetCaretElement()
            });
        Editor.Focus();
        built.Insert(Editor.TextArea);
        QueryStatus.Text = first.Count == 0 ? $"Inserted snippet {snippet.Title}." : $"Inserted snippet {snippet.Title}; Tab moves between its fields, Enter or Esc leaves them.";
        UpdateEditorInfo();
    }

    /// <summary>Replaces every &lt;name, type, value&gt; with the values the prompt returns, in one Undo step. Returns the number of replacements, or null when nothing changed.</summary>
    internal int? SpecifyTemplateParameters(Func<IReadOnlyList<SqlTemplateParameter>, IReadOnlyDictionary<string, string>?> prompt)
    {
        if (Editor.IsReadOnly) return null;
        var parameters = SqlTemplateParameters.Parse(Editor.Text);
        if (parameters.Count == 0) { QueryStatus.Text = "The query has no template parameters (<name, type, value>)."; return null; }
        if (prompt(parameters) is not { } values) return null;
        var edits = SqlTemplateParameters.Replacements(Editor.Text, values);
        var document = Editor.Document;
        using (document.RunUpdate())
            foreach (var (offset, length, text) in edits) document.Replace(offset, length, text);
        QueryStatus.Text = $"Replaced {edits.Count:N0} template parameter(s).";
        UpdateEditorInfo();
        Editor.Focus();
        return edits.Count;
    }
}
