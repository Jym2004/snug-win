using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SnugWin.App.Views;

public partial class PhoneHomeWindow : Window
{
    public enum SubPage { None, Search, PhoneModel }

    public sealed class RunningAppVm
    {
        private static readonly Brush SecondaryBrush = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

        public RunningApp App { get; }
        public string AppName => App.App.Name;
        public ImageSource Icon { get; }
        public string Subtitle { get; }
        public Brush SubtitleBrush { get; }
        public bool NotQuitting { get; }
        public Visibility NormalActionsVis { get; }
        public Visibility AskQuitVis { get; }
        public Visibility QuittingVis { get; }
        public IReadOnlyList<WindowVm> Windows { get; }

        public RunningAppVm(
            RunningApp app,
            PhoneDeskState state,
            ImageSource icon,
            bool askQuit,
            Func<WindowKey, bool> isAskClose,
            Action onStateReady)
        {
            App = app;
            Icon = icon;
            bool quitting = state.Quitting.Contains(app.Pid);
            NotQuitting = !quitting;
            QuittingVis = quitting ? Visibility.Visible : Visibility.Collapsed;
            AskQuitVis = !quitting && askQuit ? Visibility.Visible : Visibility.Collapsed;
            NormalActionsVis = !quitting && !askQuit ? Visibility.Visible : Visibility.Collapsed;

            if (askQuit)
            {
                Subtitle = $"退出 {AppName}？";
                SubtitleBrush = RedBrush;
            }
            else if (quitting)
            {
                Subtitle = "正在退出";
                SubtitleBrush = SecondaryBrush;
            }
            else
            {
                Subtitle = app.Windows.Count == 0 ? "没有窗口" : $"{app.Windows.Count} 个窗口";
                SubtitleBrush = SecondaryBrush;
            }

            Windows = app.Windows
                .Select(w => new WindowVm(w, state, isAskClose(w.Key)))
                .ToList();
        }
    }

    public sealed class WindowVm
    {
        private static readonly Brush WhiteBrush = Brushes.White;
        private static readonly Brush TintBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        private static readonly Brush SecondaryBrush = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

        public WindowInfo Window { get; }
        public bool OnPhone { get; }
        public string DisplayTitle { get; }
        public string RowSymbol => OnPhone ? "📱" : "❐";
        public Brush RowSymbolBrush => OnPhone ? TintBrush : SecondaryBrush;
        public Brush TitleBrush { get; }
        public bool NotClosing { get; }
        public Visibility TagsVis { get; }
        public Visibility OnPhoneTagVis { get; }
        public Visibility MinimizedVis { get; }
        public Visibility NormalCloseVis { get; }
        public Visibility AskCloseVis { get; }
        public Visibility ClosingVis { get; }
        public Visibility PlacedControlsVis { get; }
        public Visibility ZoomControlsVis { get; }
        public bool CanZoomOut { get; }
        public bool CanZoomIn { get; }
        public string ZoomText { get; }

        public WindowVm(WindowInfo window, PhoneDeskState state, bool askClose)
        {
            Window = window;
            OnPhone = state.Placed?.Window.Key == window.Key;
            bool closing = state.Closing.Contains(window.Key);
            NotClosing = !closing;
            DisplayTitle = askClose ? "关闭此窗口？" : (string.IsNullOrEmpty(window.Title) ? "无标题" : window.Title);
            TitleBrush = askClose ? RedBrush : WhiteBrush;

            TagsVis = askClose ? Visibility.Collapsed : Visibility.Visible;
            OnPhoneTagVis = OnPhone ? Visibility.Visible : Visibility.Collapsed;
            MinimizedVis = window.Minimized ? Visibility.Visible : Visibility.Collapsed;

            ClosingVis = closing ? Visibility.Visible : Visibility.Collapsed;
            AskCloseVis = !closing && askClose ? Visibility.Visible : Visibility.Collapsed;
            NormalCloseVis = !closing && !askClose ? Visibility.Visible : Visibility.Collapsed;

            if (OnPhone && state.Placed is { } placed)
            {
                PlacedControlsVis = Visibility.Visible;
                ZoomControlsVis = placed.Zoomable ? Visibility.Visible : Visibility.Collapsed;
                CanZoomOut = placed.Zoom > ZoomBook.MinSteps;
                CanZoomIn = placed.Zoom < ZoomBook.MaxSteps;
                ZoomText = placed.Zoom == 0 ? "默认字号" : $"字号 {(placed.Zoom > 0 ? "+" : "")}{placed.Zoom}";
            }
            else
            {
                PlacedControlsVis = Visibility.Collapsed;
                ZoomControlsVis = Visibility.Collapsed;
                ZoomText = string.Empty;
            }
        }
    }

