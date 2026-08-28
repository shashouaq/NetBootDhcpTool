using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetBootDhcpTool.App;

/// <summary>
/// Adds a separate, compact clickable round question-mark button beside an action button.
/// The action remains unchanged; the help control only displays contextual guidance.
/// </summary>
public static class HelpButtonService
{
    public static readonly DependencyProperty HelpKeyProperty = DependencyProperty.RegisterAttached(
        "HelpKey",
        typeof(string),
        typeof(HelpButtonService),
        new PropertyMetadata(null, OnHelpKeyChanged));

    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "Installed",
        typeof(bool),
        typeof(HelpButtonService),
        new PropertyMetadata(false));

    public static void SetHelpKey(DependencyObject element, string value) => element.SetValue(HelpKeyProperty, value);
    public static string GetHelpKey(DependencyObject element) => (string)element.GetValue(HelpKeyProperty);

    public static void Attach(Button button, string helpKey) => SetHelpKey(button, helpKey);

    private static void SetInstalled(DependencyObject element, bool value) => element.SetValue(InstalledProperty, value);
    private static bool GetInstalled(DependencyObject element) => (bool)element.GetValue(InstalledProperty);

    private static void OnHelpKeyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Button button) return;
        button.Loaded -= Button_Loaded;
        if (e.NewValue is not string key || string.IsNullOrWhiteSpace(key)) return;

        button.Loaded += Button_Loaded;
        if (button.IsLoaded)
        {
            button.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Install(button, key));
        }
    }

    private static void Button_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Loaded -= Button_Loaded;
            Install(button, GetHelpKey(button));
        }
    }

    private static void Install(Button actionButton, string helpKey)
    {
        if (GetInstalled(actionButton) || string.IsNullOrWhiteSpace(helpKey)) return;

        var parent = actionButton.Parent;
        if (parent is Panel panel)
        {
            var index = panel.Children.IndexOf(actionButton);
            if (index < 0) return;
            var host = CreateHost(actionButton);
            CopyPanelLayout(panel, actionButton, host);
            panel.Children.RemoveAt(index);
            panel.Children.Insert(index, host);
            host.Children.Add(actionButton);
            AddHelpButton(host, actionButton, helpKey);
            return;
        }

        if (parent is ItemsControl itemsControl)
        {
            var index = itemsControl.Items.IndexOf(actionButton);
            if (index < 0) return;
            var host = CreateHost(actionButton);
            if (itemsControl is ToolBar toolbar)
            {
                ToolBar.SetOverflowMode(host, ToolBar.GetOverflowMode(actionButton));
            }
            itemsControl.Items.RemoveAt(index);
            itemsControl.Items.Insert(index, host);
            host.Children.Add(actionButton);
            AddHelpButton(host, actionButton, helpKey);
        }
    }

    private static StackPanel CreateHost(Button actionButton) => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = actionButton.HorizontalAlignment,
        VerticalAlignment = actionButton.VerticalAlignment,
        Margin = actionButton.Margin
    };

    private static void CopyPanelLayout(Panel parent, Button actionButton, StackPanel host)
    {
        if (parent is DockPanel)
        {
            DockPanel.SetDock(host, DockPanel.GetDock(actionButton));
        }
        if (parent is Grid)
        {
            Grid.SetRow(host, Grid.GetRow(actionButton));
            Grid.SetColumn(host, Grid.GetColumn(actionButton));
            Grid.SetRowSpan(host, Grid.GetRowSpan(actionButton));
            Grid.SetColumnSpan(host, Grid.GetColumnSpan(actionButton));
        }
        if (parent is Canvas)
        {
            Canvas.SetLeft(host, Canvas.GetLeft(actionButton));
            Canvas.SetTop(host, Canvas.GetTop(actionButton));
            Canvas.SetRight(host, Canvas.GetRight(actionButton));
            Canvas.SetBottom(host, Canvas.GetBottom(actionButton));
        }
    }

    private static void AddHelpButton(StackPanel host, Button actionButton, string helpKey)
    {
        actionButton.Margin = new Thickness(0);
        var helpButton = new Button
        {
            Content = "?",
            Tag = helpKey,
            Width = 9,
            Height = 9,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 0, 0),
            FontSize = 5.5,
            FontWeight = FontWeights.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(238, 246, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(159, 186, 214)),
            BorderThickness = new Thickness(1),
            Focusable = false,
            Visibility = actionButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed,
            Template = CreateHelpTemplate()
        };
        var helpTip = new ToolTip { MaxWidth = 460, Content = "Hover for help / 悬浮查看帮助" };
        helpButton.ToolTip = helpTip;
        helpButton.Click += HelpButton_Click;
        helpButton.ToolTipOpening += (_, _) => UpdateToolTip(helpTip, helpKey, helpButton);
        if (actionButton.ToolTip == null)
        {
            var actionTip = new ToolTip { MaxWidth = 460, Content = "Hover for help / 悬浮查看帮助" };
            actionButton.ToolTip = actionTip;
            actionButton.ToolTipOpening += (_, _) => UpdateToolTip(actionTip, helpKey, actionButton);
        }
        actionButton.IsVisibleChanged += (_, _) =>
        {
            helpButton.Visibility = actionButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        };
        host.Children.Add(helpButton);
        SetInstalled(actionButton, true);
    }

    private static void UpdateToolTip(ToolTip toolTip, string helpKey, DependencyObject source)
    {
        var window = Window.GetWindow(source);
        var mainWindow = FindMainWindow(window);
        toolTip.Content = mainWindow?.GetHelpText(helpKey)
            ?? "Help content is unavailable / 暂无帮助内容。";
    }

    private static ControlTemplate CreateHelpTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6.5));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    private static void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button helpButton || helpButton.Tag is not string helpKey) return;
        var window = Window.GetWindow(helpButton);
        var mainWindow = FindMainWindow(window);
        if (mainWindow != null)
        {
            mainWindow.ShowHelp(helpKey);
        }
        else if (window != null)
        {
            AppDialog.Show(window, "Help / 帮助", "Help content is unavailable / 暂无帮助内容。\n" + helpKey);
        }
        e.Handled = true;
    }

    private static MainWindow? FindMainWindow(Window? window)
    {
        for (var current = window; current != null; current = current.Owner)
        {
            if (current is MainWindow mainWindow) return mainWindow;
        }
        return Application.Current?.MainWindow as MainWindow;
    }
}
