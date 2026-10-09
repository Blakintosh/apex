using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Apex.Editor.Services.Preview;

namespace Apex.Editor.Controls;

/// <summary>
/// GL model viewport for the preview pane: renders a <see cref="ModelScene"/> with a single
/// directional key light + hemispheric ambient and GGX spec-gloss shading (BO3 workflow).
/// Z-up orbit camera (left-drag orbit, right/middle-drag pan, wheel or Alt+right-drag zoom,
/// Shift+left-drag rotates the key light); renders on demand only.
/// All GL entry points are bound through GetProcAddress so the control works against both ANGLE ES 3
/// (the Avalonia default on Windows) and desktop core contexts; shaders are emitted per profile.
/// GL failures never crash the app — they surface as <see cref="IsGlUnavailable"/>.
/// </summary>
public sealed class GlPreviewViewport : OpenGlControlBase, ICustomHitTest
{
    // The GL surface is composited, not drawn through the normal render path, so without this the
    // control is transparent to hit-testing and every pointer gesture falls through to the Border
    // behind it. Accept input anywhere within bounds.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public static readonly StyledProperty<ModelScene?> SceneProperty =
        AvaloniaProperty.Register<GlPreviewViewport, ModelScene?>(nameof(Scene));

    public static readonly DirectProperty<GlPreviewViewport, bool> IsGlUnavailableProperty =
        AvaloniaProperty.RegisterDirect<GlPreviewViewport, bool>(nameof(IsGlUnavailable), o => o.IsGlUnavailable);

    public ModelScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Home frames the model like F (false in an anim preview, where Home goes to the first frame).</summary>
    public bool HomeFrames { get; set; } = true;

