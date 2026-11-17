using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Paper and margins of File ▸ Page Setup (PGMS-074, M1096). Sizes and margins in millimeters; the defaults are what SSMS 22.11 showed (A4, portrait, 25.4 mm).</summary>
public sealed record PrintPageSetup(string Paper = "A4", bool Landscape = false, double LeftMm = 25.4, double TopMm = 25.4, double RightMm = 25.4, double BottomMm = 25.4)
{
    /// <summary>Paper sizes offered in Page Setup, in millimeters (width × height, portrait).</summary>
    public static readonly IReadOnlyList<(string Name, double Width, double Height)> Papers = [("A4", 210, 297), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("A3", 297, 420), ("A5", 148, 210)];
    internal const double PixelsPerMm = 96 / 25.4;

    /// <summary>Page size in device-independent pixels, turned for landscape.</summary>
    public Size PageSize
    {
        get
        {
            var (_, w, h) = Papers.FirstOrDefault(p => p.Name == Paper) is { Name: not null } found ? found : Papers[0];
            return Landscape ? new Size(h * PixelsPerMm, w * PixelsPerMm) : new Size(w * PixelsPerMm, h * PixelsPerMm);
        }
    }

    public Thickness Margins => new(LeftMm * PixelsPerMm, TopMm * PixelsPerMm, RightMm * PixelsPerMm, BottomMm * PixelsPerMm);

    /// <summary>Null when valid; otherwise what to fix. Margins must leave at least 20 mm of text in each direction.</summary>
    public string? Problem()
    {
        if (!Papers.Any(p => p.Name == Paper)) return "Choose a paper size.";
        double[] margins = [LeftMm, TopMm, RightMm, BottomMm];
        if (margins.Any(m => double.IsNaN(m) || m < 0 || m > 100)) return "Enter margins from 0 to 100 millimeters.";
        var size = PageSize;
        if (size.Width - Margins.Left - Margins.Right < 20 * PixelsPerMm || size.Height - Margins.Top - Margins.Bottom < 20 * PixelsPerMm) return "The margins leave no room for the text.";
        return null;
    }
}

/// <summary>
/// Builds the pages File ▸ Print sends to the printer: the SQL text in the editor's font, wrapped at the right margin, with the document name and
/// "Page n of m" above each page (inferred from Visual Studio, whose editor SSMS uses; not compared on paper).
/// </summary>
public static class SqlPrinting
{
    public static FlowDocument Document(string text, FontFamily font, double fontSize, PrintPageSetup setup)
    {
        var size = setup.PageSize;
        var margins = setup.Margins;
        // Room for the header line above the text.
        var document = new FlowDocument
        {
            PageWidth = size.Width, PageHeight = size.Height, PagePadding = new Thickness(margins.Left, margins.Top + HeaderHeight, margins.Right, margins.Bottom),
            ColumnWidth = double.PositiveInfinity, FontFamily = font, FontSize = fontSize, TextAlignment = TextAlignment.Left
        };
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = fontSize * 1.25, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) paragraph.Inlines.Add(new Run(lines[i]));
        }
        document.Blocks.Add(paragraph);
        return document;
    }

    internal const double HeaderHeight = 24;

    /// <summary>The pages of the document, each with the header, optionally only pages first..last (1-based).</summary>
    public static DocumentPaginator Pages(FlowDocument document, string title, PrintPageSetup setup, int? first = null, int? last = null) =>
        new HeaderedPaginator(((IDocumentPaginatorSource)document).DocumentPaginator, title, setup, first, last);

    private sealed class HeaderedPaginator : DocumentPaginator
    {
        private readonly DocumentPaginator inner;
        private readonly string title;
        private readonly PrintPageSetup setup;
        private readonly int? first, last;

        public HeaderedPaginator(DocumentPaginator inner, string title, PrintPageSetup setup, int? first, int? last)
        {
            this.inner = inner; this.title = title; this.setup = setup; this.first = first; this.last = last;
            inner.ComputePageCount();
        }

        private int Total => inner.PageCount;
        private int Start => Math.Clamp((first ?? 1) - 1, 0, Math.Max(0, Total - 1));
        private int End => Math.Clamp((last ?? Total) - 1, Start, Math.Max(0, Total - 1));

        public override bool IsPageCountValid => inner.IsPageCountValid;
        public override int PageCount => Total == 0 ? 0 : End - Start + 1;
        public override Size PageSize { get => inner.PageSize; set => inner.PageSize = value; }
        public override IDocumentPaginatorSource? Source => inner.Source;

        public override DocumentPage GetPage(int pageNumber)
        {
            int real = Start + pageNumber;
            var page = inner.GetPage(real);
            var margins = setup.Margins;
            var visual = new ContainerVisual();
            visual.Children.Add(page.Visual);
            var header = new DrawingVisual();
            using (var context = header.RenderOpen())
            {
                var typeface = new Typeface("Segoe UI");
                double dpi = 1;
                var left = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.Black, dpi);
                var right = new FormattedText($"Page {real + 1} of {Total}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.Black, dpi);
                double width = page.Size.Width - margins.Left - margins.Right;
                left.MaxTextWidth = Math.Max(10, width - right.Width - 12); left.MaxLineCount = 1; left.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(left, new Point(margins.Left, margins.Top));
                context.DrawText(right, new Point(page.Size.Width - margins.Right - right.Width, margins.Top));
                context.DrawLine(new Pen(Brushes.Gray, 0.5), new Point(margins.Left, margins.Top + 16), new Point(page.Size.Width - margins.Right, margins.Top + 16));
            }
            visual.Children.Add(header);
            return new DocumentPage(visual, page.Size, page.BleedBox, page.ContentBox);
        }
    }
}
