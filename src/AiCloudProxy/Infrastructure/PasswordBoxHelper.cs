using System.Windows;
using System.Windows.Controls;

namespace AiCloudProxy.Infrastructure;

/// <summary>
/// Two-way binding support for PasswordBox (Password is not bindable by default).
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxHelper));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnPasswordChanged;
        if (!(bool)pb.GetValue(IsUpdatingProperty))
        {
            pb.Password = (string?)e.NewValue ?? "";
        }
        pb.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox pb) return;
        if ((bool)pb.GetValue(IsUpdatingProperty)) return;

        pb.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBoundPassword(pb, pb.Password);
            // Explicitly push to the source: setting the attached DP alone does not
            // reliably propagate to the bound property (e.g. on paste), which caused
            // the API key to be lost when pasting into the masked box.
            pb.GetBindingExpression(BoundPasswordProperty)?.UpdateSource();
        }
        finally
        {
            pb.SetValue(IsUpdatingProperty, false);
        }
    }
}
