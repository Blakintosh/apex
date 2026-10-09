using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Apex.Render.Resources;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Apex.Render.Shaders;

/// <summary>
/// Fills a constant buffer through its reflected layout: set variables by name with typed values; the bytes
/// land at the compiler's offsets (structs, arrays, row/column-major matrices handled from reflection).
/// Starts zero-filled, like APE's <c>Material_BuildDefaultConstantBuffer</c>.
/// Matrices use the row-vector convention of <see cref="Matrix4x4"/> (HLSL <c>m[r][c] = M(r+1, c+1)</c>),
/// which is what ToolsGfx's row_major shaders expect.
/// </summary>
public sealed class ConstantBufferWriter
{
    private readonly byte[] _data;

    public CBufferLayout Layout { get; }
    public bool IsDirty { get; private set; } = true;
    public ReadOnlySpan<byte> Data => _data;

    public ConstantBufferWriter(CBufferLayout layout)
    {
        Layout = layout;
        _data = new byte[(layout.Size + 15) & ~15];
    }

    public void Clear()
    {
        Array.Clear(_data);
        IsDirty = true;
    }

    public bool Has(string path) => Layout.TryResolve(path, out _);

    // ── Typed setters ───────────────────────────────────────────────────────

    public void Set(string path, float value) => WriteFloats(Layout.Resolve(path), stackalloc float[] { value });
    public void Set(string path, Vector2 value) => WriteFloats(Layout.Resolve(path), stackalloc float[] { value.X, value.Y });
    public void Set(string path, Vector3 value) => WriteFloats(Layout.Resolve(path), stackalloc float[] { value.X, value.Y, value.Z });
    public void Set(string path, Vector4 value) => WriteFloats(Layout.Resolve(path), stackalloc float[] { value.X, value.Y, value.Z, value.W });
    public void Set(string path, ReadOnlySpan<float> values) => WriteFloats(Layout.Resolve(path), values);
    public void Set(string path, int value) => WriteInts(Layout.Resolve(path), stackalloc int[] { value });
    public void Set(string path, uint value) => WriteInts(Layout.Resolve(path), stackalloc int[] { unchecked((int)value) });
    public void Set(string path, bool value) => WriteInts(Layout.Resolve(path), stackalloc int[] { value ? 1 : 0 });
    public void Set(string path, ReadOnlySpan<int> values) => WriteInts(Layout.Resolve(path), values);
    public void Set(string path, ReadOnlySpan<uint> values) => WriteInts(Layout.Resolve(path), MemoryMarshal.Cast<uint, int>(values));

    public void Set(string path, in Matrix4x4 m)
    {
        var r = Layout.Resolve(path);
        var f = r.Field;
        if (!f.IsMatrix)
            throw new InvalidOperationException($"'{path}' is {f.TypeName}, not a matrix");
        var span = _data.AsSpan();
        for (int row = 0; row < f.Rows; row++)
        {
            for (int col = 0; col < f.Columns; col++)
            {
                // Row-major: one register per row. Column-major: one register per column.
                int at = f.Class == ShaderVariableClass.MatrixRows ? r.Offset + row * 16 + col * 4 : r.Offset + col * 16 + row * 4;
                BinaryPrimitives.WriteSingleLittleEndian(span[at..], Element(m, row, col));
            }
        }
        IsDirty = true;
    }

    /// <summary>Sets a whole array (or struct / array element) from raw bytes laid out with the reflected stride.</summary>
    public void SetRaw(string path, ReadOnlySpan<byte> bytes)
    {
        var r = Layout.Resolve(path);
        int size = r.Count > 1 ? r.Field.Size : r.Field.ElementSize;
        if (bytes.Length > size)
            throw new ArgumentException($"{bytes.Length} bytes exceed '{path}' ({size} bytes)");
        bytes.CopyTo(_data.AsSpan(r.Offset));
        IsDirty = true;
    }

