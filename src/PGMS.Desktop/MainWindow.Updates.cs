using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace PGMS.Desktop;

/// <summary>New version notice (PGMS-083, M1118). When Tools ▸ Options ▸ Environment ▸ Product Updates allows it (on by default), PGMS reads the
/// release list of the public repository once after opening, without delaying the opening; a newer release shows the discreet "Update available"
/// item in the title band, next to Search, which opens the release page in the browser. Help ▸ Check for Updates does the same on demand.
/// A failed automatic check says nothing; nothing is downloaded or installed.</summary>
public partial class MainWindow
{
    /// <summary>The request; tests replace it so they never use the network.</summary>
    internal Func<Uri, CancellationToken, Task<string>> FetchReleases { get; set; } = UpdateCheck.FetchAsync;
    /// <summary>The informational version of this build; tests replace it (a development build has no comparable version).</summary>
    internal string? BuildVersion { get; set; } = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    /// <summary>The release the notice offers, or null when the notice is hidden.</summary>
    internal AvailableRelease? OfferedUpdate { get; private set; }
    private bool checkingUpdates;

    /// <summary>Called once after the window opens, in the real application only (test hosts never reach the network).</summary>
    private void ScheduleUpdateCheck()
    {
        if (Application.Current is not App || !EnvironmentSettings.Load().CheckForUpdates) return;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => { timer.Stop(); await CheckForUpdatesAsync(manual: false); };
        timer.Start();
    }

    /// <summary>Checks the releases. Automatic: honors the option and the skipped version, and is silent on failure or when up to date.
    /// Manual (Help ▸ Check for Updates): always asks and reports the outcome in the status bar. Returns the newer release, if any.</summary>
    internal async Task<AvailableRelease?> CheckForUpdatesAsync(bool manual)
    {
        var settings = EnvironmentSettings.Load();
        if (!manual && !settings.CheckForUpdates || checkingUpdates) return null;
        if (UpdateCheck.CurrentVersion(BuildVersion) is not { } current)
        {
            if (manual) Status.Text = "This is a development build: it has no release version to compare with the published ones.";
            return null;
        }
        checkingUpdates = true;
        if (manual) Status.Text = "Checking for updates…";
        try
        {
            using var cancellation = new CancellationTokenSource(UpdateCheck.Timeout);
            var newer = await UpdateCheck.FindNewerAsync(FetchReleases, current, manual ? null : settings.SkippedUpdateVersion, cancellation.Token);
            if (closeRequested || closingWindow) return null;
            if (newer is not null) ShowUpdateNotice(newer);
            if (manual) Status.Text = newer is null ? $"PGMS is up to date (version {current})." : $"PGMS {newer.Version} is available. Use Update available in the title bar to open its release page.";
            return newer;
        }
        catch (Exception ex) when (UpdateCheck.IsExpectedFailure(ex))
        {
            if (manual) Status.Text = "Could not check for updates: " + (ex is OperationCanceledException ? "the request timed out." : ex.Message);
            return null;
        }
        finally { checkingUpdates = false; }
    }

    private void ShowUpdateNotice(AvailableRelease release)
    {
        OfferedUpdate = release;
        UpdateMenu.Header = "Update available";
        UpdateMenu.ToolTip = $"PGMS {release.Version}{(release.Prerelease ? " (pre-release)" : "")} is available. Nothing is downloaded or installed by PGMS.";
        OpenUpdateMenu.Header = $"_Open the release page of {release.Version}";
        UpdateMenu.Visibility = Visibility.Visible;
    }

    internal void HideUpdateNotice() { OfferedUpdate = null; UpdateMenu.Visibility = Visibility.Collapsed; }

    /// <summary>Opens the release page in the browser; the notice stays until PGMS closes or the version is skipped.</summary>
    internal bool OpenOfferedUpdate() => OfferedUpdate is { } release && OpenOutside(release.Address, $"Release page of PGMS {release.Version} opened in the browser");

    /// <summary>Stops offering this version; a newer one is offered again.</summary>
    internal void SkipOfferedUpdate()
    {
        if (OfferedUpdate is not { } release) return;
        EnvironmentSettings.Update(d => d with { SkippedUpdateVersion = release.Version.ToString() });
        HideUpdateNotice();
        Status.Text = $"PGMS {release.Version} will not be offered again. Help ▸ Check for Updates still finds it.";
    }

    private void OpenUpdateClicked(object sender, RoutedEventArgs e) => OpenOfferedUpdate();
    private void SkipUpdateClicked(object sender, RoutedEventArgs e) => SkipOfferedUpdate();
    private void UpdateOptionsClicked(object sender, RoutedEventArgs e) => ShowOptions("Environment > Product Updates");
    private async void CheckForUpdatesClicked(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);
}
