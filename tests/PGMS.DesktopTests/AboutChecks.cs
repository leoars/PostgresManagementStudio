using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>The About window: who made PGMS, where to find them and the license.</summary>
internal static class AboutChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var about = new AboutWindow { ShowInTaskbar = false };
        var opened = new List<string>();
        about.OpenTarget = opened.Add;
        about.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            T Named<T>(string name) where T : class => (T)about.FindName(name);
            string Uri(string name) => Named<Hyperlink>(name).NavigateUri.AbsoluteUri;
            check(Named<TextBlock>("Attribution").Text == "Created and maintained by Leonardo Santos" && Named<TextBlock>("Attribution").IsVisible, "About names the author");
            check(Named<TextBlock>("CopyrightNotice").Text.Contains("Copyright (c) 2026 Leonardo Santos", StringComparison.Ordinal) && Named<TextBlock>("CopyrightNotice").Text.Contains("personal and business", StringComparison.Ordinal), "About shows the copyright and that use is free");
            check(Uri("GitHubLink") == "https://github.com/leoars" && Uri("LinkedInLink") == "https://www.linkedin.com/in/leonardoars/" && Uri("IssuesLink") == "https://github.com/leoars/PostgresManagementStudio/issues", "About links to GitHub, LinkedIn and issues");
            string Shown(string name) => new TextRange(Named<Hyperlink>(name).ContentStart, Named<Hyperlink>(name).ContentEnd).Text;
            check(Shown("GitHubLink") == "https://github.com/leoars" && Shown("LinkedInLink") == "https://www.linkedin.com/in/leonardoars/", "The GitHub and LinkedIn addresses are written out");
            Named<Hyperlink>("LinkedInLink").RaiseEvent(new RequestNavigateEventArgs(Named<Hyperlink>("LinkedInLink").NavigateUri, null) { RoutedEvent = Hyperlink.RequestNavigateEvent });
            check(opened.SequenceEqual(["https://www.linkedin.com/in/leonardoars/"]), "A link opens its address");
            opened.Clear();
            Named<Hyperlink>("LicenseLink").RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Named<Hyperlink>("NoticesLink").RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            check(opened.Count == 2 && Path.GetFileName(opened[0]) == "LICENSE" && Path.GetFileName(opened[1]) == "THIRD-PARTY-NOTICES.md", "View license and Third-party notices open the local files");
            string license = await File.ReadAllTextAsync(AboutWindow.FindDocument("LICENSE")!);
            string notice = await File.ReadAllTextAsync(AboutWindow.FindDocument("NOTICE")!);
            check(license.Contains("Apache License", StringComparison.Ordinal) && license.Contains("Version 2.0, January 2004", StringComparison.Ordinal) && Named<TextBlock>("CopyrightNotice").Text.Contains("Apache-2.0", StringComparison.Ordinal), "The license is Apache-2.0 and About says so");
            check(notice.Contains("Copyright 2026 Leonardo Santos", StringComparison.Ordinal) && notice.Contains("Created and maintained by Leonardo Santos", StringComparison.Ordinal) && notice.Contains("https://github.com/leoars", StringComparison.Ordinal), "NOTICE carries the author's credits");
            check(AboutWindow.VersionText("0.1.0-alpha.5+abc123") == "Version 0.1.0-alpha.5" && AboutWindow.VersionText("1.0.0") == "Development build" && AboutWindow.VersionText(null) == "Development build", "The version comes from the release, without the commit id");
            check(Named<TextBlock>("ProductVersion").Text.Length > 0, "About shows the version line");
            about.OpenTarget = _ => throw new InvalidOperationException("no browser");
            Named<Hyperlink>("GitHubLink").RaiseEvent(new RequestNavigateEventArgs(Named<Hyperlink>("GitHubLink").NavigateUri, null) { RoutedEvent = Hyperlink.RequestNavigateEvent });
            check(Named<TextBlock>("LinkStatus").Text.Contains("no browser", StringComparison.Ordinal), "A link that cannot open says so, without closing the window");
        }
        finally { about.Close(); }
    }
}
