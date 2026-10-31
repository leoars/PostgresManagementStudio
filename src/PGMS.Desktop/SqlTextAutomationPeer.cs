using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

internal sealed class SqlTextArea : TextArea
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SqlTextAreaAutomationPeer(this);
}

internal sealed class SqlTextEditorAutomationPeer : TextEditorAutomationPeer
{
    private readonly SqlTextEditor editor;
    private string previousValue = "";
    internal SqlTextEditorAutomationPeer(SqlTextEditor owner) : base(owner)
    {
        editor = owner;
        previousValue = ListenerExists(AutomationEvents.PropertyChanged) ? owner.Text : "";
        owner.TextChanged += (_, _) =>
        {
            if (ListenerExists(AutomationEvents.TextPatternOnTextChanged)) RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
            if (ListenerExists(AutomationEvents.PropertyChanged))
            {
                string value = owner.Text;
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previousValue, value);
                previousValue = value;
            }
            else previousValue = "";
        };
        owner.TextArea.Caret.PositionChanged += SelectionChanged;
        owner.TextArea.SelectionChanged += SelectionChanged;
    }
    private void SelectionChanged(object? sender, EventArgs e)
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged)) RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }
    protected override bool HasKeyboardFocusCore() => editor.TextArea.IsKeyboardFocusWithin;
    protected override void SetFocusCore()
    {
        if (!editor.IsEnabled || !editor.TextArea.IsEnabled) throw new ElementNotEnabledException();
        // TextEditor forwards keyboard focus to its TextArea. The default peer
        // tests focus on the outer owner and rejects that successful forwarding.
        editor.TextArea.Focus();
        if (!editor.TextArea.IsKeyboardFocusWithin) throw new InvalidOperationException("The SQL text area could not receive keyboard focus.");
    }
    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Text or PatternInterface.Value
            ? UIElementAutomationPeer.CreatePeerForElement(editor.TextArea)?.GetPattern(patternInterface)
            : base.GetPattern(patternInterface);
}

