using System.Diagnostics;
using Apex.Render.Device;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Presentation;

/// <summary>
/// Hosts D3D11 rendering inside Avalonia through the compositor's GPU interop: frames are drawn with our own
/// device into keyed-mutex shared textures (triple-buffered) and handed to the compositor with
/// <c>CompositionDrawingSurface.UpdateWithKeyedMutexAsync</c>, so the result is an ordinary composition visual
/// (Avalonia content overlays it, no airspace). The device is created on the compositor's adapter (interop
/// DeviceLuid) — a shared handle does not open across adapters.
/// Renders on demand (<see cref="RequestRender"/>, resize, renderer change) or every compositor tick when
/// <see cref="IsContinuous"/>, at most <see cref="MaxFrameRate"/> frames per second; while the size keeps changing it
/// keeps the last frame up and draws once the size settles. Input is left to the host. When the platform has no
/// usable interop (software compositor, missing keyed-mutex support) <see cref="IsInteropUnavailable"/> is set and
/// nothing is drawn.
/// Device loss (TDR, interop loss) tears everything down and rebuilds; the renderer sees
/// <see cref="ID3DViewportRenderer.OnDeviceDestroyed"/> then <see cref="ID3DViewportRenderer.OnDeviceCreated"/>.
/// </summary>
public sealed class D3DViewport : Control, ICustomHitTest
{
    public const int ImageCount = 3;
    private const int KeyedMutexTimeoutMs = 1000;

    public static readonly StyledProperty<ID3DViewportRenderer?> RendererProperty =
        AvaloniaProperty.Register<D3DViewport, ID3DViewportRenderer?>(nameof(Renderer));

    public static readonly StyledProperty<bool> IsContinuousProperty =
        AvaloniaProperty.Register<D3DViewport, bool>(nameof(IsContinuous));

    public static readonly StyledProperty<double> MaxFrameRateProperty =
        AvaloniaProperty.Register<D3DViewport, double>(nameof(MaxFrameRate), 120.0);

    public static readonly StyledProperty<bool> ShareDeviceProperty =
        AvaloniaProperty.Register<D3DViewport, bool>(nameof(ShareDevice), true);

    public static readonly StyledProperty<D3DSurfaceFormat> SurfaceFormatProperty =
        AvaloniaProperty.Register<D3DViewport, D3DSurfaceFormat>(nameof(SurfaceFormat), D3DSurfaceFormat.Bgra8);

    public static readonly DirectProperty<D3DViewport, bool> IsInteropUnavailableProperty =
        AvaloniaProperty.RegisterDirect<D3DViewport, bool>(nameof(IsInteropUnavailable), o => o.IsInteropUnavailable);

    public static readonly DirectProperty<D3DViewport, string> StatusProperty =
        AvaloniaProperty.RegisterDirect<D3DViewport, string>(nameof(Status), o => o.Status);

    public ID3DViewportRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    /// <summary>Render every compositor tick instead of only when asked.</summary>
    public bool IsContinuous
    {
        get => GetValue(IsContinuousProperty);
        set => SetValue(IsContinuousProperty, value);
    }

    /// <summary>Frame-rate cap, for continuous mode and requested frames alike (a pointer drag requests one per move).
    /// The compositor's update callback is not display-paced under ANGLE (the interop spike measured ~1,500 ticks/s on
    /// a 360 Hz display), so the viewport throttles itself: an early request waits on a timer, continuous mode for the
    /// next display frame. 0 = uncapped.</summary>
    public double MaxFrameRate
    {
        get => GetValue(MaxFrameRateProperty);
        set => SetValue(MaxFrameRateProperty, value);
    }

    /// <summary>Render with the app-wide device of the compositor's adapter (<see cref="SharedGfxDevices"/>) instead
    /// of a private one. Read when the viewport initialises.</summary>
    public bool ShareDevice
    {
        get => GetValue(ShareDeviceProperty);
        set => SetValue(ShareDeviceProperty, value);
    }