    public sealed record AllAppVm(AppInfo App, ImageSource Icon, bool NotQuitting, double RunningDotOpacity)
    {
        public string Name => App.Name;
    }

    public sealed record SearchAppRowVm(AppInfo App, ImageSource Icon, string Subtitle, bool NotQuitting)
    {
        public string Name => App.Name;
    }

    public sealed record SearchLetterGroupVm(string Letter, IReadOnlyList<SearchAppRowVm> Apps);

    public sealed record SearchWindowRowVm(WindowInfo Window, ImageSource Icon)
    {
        public string Title => string.IsNullOrEmpty(Window.Title) ? Window.AppName : Window.Title;
        public string AppName => Window.AppName;
    }

    public sealed record ModelBrandVm(string Name, IReadOnlyList<ModelItemVm> Models);
    public sealed record ModelItemVm(PhoneModel Model, bool Selected)
    {
        public string Name => Model.Name;
        public string SizeText => Model.Screen.Points is { } pts
            ? $"{Model.Screen.Width} × {Model.Screen.Height}（{pts}pt）"
            : $"{Model.Screen.Width} × {Model.Screen.Height}";
        public Visibility CheckVis => Selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly AppController _controller;
    private readonly bool _previewMode;
    private readonly Action _uiChangedHandler;
    private SubPage _subPage = SubPage.None;
    private bool _showAllTab;
    private bool _confirmDismiss;
    private int? _confirmQuitPid;
    private WindowKey? _confirmCloseWindow;
    private (string App, int Pid)? _stalledQuit;
    private bool _lastListVisible = true;
    private DeskWorkKind? _lastWorkKind;
    private string? _lastRunningSig;
    private string? _lastAllSig;
    private static readonly Brush SegmentActiveBrush = CreateFrozenBrush(0x63, 0x63, 0x66);

    public bool AcceptsKeys => _subPage != SubPage.None;

    public PhoneHomeWindow(AppController controller, bool previewMode = false)
    {
        InitializeComponent();
        _controller = controller;
        _previewMode = previewMode;
        if (_previewMode)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanMinimize;
            ShowInTaskbar = true;
            Topmost = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowPlacementHelper.ApplyModernDarkChrome(this);
        }
        _uiChangedHandler = () => Dispatcher.BeginInvoke(SyncState);
        _controller.UiStateChanged += _uiChangedHandler;
        Closed += (_, _) => _controller.UiStateChanged -= _uiChangedHandler;
        UpdateTabStyles();
    }

    public void ApplyPreviewLayout(SizeD workAreaDip, ScreenFit fit, PhoneChoice phone)
    {
        if (!_previewMode) return;
        var (designW, designH, scale) = ScreenFit.ComputePreviewLayout(workAreaDip, fit);
        PhoneRootGrid.Width = designW;
        PhoneRootGrid.Height = designH;
        PhoneRootGrid.LayoutTransform = new ScaleTransform(scale, scale);
        Title = $"SnugWin 预览 — {phone.Model ?? "自定义"} ({phone.Screen.Width} × {phone.Screen.Height})";
    }

    public void CloseSubPage()
    {
        if (_subPage == SubPage.None) return;
        _subPage = SubPage.None;
        SearchInputBox.Text = string.Empty;
        ModelSearchBox.Text = string.Empty;
        UpdatePageVisibility();
    }

