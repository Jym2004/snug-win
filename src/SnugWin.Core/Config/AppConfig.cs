using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Config;

public sealed record AppConfig
{
    public const int MinZoomSteps = 0;
    public const int MaxZoomSteps = 4;

    public bool PhoneScreen { get; init; } = true;
    public bool Gestures { get; init; } = true;
    public bool GestureScroll { get; init; } = true;
    public bool GestureLongPressRightClick { get; init; } = true;
    public bool CopyAfterSelection { get; init; } = true;
    public bool GesturesInjectedOnly { get; init; } = true;
    public bool Screenshot { get; init; } = false;
    public int ZoomSteps { get; init; } = 2;
    public PhoneChoice Phone { get; init; } = PhoneChoice.Standard;
    public bool Welcomed { get; init; } = false;
    public string? LastVersion { get; init; }

    public AppConfig Sanitize()
    {
        var phone = Phone.Screen.IsValid ? Phone : PhoneChoice.Standard;
        return this with
        {
            ZoomSteps = ZoomSteps.Clamp(MinZoomSteps, MaxZoomSteps),
            Phone = phone
        };
    }

    public static AppConfig FromJsonOrDefault(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return (cfg ?? new AppConfig()).Sanitize();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
