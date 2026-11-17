using System;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Persistence;

public sealed record PhoneRecord
{
    public WindowKey Window { get; init; }
    public string App { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public RectD Original { get; set; }
    public bool WasFullScreen { get; init; }
    public bool WasMinimized { get; init; }
    public int Zoom { get; set; }
    public string? AppId { get; init; }
    public string? Title { get; init; }
    public RectD? Filled { get; set; }
}
