using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SnugWin.App.Controllers;

namespace SnugWin.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private bool _suppressEvents;

    public SettingsWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        _controller.UiStateChanged += () => Dispatcher.BeginInvoke(Refresh);
        _controller.LogAppended += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        LogBox.Text = string.Join(Environment.NewLine, _controller.GetRecentLogs()) + Environment.NewLine;
        LogBox.ScrollToEnd();
        Refresh();
    }

    private void Refresh()
    {
        _suppressEvents = true;
        try
        {
            StatusText.Text = $"状态：{_controller.GetStatusSummary()}";
            if (!_controller.IsDriverPipeAvailable)
            {
                DriverWarningText.Text = "未检测到 virtual-display-rs 管道 (\\\\.\\pipe\\virtualdisplaydriver)。请先安装 virtual-display-rs 驱动及服务。";
                DriverWarningText.Visibility = Visibility.Visible;
            }
            else
            {
                DriverWarningText.Visibility = Visibility.Collapsed;
            }

            ElevationText.Text = _controller.IsElevated ? "当前权限：管理员（可控制高权限窗口）" : "当前权限：普通用户";
            ElevateBtn.Visibility = _controller.IsElevated ? Visibility.Collapsed : Visibility.Visible;

            var cfg = _controller.Config;
            PhoneScreenCheck.IsChecked = cfg.PhoneScreen;
            GesturesCheck.IsChecked = cfg.Gestures;
            ScreenshotCheck.IsChecked = cfg.Screenshot;
            InjectedOnlyCheck.IsChecked = _controller.RequireInjectedInput;
            ManualPhoneCheck.IsChecked = _controller.ManualOverride;
            PhoneModelText.Text = $"{cfg.Phone.Model ?? "自定义"}（{cfg.Phone.Screen.Width} × {cfg.Phone.Screen.Height}）";

            foreach (ComboBoxItem item in ZoomCombo.Items)
            {
                if (item.Tag is string s && int.TryParse(s, out int val) && val == cfg.ZoomSteps)
                {
                    ZoomCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnPhoneScreenChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetPhoneScreenEnabled(PhoneScreenCheck.IsChecked == true);
    }

    private void OnGesturesChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetGesturesEnabled(GesturesCheck.IsChecked == true);
    }

    private void OnScreenshotChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetScreenshotEnabled(ScreenshotCheck.IsChecked == true);
    }

    private void OnInjectedOnlyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.RequireInjectedInput = InjectedOnlyCheck.IsChecked == true;
    }

    private void OnManualChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _controller.SetManualOverride(ManualPhoneCheck.IsChecked == true);
    }

    private void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ZoomCombo.SelectedItem is ComboBoxItem { Tag: string s } && int.TryParse(s, out int val))
            _controller.SetDefaultZoomSteps(val);
    }

    private void OnOpenModels(object sender, RoutedEventArgs e)
    {
        new PhoneModelWindow(_controller).ShowDialog();
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _controller.LogDirectory,
            UseShellExecute = true
        });
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch
        {
            // User declined UAC prompt
        }
    }
}