    public D3DSurfaceFormat SurfaceFormat
    {
        get => GetValue(SurfaceFormatProperty);
        set => SetValue(SurfaceFormatProperty, value);
    }

    private bool _isInteropUnavailable;
    public bool IsInteropUnavailable
    {
        get => _isInteropUnavailable;
        private set => SetAndRaise(IsInteropUnavailableProperty, ref _isInteropUnavailable, value);
    }

    private string _status = "not attached";
    public string Status
    {
        get => _status;
        private set => SetAndRaise(StatusProperty, ref _status, value);
    }

    /// <summary>The device frames are rendered with (null until initialised, replaced after device loss).</summary>
    public GfxDevice? Device => _gfx;

    /// <summary>Frames drawn and handed to the compositor.</summary>
    public long FramesRendered { get; private set; }

    /// <summary>Frames the compositor has acquired and released again (i.e. actually consumed).</summary>
    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>Raised on the UI thread after a device (re)creation, once the renderer has been attached.</summary>
    public event EventHandler? DeviceCreated;

    // The surface is composited, not drawn through the normal render path, so without this the control is
    // transparent to hit-testing. Accept input anywhere within bounds (the host handles it).
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ── State ───────────────────────────────────────────────────────────────

    private sealed class SharedImage : IDisposable
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11RenderTargetView Rtv;
        public required IDXGIKeyedMutex Mutex;
        public required ICompositionImportedGpuImage Imported;
        public required PixelSize Size;
        public required Format Format;
        /// <summary>Completes when the compositor has released the image (key back to 0).</summary>
        public Task? Pending;

