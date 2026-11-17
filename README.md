# SnugWin

**让 Windows 在手机上拥有原生级操作体验的 UU远程 伴侣（C# / .NET 8 / WPF + virtual-display-rs）**

承接并 1:1 遵循 [zephyr4123/snug](https://github.com/zephyr4123/snug)（MIT License，`v2.5.0`）的产品体验、状态机与交互逻辑，为 Windows 10/11 + 网易 UU远程（GameViewer）实现。

## 下载

直接取第一个正式版：[**SnugWin v1.0.0**](https://github.com/nicocart/snug-win/releases/tag/v1.0.0)

| 资产 | 说明 |
| --- | --- |
| `SnugWin-v1.0.0-win-x64-selfcontained.zip` | 推荐，内置 .NET 8 运行时，解压即用 |
| `SnugWin-v1.0.0-win-x64-frameworkdependent.zip` | 体积小，需先装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime/desktop) |
| `virtual-display-driver-0.3.1-x64.zip` | 虚拟显示器驱动 + 一键安装脚本，首次使用必须装 |

安装：① 管理员运行驱动包内 `install-driver.cmd` → ② 双击 `SnugWin.App.exe`（UAC 允许）→ ③ 手机用 UU远程 连入本机。

## 主要功能

* **自动手机竖屏**：手机通过 UU远程 连入时自动创建 1 块竖屏虚拟显示器（按所选机型或自定义比例），断开后自动归位窗口并撤屏。
* **手机端 App 资源库**：1:1 还原 iPhone 风格资源库（运行中分「当前 App / 其他 App」、全部 4 列图标网格、拼音与首字母实时搜索、屏内手机型号切换、新建窗口/关窗/退出两步确认）。
* **底部小横条**：支持点击或单指上滑回到 App 资源库；可选开启左侧相机键，支持手机屏窗口一键截图、剪贴板复制、缩略图预览与 8 把手裁剪。
* **全局手势翻译**：独立高优先级 `WH_MOUSE_LL` 钩子 + 异步重放队列，支持单指滚动与 60fps 惯性、长按 0.5 秒选字、长按松开右键、点击置顶窗口关闭按钮时先归位再关闭。
* **断电/崩溃保护**：窗口原位置与缩放级别持久化在 `%LocalAppData%\SnugWin\phone-screen.json`，重启后自动恢复。

## 架构分层

```text
snug-win/
├── Assets/phone-models.json           # 手机机型屏幕比例库
├── src/
│   ├── SnugWin.Core/                  # 纯逻辑层（无 Win32/WPF 依赖）
│   │   ├── Config/AppConfig.cs        # 容错配置读写
│   │   ├── Persistence/               # 还原记录 phone-screen.json / left-behind.json
│   │   ├── PhoneScreen/               # PhoneDesk、ScreenFit、AppList、Screenshots、CropGeometry、ZoomBook、PinyinSearch
│   │   ├── Session/                   # ControllerTable、ConnectionTracker、SessionEpisode、RemoteSession
│   │   └── Touch/                     # TouchTranslator 手势状态机与惯性滚动
│   ├── SnugWin.Windows/               # Windows 系统接口层
│   │   ├── Display/                   # virtual-display-rs 命名管道协议 + CCD 高 DPI 控制
│   │   ├── Windows/                   # DWM 边框补偿搬窗、UIAutomation、App 扫描、截图裁剪
│   │   ├── Input/                     # 独立线程 WH_MOUSE_LL + 异步重放队列
│   │   └── Sensing/                   # GameViewer (UU远程) 覆盖窗 + UIA + peer TCP / 推流日志探测
│   └── SnugWin.App/                   # WPF PerMonitorV2 托盘常驻、手机屏资源库、小横条、裁剪页、设置窗
└── tests/
    └── SnugWin.Core.Tests/            # xUnit 单元测试
```

## 前置条件

1. **.NET 8.0 Desktop Runtime / SDK**（Windows x64）
2. **虚拟显示器驱动：[MolotovCherry/virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)**（Windows 10 2004+ x64，建议 `v0.3.1+`）
   * 安装其驱动程序及证书后，确保系统存在命名管道 `\\.\pipe\virtualdisplaydriver`。
3. **管理员权限**：程序清单配置为 `requireAdministrator` + `PerMonitorV2`，用于跨 UIPI 操作高权限桌面窗口、设置显示模式及驱动恢复。

## 构建、测试与发布

```powershell
dotnet build SnugWin.sln
dotnet test SnugWin.sln -c Release
dotnet publish src\SnugWin.App\SnugWin.App.csproj -c Release -r win-x64 --self-contained false -o publish-out
```

## 开源致谢

* 上游原版项目（macOS）：[zephyr4123/snug](https://github.com/zephyr4123/snug)（MIT License）
* Windows 虚拟显示器驱动：[MolotovCherry/virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)
