using System.Windows;
using System.Windows.Media;

namespace NetBootDhcpTool.App;

public partial class ProfileEditorWindow : Window
{
    private readonly bool _zh;

    public ProfileEditorWindow(bool chinese, string? name = null, string? description = null)
    {
        InitializeComponent();
        _zh = chinese;
        Title = chinese ? "配置方案" : "Network Profile";
        TitleText.Text = chinese ? "保存网络配置方案" : "Save Network Profile";
        NameLabel.Text = chinese ? "方案名称" : "Profile name";
        DescriptionLabel.Text = chinese ? "说明（可选）" : "Description (optional)";
        CancelButton.Content = chinese ? "取消" : "Cancel";
        SaveButton.Content = chinese ? "保存" : "Save";
        NameBox.Text = name ?? "";
        DescriptionBox.Text = description ?? "";
        Loaded += (_, _) => NameBox.Focus();
    }

    public string ProfileName => NameBox.Text.Trim();
    public string ProfileDescription => DescriptionBox.Text.Trim();

    public void ApplyOwnerTheme(Window owner)
    {
        var windowBackground = owner.Resources["WindowBackgroundBrush"] as Brush;
        var panelBackground = owner.Resources["PanelBackgroundBrush"] as Brush;
        var inputBackground = owner.Resources["InputBackgroundBrush"] as Brush;
        var text = owner.Resources["TextBrush"] as Brush;
        var border = owner.Resources["BorderBrush"] as Brush;
        var accent = owner.Resources["AccentBrush"] as Brush;
        if (windowBackground == null || panelBackground == null || inputBackground == null || text == null || border == null || accent == null) return;

        Background = windowBackground;
        EditorRoot.Background = panelBackground;
        EditorRoot.BorderBrush = border;
        TitleText.Foreground = text;
        NameLabel.Foreground = text;
        DescriptionLabel.Foreground = text;
        NameBox.Background = inputBackground;
        NameBox.Foreground = text;
        NameBox.BorderBrush = border;
        DescriptionBox.Background = inputBackground;
        DescriptionBox.Foreground = text;
        DescriptionBox.BorderBrush = border;
        CancelButton.Background = inputBackground;
        CancelButton.Foreground = text;
        CancelButton.BorderBrush = border;
        SaveButton.Background = accent;
        SaveButton.BorderBrush = accent;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            AppDialog.Show(this, _zh ? "配置方案" : "Network Profile", _zh ? "请输入方案名称。" : "Enter a profile name.");
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
