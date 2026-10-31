using System.Globalization;
using System.Windows.Data;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

internal sealed record ColumnIdentityChoice(ColumnIdentity Value,string Label);
internal static class ColumnIdentityChoices
{
    internal static ColumnIdentityChoice[] All=>[new(ColumnIdentity.None,"None"),new(ColumnIdentity.ByDefault,"By default"),new(ColumnIdentity.Always,"Always")];
    internal static string Label(ColumnIdentity value)=>value switch {ColumnIdentity.ByDefault=>"By default",ColumnIdentity.Always=>"Always",_=>"None"};
    internal static bool Supports(ColumnType type)=>type is ColumnType.Smallint or ColumnType.Integer or ColumnType.Bigint;
}
public sealed class ColumnIdentityTextConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is ColumnIdentity identity?ColumnIdentityChoices.Label(identity):"";
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