// All state belongs to this editor. No hooks, reflection, input injection or forced focus.
internal sealed class SqlTextAreaAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, IValueProvider
{
    internal TextArea Area { get; }
    private TextDocument? observedDocument;
    private string previousValue = "";
    internal TextDocument Document
    {
        get { Area.VerifyAccess(); return Area.Document ?? throw new ElementNotAvailableException(); }
    }
    internal SqlTextAreaAutomationPeer(TextArea area) : base(area)
    {
        Area = area;
        area.DocumentChanged += (_, _) => ObserveDocument(true);
        area.SelectionChanged += SelectionChanged;
        area.Caret.PositionChanged += SelectionChanged;
        // Peer construction can occur while WPF is traversing GetChildren.
        // Publishing an event here tries to traverse that same tree recursively.
        ObserveDocument(false);
    }
    private void ObserveDocument(bool notify)
    {
        if (observedDocument is not null) observedDocument.TextChanged -= TextChanged;
        observedDocument = Area.Document;
        if (observedDocument is not null) observedDocument.TextChanged += TextChanged;
        if (notify) TextChanged(this, EventArgs.Empty);
        else previousValue = ListenerExists(AutomationEvents.PropertyChanged) ? observedDocument?.Text ?? "" : "";
    }
    private void TextChanged(object? sender, EventArgs e)
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextChanged)) RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            string value = observedDocument?.Text ?? "";
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previousValue, value);
            previousValue = value;
        }
        else previousValue = "";
    }
    private void SelectionChanged(object? sender, EventArgs e)
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged)) RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetClassNameCore() => nameof(SqlTextArea);
    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        if (string.IsNullOrWhiteSpace(name) && Area.GetService(typeof(TextEditor)) is TextEditor editor) name = AutomationProperties.GetName(editor);
        return string.IsNullOrWhiteSpace(name) ? "SQL editor" : name;
    }
    public override object? GetPattern(PatternInterface patternInterface)
    {
        if (patternInterface is PatternInterface.Text or PatternInterface.Value) return this;
        if (patternInterface == PatternInterface.Scroll && Area.GetService(typeof(TextEditor)) is TextEditor editor)
            return UIElementAutomationPeer.CreatePeerForElement(editor)?.GetPattern(patternInterface);
        return base.GetPattern(patternInterface);
    }
    internal IRawElementProviderSimple Provider => ProviderFromPeer(this) ?? throw new ElementNotAvailableException("The editor is not connected to an automation root.");
    internal bool Enabled => Area.IsEnabled && Area.GetService(typeof(TextEditor)) is not TextEditor { IsEnabled: false };
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
    public ITextRangeProvider DocumentRange => new SqlTextRange(this, Document, 0, Document.TextLength);
    public ITextRangeProvider[] GetSelection()
    {
        var document = Document;
        if (Area.Selection.IsEmpty) return [new SqlTextRange(this, document, Area.Caret.Offset, Area.Caret.Offset)];
        return Area.Selection.Segments.Select(s => (ITextRangeProvider)new SqlTextRange(this, document, s.StartOffset, s.EndOffset)).ToArray();
    }
    internal bool HasScreenLayout => Area.IsVisible && PresentationSource.FromVisual(Area.TextView) is not null && Area.TextView.VisualLinesValid;
    public ITextRangeProvider[] GetVisibleRanges()
    {
        _ = Document;
        if (!HasScreenLayout) return [];
        return VisibleRangesFromLayout();
    }
    internal ITextRangeProvider[] VisibleRangesFromLayout()
    {
        var document = Document;
        var view = Area.TextView;
        if (!view.VisualLinesValid || view.ActualWidth <= 0 || view.ActualHeight <= 0) return [];
        var ranges = new List<ITextRangeProvider>();
        foreach (var line in view.VisualLines)
        foreach (var textLine in line.TextLines)
        {
            double top = line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop);
            if (top + textLine.Height <= view.VerticalOffset || top >= view.VerticalOffset + view.ActualHeight) continue;
            int left = line.GetRelativeOffset(line.GetVisualColumn(textLine, view.HorizontalOffset, false));
            int right = line.GetRelativeOffset(line.GetVisualColumn(textLine, view.HorizontalOffset + view.ActualWidth, false));
            int first = line.FirstDocumentLine.Offset + Math.Min(left, right);
            int last = line.FirstDocumentLine.Offset + Math.Max(left, right);
            ranges.Add(new SqlTextRange(this, document, first, last));
        }
        return ranges.ToArray();
    }
    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => throw new ArgumentException("The SQL text stream has no embedded child objects.", nameof(childElement));
    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var document = Document;
        if (!double.IsFinite(screenLocation.X) || !double.IsFinite(screenLocation.Y)) throw new ArgumentException("Finite screen coordinates required.", nameof(screenLocation));
        if (!HasScreenLayout) throw new ElementNotAvailableException();
        var view = Area.TextView;
        Point point = view.PointFromScreen(screenLocation);
        point.X = Math.Clamp(point.X, 0, view.ActualWidth);
        point.Y = Math.Clamp(point.Y, 0, view.ActualHeight);
        var position = view.GetPosition(point + view.ScrollOffset);
        int offset = position is { } p ? document.GetOffset(p.Location) : point.Y + view.VerticalOffset <= 0 ? 0 : document.TextLength;
        return new SqlTextRange(this, document, offset, offset);
    }
    public bool IsReadOnly => Area.GetService(typeof(TextEditor)) is TextEditor { IsReadOnly: true } || (!Area.ReadOnlySectionProvider.CanInsert(0) && !Area.ReadOnlySectionProvider.GetDeletableSegments(new SelectionSegment(0, Document.TextLength)).Any());
    public string Value => Document.Text;
    public void SetValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var document = Document;
        if (!Enabled) throw new ElementNotEnabledException();
        if (IsReadOnly || !Area.ReadOnlySectionProvider.CanInsert(0)) throw new InvalidOperationException("The SQL editor is read-only.");
        // Value replaces the whole document; partial read-only regions must also be respected.
        int covered = 0;
        foreach (var segment in Area.ReadOnlySectionProvider.GetDeletableSegments(new SelectionSegment(0, document.TextLength)))
        {
            if (segment.Offset != covered) throw new InvalidOperationException("The SQL editor contains read-only text.");
            covered = segment.EndOffset;
        }
        if (covered != document.TextLength) throw new InvalidOperationException("The SQL editor contains read-only text.");
        document.Text = value;
    }
}
