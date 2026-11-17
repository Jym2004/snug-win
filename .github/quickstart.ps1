<#
.SYNOPSIS
    为发布包生成「快速开始.txt」。CI 与本地打包共用，避免文案两处维护。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Target,
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'

if ($FrameworkDependent) {
    $runtimeLine = @'
本包为「框架依赖版」，体积小，但需要先安装 .NET 10 Desktop Runtime（Windows x64）：
   https://dotnet.microsoft.com/download/dotnet/10.0/runtime/desktop
安装完成后解压本包即可。
'@
}
else {
    $runtimeLine = '本包为「自包含版」，已内置 .NET 10 运行时，解压即用。'
}

$text = @"
SnugWin v$Version —— Windows 上的手机竖屏伴侣（UU远程 / GameViewer）
=====================================================================

$runtimeLine

一、安装虚拟显示器驱动（首次必须）
----------------------------------
1. 打开 driver 目录，右键 install-driver.cmd → 以管理员身份运行
   脚本会依次执行：
     · 校验安装包 SHA256（zip / msi / 证书三个文件）
     · 校验驱动签名证书指纹 5DFA7E68997ED52D4B10420407860D1C29B6F427
     · 把证书导入「受信任的根证书颁发机构」与「受信任的发布者」
     · 静默安装 virtual-display-driver 0.3.1
     · 在 %ProgramData%\SnugWin\driver-ownership.json 记录「这个驱动是 SnugWin 装的」
2. 查看状态：powershell -File driver\Install-SnugDriver.ps1 -Action Status
3. 卸载驱动：powershell -File driver\Install-SnugDriver.ps1 -Action Uninstall
   （只卸载 SnugWin 自己装的驱动；如果驱动在你装 SnugWin 之前就存在，会跳过不动）

二、启动 SnugWin
----------------------------------
双击 SnugWin.App.exe（程序清单要求管理员权限，会弹出 UAC，请允许）。
程序常驻托盘，不占用任务栏。

三、使用
----------------------------------
1. 手机通过 网易 UU远程（GameViewer）连入本机；
2. SnugWin 检测到连接后自动创建 1 块竖屏虚拟显示器，并弹出 iPhone 风格的
   「App 资源库」窗口，可搜索（支持拼音与多音字）、切换机型、新建窗口；
3. 断开手机连接后自动归位窗口并撤销虚拟屏；
4. 底部小横条：点击或单指上滑回到 App 资源库；
5. 划选文本松手会弹出悬浮操作条：
     复制 —— 普通窗口 Ctrl+C；终端自动用 Ctrl+Shift+C
     粘贴 —— 普通窗口 Ctrl+V；Windows Terminal 自动用 Ctrl+Shift+V
     删除 —— 发送 Backspace
   设置里可独立开关「划选后显示悬浮操作条」「单指滚动」「长按右键」等手势。

四、数据位置
----------------------------------
%LocalAppData%\SnugWin\phone-screen.json     窗口位置与缩放级别（崩溃/断电后恢复）
%LocalAppData%\SnugWin\left-behind.json      遗留窗口记录
%ProgramData%\SnugWin\driver-ownership.json  驱动归属标记

五、常见问题
----------------------------------
* 启动后不创建虚拟屏：确认 UU远程 已连入，且 vdd-user-session-service 正在运行；
* 提示找不到管道 \\.\pipe\virtualdisplaydriver：驱动未安装，或需要重启 Windows；
* 驱动装了但没生效：在设备管理器里重启「Cherry Virtual Display」，或重跑 install-driver.cmd；
* 杀毒软件拦截：本程序为 MIT 开源的本地工具，可自行 dotnet build 复现构建。

六、许可与致谢
----------------------------------
MIT License。上游 macOS 原版：https://github.com/zephyr4123/snug (v2.5.0)
虚拟显示器驱动：https://github.com/MolotovCherry/virtual-display-rs
"@

$out = Join-Path $Target '快速开始.txt'
[System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "已生成 $out"
