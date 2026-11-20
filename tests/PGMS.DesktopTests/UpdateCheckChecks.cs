using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;

/// <summary>PGMS-083 (M1118): new version notice. No check reaches the network: the request is replaced.</summary>
internal static class UpdateCheckChecks
{
    private const string Releases = """
        [
          {"tag_name":"v0.1.0-beta.1","html_url":"https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-beta.1","draft":false,"prerelease":true},
          {"tag_name":"v0.1.0-alpha.8","html_url":"https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.8","draft":false,"prerelease":true},
          {"tag_name":"v0.2.0","html_url":"https://example.com/elsewhere","draft":true,"prerelease":false},
          {"tag_name":"nightly","html_url":"https://github.com/leoars/PostgresManagementStudio/releases/tag/nightly","draft":false,"prerelease":true},
          {"tag_name":"v0.1.0-alpha.7","html_url":"https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-alpha.7","draft":false,"prerelease":true}
        ]
        """;

    internal static async Task RunAsync(Action<bool, string> check)
    {
        static ReleaseVersion V(string s) => ReleaseVersion.Parse(s)!;
        check(V("v0.1.0-alpha.7") is { Major: 0, Minor: 1, Patch: 0, Label: "alpha.7" } && V("0.1.0+abc").Label == "" && ReleaseVersion.Parse("nightly") is null && ReleaseVersion.Parse("1.2.x") is null && ReleaseVersion.Parse("1.0.0-") is null, "Release tags are read as versions; other tags are not");
        check(V("0.1.0-alpha.10").CompareTo(V("0.1.0-alpha.9")) > 0 && V("0.1.0-beta.1").CompareTo(V("0.1.0-alpha.10")) > 0 && V("0.1.0").CompareTo(V("0.1.0-beta.1")) > 0 && V("0.2.0-alpha.1").CompareTo(V("0.1.9")) > 0 && V("0.1.0-alpha.7").CompareTo(V("v0.1.0-alpha.7")) == 0 && V("1.0.0-alpha").CompareTo(V("1.0.0-alpha.1")) < 0, "Versions compare as in Semantic Versioning (numbers as numbers, a pre-release before its release)");
        check(UpdateCheck.CurrentVersion("1.0.0+abc") is null && UpdateCheck.CurrentVersion(null) is null && UpdateCheck.CurrentVersion("0.1.0-alpha.7+abc") == V("0.1.0-alpha.7"), "A development build has no version to compare");
        var parsed = UpdateCheck.ParseReleases(Releases);
        check(parsed.Count == 3 && parsed.All(r => r.Address.StartsWith("https://github.com/leoars/PostgresManagementStudio/")) && parsed.All(r => r.Prerelease), "Drafts and tags that are not versions are skipped; pre-releases count");
        check(UpdateCheck.Newer(parsed, V("0.1.0-alpha.7"))?.Tag == "v0.1.0-beta.1" && UpdateCheck.Newer(parsed, V("0.1.0-beta.1")) is null && UpdateCheck.Newer(parsed, V("0.1.0-alpha.7"), "0.1.0-beta.1") is null && UpdateCheck.Newer(parsed, V("0.1.0-alpha.7"), "0.1.0-alpha.8")?.Tag == "v0.1.0-beta.1", "The newest newer release is offered, unless it is the skipped one");
        check(UpdateCheck.ParseReleases("""{"message":"API rate limit exceeded"}""").Count == 0, "An answer that is not a list offers nothing");

        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        try
        {
            var status = (TextBlock)shell.FindName("Status");
            var notice = (MenuItem)shell.FindName("UpdateMenu");
            var requests = new List<Uri>();
            shell.FetchReleases = (uri, _) => { requests.Add(uri); return Task.FromResult(Releases); };
            shell.EnvironmentSettings.Update(d => d with { CheckForUpdates = true, SkippedUpdateVersion = null });
            check(new EnvironmentOptions.Document().CheckForUpdates, "The check is on by default");
            check(notice.Visibility == Visibility.Collapsed, "No notice before a check");

            shell.BuildVersion = "1.0.0+dev";
            check(await shell.CheckForUpdatesAsync(false) is null && requests.Count == 0 && notice.Visibility == Visibility.Collapsed, "A development build does not ask");
            check(await shell.CheckForUpdatesAsync(true) is null && requests.Count == 0 && status.Text.Contains("development build"), "Check for Updates on a development build says why it cannot compare");

            shell.BuildVersion = "0.1.0-alpha.7+abc";
            string before = status.Text;
            var found = await shell.CheckForUpdatesAsync(false);
            check(found?.Tag == "v0.1.0-beta.1" && requests.Count == 1 && requests[0].AbsoluteUri.StartsWith("https://api.github.com/repos/leoars/PostgresManagementStudio/releases"), "The automatic check reads the release list of the public repository");
            check(notice.Visibility == Visibility.Visible && (string)notice.Header == "Update available" && notice.ToolTip is string tip && tip.Contains("0.1.0-beta.1") && tip.Contains("Nothing is downloaded") && status.Text == before, "A newer release shows the discreet notice, without touching the status bar");
            check(notice.Parent is Menu menu && menu.Items.IndexOf(notice) == menu.Items.IndexOf(shell.FindName("FeatureSearchMenu")) + 1, "The notice sits in the title band, right after Search");
            check(notice.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["_Open the release page of 0.1.0-beta.1", "_Skip This Version", "Update _Notification Options…"]), "The notice offers Open, Skip This Version and the options page");

            var opened = new List<string>(); shell.OpenExternal = opened.Add;
            check(shell.OpenOfferedUpdate() && opened.SequenceEqual(["https://github.com/leoars/PostgresManagementStudio/releases/tag/v0.1.0-beta.1"]) && notice.Visibility == Visibility.Visible, "Open goes to the release page in the browser and keeps the notice");

            shell.SkipOfferedUpdate();
            check(notice.Visibility == Visibility.Collapsed && shell.EnvironmentSettings.Load().SkippedUpdateVersion == "0.1.0-beta.1", "Skip This Version hides the notice and remembers the version");
            check(await shell.CheckForUpdatesAsync(false) is null && notice.Visibility == Visibility.Collapsed, "The skipped version is not offered again automatically");
            check((await shell.CheckForUpdatesAsync(true))?.Tag == "v0.1.0-beta.1" && notice.Visibility == Visibility.Visible && status.Text.Contains("0.1.0-beta.1 is available"), "Check for Updates still finds the skipped version");
            shell.HideUpdateNotice();

            shell.EnvironmentSettings.Update(d => d with { CheckForUpdates = false, SkippedUpdateVersion = null });
            int asked = requests.Count;
            check(await shell.CheckForUpdatesAsync(false) is null && requests.Count == asked && notice.Visibility == Visibility.Collapsed, "Turned off, the automatic check does not ask");
            check((await shell.CheckForUpdatesAsync(true)) is not null && requests.Count == asked + 1, "Turned off, Check for Updates still asks");
            shell.HideUpdateNotice();
            shell.EnvironmentSettings.Update(d => d with { CheckForUpdates = true });

            before = status.Text;
            shell.FetchReleases = (_, _) => throw new HttpRequestException("No such host is known.");
            check(await shell.CheckForUpdatesAsync(false) is null && notice.Visibility == Visibility.Collapsed && status.Text == before, "A network failure of the automatic check is silent");
            check(await shell.CheckForUpdatesAsync(true) is null && status.Text == "Could not check for updates: No such host is known.", "Check for Updates reports a network failure");
            shell.FetchReleases = (_, c) => Task.FromCanceled<string>(new CancellationToken(true));
            check(await shell.CheckForUpdatesAsync(true) is null && status.Text.EndsWith("the request timed out."), "A timeout is reported as such");
            shell.FetchReleases = (_, _) => Task.FromResult("<html>not json</html>");
            check(await shell.CheckForUpdatesAsync(false) is null && notice.Visibility == Visibility.Collapsed, "An unexpected answer is ignored");
            shell.FetchReleases = (_, _) => Task.FromResult("[]");
            check(await shell.CheckForUpdatesAsync(true) is null && status.Text == "PGMS is up to date (version 0.1.0-alpha.7).", "Up to date is reported by Check for Updates");

            OptionsDialog? shown = null; shell.OptionsShown = d => shown = d;
            shell.ShowOptions("Environment > Product Updates");
            var box = (CheckBox)shown!.FindName("CheckForUpdatesBox");
            check(((FrameworkElement)shown.FindName("ProductUpdatesPanel")).Visibility == Visibility.Visible && box.IsChecked == true, "Environment ▸ Product Updates shows the option, on");
            box.IsChecked = false; shown.Accept(shown, new RoutedEventArgs());
            check(!shell.EnvironmentSettings.Load().CheckForUpdates, "Turning it off in Options is saved");
            shown.Close();
            shell.EnvironmentSettings.Update(d => d with { CheckForUpdates = true });
        }
        finally { shell.Close(); await shell.CloseCompletion; }
    }
}
