using System.ComponentModel;
using System.Windows;
using AiCloudProxy.ViewModels;
using AiCloudProxy.Views;

namespace AiCloudProxy;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public bool AllowClose { get; set; }

    /// <summary>Raised when the window was hidden to the system tray.</summary>
    public event Action? HideRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.LogEntries.CollectionChanged += (_, _) =>
        {
            // Defer until the ListView has finished processing the change. Calling
            // ScrollIntoView re-entrantly from CollectionChanged (with virtualization
            // on) throws "ItemsControl is inconsistent with its items source" when
            // entries arrive in bursts (e.g. proxy auto-start).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            }));
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Save the currently selected provider's key/base URL/model even when only hiding to the tray.
        _vm.PersistNow();

        if (!AllowClose && _vm.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            HideRequested?.Invoke();
            return;
        }
        base.OnClosing(e);
    }

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnQuickTourClick(object sender, RoutedEventArgs e) => QuickTourWindow.ShowDialog(this);
}
