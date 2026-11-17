using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Native;
using SnugWin.Windows.Windows;

namespace SnugWin.Windows.Input;

public sealed class TouchHookService : IDisposable
{
    private const nuint ReplayMarker = 0x534E5547; // 'SNUG'
    private const double WheelScaleFactor = 2.5; // 120 / 48 DIPs -> ~1:1 touch scroll tracking

    private readonly object _gate = new();
    private readonly long _startTick = Stopwatch.GetTimestamp();

    private TouchRegion? _region;
    private TouchTranslator _translator;
    private TouchOptions _options = TouchOptions.Default;
    private PointD _heldPressPoint;
    private bool _passthroughBrowserChrome;
    private bool _requireInjectedFlag = true;
    private bool _loggedFirstFlag;
    private readonly Dictionary<int, string> _pidExeCache = new();

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private Win32.LowLevelMouseProc? _hookProc; // keep delegate alive for GC
    private System.Threading.Timer? _coastTimer;
    private int _coastingActive;
    private BlockingCollection<Action>? _replayQueue;
    private Thread? _replayThread;

    public event Action<ulong>? CloseWindowRequested;
    public event Action<PointD>? SelectionCompleted;
    public event Action? SelectionDismissed;

    public bool RequireInjectedFlag
    {
        get { lock (_gate) return _requireInjectedFlag; }
        set { lock (_gate) _requireInjectedFlag = value; }
    }

