using System;

namespace SnugWin.Core.Touch;

/// <summary>Actions available in the floating selection toolbar bubble.</summary>
public enum SelectionBubbleAction
{
    Copy,
    Paste,
    Delete
}

/// <summary>Key combination emitted for a floating selection toolbar action.</summary>
public readonly record struct SelectionKeystroke(ushort VirtualKey, bool WithCtrl, bool WithShift);

/// <summary>Resolves safe keystrokes for selection actions across standard and terminal windows.</summary>
public static class SelectionShortcutResolver
{
    public const ushort VkBack = 0x08;
    public const ushort VkC = 0x43;
    public const ushort VkV = 0x56;

    public static bool IsModernTerminal(string? windowClass, string? exeName) =>
        string.Equals(windowClass, "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.Ordinal)
        || string.Equals(exeName, "WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminal(string? windowClass, string? exeName) =>
        IsModernTerminal(windowClass, exeName)
        || string.Equals(windowClass, "ConsoleWindowClass", StringComparison.Ordinal)
        || string.Equals(exeName, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exeName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exeName, "pwsh.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(exeName, "cmd.exe", StringComparison.OrdinalIgnoreCase);

    public static SelectionKeystroke Resolve(SelectionBubbleAction action, string? windowClass, string? exeName) =>
        action switch
        {
            SelectionBubbleAction.Copy => new SelectionKeystroke(
                VkC,
                WithCtrl: true,
                WithShift: IsTerminal(windowClass, exeName)),
            SelectionBubbleAction.Paste => new SelectionKeystroke(
                VkV,
                WithCtrl: true,
                WithShift: IsModernTerminal(windowClass, exeName)),
            SelectionBubbleAction.Delete => new SelectionKeystroke(
                VkBack,
                WithCtrl: false,
                WithShift: false),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
}
