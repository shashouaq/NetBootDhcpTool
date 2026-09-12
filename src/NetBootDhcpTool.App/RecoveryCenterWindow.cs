using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

internal sealed class RecoveryCenterWindow : Window
{
    private DataGrid? _entriesGrid;
    private readonly Button _restoreButton;

    public RecoveryCenterWindow(IReadOnlyList<RecoveryEntryViewModel> entries, LanguageService language, Window owner)
    {
        Owner = owner;
        Title = language.T("recovery.title");
        Width = 960;
        Height = 520;
        MinWidth = 720;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, language.T("recovery.title"));

        var root = new DockPanel { Margin = new Thickness(14) };
        var summary = new TextBlock
        {
            Text = language.T("recovery.items.summary"),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.ControlTextBrush
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _restoreButton = new Button
        {
            Content = language.T("recovery.restore.selected"),
            MinWidth = 160,
            IsEnabled = false,
            Margin = new Thickness(4)
        };
        var closeButton = new Button
        {
            Content = language.T("recovery.close"),
            MinWidth = 96,
            Margin = new Thickness(4)
        };
        HelpButtonService.Attach(_restoreButton, "help.recovery.center");
        HelpButtonService.Attach(closeButton, "help.dialog.cancel");
        _restoreButton.Click += (_, _) =>
        {
            if (SelectedEntry?.IsAvailable == true) DialogResult = true;
        };
        closeButton.Click += (_, _) => DialogResult = false;
        actions.Children.Add(_restoreButton);
        actions.Children.Add(closeButton);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        if (entries.Count == 0)
        {
            var emptyState = new TextBlock
            {
                Text = language.T("recovery.no.items"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8)
            };
            AutomationProperties.SetName(emptyState, language.T("recovery.no.items"));
            root.Children.Add(emptyState);
        }
        else
        {
            _entriesGrid = CreateGrid(language);
            _entriesGrid.ItemsSource = entries;
            _entriesGrid.SelectionChanged += (_, _) => _restoreButton.IsEnabled = SelectedEntry?.IsAvailable == true;
            root.Children.Add(_entriesGrid);
        }

        Content = root;
    }

    public RecoveryEntryViewModel? SelectedEntry => _entriesGrid?.SelectedItem as RecoveryEntryViewModel;

    private static DataGrid CreateGrid(LanguageService language)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            CanUserResizeColumns = true,
            CanUserResizeRows = false,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 220
        };
        AutomationProperties.SetName(grid, language.T("recovery.items.summary"));
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.type"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.TypeDisplay)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.adapter"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.AdapterName)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.identity"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.IdentityDisplay)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.captured"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.CapturedAtDisplay)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.details"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.Summary)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = language.T("recovery.column.status"), Binding = new System.Windows.Data.Binding(nameof(RecoveryEntryViewModel.StatusDisplay)), Width = 150 });
        return grid;
    }
}
