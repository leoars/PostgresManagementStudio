using System.Globalization;
using AvalonDock.Layout;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>
/// Connection texts as in SSMS 22.11 (PGMS-051, M1094): the Object Explorer server node carries the version and login, a query tab's tooltip and
/// the window title carry "server.database (login (pid))". The tab title itself stays the file name.
/// </summary>
public partial class MainWindow
{
    private string? baseTitle;

    /// <summary>"host:port (PostgreSQL 17.11 - login)" for the Object Explorer server node; without the version when it cannot be read.</summary>
    internal static string ServerNodeText(string connection, string? version)
    {
        var c = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        string server = $"{c.Host}:{c.Port.ToString(CultureInfo.InvariantCulture)}";
        return version is { Length: > 0 } ? $"{server} (PostgreSQL {version} - {c.Username})" : $"{server} ({c.Username})";
    }

    /// <summary>Reads the server version (from the connection startup, no query) and relabels the server node; on failure the node keeps "host:port (login)".</summary>
    private async Task LabelServerNodeAsync(ViewModels.CatalogNode root, string connection, CancellationToken token)
    {
        string? version = null;
        try
        {
            // Not pooled: no idle session is left behind on the explorer's database (a later DROP DATABASE would wait for it).
            await using var probe = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString);
            await probe.OpenAsync(token);
            version = probe.ServerVersion;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.IO.IOException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
        if (version is not null && !token.IsCancellationRequested && explorerConnection == connection) root.Label = ServerNodeText(connection, version);
    }

    /// <summary>Tab tooltip of a query: "path or name - server.database (login (pid))", or only the path or name when not connected.</summary>
    internal static string QueryTabToolTip(string name, string? path, string? caption) => caption is null ? path ?? name : $"{path ?? name} - {caption}";

    /// <summary>Refreshes the window title from the active document and every query tab's tooltip.</summary>
    internal void UpdateConnectionTexts()
    {
        baseTitle ??= Title;
        foreach (var (view, document) in documents) document.ToolTip = QueryTabToolTip(document.Title.TrimEnd('*'), view.FilePath, view.ConnectionCaption);
        if (DockManager.Layout?.ActiveContent is not LayoutDocument active) { if (!OpenDocuments().Any()) Title = baseTitle; return; }
        string name = active.Title;
        Title = active.Content is QueryView { ConnectionCaption: { } caption } ? $"{name} - {caption} - {baseTitle}" : $"{name} - {baseTitle}";
    }
}
