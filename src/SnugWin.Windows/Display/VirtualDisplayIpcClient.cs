using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Display;

public sealed record VddMode(
    [property: JsonPropertyName("width")] uint Width,
    [property: JsonPropertyName("height")] uint Height,
    [property: JsonPropertyName("refresh_rates")] IReadOnlyList<uint> RefreshRates);

public sealed record VddMonitor(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("modes")] IReadOnlyList<VddMode> Modes);

internal sealed record VddLegacyMode(
    [property: JsonPropertyName("width")] uint Width,
    [property: JsonPropertyName("height")] uint Height,
    [property: JsonPropertyName("refresh_rate")] uint RefreshRate);

internal sealed record VddLegacyMonitor(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("modes")] IReadOnlyList<VddLegacyMode> Modes);

internal interface IVirtualDisplayIpc : IAsyncDisposable
{
    bool IsLegacyProtocol { get; }
    Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default);
    Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> addMonitors, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default);
    Task RemoveAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Named-pipe client supporting both:
/// - virtual-display-rs master (InOut byte pipe, EOT 0x04 framing, Notify/Remove/RemoveAll)
/// - virtual-display-rs v0.3.1 (Out write-only pipe, raw JSON message, Add/Remove/RemoveAll)
/// </summary>
public sealed class VirtualDisplayIpcClient : IVirtualDisplayIpc, IAsyncDisposable
{
    public const string DefaultPipeName = "virtualdisplaydriver";
    public const byte Eot = 0x04;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private NamedPipeClientStream? _v2Pipe;
    private NamedPipeClientStream? _legacyPipe;
    private bool? _isLegacyV031;

    public VirtualDisplayIpcClient(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsLegacyProtocol => _isLegacyV031 == true;

    public static bool IsDriverPipeAvailable(string pipeName = DefaultPipeName)
    {
        try
        {
            return File.Exists($@"\\.\pipe\{pipeName}");
        }
        catch
        {
            return false;
        }
    }

    public async Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool legacy = await EnsureProtocolAsync(ct).ConfigureAwait(false);
            if (legacy)
            {
                await SendLegacyOneShotAsync(SerializeLegacyAdd(monitors), ct).ConfigureAwait(false);
            }
            else
            {
                await WriteV2FrameAsync(_v2Pipe!, SerializeNotify(monitors), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> addMonitors, CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool legacy = await EnsureProtocolAsync(ct).ConfigureAwait(false);
            if (legacy)
            {
                await SendLegacyOneShotAsync(SerializeRemove(removeIds), ct).ConfigureAwait(false);
                await Task.Delay(120, ct).ConfigureAwait(false);
                await SendLegacyOneShotAsync(SerializeLegacyAdd(addMonitors), ct).ConfigureAwait(false);
            }
            else
            {
                await WriteV2FrameAsync(_v2Pipe!, SerializeNotify(addMonitors), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool legacy = await EnsureProtocolAsync(ct).ConfigureAwait(false);
            string json = SerializeRemove(ids);
            if (legacy)
                await SendLegacyOneShotAsync(json, ct).ConfigureAwait(false);
            else
                await WriteV2FrameAsync(_v2Pipe!, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task RemoveAllAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool legacy = await EnsureProtocolAsync(ct).ConfigureAwait(false);
            string json = SerializeRemoveAll();
            if (legacy)
                await SendLegacyOneShotAsync(json, ct).ConfigureAwait(false);
            else
                await WriteV2FrameAsync(_v2Pipe!, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public static string SerializeNotify(IReadOnlyList<VddMonitor> monitors) =>
        JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<VddMonitor>>
        {
            ["Notify"] = monitors
        }, JsonOptions);

    public static string SerializeLegacyAdd(IReadOnlyList<VddMonitor> monitors)
    {
        var legacy = new List<VddLegacyMonitor>(monitors.Count);
        foreach (var m in monitors)
        {
            var modes = new List<VddLegacyMode>();
            foreach (var mode in m.Modes)
            {
                uint rr = mode.RefreshRates.Count > 0 ? mode.RefreshRates[0] : 60;
                modes.Add(new VddLegacyMode(mode.Width, mode.Height, rr));
            }
            legacy.Add(new VddLegacyMonitor(m.Id, modes));
        }
        return JsonSerializer.Serialize(new Dictionary<string, List<VddLegacyMonitor>>
        {
            ["Add"] = legacy
        }, JsonOptions);
    }

    public static string SerializeRemove(IReadOnlyList<uint> ids) =>
        JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<uint>>
        {
            ["Remove"] = ids
        }, JsonOptions);

    public static string SerializeRemoveAll() => "\"RemoveAll\"";

    public static string SerializeStateRequest() => "\"State\"";

    public static bool TryParseStateReply(string json, out IReadOnlyList<VddMonitor> monitors)
    {
        monitors = Array.Empty<VddMonitor>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("State", out var stateEl)
                || doc.RootElement.TryGetProperty("Changed", out stateEl))
            {
                monitors = JsonSerializer.Deserialize<List<VddMonitor>>(stateEl.GetRawText(), JsonOptions)
                           ?? new List<VddMonitor>();
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureProtocolAsync(CancellationToken ct)
    {
        if (_isLegacyV031 == true)
        {
            if (_legacyPipe is not { IsConnected: true })
            {
                _legacyPipe?.Dispose();
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
                await pipe.ConnectAsync(2500, ct).ConfigureAwait(false);
                _legacyPipe = pipe;
            }
            return true;
        }
        if (_v2Pipe is { IsConnected: true }) return false;

        _v2Pipe?.Dispose();
        _v2Pipe = null;

        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, ct).ConfigureAwait(false);
            _v2Pipe = pipe;
            _isLegacyV031 = false;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // virtual-display-rs v0.3.1 creates server with PIPE_ACCESS_INBOUND, which rejects InOut
            // and requires PipeDirection.Out (write-only). Keep this connected pipe open.
            var legacy = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            await legacy.ConnectAsync(2500, ct).ConfigureAwait(false);
            _legacyPipe = legacy;
            _isLegacyV031 = true;
            Log.Info("检测到 virtual-display-rs v0.3.x 写通道协议（PipeDirection.Out + Add）");
            return true;
        }
    }

    private async Task SendLegacyOneShotAsync(string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        try
        {
            if (_legacyPipe is not { IsConnected: true })
            {
                _legacyPipe?.Dispose();
                var p = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
                await p.ConnectAsync(2500, ct).ConfigureAwait(false);
                _legacyPipe = p;
            }
            await _legacyPipe.WriteAsync(payload, ct).ConfigureAwait(false);
            await _legacyPipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _legacyPipe?.Dispose();
            _legacyPipe = null;
            var p = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            await p.ConnectAsync(2500, ct).ConfigureAwait(false);
            _legacyPipe = p;
            await p.WriteAsync(payload, ct).ConfigureAwait(false);
            await p.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteV2FrameAsync(NamedPipeClientStream pipe, string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, frame, 0, payload.Length);
        frame[^1] = Eot;
        await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_v2Pipe is not null)
            {
                await _v2Pipe.DisposeAsync().ConfigureAwait(false);
                _v2Pipe = null;
            }
            if (_legacyPipe is not null)
            {
                await _legacyPipe.DisposeAsync().ConfigureAwait(false);
                _legacyPipe = null;
            }
        }
        finally
        {
            _ioGate.Release();
            _ioGate.Dispose();
        }
    }
}