    public TouchOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) _options = value; }
    }

    public bool IsRunning => _hookHandle != IntPtr.Zero;

    public bool SetRegion(TouchRegion? region)
    {
        lock (_gate)
        {
            _region = region;
            if (region is null)
                _translator = default;
        }

        if (region is null)
        {
            Stop();
            return true;
        }
        return Start();
    }

    private bool Start()
    {
        lock (_gate)
        {
            if (_hookThread is not null) return _hookHandle != IntPtr.Zero;

            using var ready = new ManualResetEventSlim(false);
            bool startedOk = false;

            _hookThread = new Thread(() =>
            {
                _hookThreadId = Win32.GetCurrentThreadId();
                _hookProc = HookCallback;
                IntPtr mod = Win32.GetModuleHandleW(null);
                _hookHandle = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _hookProc, mod, 0);
                startedOk = _hookHandle != IntPtr.Zero;
                ready.Set();

                if (!startedOk) return;

                while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessageW(ref msg);
                }

                if (_hookHandle != IntPtr.Zero)
                {
                    Win32.UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
            })
            {
                IsBackground = true,
                Name = "SnugWin.TouchHook",
                Priority = ThreadPriority.Highest
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            ready.Wait();

            if (!startedOk)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Error($"手势翻译钩子启动失败（Win32 Error={err}）");
                _hookThread = null;
                return false;
            }

            _replayQueue = new BlockingCollection<Action>(256);
            var q = _replayQueue;
            _replayThread = new Thread(() =>
            {
                foreach (var act in q.GetConsumingEnumerable())
                {
                    try { act(); } catch { }
                }
            })
            {
                IsBackground = true,
                Name = "SnugWin.TouchReplay",
                Priority = ThreadPriority.AboveNormal
            };
            _replayThread.Start();

            _coastTimer = new System.Threading.Timer(OnCoastTick, null, Timeout.Infinite, Timeout.Infinite);
            Log.Info("手势翻译已开启（WH_MOUSE_LL 独立线程）");
            return true;
        }
    }

    private void Stop()
    {
        Thread? threadToJoin;
        Thread? replayToJoin;
        BlockingCollection<Action>? queueToDispose;
        lock (_gate)
        {
            _coastTimer?.Dispose();
            _coastTimer = null;
            Interlocked.Exchange(ref _coastingActive, 0);
            threadToJoin = _hookThread;
            _hookThread = null;
            if (_hookThreadId != 0)
            {
                Win32.PostThreadMessageW(_hookThreadId, Win32.WM_QUIT, 0, 0);
                _hookThreadId = 0;
            }
            queueToDispose = _replayQueue;
            _replayQueue = null;
            replayToJoin = _replayThread;
            _replayThread = null;
            queueToDispose?.CompleteAdding();
        }

        if (replayToJoin is not null && Thread.CurrentThread != replayToJoin)
            replayToJoin.Join(500);
        queueToDispose?.Dispose();

        if (threadToJoin is not null && Thread.CurrentThread != threadToJoin)
        {
            threadToJoin.Join(1000);
            Log.Info("手势翻译已关闭");
        }
    }

    private double _scrollRemX;
    private double _scrollRemY;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        TouchInputKind? kind = msg switch
        {
            Win32.WM_LBUTTONDOWN => TouchInputKind.Press,
            Win32.WM_MOUSEMOVE => TouchInputKind.Drag,
            Win32.WM_LBUTTONUP => TouchInputKind.Release,
            _ => null
        };

        if (kind is null)
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
        if (info.dwExtraInfo == ReplayMarker)
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        TouchRegion? region;
        TouchResponse response;
        bool coasting;
        PointD pressOrigin;
        PointD currentPoint = new(info.pt.X, info.pt.Y);
        bool dismissSelectionBubble = false;
        lock (_gate)
        {
            region = _region;
            if (region is null)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            bool injected = (info.flags & Win32.LLMHF_INJECTED) != 0;
            if (!_loggedFirstFlag && kind == TouchInputKind.Press && region.Frame.Contains(currentPoint))
            {
                _loggedFirstFlag = true;
                Log.Info($"手机屏首次按下事件 flags=0x{info.flags:X} (injected={injected})");
            }

            if (_requireInjectedFlag && !injected)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (kind == TouchInputKind.Press)
            {
                dismissSelectionBubble = region.Frame.Contains(currentPoint);
                _passthroughBrowserChrome = region.Frame.Contains(currentPoint) && IsNativeBrowserChrome(currentPoint, region.Scale);
            }
            if (_passthroughBrowserChrome)
            {
                if (kind == TouchInputKind.Release) _passthroughBrowserChrome = false;
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            double now = Stopwatch.GetElapsedTime(_startTick).TotalSeconds;
            var input = new TouchInput(kind.Value, currentPoint, now);
            response = _translator.Handle(input, region, _options);
            if (kind == TouchInputKind.Press && !response.PassOriginal)
                _heldPressPoint = currentPoint;
            pressOrigin = _heldPressPoint;
            if (kind == TouchInputKind.Release)
                _heldPressPoint = default;
            coasting = _translator.IsCoasting;
        }

        if (dismissSelectionBubble)
            SelectionDismissed?.Invoke();

        if (response.Commands.Count > 0)
        {
            var q = _replayQueue;
            var commands = response.Commands;
            var curKind = kind.Value;
            if (q is not null && !q.IsAddingCompleted)
            {
                q.TryAdd(() =>
                {
                    bool hasPress = false;
                    foreach (var cmd in commands)
                    {
                        if (hasPress && cmd is TouchCommand.ReplayCurrent)
                            Thread.Sleep(curKind == TouchInputKind.Release ? 10 : 4);
                        ExecuteCommand(cmd, curKind, pressOrigin, currentPoint, region);
                        if (cmd is TouchCommand.ReplayPress) hasPress = true;
                    }
                });
            }
        }

        UpdateCoastTimer(coasting);

        return response.PassOriginal
            ? Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam)
            : new IntPtr(1);
    }

    private void ExecuteCommand(TouchCommand cmd, TouchInputKind currentKind, PointD heldPressPoint, PointD currentPoint, TouchRegion? region)
    {
        switch (cmd)
        {
            case TouchCommand.ReplayPress:
                SendMouseClick(heldPressPoint, down: true, right: false);
                break;

            case TouchCommand.ReplayCurrent:
                if (currentKind == TouchInputKind.Release)
                    SendMouseClick(currentPoint, down: false, right: false);
                else if (currentKind == TouchInputKind.Drag)
                    SendMouseMove(currentPoint);
                break;

            case TouchCommand.RightClick rc:
                SendMouseClick(rc.At, down: true, right: true);
                Thread.Sleep(10);
                SendMouseClick(rc.At, down: false, right: true);
                break;

            case TouchCommand.Scroll sc:
                SendScroll(sc.Step);
                break;

            case TouchCommand.CloseWindow cw:
                HandleCloseTap(cw.At, region?.CloseButton);
                break;

            case TouchCommand.SelectionCompleted sel:
                SelectionCompleted?.Invoke(sel.At);
                break;
        }
    }

    private bool IsNativeBrowserChrome(PointD point, double scale)
    {
        IntPtr hit = Win32.WindowFromPoint(new Win32.POINT { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        if (hit == IntPtr.Zero) return false;
        IntPtr root = Win32.GetAncestor(hit, Win32.GA_ROOT);
        if (root == IntPtr.Zero || !Win32.GetWindowRect(root, out var rect)) return false;

        Win32.GetWindowThreadProcessId(root, out int pid);
        if (pid <= 0 || pid == Environment.ProcessId) return false;

        if (!_pidExeCache.TryGetValue(pid, out string? exe))
        {
            string? path = Win32.GetProcessImagePath(pid);
            exe = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
            if (_pidExeCache.Count > 64) _pidExeCache.Clear();
            _pidExeCache[pid] = exe;
        }

        bool isBrowser = exe.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("opera.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);
        if (!isBrowser) return false;

        double chromeHeight = 120.0 * Math.Max(1.0, scale);
        return point.Y >= rect.Top && point.Y <= rect.Top + chromeHeight;
    }

    private void HandleCloseTap(PointD at, CloseButton? button)
    {
        if (button is not null)
        {
            var zOrder = WindowService.GetOnScreenWindowZOrder();
            if (button.IsFrontmost(at, zOrder))
            {
                CloseWindowRequested?.Invoke(button.WindowHandle);
                return;
            }
        }
        SendMouseClick(at, down: true, right: false);
        SendMouseClick(at, down: false, right: false);
    }

    private void UpdateCoastTimer(bool coasting)
    {
        if (coasting)
        {
            if (Interlocked.Exchange(ref _coastingActive, 1) == 0)
            {
                int intervalMs = (int)Math.Round(1000.0 / Momentum.FrameRate);
                _coastTimer?.Change(intervalMs, intervalMs);
            }
        }
        else if (Interlocked.Exchange(ref _coastingActive, 0) == 1)
        {
            _coastTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void OnCoastTick(object? _)
    {
        ScrollStep? step;
        bool stillCoasting;
        lock (_gate)
        {
            step = _translator.MomentumFrame();
            stillCoasting = _translator.IsCoasting;
        }

        if (step is { } s)
            SendScroll(s);

        if (!stillCoasting)
            UpdateCoastTimer(false);
    }

    private static (int AbsX, int AbsY) ToVirtualDesktopAbsolute(PointD pt)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        int absX = (int)Math.Round((pt.X - vx) * 65535.0 / Math.Max(1, vw - 1));
        int absY = (int)Math.Round((pt.Y - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (absX, absY);
    }

    private static void SendMouseMove(PointD pt)
    {
        var (ax, ay) = ToVirtualDesktopAbsolute(pt);
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.INPUTUNION
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = ReplayMarker
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendMouseClick(PointD pt, bool down, bool right)
    {
        var (ax, ay) = ToVirtualDesktopAbsolute(pt);
        uint btnFlag = right
            ? (down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP)
            : (down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP);

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.INPUTUNION
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK | btnFlag,
                    dwExtraInfo = ReplayMarker
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private void SendScroll(ScrollStep step)
    {
        if (step.Phase == ScrollPhase.Began)
        {
            _scrollRemX = 0;
            _scrollRemY = 0;
            Win32.SetCursorPos((int)Math.Round(step.Location.X), (int)Math.Round(step.Location.Y));
        }

        _scrollRemY += step.DeltaY * WheelScaleFactor;
        _scrollRemX += -step.DeltaX * WheelScaleFactor;
        int dy = (int)Math.Truncate(_scrollRemY);
        int dx = (int)Math.Truncate(_scrollRemX);
        _scrollRemY -= dy;
        _scrollRemX -= dx;
        if (dy == 0 && dx == 0) return;

        var list = new List<Win32.INPUT>(2);

        if (dy != 0)
        {
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new Win32.INPUTUNION
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)dy),
                        dwFlags = Win32.MOUSEEVENTF_WHEEL,
                        dwExtraInfo = ReplayMarker
                    }
                }
            });
        }

        if (dx != 0)
        {
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new Win32.INPUTUNION
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)dx),
                        dwFlags = Win32.MOUSEEVENTF_HWHEEL,
                        dwExtraInfo = ReplayMarker
                    }
                }
            });
        }

        var arr = list.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose() => Stop();
}
