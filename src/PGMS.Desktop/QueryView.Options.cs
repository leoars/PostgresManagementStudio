using System.Globalization;

namespace PGMS.Desktop;

public partial class QueryView
{
    private int commandTimeoutSeconds;
    public int CommandTimeoutSeconds
    {
        get=>commandTimeoutSeconds;
        set
        {
            if(value is <0 or >86400)throw new ArgumentOutOfRangeException(nameof(value));
            if(commandTimeoutSeconds==value)return;
            commandTimeoutSeconds=value;RecoveryRevision++;
            QueryTimeout.Text=value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