    public void ShowOnDisplay(RectD bounds, bool focusOnShow)
    {
        WindowPlacementHelper.PlacePhysical(
            this,
            bounds,
            topmost: true,
            noActivate: !AcceptsKeys,
            focusIfActivated: focusOnShow && AcceptsKeys);
    }

    public void SyncState()
    {
        var st = _controller.DeskState;

        if (st.ListVisible != _lastListVisible)
        {
            _lastListVisible = st.ListVisible;
            ClearConfirmations();
            if (st.ListVisible)
                HomeScroll.ScrollToTop();
        }

        if (st.Work?.Kind != _lastWorkKind)
        {
            _lastWorkKind = st.Work?.Kind;
            ClearConfirmations();
        }

        if (st.Placed is { } placed)
        {
            BackBtnLabel.Text = $"‹ {placed.Window.AppName}";
            BackBtn.Visibility = Visibility.Visible;
        }
        else
        {
            BackBtn.Visibility = Visibility.Collapsed;
        }

        DismissBanner.Visibility = _confirmDismiss ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotice(st.Notice);
        UpdateBusyOverlay(st);
        UpdatePageVisibility();
        RefreshRunningAndAll();
        if (_subPage == SubPage.Search)
        {
            RefreshSearchPage();
        }
        else if (_subPage == SubPage.PhoneModel)
        {
            RefreshModelPage();
        }
    }

    private void ShowPreviewReadOnlyNotice()
    {
        NoticeBanner.Visibility = Visibility.Visible;
        NoticeHeadline.Text = "本地预览模式";
        NoticeDetail.Text = "仅演示 App 资源库、拼音搜索与机型比例，不会移动或关闭桌面窗口；连入 UU 后即可实战操控。";
        NoticeActionsRow.Visibility = Visibility.Collapsed;
        NoticeCloseBtn.Visibility = Visibility.Visible;
    }

    private void ClearConfirmations()
    {
        _confirmDismiss = false;
        _confirmQuitPid = null;
        _confirmCloseWindow = null;
    }

