using System.Globalization;
using System.IO;
using System.Windows.Controls;
using PGMS.Desktop;

/// <summary>PGMS-076 (M1082): Help ▸ View Help and Release Notes.</summary>
internal static class HelpChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        string? pt=MainWindow.HelpDocument(new CultureInfo("pt-BR")),en=MainWindow.HelpDocument(new CultureInfo("en-US"));
        check(pt is not null && en is not null && File.Exists(pt) && File.Exists(en),"A guide is found next to the program (the repository README when run from a build)");
        check(Path.GetFileName(pt!) is "LEIA-ME.md" or "README.pt-BR.md" or "README.md" && Path.GetFileName(en!) is "READ-ME.md" or "README.md","Portuguese Windows prefers LEIA-ME, other languages READ-ME");
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();await shell.RecoveryCompletion;
        try
        {
            var opened=new List<string>();shell.OpenExternal=opened.Add;
            check(shell.ViewHelp() && opened.Count==1 && File.Exists(opened[0]),"View Help opens the guide outside PGMS");
            check(shell.ShowReleaseNotes() && opened[^1]==MainWindow.ReleaseNotesAddress && opened[^1].StartsWith("https://github.com/leoars/PostgresManagementStudio/releases"),"Release Notes opens the release list of the project");
            shell.OpenExternal=_=>throw new System.ComponentModel.Win32Exception("no handler");var errors=new List<string>();shell.ShowErrorOverride=ex=>errors.Add(ex.Message);
            check(!shell.ViewHelp() && errors.Count==1 && errors[0].StartsWith("Could not open it"),"A file association failure is reported, not thrown");
            var help=(MenuItem)shell.FindName("HelpMenu");
            check(help.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["View _Help","_Release Notes","Check for _Updates","_About PGMS"]) && ((MenuItem)shell.FindName("ViewHelpMenu")).InputGestureText=="Ctrl+F1","Help lists View Help (Ctrl+F1), Release Notes, Check for Updates and About");
            check(MainWindow.ViewHelpCommand.InputGestures.OfType<System.Windows.Input.KeyGesture>().Any(g=>g.Key==System.Windows.Input.Key.F1 && g.Modifiers==System.Windows.Input.ModifierKeys.Control),"Ctrl+F1 is the View Help shortcut");
        }
        finally{shell.Close();await shell.CloseCompletion;}
    }
}
