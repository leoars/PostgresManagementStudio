using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AvalonDock.Layout;
using Npgsql;
using PGMS.Desktop;

// Registered Servers (M1039). Uses a temporary profile file; never the user's connections.json.
internal static class RegisteredServersChecks
{    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pgms-registered-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "connections.json");
        try
        {
            // First save on a machine without a profile file, then a group.
            var fresh = new ConnectionProfileStore(Path.Combine(dir, "fresh.json"));
            fresh.Save(new ConnectionProfile(Guid.NewGuid(), "First", "localhost", 5432, "postgres", "app", "Prefer"));
            fresh.AddGroup("New");
            check(fresh.Load().Count == 1 && fresh.LoadGroups().SequenceEqual(["New"]), "The first profile and group are saved when no profile file exists yet");

            // A file written before groups existed still loads, and new fields do not break its shape.
            var legacyId = Guid.NewGuid();
            File.WriteAllText(file, $$"""{"Version":1,"Profiles":[{"Id":"{{legacyId}}","Name":"Legacy","Host":"db.example.com","Port":5432,"Database":"postgres","Username":"app","SslMode":"Require","ProtectedPassword":null}]}""");
            var store = new ConnectionProfileStore(file);
            check(store.Load().Single().Group is null && store.LoadGroups().Count == 0, "A profile file without groups loads with every server at the top level");
            store.AddGroup("Production"); store.AddGroup("Production/Billing");
            store.Move(legacyId, "Staging/East");
            check(store.LoadGroups().SequenceEqual(["Production", "Production/Billing", "Staging", "Staging/East"]), "Groups include explicit empty groups and the ancestors of servers' groups");
            using (var json = JsonDocument.Parse(File.ReadAllText(file)))
                check(json.RootElement.GetProperty("Version").GetInt32() == 1 && json.RootElement.GetProperty("Profiles").GetArrayLength() == 1 && json.RootElement.GetProperty("Groups").GetArrayLength() == 2,
                    "The file keeps version 1 and the Profiles array; groups are an extra field older versions ignore");
            bool Refused(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
            check(Refused(() => store.AddGroup("production")) && Refused(() => store.AddGroup("")) && Refused(() => store.AddGroup(" Ops")) && Refused(() => store.AddGroup("a//b")),
                "Duplicate (ignoring case), empty, padded and empty-segment group names are refused");
            check(Refused(() => store.DeleteGroup("Staging")) && Refused(() => store.DeleteGroup("Production")), "A group with servers or subgroups cannot be deleted, so no saved server is lost");
            store.DeleteGroup("Production/Billing");
            store.Move(legacyId, null);
            check(store.LoadGroups().SequenceEqual(["Production"]) && store.Load().Single().Group is null, "Empty groups are deleted and a server moves back to the top level");

            var profile = store.Load().Single();
            var settings = new NpgsqlConnectionStringBuilder(profile.ToConnectionString("pw"));
            check(settings.Host == "db.example.com" && settings.SslMode == SslMode.Require && settings.Timeout == 10 && settings.ApplicationName == "PGMS" && !settings.Pooling && settings.Password == "pw",
                "A registered server connects with the same settings as the Connect dialog");

            // Editing a profile in the Connect dialog keeps its group; a new registration lands in the chosen group.
            store.Move(legacyId, "Production");
            var edit = new ConnectionDialog(legacyId, profilePath: file) { ShowInTaskbar = false, Opacity = 0 };
            edit.Show();
            ((TextBox)edit.FindName("ProfileName")).Text = "Legacy renamed";
            ((Button)edit.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            edit.Close();
            check(store.Load().Single() is { Name: "Legacy renamed", Group: "Production" }, "Saving an edited profile keeps its server group");
            var c = new NpgsqlConnectionStringBuilder(connection);
            var create = new ConnectionDialog(newProfileGroup: "Production", profilePath: file) { ShowInTaskbar = false, Opacity = 0 };
            create.Show();
            create.Prefill(connection);
            ((PasswordBox)create.FindName("Password")).Password = "unused-with-trust";
            ((CheckBox)create.FindName("RememberPassword")).IsChecked = true;
            ((Button)create.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            create.Close();
            var test = store.Load().SingleOrDefault(p => p.Id == create.SavedProfileId);
            check(test is { Group: "Production" } && test.Name == $"{c.Host}:{c.Port}" && test.Port == c.Port && test.Username == c.Username, "Register… fills the dialog from the connection and saves into the chosen group");

            // The tool window.
            ConnectionProfile? prompted = null;
            var actions = new List<string>();
            var window = new MainWindow
            {
                ShowInTaskbar = false, RegisteredServersProfilePath = file,
                LocalServiceResolver = (host, port) => port == c.Port ? (new LocalPostgresService("pg-fake", "C:\\fake", port), LocalServiceState.Running) : (null, LocalServiceState.Unknown),
                LocalServiceController = (name, action) => { actions.Add($"{name}:{action}"); return Task.FromResult(action == LocalServiceAction.Stop ? LocalServiceState.Stopped : LocalServiceState.Running); },
                ConfirmServiceAction = _ => true,
                RegisteredPasswordPrompt = p => { prompted = p; return null; }
            };
            window.Show();
            try
            {
                window.ShowRegisteredServers();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = window.RegisteredServers;
                var anchorable = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "registered");
                var explorerPane = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "explorer").Parent;
                check(anchorable.Parent == explorerPane && anchorable.IsActive && anchorable.Title == "Registered Servers", "View ▸ Registered Servers docks the window next to Object Explorer");
                var root = view.Root!;
                var production = root.Children.Single();
                check(root.Name == "Local Server Groups" && production is { Kind: RegisteredServerNodeKind.Group, Name: "Production" } && production.Children.Select(n => n.Name).SequenceEqual(["127.0.0.1:" + c.Port, "Legacy renamed"]),
                    "The tree shows Local Server Groups, then groups and their servers sorted by name");

                List<MenuItem> MenuFor(RegisteredServerNode node) { var menu = new ContextMenu(); view.FillMenu(menu, node); return menu.Items.OfType<MenuItem>().ToList(); }
                var local = production.Children.Single(n => n.Profile!.Port == c.Port);
                var serverMenu = MenuFor(local);
                check(serverMenu.Select(m => m.Name).SequenceEqual(["RegisteredObjectExplorer", "RegisteredNewQuery", "RegisteredServiceControl", "RegisteredMoveTo", "RegisteredDeleteServer", "RegisteredRefresh", "RegisteredProperties"])
                    && serverMenu[0].FontWeight == FontWeights.Bold, "A registered server offers Object Explorer (default), New Query, Service Control, Move To, Delete, Refresh and Properties");
                var service = serverMenu.Single(m => m.Name == "RegisteredServiceControl");
                check(service.IsEnabled && service.Items.OfType<MenuItem>().Select(m => (m.Name, m.IsEnabled)).SequenceEqual([("RegisteredStartService", false), ("RegisteredStopService", true), ("RegisteredRestartService", true)]),
                    "Service Control follows the local service state");
                var moveTargets = serverMenu.Single(m => m.Name == "RegisteredMoveTo").Items.OfType<MenuItem>().ToList();
                check(moveTargets.Select(m => (string)m.Header).SequenceEqual(["Local Server Groups", "Production"]) && !moveTargets[1].IsEnabled && moveTargets[0].IsEnabled, "Move To lists the groups and disables the current one");
                var remoteService = MenuFor(production.Children.Single(n => n.Name == "Legacy renamed")).Single(m => m.Name == "RegisteredServiceControl");
                check(!remoteService.IsEnabled && ToolTipService.GetShowOnDisabled(remoteService), "Service Control is disabled for a remote server");
                check(MenuFor(production).Select(m => m.Name).SequenceEqual(["RegisteredNewGroup", "RegisteredNewRegistration", "RegisteredDeleteGroup", "RegisteredRefresh"]) && !MenuFor(production).Single(m => m.Name == "RegisteredDeleteGroup").IsEnabled,
                    "A group offers New Server Group, New Server Registration, Delete (only when empty) and Refresh");

                // Object Explorer from a registered server, using its saved password.
                await window.ConnectRegisteredExplorerAsync(local.Profile!);
                var tree = (TreeView)window.FindName("ExplorerTree");
                check(tree.Items.Count == 1, "Object Explorer connects to a registered server with its saved password");
                await window.ConnectRegisteredExplorerAsync(production.Children.Single(n => n.Name == "Legacy renamed").Profile!);
                check(prompted?.Name == "Legacy renamed" && tree.Items.Count == 1, "Without a saved password the password prompt opens, and cancelling it changes nothing");

                // Service Control from Registered Servers acts on the matching Object Explorer server.
                await window.ControlLocalServiceAsync(local.Profile!.Host, local.Profile.Port, LocalServiceAction.Stop);
                check(actions.SequenceEqual(["pg-fake:Stop"]) && tree.Items.Count == 0, "Stopping a registered local server disconnects Object Explorer when it shows that server");

                view.PromptGroupName = parent => parent == "Production" ? "Billing" : null;
                view.NewGroup("Production");
                view.Confirm = _ => true;
                view.MoveServer(local.Profile, "Production/Billing");
                var billing = view.Root!.Children.Single().Children.First();
                check(billing is { Name: "Billing", Kind: RegisteredServerNodeKind.Group } && billing.Children.Single().Profile!.Id == local.Profile.Id && view.Selected?.Profile?.Id == local.Profile.Id,
                    "New Server Group creates a subgroup and Move To moves the server, keeping it selected");                view.DeleteServer(local.Profile);
                view.DeleteGroup("Production/Billing");
                check(store.Load().Count == 1 && store.LoadGroups().SequenceEqual(["Production"]), "Delete removes the registered server and then its empty group");
            }
            finally
            {
                // The layout is saved on close and restored by later test windows: leave Object Explorer as the visible tab.
                var explorer = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "explorer");
                explorer.IsActive = true;
                window.Close();
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
