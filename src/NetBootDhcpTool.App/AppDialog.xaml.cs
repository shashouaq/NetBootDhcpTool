using System.Windows;
using System.Windows.Media;

namespace NetBootDhcpTool.App;

public partial class AppDialog : Window
{
    public AppDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, string title, string message, bool confirm = false, bool danger = false)
    {
        var dialog = new AppDialog
        {
            Owner = owner,
            Title = title
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        if (danger)
        {
            dialog.IconBadge.Background = new SolidColorBrush(Color.FromRgb(255, 244, 229));
            dialog.IconText.Foreground = new SolidColorBrush(Color.FromRgb(176, 96, 0));
        }
        var result = dialog.ShowDialog();
        return result == true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
