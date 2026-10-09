using Apex.Render.Device;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Presentation;

/// <summary>
/// What a <see cref="D3DViewport"/> draws. All calls arrive on the UI thread. Device resources must be created in
/// <see cref="OnDeviceCreated"/> and released in <see cref="OnDeviceDestroyed"/>: after a device loss the viewport
/// destroys and recreates the device and calls both again.
/// </summary>
public interface ID3DViewportRenderer
{
    void OnDeviceCreated(GfxDevice device);

    void OnDeviceDestroyed();

    /// <summary>Draw the frame into <see cref="D3DFrame.Target"/> (8-bit UNORM, already display-encoded:
    /// ToolsGfx's tonemap writes sRGB values itself, so the surface is not an _SRGB view).</summary>
    void Render(in D3DFrame frame);
}

/// <summary>One frame's target: a shared swap image owned by the viewport.</summary>
public readonly record struct D3DFrame(
    GfxDevice Device,
    ID3D11Texture2D Texture,
    ID3D11RenderTargetView Target,
    Format Format,
    int Width,
    int Height,
    double RenderScaling,
    TimeSpan Time);

public enum D3DSurfaceFormat
{
    Bgra8,
    Rgba8,
}
