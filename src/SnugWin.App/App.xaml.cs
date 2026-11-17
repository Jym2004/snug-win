using System;
using System.Threading;
using System.Windows;
using SnugWin.App.Controllers;
using SnugWin.App.Views;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.App;

public partial class App : System.Windows.Application
{
    static App()
    {
        // Enforce Per-Monitor V2 DPI awareness before WPF/WinForms initializes
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        // Prevent D3D9 hardware-surface white-screen issues on WDDM IddCx virtual displays
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
    private Mutex? _singleInstanceMutex;
    private AppController? _controller;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _idleTrayIcon;
    private System.Drawing.Icon? _connectedTrayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _statusMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _manualPhoneMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _updateMenuItem;
    private bool _notifiedUpdate;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\SnugWin_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("SnugWin 已在后台运行，请从系统托盘打开设置。", "SnugWin", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _controller = new AppController(Dispatcher);
        SetupTrayIcon();
        _controller.Start();

        _controller.StateSummaryChanged += summary =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_statusMenuItem is not null) _statusMenuItem.Text = $"状态：{summary}";
                if (_trayIcon is not null)
                {
                    _trayIcon.Text = $"SnugWin - {summary}";
                    var desiredIcon = _controller.IsPhoneScreenReady ? (_connectedTrayIcon ?? _idleTrayIcon) : _idleTrayIcon;
                    if (desiredIcon is not null && !ReferenceEquals(_trayIcon.Icon, desiredIcon))
                        _trayIcon.Icon = desiredIcon;
                }
                if (_manualPhoneMenuItem is not null) _manualPhoneMenuItem.Checked = _controller.ManualOverride;
            });
        };

        _controller.UpdateStatusChanged += upd =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (upd is not null && upd.HasUpdate)
                {
                    if (_updateMenuItem is not null)
                        _updateMenuItem.Text = $"发现新版本 v{upd.LatestVersion}（点击更新）";
                    if (!_notifiedUpdate && _trayIcon is not null)
                    {
                        _notifiedUpdate = true;
                        _trayIcon.ShowBalloonTip(
                            3500,
                            $"SnugWin v{upd.LatestVersion} 已发布",
                            "点击托盘「关于与更新」可一键校验并升级。",
                            System.Windows.Forms.ToolTipIcon.Info);
                    }
                }
                else if (_updateMenuItem is not null)
                {
                    _updateMenuItem.Text = "检查更新...";
                }
            });
        };

        // Show Settings on manual startup (unless --minimized is passed for autostart)
        bool minimized = Array.Exists(e.Args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized)
            ShowSettings();
    }

    private void SetupTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _statusMenuItem = new System.Windows.Forms.ToolStripMenuItem("状态：初始化中") { Enabled = false };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var openSettings = new System.Windows.Forms.ToolStripMenuItem("打开设置与诊断...");
        openSettings.Click += (_, _) => Dispatcher.Invoke(() => ShowSettings(0));
        menu.Items.Add(openSettings);

        _updateMenuItem = new System.Windows.Forms.ToolStripMenuItem("检查更新...");
        _updateMenuItem.Click += (_, _) => Dispatcher.Invoke(() => ShowSettings(3));
        menu.Items.Add(_updateMenuItem);

        _manualPhoneMenuItem = new System.Windows.Forms.ToolStripMenuItem("手动开启手机屏（测试/兜底）")
        {
            CheckOnClick = true
        };
        _manualPhoneMenuItem.Click += (_, _) =>
        {
            if (_controller is not null)
                _controller.SetManualOverride(_manualPhoneMenuItem.Checked);
        };
        menu.Items.Add(_manualPhoneMenuItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 SnugWin（先还原窗口）");
        exitItem.Click += async (_, _) =>
        {
            if (_controller is not null)
                await _controller.ShutdownAsync();
            _trayIcon?.Dispose();
            Shutdown();
        };
        menu.Items.Add(exitItem);

        _idleTrayIcon = LoadResourceIcon("Assets/snugwin-idle.ico") ?? System.Drawing.SystemIcons.Application;
        _connectedTrayIcon = LoadResourceIcon("Assets/snugwin-connected.ico") ?? _idleTrayIcon;

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _idleTrayIcon,
            Text = "SnugWin - UU远程手机竖屏伴侣",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowSettings(0));
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => ShowSettings(3));
    }

    private static System.Drawing.Icon? LoadResourceIcon(string relativeUri)
    {
        try
        {
            var streamInfo = GetResourceStream(new Uri($"pack://application:,,,/{relativeUri}"));
            if (streamInfo?.Stream is null) return null;
            using var s = streamInfo.Stream;
            return new System.Drawing.Icon(s);
        }
        catch
        {
            return null;
        }
    }

    private void ShowSettings(int initialTab = 0)
    {
        if (_controller is null) return;
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_controller, initialTab);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.SelectTab(initialTab);
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _idleTrayIcon?.Dispose();
        _connectedTrayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
