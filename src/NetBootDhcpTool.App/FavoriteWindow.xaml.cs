using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Media;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

public partial class FavoriteWindow : Window
{
    private readonly FavoriteConfig _favorite;
    private readonly ObservableCollection<FavoriteField> _customFields;

    public FavoriteWindow(FavoriteConfig favorite)
    {
        InitializeComponent();
        Title = "Favorite / 收藏配置";
        _favorite = favorite;
        _customFields = new ObservableCollection<FavoriteField>(favorite.CustomFields.Select(x => new FavoriteField { Name = x.Name, Value = x.Value }));
        CustomFieldGrid.ItemsSource = _customFields;
        NameBox.Text = favorite.Name;
        DeviceBox.Text = favorite.DeviceNumber;
        SnBox.Text = favorite.SerialNumber;
        RemarkBox.Text = favorite.RemarkName;
        UserBox.Text = favorite.Username;
        PasswordBox.Password = favorite.Password;
        CredentialStatus.Text = favorite.PasswordUnavailable
            ? "The saved password is unavailable. Enter a replacement or select Clear saved password. / 已存密码无法解密；请输入新密码或勾选清空。"
            : "Leave the password unchanged to keep it. / 保持密码栏原样即可保留密码。";
        PreferHttpsBox.IsChecked = favorite.PreferHttps;
        IpBox.Text = favorite.LocalIp;
        MaskBox.Text = favorite.SubnetMask;
        TargetIpBox.Text = favorite.TargetIp;
        MemoryBox.Text = string.IsNullOrWhiteSpace(favorite.MemoryText) ? favorite.Description : favorite.MemoryText;
    }

    public void ApplyOwnerTheme(Window owner)
    {
        foreach (var key in new[]
        {
            "WindowBackgroundBrush", "PanelBackgroundBrush", "InputBackgroundBrush", "TextBrush",
            "MutedTextBrush", "BorderBrush", "AccentBrush", "DangerBrush", "WarningBrush",
            "PositiveActionBrush"
        })
        {
            if (owner.Resources[key] is Brush brush) Resources[key] = brush;
        }
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
        if (!ScanRangePlan.TryCreate(ip, mask, TargetIpBox.Text, out _, out var rangeError))
        {
            ValidationText.Text = rangeError switch
            {
                ScanRangeError.TooManyTargets => $"Scan range exceeds {ScanRangePlan.MaxProbeTargets} actual targets / 扫描目标超过 {ScanRangePlan.MaxProbeTargets} 个",
                ScanRangeError.InvalidTargetIp => "Target must be a valid IPv4 address or empty / 目标必须是有效 IPv4 地址或留空",
                ScanRangeError.TargetOutsideSubnet => "Target must be in the local subnet / 目标必须在本机同一网段",
                ScanRangeError.TargetNotUsableHost => "Target cannot be network or broadcast address / 目标不能是网络地址或广播地址",
                _ => "Invalid IPv4 scan range / IPv4 扫描范围无效"
            };
            return;
        }

        _favorite.Name = name;
        _favorite.DeviceNumber = DeviceBox.Text.Trim();
        _favorite.SerialNumber = SnBox.Text.Trim();
        _favorite.RemarkName = RemarkBox.Text.Trim();
        _favorite.Username = UserBox.Text.Trim();
        var keepPublicDefault = _favorite.IsPublicDefault
            && !string.IsNullOrWhiteSpace(_favorite.PublicPassword)
            && string.Equals(PasswordBox.Password.Trim(), _favorite.Password, StringComparison.Ordinal)
            && string.Equals(PasswordBox.Password.Trim(), _favorite.PublicPassword, StringComparison.Ordinal);
        if (ClearPasswordBox.IsChecked == true)
        {
            _favorite.Password = "";
            _favorite.ProtectedPassword = "";
            _favorite.PublicPassword = "";
            _favorite.PasswordUnavailable = false;
            _favorite.IsPublicDefault = false;
        }
        else if (!string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            if (!keepPublicDefault)
            {
                _favorite.Password = PasswordBox.Password.Trim();
                _favorite.ProtectedPassword = "";
                _favorite.PublicPassword = "";
                _favorite.PasswordUnavailable = false;
                _favorite.IsPublicDefault = false;
            }
        }
        _favorite.PreferHttps = PreferHttpsBox.IsChecked == true;
        _favorite.LocalIp = ip;
        _favorite.SubnetMask = mask;
        _favorite.TargetIp = TargetIpBox.Text.Trim();
        _favorite.MemoryText = MemoryBox.Text.Trim();
        _favorite.CustomFields = _customFields
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new FavoriteField { Name = x.Name.Trim(), Value = x.Value.Trim() })
            .ToList();
        _favorite.UpdatedAt = DateTime.Now;
        DialogResult = true;
    }
}