        public void Dispose()
        {
            // Release the Avalonia side after any in-flight present, then our D3D objects.
            var pending = Pending ?? Task.CompletedTask;
            var imported = Imported;
            _ = pending.ContinueWith(async _ => await imported.DisposeAsync(), TaskScheduler.FromCurrentSynchronizationContext());
            Rtv.Dispose();
            Mutex.Dispose();
            Texture.Dispose();
        }
    }

    private enum FrameResult { Rendered, Busy, Skipped, Resizing }

    /// <summary>How long a new size must hold before frames are drawn at it (<see cref="ResizeSettling"/>).</summary>
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(100);

    private Compositor? _compositor;
    private CompositionContainerVisual? _container;
    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private ICompositionGpuInterop? _interop;
    private GfxDevice? _gfx;
    private GfxDeviceLease? _lease;
    private ID3DViewportRenderer? _attachedRenderer;
    private readonly List<SharedImage> _images = new();
    private int _next;
    private bool _running, _dirty, _frameScheduled, _waitingForDisplay, _recreating, _initializing;
    private int _generation;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _framesPresented;
    private TimeSpan _nextFrameDue;
    private PixelSize _settlingSize;
    private TimeSpan _settlingSince;
    private IDisposable? _settleTimer, _dueTimer;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _ = InitAsync();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _running = false;
        Teardown();
        Status = "not attached";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RendererProperty)
        {
            // Attaching creates the renderer's device resources; a failure there is handled like a failed frame
            // (device loss recreates, anything else marks this viewport unavailable) instead of escaping a setter.
            try
            {
                AttachRenderer(change.GetNewValue<ID3DViewportRenderer?>());
            }
            catch (Exception ex)
            {
                HandleFailure(ex);
                return;
            }
            RequestRender();
        }
        else if (change.Property == IsContinuousProperty || change.Property == MaxFrameRateProperty)
        {
            RequestRender();
        }
        else if (change.Property == SurfaceFormatProperty)
        {
            DisposeImages();
            RequestRender();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateVisualSize();
            RequestRender();
        }
    }

    /// <summary>Schedules one frame (coalesced; no-op while unavailable or detached).</summary>
    public void RequestRender()
    {
        _dirty = true;
        ScheduleFrame();
    }

    private async Task InitAsync()
    {
        if (_initializing)
            return;
        _initializing = true;
        try
        {
            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual == null)
            {
                Unavailable("control has no composition visual");
                return;
            }
            _compositor = selfVisual.Compositor;
            _surface ??= _compositor.CreateDrawingSurface();
            if (_visual == null)
            {
                _visual = _compositor.CreateSurfaceVisual();
                _visual.Surface = _surface;
                _container = _compositor.CreateContainerVisual();
                _container.Children.Add(_visual);
                ElementComposition.SetElementChildVisual(this, _container);
            }
            UpdateVisualSize();

            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (_interop == null)
            {
                Unavailable("compositor has no GPU interop (software rendering?)");
                return;
            }
            var handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
            if (!_interop.SupportedImageHandleTypes.Contains(handleType))
            {
                Unavailable("compositor cannot import D3D11 shared textures");
                return;
            }
            if ((_interop.GetSynchronizationCapabilities(handleType) & CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex) == 0)
            {
                Unavailable("compositor cannot synchronise D3D11 shared textures with a keyed mutex");
                return;
            }

            var luid = _interop.DeviceLuid ?? Array.Empty<byte>();
            if (ShareDevice)
            {
                _lease = SharedGfxDevices.Acquire(luid);
                _gfx = _lease.Device;
            }
            else
            {
                _gfx = GfxDevice.Create(luid);
            }
            if (luid.Length == 8 && !_gfx.AdapterLuid.AsSpan().SequenceEqual(luid))
            {
                ReleaseDevice();
                Unavailable("no D3D11 adapter matches the compositor's adapter");
                return;
            }
            _gfx.DeviceLost += OnDeviceLost;

            IsInteropUnavailable = false;
            Status = $"D3D11 on {_gfx.AdapterName}";
            _running = true;
            AttachRenderer(Renderer);
            DeviceCreated?.Invoke(this, EventArgs.Empty);
            RequestRender();
        }
        catch (Exception ex)
        {
            Teardown();
            Unavailable(ex.Message);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void Unavailable(string reason)
    {
        _running = false;
        IsInteropUnavailable = true;
        Status = "unavailable: " + reason;
    }

    private void AttachRenderer(ID3DViewportRenderer? renderer)
    {
        if (ReferenceEquals(renderer, _attachedRenderer))
            return;
        _attachedRenderer?.OnDeviceDestroyed();
        _attachedRenderer = null;
        if (renderer != null && _gfx != null)
        {
            try
            {
                renderer.OnDeviceCreated(_gfx);
            }
            catch
            {
                // Release whatever it created before failing; the caller decides whether to recreate or give up.
                try { renderer.OnDeviceDestroyed(); }
                catch (Exception) { /* already failing */ }
                throw;
            }
            _attachedRenderer = renderer;
        }
    }

    // ── Frame loop ──────────────────────────────────────────────────────────

    private void ScheduleFrame()
    {
        if (_frameScheduled || _dueTimer != null || _waitingForDisplay || !_running || _compositor == null)
            return;
        _frameScheduled = true;
        _compositor.RequestCompositionUpdate(OnFrame);
    }

    private void OnFrame()
    {
        _frameScheduled = false;
        if (!_running || (!_dirty && !IsContinuous))
            return;
        if (MaxFrameRate > 0 && _clock.Elapsed < _nextFrameDue)
        {
            // Too early — requested frames included: a pointer drag requests one per move. Wait rather than
            // re-requesting the compositor update, which is not display-paced under ANGLE and would spin the UI thread
            // (~1,500 ticks/s). A timer is exact while input keeps the dispatcher busy (a drag) but otherwise rounds up
            // to the system tick (~15.6 ms), so continuous mode waits for the display frame instead.
            WaitUntilDue();
            return;
        }

        // Cleared before drawing, so a RequestRender() made during the frame (by the renderer or a handler it raises)
        // stays pending and gets the next tick instead of being lost.
        bool wasDirty = _dirty;
        _dirty = false;
        FrameResult result;
        try
        {
            result = RenderFrame();
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
            return;
        }

        if (result != FrameResult.Rendered)
            _dirty |= wasDirty;
        switch (result)
        {
            case FrameResult.Rendered:
                if (IsContinuous || _dirty)
                    ScheduleFrame();
                break;
            case FrameResult.Busy:
                // The compositor still holds the next image: retry once it lets go.
                var pending = _images[_next].Pending!;
                pending.ContinueWith(_ => ScheduleFrame(), TaskScheduler.FromCurrentSynchronizationContext());
                break;
            case FrameResult.Skipped:
                break; // zero-size; a resize requests the next frame
            case FrameResult.Resizing:
                break; // the settle timer requests the next frame
        }
    }

    /// <summary>
    /// True while the size keeps changing (an interactive resize): every new size would recreate the shared images
    /// and all of the renderer's window-sized targets, so the last frame stays up (the visual stretches it) until the
    /// size has held for <see cref="ResizeSettle"/>; a timer then requests the frame at the final size.
    /// </summary>
    private bool ResizeSettling(PixelSize size)
    {
        var now = _clock.Elapsed;
        if (size != _settlingSize)
        {
            _settlingSize = size;
            _settlingSince = now;
        }
        var wait = ResizeSettle - (now - _settlingSince);
        if (wait <= TimeSpan.Zero)
            return false;
        _settleTimer?.Dispose();
        _settleTimer = DispatcherTimer.RunOnce(() =>
        {
            _settleTimer = null;
            ScheduleFrame();
        }, wait);
        return true;
    }

    /// <summary>Requests the next frame once it is due (<see cref="MaxFrameRate"/>); requests meanwhile coalesce.
    /// Continuous mode waits for the next display frame instead (paced, without the timer's rounding).</summary>
    private void WaitUntilDue()
    {
        if (_dueTimer != null || _waitingForDisplay)
            return;
        if (IsContinuous && TopLevel.GetTopLevel(this) is { } top)
        {
            _waitingForDisplay = true;
            int generation = _generation;
            top.RequestAnimationFrame(_ =>
            {
                if (generation != _generation)
                    return;
                _waitingForDisplay = false;
                ScheduleFrame();
            });
            return;
        }
        _dueTimer = DispatcherTimer.RunOnce(() =>
        {
            _dueTimer = null;
            ScheduleFrame();
        }, TimeSpan.FromTicks(Math.Max(0, (_nextFrameDue - _clock.Elapsed).Ticks)), DispatcherPriority.Render);
    }

    private FrameResult RenderFrame()
    {
        if (_interop!.IsLost)
            throw new InvalidOperationException("compositor GPU interop lost");
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
            return FrameResult.Skipped;
        double scaling = top.RenderScaling;
        var size = PixelSize.FromSize(Bounds.Size, scaling);
        if (size.Width < 1 || size.Height < 1)
            return FrameResult.Skipped;
        UpdateVisualSize();

        var format = SurfaceFormat == D3DSurfaceFormat.Rgba8 ? Format.R8G8B8A8_UNorm : Format.B8G8R8A8_UNorm;
        if (_images.Count > 0 && _images[0].Format == format && _images[0].Size != size && ResizeSettling(size))
            return FrameResult.Resizing;
        if (_images.Count == 0 || _images[0].Size != size || _images[0].Format != format)
        {
            _settlingSize = default;
            DisposeImages();
            for (int i = 0; i < ImageCount; i++)
                _images.Add(CreateImage(size, format));
        }

        var image = _images[_next];
        if (image.Pending is { IsCompleted: false })
            return FrameResult.Busy;
        if (image.Pending is { IsFaulted: true })
            throw image.Pending.Exception!;
        _next = (_next + 1) % _images.Count;

        var gfx = _gfx!;
        var now = _clock.Elapsed;
        var budget = MaxFrameRate > 0 ? TimeSpan.FromSeconds(1.0 / MaxFrameRate) : TimeSpan.Zero;
        // On time: keep the cadence, so display ticks that do not divide the budget still average the cap. After a
        // pause: start over from now.
        _nextFrameDue = now - _nextFrameDue < budget ? _nextFrameDue + budget : now + budget;
        // Pending completed above, so the compositor has released key 0 and this does not block.
        image.Mutex.AcquireSync(0, KeyedMutexTimeoutMs);
        try
        {
            if (_attachedRenderer != null)
            {
                _attachedRenderer.Render(new D3DFrame(gfx, image.Texture, image.Rtv, format, size.Width, size.Height, scaling, _clock.Elapsed));
            }
            else
            {
                gfx.Context.ClearRenderTargetView(image.Rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            }
            gfx.Context.Flush();
        }
        finally
        {
            image.Mutex.ReleaseSync(1);
        }
        gfx.CheckDeviceRemoved();

        // The compositor acquires key 1, samples, and releases key 0.
        var pending = _surface!.UpdateWithKeyedMutexAsync(image.Imported, 1, 0);
        image.Pending = pending;
        FramesRendered++;
        _ = pending.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Interlocked.Increment(ref _framesPresented);
        }, TaskScheduler.Default);
        return FrameResult.Rendered;
    }

    private SharedImage CreateImage(PixelSize size, Format format)
    {
        var dev = _gfx!.Device;
        var tex = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            ArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
        });
        tex.DebugName = "D3DViewport shared image";
        using var res = tex.QueryInterface<IDXGIResource>();
        var imported = _interop!.ImportImage(
            new PlatformHandle(res.SharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
            new PlatformGraphicsExternalImageProperties
            {
                Width = size.Width,
                Height = size.Height,
                Format = format == Format.R8G8B8A8_UNorm ? PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm : PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                // D3D textures are top-left origin; without this the (GL-based) compositor shows them upside down.
                TopLeftOrigin = true,
            });
        return new SharedImage
        {
            Texture = tex,
            Rtv = dev.CreateRenderTargetView(tex),
            Mutex = tex.QueryInterface<IDXGIKeyedMutex>(),
            Imported = imported,
            Size = size,
            Format = format,
        };
    }

    private void UpdateVisualSize()
    {
        var v = new Vector(Bounds.Width, Bounds.Height);
        if (_visual != null)
            _visual.Size = v;
        if (_container != null)
            _container.Size = v;
    }

    // ── Failure / device loss ───────────────────────────────────────────────

    private void OnDeviceLost(GfxDevice device, string reason)
        => Dispatcher.UIThread.Post(() => RecreateAfterLoss("device removed: " + reason));

    private void HandleFailure(Exception ex)
    {
        bool lost = _interop?.IsLost == true || (_gfx?.IsDeviceLossException(ex) ?? false);
        if (lost)
        {
            RecreateAfterLoss(ex.Message);
            return;
        }
        // A non-loss failure (e.g. ImportImage rejected the texture): stop rather than spin.
        Teardown();
        Unavailable(ex.Message);
    }

    /// <summary>Tears down the device, renderer resources and every imported image, then rebuilds.</summary>
    public void RecreateAfterLoss(string reason = "requested")
    {
        if (_recreating)
            return;
        _recreating = true;
        _running = false;
        Status = "recovering: " + reason;
        Teardown();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await InitAsync();
            }
            finally
            {
                _recreating = false;
            }
        });
    }

    private void DisposeImages()
    {
        foreach (var img in _images)
            img.Dispose();
        _images.Clear();
        _next = 0;
    }

    private void Teardown()
    {
        _frameScheduled = false;
        _settleTimer?.Dispose();
        _dueTimer?.Dispose();
        _settleTimer = _dueTimer = null;
        _waitingForDisplay = false;
        _generation++;
        _attachedRenderer?.OnDeviceDestroyed();
        _attachedRenderer = null;
        DisposeImages();
        ReleaseDevice();
        _interop = null;
    }

    private void ReleaseDevice()
    {
        if (_gfx != null)
        {
            _gfx.DeviceLost -= OnDeviceLost;
            // Shared: the last lease disposes it (after an idle grace period unless it is lost).
            if (_lease != null)
                _lease.Dispose();
            else
                _gfx.Dispose();
        }
        _lease = null;
        _gfx = null;
    }
}
