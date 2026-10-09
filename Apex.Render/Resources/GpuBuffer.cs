using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apex.Render.Device;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Resources;

/// <summary>D3D11 buffer wrapper: constant buffers (dynamic, map-discard), structured buffers (SRV/UAV),
/// vertex and index buffers.</summary>
public sealed class GpuBuffer : IDisposable
{
    public ID3D11Buffer Buffer { get; }
    public ID3D11ShaderResourceView? Srv { get; }
    public ID3D11UnorderedAccessView? Uav { get; }
    public int SizeInBytes { get; }
    public int Stride { get; }
    public bool IsDynamic { get; }

    private GpuBuffer(ID3D11Buffer buffer, int size, int stride, bool dynamic, ID3D11ShaderResourceView? srv, ID3D11UnorderedAccessView? uav)
    {
        Buffer = buffer;
        SizeInBytes = size;
        Stride = stride;
        IsDynamic = dynamic;
        Srv = srv;
        Uav = uav;
    }

    /// <summary>Dynamic constant buffer of <paramref name="size"/> bytes (rounded up to 16).</summary>
    public static GpuBuffer CreateConstant(GfxDevice gfx, int size, string? debugName = null)
    {
        int aligned = (size + 15) & ~15;
        var buffer = gfx.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)aligned,
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        GpuTexture.SetName(buffer, debugName);
        return new GpuBuffer(buffer, aligned, 0, true, null, null);
    }

    /// <summary>Immutable constant buffer holding <paramref name="data"/> (zero-padded to a multiple of 16 bytes).</summary>
    public static GpuBuffer CreateImmutableConstant(GfxDevice gfx, ReadOnlySpan<byte> data, string? debugName = null)
    {
        int aligned = (data.Length + 15) & ~15;
        var init = new byte[aligned];
        data.CopyTo(init);
        var buffer = Create(gfx, init, new BufferDescription
        {
            ByteWidth = (uint)aligned,
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Immutable,
        });
        GpuTexture.SetName(buffer, debugName);
        return new GpuBuffer(buffer, aligned, 0, false, null, null);
    }

    /// <summary>Immutable (or UAV-capable) structured buffer with an SRV over the first <paramref name="viewBytes"/> bytes.</summary>
    public static GpuBuffer CreateStructured(GfxDevice gfx, ReadOnlySpan<byte> data, int stride, bool uav = false,
        int? viewBytes = null, string? debugName = null)
    {
        int size = Math.Max(stride, data.Length);
        size = (size + stride - 1) / stride * stride;
        // Padded to whole elements (zeros) when the data is not.
        if (data.Length != size)
        {
            var init = new byte[size];
            data.CopyTo(init);
            data = init;
        }
        var bind = BindFlags.ShaderResource | (uav ? BindFlags.UnorderedAccess : BindFlags.None);
        var buffer = Create(gfx, data, new BufferDescription
        {
            ByteWidth = (uint)size,
            BindFlags = bind,
            Usage = uav ? ResourceUsage.Default : ResourceUsage.Immutable,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)stride,
        });
        GpuTexture.SetName(buffer, debugName);
        uint elements = (uint)((viewBytes ?? size) / stride);
        var srv = gfx.Device.CreateShaderResourceView(buffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = elements },
        });
        ID3D11UnorderedAccessView? u = null;
        if (uav)
        {
            u = gfx.Device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = elements },
            });
        }
        return new GpuBuffer(buffer, size, stride, false, srv, u);
    }

    /// <summary>CPU-writable (map-discard) structured buffer of <paramref name="elements"/> × <paramref name="stride"/>
    /// bytes with a full-range SRV — per-pass code data such as <c>gObjectInstanceData</c> (t30).</summary>
    public static GpuBuffer CreateDynamicStructured(GfxDevice gfx, int stride, int elements, string? debugName = null)
    {
        int size = stride * elements;
        var buffer = gfx.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)size,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)stride,
        });
        GpuTexture.SetName(buffer, debugName);
        var srv = gfx.Device.CreateShaderResourceView(buffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)elements },
        });
        return new GpuBuffer(buffer, size, stride, true, srv, null);
    }

    public static GpuBuffer CreateVertex<T>(GfxDevice gfx, ReadOnlySpan<T> data, string? debugName = null) where T : unmanaged
        => CreateImmutable(gfx, MemoryMarshal.AsBytes(data), BindFlags.VertexBuffer, Unsafe.SizeOf<T>(), debugName);

    /// <summary>Immutable vertex buffer from raw bytes (e.g. a packed 44-byte generic vertex stream).</summary>
    public static GpuBuffer CreateVertexRaw(GfxDevice gfx, ReadOnlySpan<byte> data, int stride, string? debugName = null)
        => CreateImmutable(gfx, data, BindFlags.VertexBuffer, stride, debugName);

    /// <summary>Immutable index buffer from raw bytes (<paramref name="indexSize"/> 2 or 4).</summary>
    public static GpuBuffer CreateIndexRaw(GfxDevice gfx, ReadOnlySpan<byte> data, int indexSize, string? debugName = null)
        => CreateImmutable(gfx, data, BindFlags.IndexBuffer, indexSize, debugName);

    public static GpuBuffer CreateIndex(GfxDevice gfx, ReadOnlySpan<ushort> data, string? debugName = null)
        => CreateImmutable(gfx, MemoryMarshal.AsBytes(data), BindFlags.IndexBuffer, 2, debugName);

    public static GpuBuffer CreateIndex(GfxDevice gfx, ReadOnlySpan<uint> data, string? debugName = null)
        => CreateImmutable(gfx, MemoryMarshal.AsBytes(data), BindFlags.IndexBuffer, 4, debugName);

    private static GpuBuffer CreateImmutable(GfxDevice gfx, ReadOnlySpan<byte> data, BindFlags bind, int stride, string? debugName)
    {
        var buffer = Create(gfx, data, new BufferDescription
        {
            ByteWidth = (uint)data.Length,
            BindFlags = bind,
            Usage = ResourceUsage.Immutable,
        });
        GpuTexture.SetName(buffer, debugName);
        return new GpuBuffer(buffer, data.Length, stride, false, null, null);
    }

    /// <summary>Creates a buffer initialised straight from <paramref name="data"/> (no managed copy).</summary>
    private static unsafe ID3D11Buffer Create(GfxDevice gfx, ReadOnlySpan<byte> data, in BufferDescription desc)
    {
        fixed (byte* p = data)
            return gfx.Device.CreateBuffer(desc, new SubresourceData((IntPtr)p));
    }

    /// <summary>Map-discard upload into a dynamic buffer; <paramref name="data"/> may be shorter than the buffer.</summary>
    public unsafe void Update(ID3D11DeviceContext context, ReadOnlySpan<byte> data)
    {
        if (!IsDynamic)
            throw new InvalidOperationException("Update requires a dynamic buffer.");
        if (data.Length > SizeInBytes)
            throw new ArgumentException($"{data.Length} bytes do not fit a {SizeInBytes}-byte buffer.");
        var mapped = context.Map(Buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            data.CopyTo(new Span<byte>((void*)mapped.DataPointer, SizeInBytes));
        }
        finally
        {
            context.Unmap(Buffer, 0);
        }
    }

    public void Update<T>(ID3D11DeviceContext context, in T value) where T : unmanaged
        => Update(context, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>Map-discard upload of <paramref name="data"/> at byte <paramref name="offset"/>; every other byte of the
    /// buffer is written as zero.</summary>
    public unsafe void UpdateZeroPadded(ID3D11DeviceContext context, ReadOnlySpan<byte> data, int offset)
    {
        if (!IsDynamic)
            throw new InvalidOperationException("Update requires a dynamic buffer.");
        if (offset < 0 || offset + data.Length > SizeInBytes)
            throw new ArgumentException($"{data.Length} bytes at {offset} do not fit a {SizeInBytes}-byte buffer.");
        var mapped = context.Map(Buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = new Span<byte>((void*)mapped.DataPointer, SizeInBytes);
            dst[..offset].Clear();
            data.CopyTo(dst[offset..]);
            dst[(offset + data.Length)..].Clear();
        }
        finally
        {
            context.Unmap(Buffer, 0);
        }
    }

    public void Dispose()
    {
        Srv?.Dispose();
        Uav?.Dispose();
        Buffer.Dispose();
    }
}
