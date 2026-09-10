using System.Windows;
using System.Windows.Media;

namespace NetBootDhcpTool.App;

public partial class AppDialog : Window
{
    public AppDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, string title, string message, bool confirm = false, bool danger = false, string? okText = null, string? cancelText = null)
    {
        var dialog = new AppDialog
        {
            Owner = owner,
            Title = title
        };
        dialog.ApplyOwnerTheme(owner, danger);
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(okText)) dialog.OkButton.Content = okText;
        if (!string.IsNullOrWhiteSpace(cancelText)) dialog.CancelButton.Content = cancelText;
        if (danger)
        {
            dialog.IconBadge.Background = new SolidColorBrush(Color.FromRgb(255, 244, 229));
            dialog.IconText.Foreground = new SolidColorBrush(Color.FromRgb(176, 96, 0));
            dialog.OkButton.Background = new SolidColorBrush(Color.FromRgb(179, 38, 30));
            dialog.OkButton.BorderBrush = new SolidColorBrush(Color.FromRgb(179, 38, 30));
        }
        var result = dialog.ShowDialog();
        return result == true;
    }

    private void ApplyOwnerTheme(Window owner, bool danger)
    {
        var themeOwner = owner;
        while (themeOwner != null && !themeOwner.Resources.Contains("WindowBackgroundBrush"))
        {
            themeOwner = themeOwner.Owner;
        }
        if (themeOwner == null) return;

        Brush Brush(string key, Brush fallback) => themeOwner.Resources[key] as Brush ?? fallback;
        var windowBackground = Brush("WindowBackgroundBrush", new SolidColorBrush(Color.FromRgb(247, 250, 253)));
        var panelBackground = Brush("PanelBackgroundBrush", Brushes.White);
        var inputBackground = Brush("InputBackgroundBrush", Brushes.White);
        var text = Brush("TextBrush", new SolidColorBrush(Color.FromRgb(22, 32, 51)));
        var border = Brush("BorderBrush", new SolidColorBrush(Color.FromRgb(208, 215, 222)));
        var accent = Brush("AccentBrush", new SolidColorBrush(Color.FromRgb(23, 105, 170)));
        var dangerBrush = Brush("DangerBrush", new SolidColorBrush(Color.FromRgb(179, 38, 30)));

        Background = windowBackground;
        DialogRoot.Background = panelBackground;
        DialogRoot.BorderBrush = border;
        TitleText.Foreground = text;
        MessageText.Foreground = text;
        CancelButton.Background = inputBackground;
        CancelButton.BorderBrush = border;
        CancelButton.Foreground = text;
        OkButton.Background = danger ? dangerBrush : accent;
        OkButton.BorderBrush = danger ? dangerBrush : accent;
        OkButton.Foreground = Brushes.White;
        IconBadge.Background = danger ? new SolidColorBrush(Color.FromArgb(55, 255, 139, 133)) : new SolidColorBrush(Color.FromArgb(55, 91, 169, 230));
        IconText.Foreground = danger ? dangerBrush : accent;
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
