using System.Windows;
using System.Windows.Media;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.App;

public partial class MacAddressWindow : Window
{
    public string MacAddress { get; private set; } = "";
    public bool RestoreOnExit => RestoreOnExitBox.IsChecked == true;

    public MacAddressWindow(NetworkAdapterInfo adapter)
    {
        InitializeComponent();
        AdapterText.Text = $"{adapter.DisplayName}\nCurrent MAC / 当前 MAC: {adapter.MacAddress}";
        MacBox.Text = NetworkAdapterService.GenerateRandomMacAddress();
    }

    public void ApplyOwnerTheme(Window owner)
    {
        foreach (var key in new[]
        {
            "WindowBackgroundBrush", "PanelBackgroundBrush", "InputBackgroundBrush", "TextBrush",
            "MutedTextBrush", "BorderBrush", "AccentBrush", "DangerBrush", "WarningBrush"
        })
        {
            if (owner.Resources[key] is Brush brush) Resources[key] = brush;
        }
    }

    private void Random_Click(object sender, RoutedEventArgs e)
    {
        MacBox.Text = NetworkAdapterService.GenerateRandomMacAddress();
        ValidationText.Text = "";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MacAddress = NetworkAdapterService.NormalizeMacAddress(MacBox.Text);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
        }
    }
}
