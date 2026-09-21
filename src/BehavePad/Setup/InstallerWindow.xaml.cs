using System.Windows;

namespace BehavePad.Setup;

public partial class InstallerWindow : Window
{
    public InstallerWindow(InstallerViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.CloseRequested += (_, _) => Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
