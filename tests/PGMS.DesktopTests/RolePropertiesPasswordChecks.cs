using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class RolePropertiesPasswordChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var catalog = new CatalogService(connection);
        var roles = await catalog.ChildrenAsync(new CatalogEntry(CatalogKind.Roles, "Roles"));
        var login = roles.Single(r => r.RoleName == "pgms_test");
        check(login.Name == "pgms_test (login)" && login.CanManageRole, "Explorer keeps the exact rolname apart from the (login) label");

        var tls = new RoleAttributeDialog(connection.Replace("SSL Mode=Disable", "SSL Mode=Require", StringComparison.OrdinalIgnoreCase)) { ShowInTaskbar = false, Opacity = 0 };
        T Find<T>(string name) where T : FrameworkElement => (T)tls.FindName(name);
        try
        {
            tls.Show();
            Find<TextBox>("RoleName").Text = "synthetic_properties_role";
            check(!Find<Button>("PasswordButton").IsEnabled, "Role Properties password action starts disabled");
            Find<PasswordBox>("Password").Password = "Café_密碼"; Find<PasswordBox>("ConfirmPassword").Password = "Café_密碼";
            check(Find<Button>("PasswordButton").IsEnabled, "Matching passwords over TLS enable Set Password in Role Properties");
            Find<PasswordBox>("ConfirmPassword").Password = "Café_密碼x";
            check(!Find<Button>("PasswordButton").IsEnabled && Find<TextBlock>("PasswordFeedback").Text == "Passwords do not match.", "Role Properties mismatch disables Set Password");
            check(Find<TextBox>("Preview").Text.Length == 0 || !Find<TextBox>("Preview").Text.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase), "Password never reaches the attribute SQL preview");
        }
        finally { tls.Close(); }
        check(Find<PasswordBox>("Password").Password.Length == 0 && Find<PasswordBox>("ConfirmPassword").Password.Length == 0 && !tls.PasswordWasApplied, "Closing Role Properties clears both password inputs");

        // The fixture connects with SSL Mode=Require; Prefer is what an unconfigured local profile uses.
        string prefer = connection.Replace("SSL Mode=Require", "SSL Mode=Prefer", StringComparison.OrdinalIgnoreCase);
        foreach (bool remote in new[] { false, true })
        {
            var plain = new RoleAttributeDialog(remote ? prefer.Replace("Host=127.0.0.1", "Host=pgms-password-check.invalid", StringComparison.Ordinal) : prefer) { ShowInTaskbar = false, Opacity = 0 };
            T Plain<T>(string name) where T : FrameworkElement => (T)plain.FindName(name);
            try
            {
                plain.Show(); Plain<TextBox>("RoleName").Text = "pgms_test";
                Plain<PasswordBox>("Password").Password = "secret"; Plain<PasswordBox>("ConfirmPassword").Password = "secret";
                if (!remote)
                    check(Plain<Button>("PasswordButton").IsEnabled && Plain<CheckBox>("AllowUnencrypted").Visibility == Visibility.Collapsed && Plain<TextBlock>("PasswordFeedback").Text.StartsWith("Loopback", StringComparison.Ordinal), "Loopback profile without TLS allows the password change without an opt-in");
                else
                {
                    check(!Plain<Button>("PasswordButton").IsEnabled && Plain<CheckBox>("AllowUnencrypted").Visibility == Visibility.Visible && Plain<TextBlock>("PasswordFeedback").Text.Contains("TLS", StringComparison.Ordinal), "Remote profile without TLS refuses the password change until explicitly allowed");
                    Plain<CheckBox>("AllowUnencrypted").IsChecked = true;
                    check(Plain<Button>("PasswordButton").IsEnabled && Plain<TextBlock>("PasswordFeedback").Text.StartsWith("Warning", StringComparison.Ordinal), "Explicit opt-in enables an unencrypted remote password change with a warning");
                }
            }
            finally { plain.Close(); }
        }
        var remoteDialog = new RolePasswordDialog(prefer.Replace("Host=127.0.0.1", "Host=pgms-password-check.invalid", StringComparison.Ordinal), "pgms_test") { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            remoteDialog.Show();
            ((PasswordBox)remoteDialog.FindName("Password")).Password = "secret"; ((PasswordBox)remoteDialog.FindName("ConfirmPassword")).Password = "secret";
            bool refused = !((Button)remoteDialog.FindName("ApplyButton")).IsEnabled;
            ((CheckBox)remoteDialog.FindName("AllowUnencrypted")).IsChecked = true;
            check(refused && ((Button)remoteDialog.FindName("ApplyButton")).IsEnabled, "Set Role Password offers the same explicit opt-in for remote profiles without TLS");
        }
        finally { remoteDialog.Close(); }
        var inspected = new RoleAttributeDialog(connection, "pgms_test") { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            inspected.Show(); // Loaded starts the inspection on its own.
            var inspection = (TextBox)inspected.FindName("Inspection"); var close = (Button)inspected.FindName("CloseButton");
            for (int i = 0; i < 200 && (inspection.Text.Length == 0 || !close.IsEnabled); i++) await Task.Delay(50);
            check(inspection.Text.Contains("LOGIN = True"), "Role Properties opened for a role inspects it (" + ((TextBlock)inspected.FindName("Feedback")).Text + ")");
        }
        finally { inspected.Close(); }

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            tree.Items.Clear();
            var node = new CatalogNode(login, catalog); tree.Items.Add(node); node.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var action = menu.Items.OfType<MenuItem>().Single(m => m.Name == "ExplorerRoleProperties");
            check(action.IsEnabled, "Explorer context menu enables Role Properties on a role");
            menu.IsOpen = false;
            check(OpenRoleProperties(() => action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))) == "pgms_test", "Explorer Role Properties opens with the exact role name");
            node.IsSelected = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent };
            doubleClick.Source = container; // The first Source is also the OriginalSource the handler inspects.
            check(OpenRoleProperties(() => tree.RaiseEvent(doubleClick)) == "pgms_test" && node.IsSelected, "Double-clicking a role opens Role Properties as its default action");
        }
        finally { window.Close(); }
    }

    // The dialog is modal, so the trigger returns only after the timer closes it.
    private static string? OpenRoleProperties(Action trigger)
    {
        string? opened = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            // Wait for the automatic inspection; a busy dialog refuses to close.
            if (Application.Current.Windows.OfType<RoleAttributeDialog>().FirstOrDefault() is not { } properties || ((TextBox)properties.FindName("Inspection")).Text.Length == 0 || !((Button)properties.FindName("CloseButton")).IsEnabled) return;
            timer.Stop(); opened = ((TextBox)properties.FindName("RoleName")).Text; properties.Close();
        };
        timer.Start();
        try { trigger(); }
        finally { timer.Stop(); }
        return opened;
    }
}