    public GlPreviewViewport()
    {
        // Focusable so F / Home can frame the model once the user has clicked into the preview.
        Focusable = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            UpdateClearColor();
            RequestNextFrameRendering();
        };
    }

    private bool _isGlUnavailable;
    public bool IsGlUnavailable
    {
        get => _isGlUnavailable;
        private set => SetAndRaise(IsGlUnavailableProperty, ref _isGlUnavailable, value);
    }

    // ── Camera (Z-up orbit around the scene bounds center) ──────────────────
    private Vector3 _target;
    private float _yaw = MathF.PI * 0.35f;
    private float _pitch = 0.35f;
    private float _distance = 10f;
    private float _sceneRadius = 1f;

    // ── Key light (per-instance; survives scene reloads, resets only with the control) ──
    // Default = APE's captured sun direction (RenderDoc): normalize(-0.1177, -0.9587, 0.2588),
    // a low warm key. Yaw/pitch are the spherical form the Shift-drag light control edits.
    private float _lightYaw = MathF.Atan2(-0.9587f, -0.1177f);
    private float _lightPitch = MathF.Asin(0.2588f);
    private const float LightPitchLimit = 80f * MathF.PI / 180f;

    private enum DragMode { None, Orbit, Pan, Zoom, Light }

    private Point _lastPointer;
    private DragMode _dragMode;

    // ── GL state (valid only while a context is live) ───────────────────────
    private Gl? _gl;
    private uint _program;
    private int _uMvp, _uCamPos, _uLightDir, _uHasColor, _uHasNormal, _uHasSpec;
    private int _uHasGloss, _uHasOcc, _uGlossMin, _uGlossMax, _uKeyScale;
    private int _uEnvMap, _uEnvScale, _uEnvYaw, _uEnvMaxMip;
    private uint _envTex;

    // Multiplier on APE's captured (exposure-baked) sun radiance. With real probe IBL the
    // environment now supplies most of the light, so the direct key is a crisp accent only.
    private const float KeyScale = 1.0f;
    // Environment (APE's captured reflection probe): RGBM equirect, MAXRANGE matches the bake.
    private const float EnvRgbmRange = 64f;
    private const float EnvScale = 0.55f;   // brings the probe's HDR radiance into display range (tuned vs APE)
    private const float EnvYaw = 0f;         // azimuth offset to align the probe's sun with the view
    private float _envMaxMip = 10f;
    private ModelScene? _uploadedScene;
    private int _uploadedVersion;
    private readonly List<GpuPart> _gpuParts = new();
    private readonly Dictionary<RawTexture, uint> _textureIds = new();
    private bool _glFailed;

    private ModelScene? _observedScene;

    private sealed class GpuPart
    {
        public uint Vao;
        public uint Vbo;
        public uint Ibo;
        public int IndexCount;
        public uint ColorTex;
        public uint NormalTex;
        public uint SpecTex;
        public uint GlossTex;
        public uint OccTex;
        public float GlossMin;
        public float GlossMax;

        /// <summary>The vertex array currently in the VBO — skips redundant re-uploads on version bumps.</summary>
        public float[]? UploadedData;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            var scene = change.GetNewValue<ModelScene?>();
            ObserveScene(scene);
            // A reload of the same asset keeps the user's camera; a new asset is framed.
            if (scene is { Parts.Count: > 0 } && (scene.Subject is null || !ReferenceEquals(scene.Subject, _framedSubject)))
                FrameScene(scene);
            RequestNextFrameRendering();
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveScene(Scene);
        UpdateClearColor();
    }

    // The viewport background token, read on the UI thread for the GL clear.
    private float _clearR = 0.063f, _clearG = 0.078f, _clearB = 0.118f;

    private void UpdateClearColor()
    {
        if (this.TryFindResource("ViewportBgBrush", ActualThemeVariant, out var res) && res is Avalonia.Media.ISolidColorBrush brush)
        {
            var c = brush.Color;
            _clearR = c.R / 255f;
            _clearG = c.G / 255f;
            _clearB = c.B / 255f;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ObserveScene(null);
    }

    private void ObserveScene(ModelScene? scene)
    {
        if (ReferenceEquals(_observedScene, scene))
            return;
        if (_observedScene is not null)
            _observedScene.VerticesUpdated -= OnSceneVerticesUpdated;
        _observedScene = scene;
        if (scene is not null)
            scene.VerticesUpdated += OnSceneVerticesUpdated;
    }

    // May fire on a thread-pool thread (skinning task) — marshal the render request.
    private void OnSceneVerticesUpdated() =>
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

    private object? _framedSubject;

    /// <summary>Back to the default framing of the scene (the Frame button, F / Home, double-click).</summary>
    public void Frame()
    {
        FrameScene(Scene);
        RequestNextFrameRendering();
    }

    private void FrameScene(ModelScene? scene)
    {
        if (scene is null || scene.Parts.Count == 0)
            return;
        _framedSubject = scene.Subject;

        var center = (scene.BoundsMin + scene.BoundsMax) * 0.5f;
        var radius = MathF.Max((scene.BoundsMax - scene.BoundsMin).Length() * 0.5f, 0.001f);
        _target = center;
        _sceneRadius = radius;
        _yaw = MathF.PI * 0.35f;
        _pitch = 0.35f;
        // Fit the bounding sphere into a 40° vertical FOV with a little margin.
        _distance = radius / MathF.Sin(20f * MathF.PI / 180f) * 1.1f;
    }

    // ── Interaction ─────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        Focus(NavigationMethod.Pointer);
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed)
        {
            Frame();
            e.Handled = true;
            return;
        }
        _lastPointer = point.Position;
        // Mode is locked in for the whole capture; mid-drag modifier changes don't switch it.
        _dragMode = ResolveDragMode(point.Properties, e.KeyModifiers);
        if (_dragMode != DragMode.None)
        {
            e.Pointer.Capture(this);
            e.Handled = true; // also keeps Alt+left from triggering menu/access-key handling
        }
    }

    private static DragMode ResolveDragMode(PointerPointProperties props, KeyModifiers modifiers)
    {
        if (props.IsLeftButtonPressed)
        {
            // Shift rotates the key light; Ctrl kept as a silent alias.
            return (modifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0
                ? DragMode.Light
                : DragMode.Orbit;
        }
        if (props.IsRightButtonPressed)
            return (modifiers & KeyModifiers.Alt) != 0 ? DragMode.Zoom : DragMode.Pan;
        if (props.IsMiddleButtonPressed)
            return DragMode.Pan;
        return DragMode.None;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragMode == DragMode.None)
            return;
        // The release can be lost (another window took the mouse mid-drag): with no button down the drag is over.
        var buttons = e.GetCurrentPoint(this).Properties;
        if (!buttons.IsLeftButtonPressed && !buttons.IsRightButtonPressed && !buttons.IsMiddleButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var pos = e.GetPosition(this);
        var dx = (float)(pos.X - _lastPointer.X);
        var dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;

        switch (_dragMode)
        {
            case DragMode.Orbit:
                _yaw -= dx * 0.01f;
                _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.55f, 1.55f);
                break;
            case DragMode.Pan:
            {
                // Pan along the camera's right/up axes, scaled so a drag tracks the model roughly 1:1.
                var (right, up) = CameraRightUp();
                var scale = _distance * 0.0016f;
                _target += right * (-dx * scale) + up * (dy * scale);
                break;
            }
            case DragMode.Zoom:
                // Exponential dolly: drag down zooms out, up zooms in.
                _distance = Math.Clamp(
                    _distance * MathF.Pow(1.005f, dy),
                    _sceneRadius * 0.15f,
                    _sceneRadius * 25f);
                break;
            case DragMode.Light:
                _lightYaw -= dx * 0.01f;
                _lightPitch = Math.Clamp(_lightPitch - dy * 0.01f, -LightPitchLimit, LightPitchLimit);
                break;
        }

        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragMode != DragMode.None)
        {
            EndDrag(e.Pointer);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragMode = DragMode.None;
    }

    private void EndDrag(IPointer pointer)
    {
        _dragMode = DragMode.None;
        if (ReferenceEquals(pointer.Captured, this))
            pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !Commands.CommandCatalog.Is(Commands.CommandCatalog.FramePreview, e))
            return;
        // In an anim preview Home belongs to the transport (first frame); F always frames.
        if (e.Key == Key.Home && !HomeFrames)
            return;
        Frame();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _distance = Math.Clamp(
            _distance * MathF.Pow(0.86f, (float)e.Delta.Y),
            _sceneRadius * 0.15f,
            _sceneRadius * 25f);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    private (Vector3 Right, Vector3 Up) CameraRightUp()
    {
        var forward = Vector3.Normalize(_target - CameraPosition());
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);
        return (right, up);
    }

    private Vector3 CameraPosition()
    {
        var cp = MathF.Cos(_pitch);
        return _target + _distance * new Vector3(cp * MathF.Cos(_yaw), cp * MathF.Sin(_yaw), MathF.Sin(_pitch));
    }

    // ── GL lifecycle ────────────────────────────────────────────────────────

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glFailed = false;
        try
        {
            _gl = new Gl(gl);
            BuildProgram();
            LoadEnvironment();
            IsGlUnavailable = false;
        }
        catch
        {
            FailGl();
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            ReleaseSceneResources();
            if (_gl is { } g && _envTex != 0)
                g.DeleteTexture(_envTex);
            _envTex = 0;
            if (_gl is not null && _program != 0)
                _gl.DeleteProgram(_program);
        }
        catch
        {
            // Context may already be gone; nothing to salvage.
        }
        _program = 0;
        _gl = null;
    }

    protected override void OnOpenGlLost()
    {
        // The context died with its resources; forget the ids without touching GL.
        _gpuParts.Clear();
        _textureIds.Clear();
        _uploadedScene = null;
        _program = 0;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)(Bounds.Width * scaling));
        int height = Math.Max(1, (int)(Bounds.Height * scaling));

        if (_glFailed || _gl is null)
            return;

        try
        {
            var g = _gl;
            g.Viewport(0, 0, width, height);
            g.ClearColor(_clearR, _clearG, _clearB, 1f); // ViewportBgBrush
            g.Clear(GlConsts.ColorBufferBit | GlConsts.DepthBufferBit);

            var scene = Scene;
            if (scene is null || scene.Parts.Count == 0 || _program == 0)
                return;

            if (!ReferenceEquals(scene, _uploadedScene))
            {
                // Textures the new scene shares with the old one (same decoded texture, e.g. an anim reload) stay.
                ReleaseSceneBuffers();
                UploadScene(scene);
                ReleaseTexturesNotIn(scene);
                _uploadedScene = scene;
                _uploadedVersion = scene.Version;
            }
            else if (scene.Version != _uploadedVersion)
            {
                UpdateDynamicVertices(scene);
                _uploadedVersion = scene.Version;
            }

            g.Enable(GlConsts.DepthTest);
            g.DepthFunc(GlConsts.Lequal);
            g.Disable(GlConsts.CullFace); // many BO3 meshes are effectively double-sided

            var camPos = CameraPosition();
            var view = Matrix4x4.CreateLookAt(camPos, _target, Vector3.UnitZ);
            float near = MathF.Max(_sceneRadius * 0.01f, _distance - _sceneRadius * 4f);
            float far = _distance + _sceneRadius * 4f;
            var proj = GlPerspective(40f * MathF.PI / 180f, (float)width / height, near, far);
            var mvp = view * proj;

            g.UseProgram(_program);
            g.UniformMatrix4(_uMvp, ref mvp);
            g.Uniform3(_uCamPos, camPos.X, camPos.Y, camPos.Z);
            var lcp = MathF.Cos(_lightPitch);
            var light = new Vector3(lcp * MathF.Cos(_lightYaw), lcp * MathF.Sin(_lightYaw), MathF.Sin(_lightPitch));
            g.Uniform3(_uLightDir, light.X, light.Y, light.Z);
            g.Uniform1(_uKeyScale, KeyScale);
            g.Uniform1(_uEnvScale, EnvScale);
            g.Uniform1(_uEnvYaw, EnvYaw);
            g.Uniform1(_uEnvMaxMip, _envMaxMip);
            g.ActiveTexture(GlConsts.Texture0 + 5);
            g.BindTexture(GlConsts.Texture2D, _envTex);

            foreach (var part in _gpuParts)
            {
                g.Uniform1(_uHasColor, part.ColorTex != 0 ? 1 : 0);
                g.Uniform1(_uHasNormal, part.NormalTex != 0 ? 1 : 0);
                g.Uniform1(_uHasSpec, part.SpecTex != 0 ? 1 : 0);
                g.Uniform1(_uHasGloss, part.GlossTex != 0 ? 1 : 0);
                g.Uniform1(_uHasOcc, part.OccTex != 0 ? 1 : 0);
                g.Uniform1(_uGlossMin, part.GlossMin);
                g.Uniform1(_uGlossMax, part.GlossMax);

                g.ActiveTexture(GlConsts.Texture0);
                g.BindTexture(GlConsts.Texture2D, part.ColorTex);
                g.ActiveTexture(GlConsts.Texture0 + 1);
                g.BindTexture(GlConsts.Texture2D, part.NormalTex);
                g.ActiveTexture(GlConsts.Texture0 + 2);
                g.BindTexture(GlConsts.Texture2D, part.SpecTex);
                g.ActiveTexture(GlConsts.Texture0 + 3);
                g.BindTexture(GlConsts.Texture2D, part.GlossTex);
                g.ActiveTexture(GlConsts.Texture0 + 4);
                g.BindTexture(GlConsts.Texture2D, part.OccTex);

                BindPart(part);
                g.DrawElements(GlConsts.Triangles, part.IndexCount, GlConsts.UnsignedInt, IntPtr.Zero);
            }

            if (_gl.HasVao)
                g.BindVertexArray(0);
        }
        catch
        {
            FailGl();
        }
    }

    private void FailGl()
    {
        _glFailed = true;
        IsGlUnavailable = true;
        _gpuParts.Clear();
        _textureIds.Clear();
        _uploadedScene = null;
    }

    // ── Resource management ─────────────────────────────────────────────────

    private void ReleaseSceneResources()
    {
        ReleaseSceneBuffers();
        if (_gl is { } g)
            foreach (var tex in _textureIds.Values)
                g.DeleteTexture(tex);
        _textureIds.Clear();
    }

    private void ReleaseSceneBuffers()
    {
        if (_gl is { } g)
        {
            foreach (var part in _gpuParts)
            {
                if (part.Vao != 0) g.DeleteVertexArray(part.Vao);
                if (part.Vbo != 0) g.DeleteBuffer(part.Vbo);
                if (part.Ibo != 0) g.DeleteBuffer(part.Ibo);
            }
        }
        _gpuParts.Clear();
        _uploadedScene = null;
    }

    private void ReleaseTexturesNotIn(ModelScene scene)
    {
        var used = new HashSet<RawTexture>();
        foreach (var part in scene.Parts)
            foreach (var t in new[] { part.ColorMap, part.NormalMap, part.SpecColorMap, part.GlossMap, part.OcclusionMap })
                if (t is not null)
                    used.Add(t);
        foreach (var (texture, id) in _textureIds.Where(kv => !used.Contains(kv.Key)).ToList())
        {
            _gl!.DeleteTexture(id);
            _textureIds.Remove(texture);
        }
    }

    private void UploadScene(ModelScene scene)
    {
        var g = _gl!;
        for (int i = 0; i < scene.Parts.Count; i++)
        {
            var part = scene.Parts[i];
            var gpu = new GpuPart { IndexCount = part.Indices.Length };

            if (g.HasVao)
            {
                gpu.Vao = g.GenVertexArray();
                g.BindVertexArray(gpu.Vao);
            }

            var vertexData = scene.CurrentVertexData(i);
            gpu.Vbo = g.GenBuffer();
            g.BindBuffer(GlConsts.ArrayBuffer, gpu.Vbo);
            g.BufferData(GlConsts.ArrayBuffer, vertexData);
            gpu.UploadedData = vertexData;

            gpu.Ibo = g.GenBuffer();
            g.BindBuffer(GlConsts.ElementArrayBuffer, gpu.Ibo);
            g.BufferData(GlConsts.ElementArrayBuffer, part.Indices);

            SetVertexLayout();

            gpu.ColorTex = UploadTexture(part.ColorMap);
            gpu.NormalTex = UploadTexture(part.NormalMap);
            gpu.SpecTex = UploadTexture(part.SpecColorMap);
            gpu.GlossTex = UploadTexture(part.GlossMap);
            gpu.OccTex = UploadTexture(part.OcclusionMap);
            gpu.GlossMin = part.GlossRangeMin;
            gpu.GlossMax = part.GlossRangeMax;
            _gpuParts.Add(gpu);
        }

        if (g.HasVao)
            g.BindVertexArray(0);
    }

    /// <summary>Re-uploads (glBufferSubData) any part whose current vertex array changed since last upload.</summary>
    private void UpdateDynamicVertices(ModelScene scene)
    {
        var g = _gl!;
        for (int i = 0; i < _gpuParts.Count && i < scene.Parts.Count; i++)
        {
            var gpu = _gpuParts[i];
            var data = scene.CurrentVertexData(i);
            if (ReferenceEquals(data, gpu.UploadedData))
                continue;
            g.BindBuffer(GlConsts.ArrayBuffer, gpu.Vbo);
            g.BufferSubData(GlConsts.ArrayBuffer, data);
            gpu.UploadedData = data;
        }
    }

    private void BindPart(GpuPart part)
    {
        var g = _gl!;
        if (g.HasVao)
        {
            g.BindVertexArray(part.Vao);
            return;
        }
        g.BindBuffer(GlConsts.ArrayBuffer, part.Vbo);
        g.BindBuffer(GlConsts.ElementArrayBuffer, part.Ibo);
        SetVertexLayout();
    }

    private void SetVertexLayout()
    {
        var g = _gl!;
        const int stride = 8 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, GlConsts.Float, false, stride, IntPtr.Zero);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, GlConsts.Float, false, stride, (IntPtr)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 2, GlConsts.Float, false, stride, (IntPtr)(6 * sizeof(float)));
    }

    private uint UploadTexture(RawTexture? texture)
    {
        if (texture is null)
            return 0;

        var g = _gl!;
        if (_textureIds.TryGetValue(texture, out var existing))
            return existing;

        var id = g.GenTexture();
        g.BindTexture(GlConsts.Texture2D, id);
        g.PixelStore(GlConsts.UnpackAlignment, 1);
        g.TexImage2D(GlConsts.Texture2D, 0, GlConsts.Rgba8, texture.Width, texture.Height, GlConsts.Rgba, GlConsts.UnsignedByte, texture.Rgba);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureMinFilter, GlConsts.LinearMipmapLinear);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureMagFilter, GlConsts.Linear);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureWrapS, GlConsts.Repeat);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureWrapT, GlConsts.Repeat);
        g.GenerateMipmap(GlConsts.Texture2D);
        _textureIds[texture] = id;
        return id;
    }

    /// <summary>APE's captured reflection-probe studio (embedded RGBM equirect) decoded to linear HDR radiance, once per
    /// process; null when it is not embedded.</summary>
    private static readonly Lazy<(float[] Hdr, int Width, int Height)?> StudioProbe = new(() =>
    {
        using var stream = typeof(GlPreviewViewport).Assembly
            .GetManifestResourceStream("Apex.Editor.studio_probe.png");
        if (stream is null)
            return null;

        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(stream);
        int w = image.Width, h = image.Height;
        var hdr = new float[w * h * 4];
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    var p = row[x];
                    // RGBM decode: linear = rgb * m * range.
                    float m = p.A / 255f * EnvRgbmRange;
                    int o = (y * w + x) * 4;
                    hdr[o] = p.R / 255f * m;
                    hdr[o + 1] = p.G / 255f * m;
                    hdr[o + 2] = p.B / 255f * m;
                    hdr[o + 3] = 1f;
                }
            }
        });
        return (hdr, w, h);
    });

    /// <summary>
    /// Uploads the studio probe (<see cref="StudioProbe"/>) as a float cubemap-substitute 2D texture, so glossy
    /// surfaces reflect the real environment.
    /// </summary>
    private void LoadEnvironment()
    {
        var g = _gl!;
        if (StudioProbe.Value is not (var hdr, var w, var h))
            return; // no env embedded — shader falls back to ambient-only

        _envTex = g.GenTexture();
        g.BindTexture(GlConsts.Texture2D, _envTex);
        g.PixelStore(GlConsts.UnpackAlignment, 1);
        g.TexImage2D(GlConsts.Texture2D, 0, GlConsts.Rgba16f, w, h, GlConsts.Rgba, GlConsts.Float, hdr);
        // Azimuth wraps (Repeat), poles clamp (ClampToEdge). Trilinear for roughness-driven mip blur.
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureWrapS, GlConsts.Repeat);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureWrapT, GlConsts.ClampToEdge);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureMinFilter, GlConsts.LinearMipmapLinear);
        g.TexParameter(GlConsts.Texture2D, GlConsts.TextureMagFilter, GlConsts.Linear);
        g.GenerateMipmap(GlConsts.Texture2D);
        _envMaxMip = MathF.Floor(MathF.Log2(Math.Max(w, h)));
    }

    // ── Shaders ─────────────────────────────────────────────────────────────

    private void BuildProgram()
    {
        var g = _gl!;
        var es = GlVersion.Type == GlProfileType.OpenGLES;
        if ((es && GlVersion.Major < 3) || (!es && GlVersion.Major < 3))
            throw new InvalidOperationException("OpenGL 3.0 / ES 3.0 required.");

        var header = es
            ? "#version 300 es\nprecision highp float;\nprecision highp int;\n"
            : "#version 330 core\n";

        const string vertexBody = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
uniform mat4 uMvp;
out vec3 vNormal;
out vec2 vUV;
out vec3 vWorldPos;
void main()
{
    vNormal = aNormal;
    vUV = aUV;
    vWorldPos = aPos;
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

        const string fragmentBody = @"
in vec3 vNormal;
in vec2 vUV;
in vec3 vWorldPos;
uniform vec3 uCamPos;
uniform vec3 uLightDir;
uniform int uHasColor;
uniform int uHasNormal;
uniform int uHasSpec;
uniform int uHasGloss;
uniform int uHasOcc;
uniform float uGlossMin;
uniform float uGlossMax;
uniform float uKeyScale;
uniform float uEnvScale;
uniform float uEnvYaw;
uniform float uEnvMaxMip;
uniform sampler2D uEnvMap;
uniform sampler2D uColorMap;
uniform sampler2D uNormalMap;
uniform sampler2D uSpecMap;
uniform sampler2D uGlossMap;
uniform sampler2D uOccMap;
out vec4 fragColor;

// Sample APE's captured reflection-probe studio (equirect, BO3 Z-up). mip drives roughness blur.
const float PI = 3.14159265;
vec3 sampleEnv(vec3 d, float mip)
{
    d = normalize(d);
    float u = atan(d.y, d.x) * (0.5 / PI) + uEnvYaw; // azimuth about world Z
    float v = acos(clamp(d.z, -1.0, 1.0)) * (1.0 / PI); // polar from +Z (0 = up)
    return textureLod(uEnvMap, vec2(u, v), mip).rgb * uEnvScale;
}

// Screen-space cotangent frame (Mikkelsen) — avoids CPU tangent generation.
vec3 perturbNormal(vec3 N, vec3 V)
{
    vec3 dp1 = dFdx(vWorldPos);
    vec3 dp2 = dFdy(vWorldPos);
    vec2 duv1 = dFdx(vUV);
    vec2 duv2 = dFdy(vUV);
    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = inversesqrt(max(dot(T, T), max(dot(B, B), 1e-12)));
    vec3 nTex = texture(uNormalMap, vUV).xyz * 2.0 - 1.0;
    nTex.z = sqrt(max(1.0 - dot(nTex.xy, nTex.xy), 0.0));
    return normalize(mat3(T * invmax, B * invmax, N) * nTex);
}

void main()
{
    vec3 N = normalize(gl_FrontFacing ? vNormal : -vNormal);
    vec3 V = normalize(uCamPos - vWorldPos);
    if (uHasNormal == 1)
        N = perturbNormal(N, V);

    vec3 albedo = vec3(0.62);
    if (uHasColor == 1)
        albedo = pow(texture(uColorMap, vUV).rgb, vec3(2.2));

    // Missing spec → dielectric F0; BO3 gloss lives in a dedicated mask (cosinePowerMap),
    // remapped by the material's gloss range — never in the spec map's alpha.
    // Spec maps are authored LINEAR (classic CoD ~56-RGB 'plastic' default = F0 0.22);
    // sRGB-decoding them crushes dielectric F0 to ~0.03 and kills all reflection sheen.
    vec3 specColor = vec3(0.04);
    if (uHasSpec == 1)
        specColor = texture(uSpecMap, vUV).rgb;
    float gloss = 0.5;
    if (uHasGloss == 1)
        gloss = mix(uGlossMin, uGlossMax, texture(uGlossMap, vUV).r);
    float rough = clamp(1.0 - gloss, 0.06, 1.0);
    float a2 = rough * rough * rough * rough;

    vec3 L = uLightDir;
    vec3 H = normalize(L + V);
    float ndl = max(dot(N, L), 0.0);
    float ndv = max(dot(N, V), 1e-4);
    float ndh = max(dot(N, H), 0.0);
    float vdh = max(dot(V, H), 0.0);

    // GGX D, Smith-Schlick G, Schlick F.
    float d = a2 / max(3.14159 * pow(ndh * ndh * (a2 - 1.0) + 1.0, 2.0), 1e-6);
    float k = rough * rough * 0.5;
    float visV = ndv / (ndv * (1.0 - k) + k);
    float visL = ndl / (ndl * (1.0 - k) + k);
    vec3 f = specColor + (vec3(1.0) - specColor) * pow(1.0 - vdh, 5.0);
    vec3 spec = d * visV * visL * f / max(4.0 * ndv, 1e-4);

    // Direct key: APE's captured sun (RenderDoc, exposure baked) as a crisp accent on top of
    // the environment lighting. keyColor = sun.color * invExposure.
    vec3 keyColor = vec3(1.42, 1.10, 0.73) * uKeyScale;
    float occ = uHasOcc == 1 ? texture(uOccMap, vUV).r : 1.0; // AO shadows indirect only, never the key light

    // Image-based lighting from APE's ACTUAL captured reflection probe (the sandy studio):
    //   • diffuse irradiance ≈ the fully-blurred env in the surface-normal direction
    //   • specular reflection ≈ the env in the mirror direction, blurred by roughness (mip)
    // This is the real environment APE reflects, so dark-albedo spec-gloss metals pick up the
    // studio's bright sky/horizon exactly as they do in APE, not via a procedural stand-in.
    vec3 R = reflect(-V, N);
    vec3 envDiffuse = sampleEnv(N, uEnvMaxMip);                          // Lambert irradiance approx
    vec3 envSpec = sampleEnv(R, sqrt(rough) * uEnvMaxMip);              // roughness → mip blur
    // Gloss-aware Fresnel (Fdez-Aguera): grazing brightens, F0 sets base reflectance so
    // dielectrics stay matte at normal incidence and albedo reads through.
    vec3 Fr = specColor + (max(vec3(gloss), specColor) - specColor) * pow(1.0 - ndv, 5.0);

    vec3 color = albedo * envDiffuse * occ                              // environment diffuse
               + albedo * keyColor * ndl * (1.0 / 3.14159)             // direct sun diffuse
               + spec * keyColor * ndl * 1.35                          // direct sun specular (crisp)
               + envSpec * Fr * occ;                                    // environment specular reflection

    // ACES-fitted filmic tonemap (Narkowicz) — closer to APE's TonemapLUT stage than Reinhard.
    vec3 x = color;
    color = clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
    fragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
}";

        var vs = g.CompileShader(GlConsts.VertexShader, header + vertexBody);
        var fs = g.CompileShader(GlConsts.FragmentShader, header + fragmentBody);
        _program = g.LinkProgram(vs, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);

        g.UseProgram(_program);
        _uMvp = g.GetUniformLocation(_program, "uMvp");
        _uCamPos = g.GetUniformLocation(_program, "uCamPos");
        _uLightDir = g.GetUniformLocation(_program, "uLightDir");
        _uHasColor = g.GetUniformLocation(_program, "uHasColor");
        _uHasNormal = g.GetUniformLocation(_program, "uHasNormal");
        _uHasSpec = g.GetUniformLocation(_program, "uHasSpec");
        _uHasGloss = g.GetUniformLocation(_program, "uHasGloss");
        _uHasOcc = g.GetUniformLocation(_program, "uHasOcc");
        _uGlossMin = g.GetUniformLocation(_program, "uGlossMin");
        _uGlossMax = g.GetUniformLocation(_program, "uGlossMax");
        _uKeyScale = g.GetUniformLocation(_program, "uKeyScale");
        _uEnvScale = g.GetUniformLocation(_program, "uEnvScale");
        _uEnvYaw = g.GetUniformLocation(_program, "uEnvYaw");
        _uEnvMaxMip = g.GetUniformLocation(_program, "uEnvMaxMip");
        g.Uniform1(g.GetUniformLocation(_program, "uColorMap"), 0);
        g.Uniform1(g.GetUniformLocation(_program, "uNormalMap"), 1);
        g.Uniform1(g.GetUniformLocation(_program, "uSpecMap"), 2);
        g.Uniform1(g.GetUniformLocation(_program, "uGlossMap"), 3);
        g.Uniform1(g.GetUniformLocation(_program, "uOccMap"), 4);
        g.Uniform1(g.GetUniformLocation(_program, "uEnvMap"), 5);
    }

    /// <summary>GL-style perspective (z to [-1,1]) in System.Numerics row-vector convention.</summary>
    private static Matrix4x4 GlPerspective(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY * 0.5f);
        float nf = 1f / (near - far);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) * nf, -1f,
            0, 0, 2f * far * near * nf, 0);
    }

    // ── GL binding via GetProcAddress (works on ANGLE ES and desktop core) ──

    private static class GlConsts
    {
        public const int ColorBufferBit = 0x4000;
        public const int DepthBufferBit = 0x0100;
        public const int DepthTest = 0x0B71;
        public const int CullFace = 0x0B44;
        public const int Lequal = 0x0203;
        public const int Triangles = 0x0004;
        public const int UnsignedInt = 0x1405;
        public const int UnsignedByte = 0x1401;
        public const int Float = 0x1406;
        public const int ArrayBuffer = 0x8892;
        public const int ElementArrayBuffer = 0x8893;
        public const int StaticDraw = 0x88E4;
        public const int VertexShader = 0x8B31;
        public const int FragmentShader = 0x8B30;
        public const int CompileStatus = 0x8B81;
        public const int LinkStatus = 0x8B82;
        public const int Texture2D = 0x0DE1;
        public const int Texture0 = 0x84C0;
        public const int Rgba = 0x1908;
        public const int Rgba8 = 0x8058;
        public const int Rgba16f = 0x881A;
        public const int TextureMinFilter = 0x2801;
        public const int TextureMagFilter = 0x2800;
        public const int TextureWrapS = 0x2802;
        public const int TextureWrapT = 0x2803;
        public const int Linear = 0x2601;
        public const int LinearMipmapLinear = 0x2703;
        public const int Repeat = 0x2901;
        public const int ClampToEdge = 0x812F;
        public const int UnpackAlignment = 0x0CF5;
    }

    /// <summary>Typed delegate bindings over GetProcAddress for the exact entry points we use.</summary>
    private sealed class Gl
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EnableD(int cap);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearColorD(float r, float g, float b, float a);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearD(int mask);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ViewportD(int x, int y, int w, int h);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DepthFuncD(int func);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenD(int n, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteD(int n, ref uint id);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBufferD(int target, uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferDataD(int target, IntPtr size, IntPtr data, int usage);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferSubDataD(int target, IntPtr offset, IntPtr size, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindVertexArrayD(uint array);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateShaderD(int type);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ShaderSourceD(uint shader, int count, IntPtr[] strings, int[] lengths);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CompileShaderD(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetShaderivD(uint shader, int pname, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetInfoLogD(uint obj, int maxLength, out int length, byte[] log);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateProgramD();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttachShaderD(uint program, uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void LinkProgramD(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetProgramivD(uint program, int pname, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UseProgramD(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteShaderD(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteProgramD(uint program);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] private delegate int GetUniformLocationD(uint program, string name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform1iD(int location, int v);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform1fD(int location, float v);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform3fD(int location, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UniformMatrix4fvD(int location, int count, byte transpose, ref Matrix4x4 value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EnableVertexAttribArrayD(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VertexAttribPointerD(uint index, int size, int type, byte normalized, int stride, IntPtr offset);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DrawElementsD(int mode, int count, int type, IntPtr indices);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindTextureD(int target, uint texture);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TexImage2DD(int target, int level, int internalFormat, int w, int h, int border, int format, int type, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TexParameteriD(int target, int pname, int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenerateMipmapD(int target);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ActiveTextureD(int unit);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void PixelStoreiD(int pname, int value);

        private readonly EnableD _enable;
        private readonly EnableD _disable;
        private readonly ClearColorD _clearColor;
        private readonly ClearD _clear;
        private readonly ViewportD _viewport;
        private readonly DepthFuncD _depthFunc;
        private readonly GenD _genBuffers;
        private readonly DeleteD _deleteBuffers;
        private readonly BindBufferD _bindBuffer;
        private readonly BufferDataD _bufferData;
        private readonly BufferSubDataD _bufferSubData;
        private readonly GenD? _genVertexArrays;
        private readonly DeleteD? _deleteVertexArrays;
        private readonly BindVertexArrayD? _bindVertexArray;
        private readonly CreateShaderD _createShader;
        private readonly ShaderSourceD _shaderSource;
        private readonly CompileShaderD _compileShader;
        private readonly GetShaderivD _getShaderiv;
        private readonly GetInfoLogD _getShaderInfoLog;
        private readonly CreateProgramD _createProgram;
        private readonly AttachShaderD _attachShader;
        private readonly LinkProgramD _linkProgram;
        private readonly GetProgramivD _getProgramiv;
        private readonly GetInfoLogD _getProgramInfoLog;
        private readonly UseProgramD _useProgram;
        private readonly DeleteShaderD _deleteShader;
        private readonly DeleteProgramD _deleteProgram;
        private readonly GetUniformLocationD _getUniformLocation;
        private readonly Uniform1iD _uniform1i;
        private readonly Uniform1fD _uniform1f;
        private readonly Uniform3fD _uniform3f;
        private readonly UniformMatrix4fvD _uniformMatrix4fv;
        private readonly EnableVertexAttribArrayD _enableVertexAttribArray;
        private readonly VertexAttribPointerD _vertexAttribPointer;
        private readonly DrawElementsD _drawElements;
        private readonly GenD _genTextures;
        private readonly DeleteD _deleteTextures;
        private readonly BindTextureD _bindTexture;
        private readonly TexImage2DD _texImage2D;
        private readonly TexParameteriD _texParameteri;
        private readonly GenerateMipmapD _generateMipmap;
        private readonly ActiveTextureD _activeTexture;
        private readonly PixelStoreiD _pixelStorei;

        public bool HasVao { get; }

        public Gl(GlInterface gl)
        {
            _enable = Load<EnableD>(gl, "glEnable");
            _disable = Load<EnableD>(gl, "glDisable");
            _clearColor = Load<ClearColorD>(gl, "glClearColor");
            _clear = Load<ClearD>(gl, "glClear");
            _viewport = Load<ViewportD>(gl, "glViewport");
            _depthFunc = Load<DepthFuncD>(gl, "glDepthFunc");
            _genBuffers = Load<GenD>(gl, "glGenBuffers");
            _deleteBuffers = Load<DeleteD>(gl, "glDeleteBuffers");
            _bindBuffer = Load<BindBufferD>(gl, "glBindBuffer");
            _bufferData = Load<BufferDataD>(gl, "glBufferData");
            _bufferSubData = Load<BufferSubDataD>(gl, "glBufferSubData");
            _genVertexArrays = TryLoad<GenD>(gl, "glGenVertexArrays");
            _deleteVertexArrays = TryLoad<DeleteD>(gl, "glDeleteVertexArrays");
            _bindVertexArray = TryLoad<BindVertexArrayD>(gl, "glBindVertexArray");
            _createShader = Load<CreateShaderD>(gl, "glCreateShader");
            _shaderSource = Load<ShaderSourceD>(gl, "glShaderSource");
            _compileShader = Load<CompileShaderD>(gl, "glCompileShader");
            _getShaderiv = Load<GetShaderivD>(gl, "glGetShaderiv");
            _getShaderInfoLog = Load<GetInfoLogD>(gl, "glGetShaderInfoLog");
            _createProgram = Load<CreateProgramD>(gl, "glCreateProgram");
            _attachShader = Load<AttachShaderD>(gl, "glAttachShader");
            _linkProgram = Load<LinkProgramD>(gl, "glLinkProgram");
            _getProgramiv = Load<GetProgramivD>(gl, "glGetProgramiv");
            _getProgramInfoLog = Load<GetInfoLogD>(gl, "glGetProgramInfoLog");
            _useProgram = Load<UseProgramD>(gl, "glUseProgram");
            _deleteShader = Load<DeleteShaderD>(gl, "glDeleteShader");
            _deleteProgram = Load<DeleteProgramD>(gl, "glDeleteProgram");
            _getUniformLocation = Load<GetUniformLocationD>(gl, "glGetUniformLocation");
            _uniform1i = Load<Uniform1iD>(gl, "glUniform1i");
            _uniform1f = Load<Uniform1fD>(gl, "glUniform1f");
            _uniform3f = Load<Uniform3fD>(gl, "glUniform3f");
            _uniformMatrix4fv = Load<UniformMatrix4fvD>(gl, "glUniformMatrix4fv");
            _enableVertexAttribArray = Load<EnableVertexAttribArrayD>(gl, "glEnableVertexAttribArray");
            _vertexAttribPointer = Load<VertexAttribPointerD>(gl, "glVertexAttribPointer");
            _drawElements = Load<DrawElementsD>(gl, "glDrawElements");
            _genTextures = Load<GenD>(gl, "glGenTextures");
            _deleteTextures = Load<DeleteD>(gl, "glDeleteTextures");
            _bindTexture = Load<BindTextureD>(gl, "glBindTexture");
            _texImage2D = Load<TexImage2DD>(gl, "glTexImage2D");
            _texParameteri = Load<TexParameteriD>(gl, "glTexParameteri");
            _generateMipmap = Load<GenerateMipmapD>(gl, "glGenerateMipmap");
            _activeTexture = Load<ActiveTextureD>(gl, "glActiveTexture");
            _pixelStorei = Load<PixelStoreiD>(gl, "glPixelStorei");

            HasVao = _genVertexArrays is not null && _bindVertexArray is not null && _deleteVertexArrays is not null;
        }

        private static T Load<T>(GlInterface gl, string name) where T : Delegate =>
            TryLoad<T>(gl, name) ?? throw new InvalidOperationException($"Missing GL entry point {name}.");

        private static T? TryLoad<T>(GlInterface gl, string name) where T : Delegate
        {
            var ptr = gl.GetProcAddress(name);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        public void Enable(int cap) => _enable(cap);
        public void Disable(int cap) => _disable(cap);
        public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
        public void Clear(int mask) => _clear(mask);
        public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
        public void DepthFunc(int func) => _depthFunc(func);
        public void UseProgram(uint program) => _useProgram(program);
        public void DeleteShader(uint shader) => _deleteShader(shader);
        public void DeleteProgram(uint program) => _deleteProgram(program);
        public int GetUniformLocation(uint program, string name) => _getUniformLocation(program, name);
        public void Uniform1(int location, int v) => _uniform1i(location, v);
        public void Uniform1(int location, float v) => _uniform1f(location, v);
        public void Uniform3(int location, float x, float y, float z) => _uniform3f(location, x, y, z);
        public void UniformMatrix4(int location, ref Matrix4x4 value) => _uniformMatrix4fv(location, 1, 0, ref value);
        public void EnableVertexAttribArray(uint index) => _enableVertexAttribArray(index);
        public void VertexAttribPointer(uint index, int size, int type, bool normalized, int stride, IntPtr offset) =>
            _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, offset);
        public void DrawElements(int mode, int count, int type, IntPtr indices) => _drawElements(mode, count, type, indices);
        public void ActiveTexture(int unit) => _activeTexture(unit);
        public void BindTexture(int target, uint texture) => _bindTexture(target, texture);
        public void TexParameter(int target, int pname, int value) => _texParameteri(target, pname, value);
        public void GenerateMipmap(int target) => _generateMipmap(target);
        public void PixelStore(int pname, int value) => _pixelStorei(pname, value);

        public uint GenBuffer() { _genBuffers(1, out var id); return id; }
        public void DeleteBuffer(uint id) => _deleteBuffers(1, ref id);
        public void BindBuffer(int target, uint buffer) => _bindBuffer(target, buffer);
        public uint GenTexture() { _genTextures(1, out var id); return id; }
        public void DeleteTexture(uint id) => _deleteTextures(1, ref id);
        public uint GenVertexArray() { _genVertexArrays!(1, out var id); return id; }
        public void DeleteVertexArray(uint id) => _deleteVertexArrays!(1, ref id);
        public void BindVertexArray(uint array) => _bindVertexArray!(array);

        public void BufferData(int target, float[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _bufferData(target, (IntPtr)(data.Length * sizeof(float)), handle.AddrOfPinnedObject(), GlConsts.StaticDraw);
            }
            finally
            {
                handle.Free();
            }
        }

        public void BufferSubData(int target, float[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _bufferSubData(target, IntPtr.Zero, (IntPtr)(data.Length * sizeof(float)), handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public void BufferData(int target, uint[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _bufferData(target, (IntPtr)(data.Length * sizeof(uint)), handle.AddrOfPinnedObject(), GlConsts.StaticDraw);
            }
            finally
            {
                handle.Free();
            }
        }

        public void TexImage2D(int target, int level, int internalFormat, int w, int h, int format, int type, byte[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _texImage2D(target, level, internalFormat, w, h, 0, format, type, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>Float-data upload path (RGBA16F environment map).</summary>
        public void TexImage2D(int target, int level, int internalFormat, int w, int h, int format, int type, float[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _texImage2D(target, level, internalFormat, w, h, 0, format, type, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public uint CompileShader(int type, string source)
        {
            var shader = _createShader(type);
            var bytes = Encoding.UTF8.GetBytes(source);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                Marshal.WriteByte(ptr, bytes.Length, 0);
                _shaderSource(shader, 1, new[] { ptr }, new[] { bytes.Length });
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            _compileShader(shader);
            _getShaderiv(shader, GlConsts.CompileStatus, out var ok);
            if (ok == 0)
            {
                var log = new byte[4096];
                _getShaderInfoLog(shader, log.Length, out var len, log);
                _deleteShader(shader);
                throw new InvalidOperationException(
                    $"Shader compile failed: {Encoding.UTF8.GetString(log, 0, Math.Max(0, len))}");
            }
            return shader;
        }

        public uint LinkProgram(uint vs, uint fs)
        {
            var program = _createProgram();
            _attachShader(program, vs);
            _attachShader(program, fs);
            _linkProgram(program);
            _getProgramiv(program, GlConsts.LinkStatus, out var ok);
            if (ok == 0)
            {
                var log = new byte[4096];
                _getProgramInfoLog(program, log.Length, out var len, log);
                _deleteProgram(program);
                throw new InvalidOperationException(
                    $"Program link failed: {Encoding.UTF8.GetString(log, 0, Math.Max(0, len))}");
            }
            return program;
        }
    }
}