    private void UpdatePageVisibility()
    {
        HomePageView.Visibility = _subPage == SubPage.None ? Visibility.Visible : Visibility.Collapsed;
        SearchPageView.Visibility = _subPage == SubPage.Search ? Visibility.Visible : Visibility.Collapsed;
        ModelPageView.Visibility = _subPage == SubPage.PhoneModel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBusyOverlay(PhoneDeskState st)
    {
        if (st.Work is null || st.Work.Kind is DeskWorkKind.PuttingBack or DeskWorkKind.Zooming)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        switch (st.Work.Kind)
        {
            case DeskWorkKind.Opening:
                var appMatch = _controller.InstalledApps.FirstOrDefault(a => a.Name == st.Work.Label)
                               ?? _controller.RunningApps.Select(r => r.App).FirstOrDefault(a => a.Name == st.Work.Label);
                BusyIcon.Source = appMatch is not null ? _controller.Icons.GetIcon(appMatch) : null;
                BusyTitle.Text = st.Work.Label ?? string.Empty;
                BusySubtitle.Visibility = Visibility.Collapsed;
                BusyStatus.Text = "正在打开";
                break;

            case DeskWorkKind.Placing:
                var winMatch = _controller.RunningApps.SelectMany(r => r.Windows).FirstOrDefault(w => w.Label == st.Work.Label);
                BusyIcon.Source = winMatch is not null ? _controller.Icons.GetIcon(winMatch) : null;
                BusyTitle.Text = winMatch?.AppName ?? st.Work.Label ?? string.Empty;
                BusySubtitle.Text = winMatch is not null && !string.IsNullOrEmpty(winMatch.Title) ? winMatch.Title : string.Empty;
                BusySubtitle.Visibility = string.IsNullOrEmpty(BusySubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
                BusyStatus.Text = "正在打开";
                break;

            case DeskWorkKind.Reshaping:
                BusyIcon.Source = null;
                BusyTitle.Text = _controller.Config.Phone.Model ?? "自定义";
                var sc = _controller.Config.Phone.Screen;
                BusySubtitle.Text = $"{sc.Width} × {sc.Height}";
                BusySubtitle.Visibility = Visibility.Visible;
                BusyStatus.Text = "正在调整屏幕比例";
                break;

            case DeskWorkKind.Dismissing:
                BusyIcon.Source = null;
                BusyTitle.Text = PhoneScreenSpec.Name;
                BusySubtitle.Visibility = Visibility.Collapsed;
                BusyStatus.Text = "正在退出";
                break;
        }

        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateNotice(DeskNotice? notice)
    {
        _stalledQuit = null;
        if (notice is null || _confirmDismiss)
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            return;
        }

        NoticeBanner.Visibility = Visibility.Visible;
        (string headline, string detail) = notice switch
        {
            DeskNotice.WindowGone g => ($"{g.App} 窗口已关闭", "请另选一个窗口"),
            DeskNotice.WindowUnresponsive u => ($"{u.App} 无响应", "可能正忙，请稍后再试（或以管理员身份运行）"),
            DeskNotice.RecordUnwritable => ("无法保存原位置", "为确保能放回原处，窗口未移动"),
            DeskNotice.ScreenNotReady => ("手机屏未就绪", "屏幕正在调整，请稍后再试"),
            DeskNotice.OpenFailed o => ($"无法打开 {o.App}", "请稍后再试"),
            DeskNotice.NoNewWindow n => ($"{n.App} 无法新建窗口", "可点 App 打开已有窗口"),
            DeskNotice.NoWindow n => ($"未找到 {n.App} 的窗口", "App 已打开，但没有可显示的窗口"),
            DeskNotice.QuitStalled q => ($"{q.App} 未退出", "强制退出将丢失未存储的内容"),
            DeskNotice.Reshaped => ("屏幕比例已调整", "在 UU远程 中切到其他屏幕再切回，点按位置才准确"),
            DeskNotice.ReshapeFailed => ("屏幕比例未能调整", "下次连接时生效"),
            DeskNotice.ZoomLeftBehind z => ($"{z.App} 窗口已关闭", $"再打开时字号若偏大，按 {z.Steps} 次 Ctrl+- 复原"),
            _ => (string.Empty, string.Empty)
        };

        NoticeHeadline.Text = headline;
        NoticeDetail.Text = detail;

        if (notice is DeskNotice.QuitStalled qs)
        {
            _stalledQuit = (qs.App, qs.Pid);
            NoticeActionsRow.Visibility = Visibility.Visible;
            NoticeCloseBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoticeActionsRow.Visibility = Visibility.Collapsed;
            NoticeCloseBtn.Visibility = Visibility.Visible;
        }
    }

    private void RefreshRunningAndAll()
    {
        var st = _controller.DeskState;
        var running = _controller.RunningApps;

        string runningSig = BuildRunningSignature(running, st, _controller.CatalogLoaded, _confirmQuitPid, _confirmCloseWindow);
        if (!string.Equals(runningSig, _lastRunningSig, StringComparison.Ordinal))
        {
            _lastRunningSig = runningSig;
            if (st.Placed is { } placed)
            {
                var placedApp = BuildPlacedApp(placed.Window, running);
                CurrentAppCardHost.Content = ToVm(placedApp, st);
                CurrentAppSection.Visibility = Visibility.Visible;
                var others = running.Where(r => r.Pid != placed.Window.Key.Pid).ToList();
                OtherAppsHeader.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RunningList.ItemsSource = others.Select(r => ToVm(r, st)).ToList();
                RunningEmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                CurrentAppSection.Visibility = Visibility.Collapsed;
                CurrentAppCardHost.Content = null;
                OtherAppsHeader.Visibility = Visibility.Collapsed;
                RunningList.ItemsSource = running.Select(r => ToVm(r, st)).ToList();
                if (!_controller.CatalogLoaded)
                {
                    RunningEmptyText.Text = "正在读取";
                    RunningEmptyState.Visibility = Visibility.Visible;
                }
                else if (running.Count == 0)
                {
                    RunningEmptyText.Text = "没有正在运行的 App";
                    RunningEmptyState.Visibility = Visibility.Visible;
                }
                else
                {
                    RunningEmptyState.Visibility = Visibility.Collapsed;
                }
            }
        }

        // All tab (4-column icon grid)
        var installed = _controller.InstalledApps;
        string allSig = BuildAllSignature(installed, running, st.Quitting);
        if (!string.Equals(allSig, _lastAllSig, StringComparison.Ordinal))
        {
            _lastAllSig = allSig;
            if (installed.Count == 0)
            {
                AllLoadingText.Visibility = Visibility.Visible;
                AllAppsGrid.ItemsSource = null;
            }
            else
            {
                AllLoadingText.Visibility = Visibility.Collapsed;
                var runningById = running
                    .GroupBy(r => r.App.AppId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Pid, StringComparer.OrdinalIgnoreCase);

                AllAppsGrid.ItemsSource = installed.Select(a =>
                {
                    bool hasPid = runningById.TryGetValue(a.AppId, out int pid);
                    bool quitting = hasPid && st.Quitting.Contains(pid);
                    return new AllAppVm(a, _controller.Icons.GetIcon(a, hasPid ? pid : null), !quitting, hasPid ? 1.0 : 0.0);
                }).ToList();
            }
        }
    }

    private static string BuildRunningSignature(
        IReadOnlyList<RunningApp> running,
        PhoneDeskState st,
        bool loaded,
        int? confirmQuitPid,
        WindowKey? confirmClose)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(loaded ? '1' : '0').Append('|');
        sb.Append(st.Placed?.Window.Key.ToString() ?? "-").Append('|').Append(st.Placed?.Zoom ?? 0).Append('|');
        sb.Append(confirmQuitPid?.ToString() ?? "-").Append('|').Append(confirmClose?.ToString() ?? "-").Append('|');
        foreach (int q in st.Quitting.OrderBy(x => x)) sb.Append('Q').Append(q).Append(',');
        sb.Append('|');
        foreach (var c in st.Closing.OrderBy(x => x.Handle)) sb.Append('C').Append(c).Append(',');
        sb.Append('|');
        foreach (var app in running)
        {
            sb.Append(app.Pid).Append(':').Append(app.App.AppId).Append('[');
            foreach (var w in app.Windows)
                sb.Append(w.Key).Append(':').Append(w.Title).Append(':').Append(w.Minimized ? '1' : '0').Append(';');
            sb.Append("];");
        }
        return sb.ToString();
    }

    private static string BuildAllSignature(
        IReadOnlyList<AppInfo> installed,
        IReadOnlyList<RunningApp> running,
        IReadOnlySet<int> quitting)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(installed.Count).Append('|');
        if (installed.Count > 0)
            sb.Append(installed[0].AppId).Append('|').Append(installed[^1].AppId).Append('|');
        foreach (var r in running.OrderBy(x => x.App.AppId, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.App.AppId).Append(':').Append(quitting.Contains(r.Pid) ? 'Q' : 'R').Append(';');
        return sb.ToString();
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static RunningApp BuildPlacedApp(WindowInfo placedWindow, IReadOnlyList<RunningApp> running)
    {
        var found = running.FirstOrDefault(r => r.Pid == placedWindow.Key.Pid)
                    ?? running.FirstOrDefault(r => !string.IsNullOrEmpty(placedWindow.AppId)
                                                   && string.Equals(r.App.AppId, placedWindow.AppId, StringComparison.OrdinalIgnoreCase));
        var windows = found?.Windows ?? Array.Empty<WindowInfo>();
        var latest = windows.FirstOrDefault(w => w.Key == placedWindow.Key) ?? placedWindow;
        var reordered = new List<WindowInfo> { latest };
        reordered.AddRange(windows.Where(w => w.Key != placedWindow.Key));
        var app = found?.App ?? new AppInfo(placedWindow.AppId ?? placedWindow.AppName, placedWindow.AppName, null);
        return new RunningApp(app, placedWindow.Key.Pid, reordered);
    }

    private RunningAppVm ToVm(RunningApp ra, PhoneDeskState st) =>
        new(
            ra,
            st,
            _controller.Icons.GetIcon(ra.App, ra.Pid),
            _confirmQuitPid == ra.Pid,
            k => _confirmCloseWindow == k,
            SyncState);

    private void RefreshSearchPage()
    {
        string q = SearchInputBox.Text.Trim();
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        var running = _controller.RunningApps;
        var installed = _controller.InstalledApps;
        var st = _controller.DeskState;

        SearchAppRowVm MakeAppRow(AppInfo a)
        {
            var r = running.FirstOrDefault(x => string.Equals(x.App.AppId, a.AppId, StringComparison.OrdinalIgnoreCase));
            bool quitting = r is not null && st.Quitting.Contains(r.Pid);
            string sub = quitting
                ? "正在退出"
                : (r is null ? "未打开" : (r.Windows.Count == 0 ? "没有窗口" : $"{r.Windows.Count} 个窗口"));
            return new SearchAppRowVm(a, _controller.Icons.GetIcon(a, r?.Pid), sub, !quitting);
        }

        if (string.IsNullOrEmpty(q))
        {
            LetterGroupsList.Visibility = Visibility.Visible;
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            LetterGroupsList.ItemsSource = AppList.GroupedByLetter(installed)
                .Select(g => new SearchLetterGroupVm(g.Letter, g.Apps.Select(MakeAppRow).ToList()))
                .ToList();
        }
        else
        {
            LetterGroupsList.Visibility = Visibility.Collapsed;
            SearchResultsPanel.Visibility = Visibility.Visible;
            var res = AppList.Search(q, installed, running);
            SearchNoResultText.Visibility = res.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            SearchAppsSection.Visibility = res.Apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchWindowsSection.Visibility = res.Windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchAppsResults.ItemsSource = res.Apps.Select(MakeAppRow).ToList();
            SearchWindowsResults.ItemsSource = res.Windows
                .Select(w => new SearchWindowRowVm(w, _controller.Icons.GetIcon(w)))
                .ToList();
        }
    }

    private void RefreshModelPage()
    {
        string q = ModelSearchBox.Text.Trim();
        var current = _controller.Config.Phone;
        CurrentModelTitle.Text = current.Model ?? "自定义";
        CurrentModelSize.Text = $"{current.Screen.Width} × {current.Screen.Height}";

        if (current.Model is null && string.IsNullOrEmpty(CustomWidthBox.Text) && string.IsNullOrEmpty(CustomHeightBox.Text))
        {
            CustomWidthBox.Text = current.Screen.Width.ToString();
            CustomHeightBox.Text = current.Screen.Height.ToString();
        }

        var brands = _controller.Catalog.Search(q);
        CurrentModelSection.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
        CustomModelSection.Visibility = (string.IsNullOrEmpty(q) || brands.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

        ModelBrandsList.ItemsSource = brands
            .Select(b => new ModelBrandVm(
                b.Name,
                b.Models.Select(m => new ModelItemVm(m, m.Id == current.Model)).ToList()))
            .ToList();
    }

    private void UpdateTabStyles()
    {
        RunningTabBd.Background = !_showAllTab ? SegmentActiveBrush : Brushes.Transparent;
        AllTabBd.Background = _showAllTab ? SegmentActiveBrush : Brushes.Transparent;
        RunningPagePanel.Visibility = !_showAllTab ? Visibility.Visible : Visibility.Collapsed;
        AllAppsPanel.Visibility = _showAllTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectRunningTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = false;
        ClearConfirmations();
        UpdateTabStyles();
    }

    private void OnSelectAllTab(object sender, RoutedEventArgs e)
    {
        _showAllTab = true;
        ClearConfirmations();
        UpdateTabStyles();
    }

    private void OnOpenSearchPage(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _subPage = SubPage.Search;
        SearchInputBox.Text = string.Empty;
        RefreshSearchPage();
        UpdatePageVisibility();
        _controller.SyncSubPageActivation(focus: true);
        Dispatcher.BeginInvoke(new Action(() => SearchInputBox.Focus()));
    }

    private void OnOpenModelsPage(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _subPage = SubPage.PhoneModel;
        ModelSearchBox.Text = string.Empty;
        RefreshModelPage();
        UpdatePageVisibility();
        _controller.SyncSubPageActivation(focus: true);
    }

    private void OnCloseSubPage(object sender, RoutedEventArgs e)
    {
        CloseSubPage();
        _controller.SyncSubPageActivation(focus: false);
    }

    private void OnSearchInputChanged(object sender, TextChangedEventArgs e) => RefreshSearchPage();
    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchInputBox.Text = string.Empty;
    private void OnModelSearchChanged(object sender, TextChangedEventArgs e) => RefreshModelPage();

    private void OnSelectPhoneModel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: PhoneModel m })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(focus: false);
            _controller.SelectPhone(PhoneChoice.FromModel(m));
        }
    }

    private void OnApplyCustomModel(object sender, RoutedEventArgs e)
    {
        int? w = int.TryParse(CustomWidthBox.Text.Trim(), out int wi) ? wi : null;
        int? h = int.TryParse(CustomHeightBox.Text.Trim(), out int hi) ? hi : null;
        var custom = PhoneScreenSpecData.Custom(w, h);
        if (custom is null) return;
        CloseSubPage();
        _controller.SyncSubPageActivation(focus: false);
        _controller.SelectPhone(new PhoneChoice(null, custom));
    }

    private void OnBackToPlaced(object sender, RoutedEventArgs e) => _controller.ReturnToPlaced();
    private void OnPutBack(object sender, RoutedEventArgs e) => _controller.PutBack();
    private void OnZoomIn(object sender, RoutedEventArgs e) => _controller.ZoomBy(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => _controller.ZoomBy(-1);
    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
            NoticeBanner.Visibility = Visibility.Collapsed;
        else
            _controller.DismissNotice();
    }

    private void OnForceQuitFromNotice(object sender, RoutedEventArgs e)
    {
        if (_stalledQuit is { } sq)
            _controller.ForceQuitApp(sq.Pid, sq.App);
    }

    private void OnAskDismissScreen(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            Close();
            return;
        }
        ClearConfirmations();
        _confirmDismiss = true;
        HomeScroll.ScrollToTop();
        SyncState();
    }

    private void OnConfirmDismissScreen(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        _controller.DismissScreen();
    }

    private void OnCancelConfirm(object sender, RoutedEventArgs e)
    {
        ClearConfirmations();
        SyncState();
    }

    private void OnAppHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.OpenApp(ra.App);
        }
    }

    private void OnPullWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            ClearConfirmations();
            _controller.PullWindow(w);
        }
    }

    private void OnNewWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.NewWindow(ra);
        }
    }

    private void OnAskQuitApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: RunningAppVm vm })
        {
            ClearConfirmations();
            _confirmQuitPid = vm.App.Pid;
            RefreshRunningAndAll();
        }
    }

    private void OnConfirmQuitApp(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RunningApp ra })
        {
            ClearConfirmations();
            _controller.QuitApp(ra);
        }
    }

    private void OnAskCloseWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: WindowVm vm })
        {
            ClearConfirmations();
            _confirmCloseWindow = vm.Window.Key;
            RefreshRunningAndAll();
        }
    }

    private void OnConfirmCloseWindow(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            ClearConfirmations();
            _controller.CloseWindow(w);
        }
    }

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode) { ShowPreviewReadOnlyNotice(); return; }
        if (sender is System.Windows.Controls.Button { Tag: AppInfo app })
        {
            ClearConfirmations();
            _controller.OpenApp(app);
        }
    }

    private void OnSearchOpenApp(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            CloseSubPage();
            ShowPreviewReadOnlyNotice();
            return;
        }
        if (sender is System.Windows.Controls.Button { Tag: AppInfo app })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(focus: false);
            _controller.OpenApp(app);
        }
    }

    private void OnSearchPullWindow(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            CloseSubPage();
            ShowPreviewReadOnlyNotice();
            return;
        }
        if (sender is System.Windows.Controls.Button { Tag: WindowInfo w })
        {
            CloseSubPage();
            _controller.SyncSubPageActivation(focus: false);
            _controller.PullWindow(w);
        }
    }
}
