using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public sealed class CatalogIconConverter : IMultiValueConverter
{
    private static readonly IReadOnlyDictionary<string, DrawingImage> Icons = CreateIcons();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is not CatalogEntry entry) return DependencyProperty.UnsetValue;
        if (entry.Kind == CatalogKind.Column && entry.Oid == 0 && entry.Name is
            "(empty)" or "(no matching items)" or "Expand to load…" or "Could not load. Collapse and expand to retry.")
            return DependencyProperty.UnsetValue;
        string key = entry.Kind switch
        {
            CatalogKind.Server => "server",
            CatalogKind.Database => "database",
            CatalogKind.Schema => "schema",
            CatalogKind.Relation => entry.RelationType switch
            {
                "v" => "view", "m" => "materialized", "S" => "sequence", "p" => "partition", _ => "table"
            },
            CatalogKind.Routine => "routine",
            CatalogKind.Column => "column",
            CatalogKind.Index => "index",
            CatalogKind.Role => "role",
            CatalogKind.DataType => entry.DataTypeKind == "d" ? "domain" : "enum",
            _ => values.Length > 1 && values[1] is true ? "folder-open" : "folder"
        };
        return Icons[key];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IReadOnlyDictionary<string, DrawingImage> CreateIcons()
    {
        var icons = new Dictionary<string, DrawingImage>();
        void Add(string key, params (string Color, string Shape)[] layers)
        {
            var group = new DrawingGroup();
            // Transparent bounds keep every glyph aligned in the same 16-DIP cell.
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            foreach (var (color, shape) in layers)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(shape)));
            }
            var image = new DrawingImage(group); image.Freeze(); icons.Add(key, image);
        }
        const string ink = "#536675", blue = "#4E8EB8", light = "#E1EFF7", gold = "#D0A64B", paper = "#FFF5D4", purple = "#8066A7";
        Add("folder", (gold, "M1,3 L6,3 8,5 15,5 15,14 1,14 Z"), (paper, "M2,6 L14,6 14,13 2,13 Z"));
        Add("folder-open", (gold, "M1,3 L6,3 8,5 14,5 14,7 3,7 1,14 Z"), (gold, "M3,7 L16,7 13,14 1,14 Z"), (paper, "M4,8 L14.5,8 12.5,13 2.5,13 Z"));
        Add("server", (ink, "M3,1 L13,1 13,15 3,15 Z"), ("#DCE4E9", "M4,2 L12,2 12,5 4,5 Z M4,6 L12,6 12,9 4,9 Z M4,10 L12,10 12,14 4,14 Z"), ("#3B8751", "M5,11 L7,11 7,13 5,13 Z"));
        Add("database", (blue, "M2,4 C2,0 14,0 14,4 L14,12 C14,16 2,16 2,12 Z"), (light, "M3,5 C5,7 11,7 13,5 L13,11 C11,13 5,13 3,11 Z"), ("#9AC2D9", "M3,3 C3,1 13,1 13,3 C13,5 3,5 3,3 Z"));
        Add("schema", (ink, "M7,5 L9,5 9,9 14,9 14,12 12,12 12,11 4,11 4,12 2,12 2,9 7,9 Z"), (blue, "M5,1 L11,1 11,6 5,6 Z M0,11 L6,11 6,16 0,16 Z M10,11 L16,11 16,16 10,16 Z"), (light, "M6,2 L10,2 10,5 6,5 Z M1,12 L5,12 5,15 1,15 Z M11,12 L15,12 15,15 11,15 Z"));
        const string grid = "M1,2 L15,2 15,14 1,14 Z";
        const string cells = "M2,6 L5,6 5,9 2,9 Z M6,6 L10,6 10,9 6,9 Z M11,6 L14,6 14,9 11,9 Z M2,10 L5,10 5,13 2,13 Z M6,10 L10,10 10,13 6,13 Z M11,10 L14,10 14,13 11,13 Z";
        Add("table", (blue, grid), (light, cells));
        Add("partition", (blue, grid), (light, cells), (gold, "M10,9 L16,9 16,15 10,15 Z"), (paper, "M11,10 L15,10 15,12 11,12 Z M11,13 L13,13 13,14 11,14 Z M14,13 L15,13 15,14 14,14 Z"));
        Add("view", (purple, grid), (light, cells), (ink, "M6,10 Q11,4 16,10 Q11,16 6,10 Z"), ("#FFFFFF", "M8,10 Q11,7 14,10 Q11,13 8,10 Z"), (purple, "M10,9 L12,9 12,11 10,11 Z"));
        Add("materialized", (purple, grid), (light, cells), (gold, "M9,8 L15,8 15,15 9,15 Z"), (paper, "M10,9 L14,9 14,14 10,14 Z"));
        Add("sequence", ("#3B8751", "M1,2 L8,2 8,5 1,5 Z M5,7 L12,7 12,10 5,10 Z M9,12 L16,12 16,15 9,15 Z M6,4 L8,4 8,8 6,8 Z M10,9 L12,9 12,13 10,13 Z"));
        Add("routine", (purple, "M5,2 L1,8 5,14 7,13 3.5,8 7,3 Z M11,2 L15,8 11,14 9,13 12.5,8 9,3 Z"), (gold, "M7,6 L9,6 9,10 7,10 Z"));
        Add("column", (blue, "M5,1 L11,1 11,15 5,15 Z"), (light, "M6,5 L10,5 10,8 6,8 Z M6,9 L10,9 10,11 6,11 Z M6,12 L10,12 10,14 6,14 Z"));
        Add("index", (gold, "M1,5 A4,4 0 1 1 8,8 L15,8 15,11 13,11 13,13 11,13 11,10 8,10 7,8 A4,4 0 0 1 1,5 Z"), (paper, "M3,4 A1.5,1.5 0 1 0 6,4 A1.5,1.5 0 1 0 3,4 Z"));
        Add("role", (blue, "M5,4 A3,3 0 1 0 11,4 A3,3 0 1 0 5,4 Z M2,15 L2,12 C2,7 14,7 14,12 L14,15 Z"), (light, "M4,14 L4,12 C4,9 12,9 12,12 L12,14 Z"));
        Add("enum", (gold, "M1,2 L4,2 4,5 1,5 Z M1,7 L4,7 4,10 1,10 Z M1,12 L4,12 4,15 1,15 Z"), (ink, "M6,3 L15,3 15,5 6,5 Z M6,8 L15,8 15,10 6,10 Z M6,13 L15,13 15,15 6,15 Z"));
        Add("domain", (purple, "M8,1 L15,8 8,15 1,8 Z"), (light, "M8,4 L12,8 8,12 4,8 Z"));
        return icons;
    }
}
