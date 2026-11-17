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
    private System.Windows.Forms.ToolStripMenuItem? _statusMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _manualPhoneMenuItem;
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
                if (_trayIcon is not null) _trayIcon.Text = $"SnugWin - {summary}";
                if (_manualPhoneMenuItem is not null) _manualPhoneMenuItem.Checked = _controller.ManualOverride;
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
        openSettings.Click += (_, _) => Dispatcher.Invoke(ShowSettings);
        menu.Items.Add(openSettings);

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

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "SnugWin - UU远程手机竖屏伴侣",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);
    }

    private void ShowSettings()
    {
        if (_controller is null) return;
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_controller);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
