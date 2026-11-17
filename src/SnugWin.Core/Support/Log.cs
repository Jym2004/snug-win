using System;

namespace SnugWin.Core.Support;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public interface ILogSink
{
    void Write(LogLevel level, string message, DateTimeOffset timestamp);
}

public static class Log
{
    private static readonly object Gate = new();
    private static ILogSink? _sink;

    public static event Action<LogLevel, string, DateTimeOffset>? Logged;

    public static void SetSink(ILogSink? sink)
    {
        lock (Gate)
        {
            _sink = sink;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        var now = DateTimeOffset.Now;
        ILogSink? sink;
        lock (Gate)
        {
            sink = _sink;
        }
        sink?.Write(level, message, now);
        Logged?.Invoke(level, message, now);
    }
}
