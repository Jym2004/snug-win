## SnugWin v{{VERSION}}

Windows 10/11 上为 网易 UU远程 提供手机原生级操作体验的伴侣程序。承接 [zephyr4123/snug](https://github.com/zephyr4123/snug)（MIT，v2.5.0）的产品体验与状态机，C# / .NET 10 LTS / WPF + virtual-display-rs。

### 下载

| 资产 | 说明 |
| --- | --- |
| `SnugWin-v{{VERSION}}-win-x64-selfcontained.zip` | **推荐**，内置 .NET 10 运行时，解压即用 |
| `SnugWin-v{{VERSION}}-win-x64-frameworkdependent.zip` | 体积小，需先装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0/runtime/desktop) |
| `virtual-display-driver-0.3.1-x64.zip` | 虚拟显示器驱动 + 校验安装脚本，**首次使用必须装** |

### 安装三步

```text
1. 解压驱动包，右键 install-driver.cmd → 以管理员身份运行
   （脚本做 SHA256 与证书指纹校验，并记录驱动归属，不会误删你原有的同款驱动）
2. 解压 SnugWin，双击 SnugWin.App.exe（UAC 允许，程序需要管理员权限）
3. 手机用 UU远程 连入本机 → 自动生成竖屏虚拟屏 + App 资源库
```

每个包内都附带 `快速开始.txt`。

### 本版变化

<!-- CI 自动发布：请在下面补一段本版要点，或直接由 tag 前的提交说明概括 -->
- 构建与 129 项单元测试全部通过（CI 门禁）
- 详细变更见本仓库提交历史与 [README](https://github.com/nicocart/snug-win#readme)

### 前置条件

- Windows 10 2004+ / Windows 11，x64
- [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) 0.3.1（本 Release 已附带）
- 管理员权限（清单为 `requireAdministrator` + PerMonitorV2）

### 已知限制

- 仅在 Windows 11 x64 + UU远程 实测；Windows 10 2004+ 理论可用但未逐一验证
- 依赖 virtual-display-rs 的命名管道 `\\.\pipe\virtualdisplaydriver`
- 多屏场景（多台手机同时连入）尚未支持
- 换机型比例后，UU 侧需重选一次屏幕，点按位置才准确（设备与窗口不会重建）

### 致谢与许可

- 上游 macOS 原版：[zephyr4123/snug](https://github.com/zephyr4123/snug)（v2.5.0）
- 虚拟显示器驱动：[MolotovCherry/virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)（0.3.1）
- 本项目：MIT License，第三方声明见 [THIRD_PARTY_NOTICES.md](https://github.com/nicocart/snug-win/blob/main/THIRD_PARTY_NOTICES.md)

