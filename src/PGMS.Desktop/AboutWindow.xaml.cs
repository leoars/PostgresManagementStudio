using System.Windows;

namespace PGMS.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Milestone.Text = $"Development milestone {BuildInfo.Milestone}";
    }

    private void CloseAbout(object sender, RoutedEventArgs e) => Close();
}
