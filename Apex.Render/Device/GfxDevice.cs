using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Device;

/// <summary>
/// Owns the D3D11 device + immediate context the ToolsGfx renderer draws with.
/// For on-screen use the device must live on the same adapter as Avalonia's compositor
/// (<see cref="Create(ReadOnlySpan{byte}, GfxDeviceOptions)"/> with the interop DeviceLuid), otherwise
/// shared-handle import fails. Headless tools pass no LUID and get the default hardware adapter
/// (or WARP when asked / when no hardware device can be created).
/// Device loss (TDR, driver update, adapter removal) is sticky: once <see cref="IsLost"/> is set the
/// owner must dispose every resource created from this device and create a new <see cref="GfxDevice"/>.
/// </summary>
public sealed class GfxDevice : IDisposable
{
    private static readonly FeatureLevel[] s_featureLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

    private readonly List<IDisposable> _ownedCaches = new();
    private bool _disposed;
    private int _backgroundWork;
    private bool _disposeDeferred;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public FeatureLevel FeatureLevel { get; }
    public string AdapterName { get; }

    /// <summary>Adapter LUID as the 8 little-endian bytes Avalonia reports in <c>ICompositionGpuInterop.DeviceLuid</c>.</summary>
    public byte[] AdapterLuid { get; }

    public bool IsWarp { get; }
    public bool IsDebug { get; }

    /// <summary>Set once a device-removed error has been observed; never cleared.</summary>
    public bool IsLost { get; private set; }

    /// <summary>Raised (once, on the thread that noticed it) when the device is detected as removed.</summary>
    public event Action<GfxDevice, string>? DeviceLost;

    private GfxDevice(ID3D11Device device, ID3D11DeviceContext context, bool isWarp, bool isDebug)
    {
        Device = device;
        Context = context;
        FeatureLevel = device.FeatureLevel;
        IsWarp = isWarp;
        IsDebug = isDebug;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var desc = adapter.Description;
        AdapterName = desc.Description;
        AdapterLuid = LuidToBytes(desc.Luid);
    }

    /// <summary>Creates a device on the adapter whose LUID matches <paramref name="luid"/> (8 bytes, little endian).
    /// Falls back to the default adapter when no adapter matches or <paramref name="luid"/> is empty.</summary>
    public static GfxDevice Create(ReadOnlySpan<byte> luid, GfxDeviceOptions options = default)
    {
        IDXGIAdapter1? adapter = luid.Length == 8 ? FindAdapter(luid) : null;
        try
        {
            return Create(adapter, options);
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    /// <summary>Creates a device on the default hardware adapter (headless tools, offscreen rendering).</summary>
    public static GfxDevice CreateDefault(GfxDeviceOptions options = default) => Create(ReadOnlySpan<byte>.Empty, options);

    private static GfxDevice Create(IDXGIAdapter1? adapter, GfxDeviceOptions options)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        if (options.Debug)
            flags |= DeviceCreationFlags.Debug;

        if (!options.ForceWarp)
        {
            var driver = adapter == null ? DriverType.Hardware : DriverType.Unknown;
            var hr = D3D11.D3D11CreateDevice(adapter, driver, flags, s_featureLevels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
            if (hr.Failure && options.Debug)
            {
                // The debug layer is optional (Graphics Tools feature); retry without it rather than failing.
                flags &= ~DeviceCreationFlags.Debug;
                hr = D3D11.D3D11CreateDevice(adapter, driver, flags, s_featureLevels, out dev, out ctx);
            }
            if (hr.Success && dev != null && ctx != null)
                return new GfxDevice(dev, ctx, isWarp: false, isDebug: (flags & DeviceCreationFlags.Debug) != 0);
            if (!options.AllowWarpFallback)
                throw new GfxDeviceException($"D3D11CreateDevice failed: {hr}");
        }

        D3D11.D3D11CreateDevice(null, DriverType.Warp, flags & ~DeviceCreationFlags.Debug, s_featureLevels,
            out ID3D11Device? warp, out ID3D11DeviceContext? warpCtx).CheckError();
        return new GfxDevice(warp!, warpCtx!, isWarp: true, isDebug: false);
    }

    private static IDXGIAdapter1? FindAdapter(ReadOnlySpan<byte> luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (LuidToBytes(adapter.Description1.Luid).AsSpan().SequenceEqual(luid))
                return adapter;
            adapter.Dispose();
        }
        return null;
    }

    public static byte[] LuidToBytes(Vortice.Luid luid)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), luid.LowPart);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), luid.HighPart);
        return bytes;
    }

    /// <summary>Removal reason (S_OK while healthy).</summary>
    public SharpGen.Runtime.Result DeviceRemovedReason => Device.DeviceRemovedReason;

    /// <summary>Polls the device; returns true (and raises <see cref="DeviceLost"/> once) when it has been removed.</summary>
    public bool CheckDeviceRemoved()
    {
        if (IsLost)
            return true;
        var reason = Device.DeviceRemovedReason;
        if (reason.Success)
            return false;
        MarkLost(reason.ToString());
        return true;
    }

    /// <summary>Classifies an exception thrown by a D3D call; marks the device lost when it is a removal error.</summary>
    public bool IsDeviceLossException(Exception ex)
    {
        if (ex is SharpGen.Runtime.SharpGenException sg)
        {
            var code = sg.ResultCode.Code;
            if (code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || code == Vortice.DXGI.ResultCode.DeviceReset.Code
                || code == Vortice.DXGI.ResultCode.DeviceHung.Code || code == Vortice.DXGI.ResultCode.DriverInternalError.Code)
            {
                MarkLost(sg.ResultCode.ToString());
                return true;
            }
        }
        return CheckDeviceRemoved();
    }

    private void MarkLost(string reason)
    {
        if (IsLost)
            return;
        IsLost = true;
        DeviceLost?.Invoke(this, reason);
    }

    /// <summary>Registers a device-bound cache (state objects, shaders, targets) to be disposed with the device.</summary>
    public T Own<T>(T cache) where T : IDisposable
    {
        _ownedCaches.Add(cache);
        return cache;
    }

    /// <summary>
    /// Marks the start of work that creates objects on <see cref="Device"/> from another thread (the device is
    /// free-threaded; <see cref="Context"/> is not and must not be used there). While any is outstanding,
    /// <see cref="Dispose"/> waits for the matching <see cref="EndBackgroundWork"/>. Both are called on the thread that
    /// owns the device.
    /// </summary>
    public void BeginBackgroundWork()
    {
        ObjectDisposedException.ThrowIf(_disposed || _disposeDeferred, this);
        _backgroundWork++;
    }

    public void EndBackgroundWork()
    {
        if (--_backgroundWork == 0 && _disposeDeferred)
        {
            _disposeDeferred = false;
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_backgroundWork > 0)
        {
            _disposeDeferred = true;
            return;
        }
        _disposed = true;
        for (int i = _ownedCaches.Count - 1; i >= 0; i--)
            _ownedCaches[i].Dispose();
        _ownedCaches.Clear();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

public readonly record struct GfxDeviceOptions(bool Debug = false, bool ForceWarp = false, bool AllowWarpFallback = true);

public sealed class GfxDeviceException : Exception
{
    public GfxDeviceException(string message) : base(message) { }
}