    /// <summary>
    /// APE's material rule (materials.md §3.1): the evaluated float components are converted by the variable's
    /// reflected type — BOOL → 0/1, INT/UINT → (int)(v + 0.5), FLOAT → the first rows×cols components.
    /// </summary>
    public void SetFromFloats(string path, ReadOnlySpan<float> values)
    {
        var r = Layout.Resolve(path);
        switch (r.Field.Type)
        {
            case ShaderVariableType.Bool:
            {
                Span<int> ints = stackalloc int[Math.Min(values.Length, 16)];
                for (int i = 0; i < ints.Length; i++) ints[i] = values[i] != 0 ? 1 : 0;
                WriteInts(r, ints);
                break;
            }
            case ShaderVariableType.Int or ShaderVariableType.UInt:
            {
                Span<int> ints = stackalloc int[Math.Min(values.Length, 16)];
                for (int i = 0; i < ints.Length; i++) ints[i] = (int)(values[i] + 0.5f);
                WriteInts(r, ints);
                break;
            }
            default:
                WriteFloats(r, values[..Math.Min(values.Length, r.Field.ComponentCount)]);
                break;
        }
    }

    /// <summary>Copies a typed engine struct (e.g. <c>CodeSceneConsts</c>) over the whole buffer.</summary>
    public void CopyFrom<T>(in T value) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));
        bytes[..Math.Min(bytes.Length, _data.Length)].CopyTo(_data);
        IsDirty = true;
    }

    /// <summary>Uploads to a dynamic buffer when changed (or always when <paramref name="force"/>).</summary>
    public void Upload(ID3D11DeviceContext context, GpuBuffer buffer, bool force = false)
    {
        if (!IsDirty && !force)
            return;
        buffer.Update(context, _data);
        IsDirty = false;
    }

    // ── Component writers ───────────────────────────────────────────────────

    private void WriteFloats(FieldRef r, ReadOnlySpan<float> values)
    {
        var f = r.Field;
        if (f.IsStruct)
            throw new InvalidOperationException($"'{f.Name}' is a struct ({f.TypeName}); set its members");
        if (f.Type is not ShaderVariableType.Float)
        {
            // Allow float → int/uint/bool only through the explicit material rule.
            throw new InvalidOperationException($"'{f.Name}' is {f.TypeName}; use an integer setter or SetFromFloats");
        }
        WriteComponents(r, MemoryMarshal.AsBytes(values), values.Length);
    }

    private void WriteInts(FieldRef r, ReadOnlySpan<int> values)
    {
        var f = r.Field;
        if (f.IsStruct)
            throw new InvalidOperationException($"'{f.Name}' is a struct ({f.TypeName}); set its members");
        if (f.Type is not (ShaderVariableType.Int or ShaderVariableType.UInt or ShaderVariableType.Bool))
            throw new InvalidOperationException($"'{f.Name}' is {f.TypeName}; use a float setter");
        WriteComponents(r, MemoryMarshal.AsBytes(values), values.Length);
    }

    /// <summary>Writes scalar/vector components; for arrays of scalars/vectors the values spill into
    /// successive elements (each on its own 16-byte register in a cbuffer).</summary>
    private void WriteComponents(FieldRef r, ReadOnlySpan<byte> src, int count)
    {
        var f = r.Field;
        int perElement = f.IsMatrix ? f.ComponentCount : Math.Max(1, f.Columns * f.Rows);
        int capacity = perElement * r.Count;
        if (count > capacity)
            throw new ArgumentException($"{count} components exceed '{f.Name}' ({f.TypeName}, {capacity} components)");
        if (f.IsMatrix)
            throw new InvalidOperationException($"'{f.Name}' is a matrix; use Set(path, Matrix4x4)");

        var dst = _data.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int element = i / perElement, comp = i % perElement;
            int at = r.Offset + element * f.ElementStride + comp * 4;
            src.Slice(i * 4, 4).CopyTo(dst[at..]);
        }
        IsDirty = true;
    }

    private static float Element(in Matrix4x4 m, int row, int col) => m[row, col];
}
