using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

public partial class FavoriteWindow : Window
{
    private readonly FavoriteConfig _favorite;
    private readonly ObservableCollection<FavoriteField> _customFields;

    public FavoriteWindow(FavoriteConfig favorite)
    {
        InitializeComponent();
        _favorite = favorite;
        _customFields = new ObservableCollection<FavoriteField>(favorite.CustomFields.Select(x => new FavoriteField { Name = x.Name, Value = x.Value }));
        CustomFieldGrid.ItemsSource = _customFields;
        NameBox.Text = favorite.Name;
        DeviceBox.Text = favorite.DeviceNumber;
        SnBox.Text = favorite.SerialNumber;
        RemarkBox.Text = favorite.RemarkName;
        UserBox.Text = favorite.Username;
        PasswordBox.Text = favorite.Password;
        IpBox.Text = favorite.LocalIp;
        MaskBox.Text = favorite.SubnetMask;
        TargetIpBox.Text = favorite.TargetIp;
        MemoryBox.Text = string.IsNullOrWhiteSpace(favorite.MemoryText) ? favorite.Description : favorite.MemoryText;
    }

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        var field = new FavoriteField { Name = "Field", Value = "" };
        _customFields.Add(field);
        CustomFieldGrid.SelectedItem = field;
        CustomFieldGrid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(field, CustomFieldGrid.Columns[0]);
        CustomFieldGrid.BeginEdit();
    }

    private void DeleteField_Click(object sender, RoutedEventArgs e)
    {
        if (CustomFieldGrid.SelectedItem is FavoriteField field)
        {
            _customFields.Remove(field);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var ip = IpBox.Text.Trim();
        var mask = MaskBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(mask))
        {
            ValidationText.Text = "Name, IP, Mask are required / 名称、IP、掩码必填";
            return;
        }
        if (!IPAddress.TryParse(ip, out _) || !IPAddress.TryParse(mask, out _))
        {
            ValidationText.Text = "Invalid IP or mask / IP 或掩码格式不正确";
            return;
        }

        _favorite.Name = name;
        _favorite.DeviceNumber = DeviceBox.Text.Trim();
        _favorite.SerialNumber = SnBox.Text.Trim();
        _favorite.RemarkName = RemarkBox.Text.Trim();
        _favorite.Username = UserBox.Text.Trim();
        _favorite.Password = PasswordBox.Text.Trim();
        _favorite.LocalIp = ip;
        _favorite.SubnetMask = mask;
        _favorite.TargetIp = TargetIpBox.Text.Trim();
        _favorite.Gateway = "";
        _favorite.Dns = "";
        _favorite.MemoryText = MemoryBox.Text.Trim();
        _favorite.Description = MemoryBox.Text.Trim();
        _favorite.CustomFields = _customFields
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new FavoriteField { Name = x.Name.Trim(), Value = x.Value.Trim() })
            .ToList();
        _favorite.UpdatedAt = DateTime.Now;
        DialogResult = true;
    }
}
