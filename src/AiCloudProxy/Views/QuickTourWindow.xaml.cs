using System.Windows;

namespace AiCloudProxy.Views;

public partial class QuickTourWindow : Window
{
    public QuickTourWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens the quick tour as a modal dialog centered on the given owner window.</summary>
    public static void ShowDialog(Window? owner)
    {
        var window = new QuickTourWindow { Owner = owner };
        window.ShowDialog();
    }

    private void OnGotItClick(object sender, RoutedEventArgs e) => Close();
}
