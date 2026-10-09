using Apex.Render.Device;
using Avalonia.Threading;

namespace Apex.Render.Presentation;

/// <summary>
/// One <see cref="GfxDevice"/> per adapter, shared by every <see cref="D3DViewport"/> with
/// <see cref="D3DViewport.ShareDevice"/> set, so device-bound caches (shaders, states, lighting, images) are built
/// once per app rather than once per viewport. Reference counted through <see cref="GfxDeviceLease"/>: a device whose
/// last lease is released stays alive for <see cref="IdleTimeout"/> (tab switches and pane re-templating release and
/// re-acquire immediately) and is then disposed, together with everything registered through
/// <see cref="GfxDevice.Own{T}"/>. A lost device is never handed out again: the next <see cref="Acquire"/> creates a
/// replacement, and the lost one is disposed as soon as its last holder lets go. UI thread only.
/// </summary>
public static class SharedGfxDevices
{
    private sealed class Entry
    {
        public required GfxDevice Device;
        public int Refs;
        public IDisposable? IdleTimer;
    }

    private static readonly Dictionary<string, Entry> s_current = new();
    private static readonly Dictionary<GfxDevice, Entry> s_all = new();

    /// <summary>How long an unused device is kept before it is disposed. Re-opening a preview within it reuses the
    /// shaders, lighting and idle models (whose image data has been released, so a new device re-reads it from disk);
    /// meanwhile the device holds that VRAM — the window-sized targets go with each viewport.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Number of live devices (diagnostics).</summary>
    public static int LiveDeviceCount => s_all.Count;

    /// <summary>A lease on the shared device for the adapter with <paramref name="luid"/> (8 bytes, little endian;
    /// empty = default adapter), creating it when there is none or the current one is lost.</summary>
    public static GfxDeviceLease Acquire(ReadOnlySpan<byte> luid, GfxDeviceOptions options = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        var key = Convert.ToHexString(luid);
        if (!s_current.TryGetValue(key, out var e) || e.Device.IsLost)
        {
            var device = GfxDevice.Create(luid, options);
            e = new Entry { Device = device };
            s_current[key] = e;
            s_all[device] = e;
        }
        e.IdleTimer?.Dispose();
        e.IdleTimer = null;
        e.Refs++;
        return new GfxDeviceLease(e.Device);
    }

    internal static void Release(GfxDevice device)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!s_all.TryGetValue(device, out var e) || --e.Refs > 0)
            return;
        if (device.IsLost || !ReferenceEquals(CurrentEntry(device), e))
        {
            DisposeEntry(e);
            return;
        }
        e.IdleTimer = DispatcherTimer.RunOnce(() =>
        {
            e.IdleTimer = null;
            if (e.Refs == 0)
                DisposeEntry(e);
        }, IdleTimeout);
    }

    /// <summary>Disposes every device nobody holds (app exit).</summary>
    public static void DisposeIdle()
    {
        foreach (var e in s_all.Values.Where(e => e.Refs == 0).ToList())
            DisposeEntry(e);
    }

    private static Entry? CurrentEntry(GfxDevice device)
        => s_current.Values.FirstOrDefault(x => ReferenceEquals(x.Device, device));

    private static void DisposeEntry(Entry e)
    {
        e.IdleTimer?.Dispose();
        e.IdleTimer = null;
        s_all.Remove(e.Device);
        foreach (var (k, v) in s_current.ToList())
            if (ReferenceEquals(v, e))
                s_current.Remove(k);
        e.Device.Dispose();
    }
}

/// <summary>A reference on a shared <see cref="GfxDevice"/> (<see cref="SharedGfxDevices.Acquire"/>). Dispose once.</summary>
public sealed class GfxDeviceLease : IDisposable
{
    private GfxDevice? _device;

    internal GfxDeviceLease(GfxDevice device) => _device = device;

    public GfxDevice Device => _device ?? throw new ObjectDisposedException(nameof(GfxDeviceLease));

    public void Dispose()
    {
        var d = Interlocked.Exchange(ref _device, null);
        if (d != null)
            SharedGfxDevices.Release(d);
    }
}
