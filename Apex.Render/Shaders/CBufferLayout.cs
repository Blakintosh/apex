using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace Apex.Render.Shaders;

/// <summary>
/// One reflected variable or struct member. <see cref="Offset"/> is absolute within the buffer (for arrays:
/// element 0; element <c>i</c> lives at <c>Offset + i * ElementStride</c>). Packing is exactly what the
/// compiler reflected: cbuffer arrays/structs/matrix rows start on 16-byte registers, structured buffer
/// elements are tightly packed.
/// </summary>
public sealed class CBufferField
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required int Offset { get; init; }
    public required ShaderVariableClass Class { get; init; }
    public required ShaderVariableType Type { get; init; }
    public required int Rows { get; init; }
    public required int Columns { get; init; }
    /// <summary>Array length; 0 for a non-array.</summary>
    public required int Elements { get; init; }
    /// <summary>Unpadded byte size of one element.</summary>
    public required int ElementSize { get; init; }
    /// <summary>Byte distance between array elements (cbuffer: rounded up to 16).</summary>
    public required int ElementStride { get; init; }
    /// <summary>Size of the whole variable (all elements, last one unpadded).</summary>
    public int Size => Elements > 0 ? (Elements - 1) * ElementStride + ElementSize : ElementSize;
    /// <summary>D3D_SVF_USED for top-level variables (members inherit their parent's flag).</summary>
    public required bool Used { get; init; }
    public required IReadOnlyList<CBufferField> Members { get; init; }

    public bool IsArray => Elements > 0;
    public bool IsStruct => Class == ShaderVariableClass.Struct;
    public bool IsMatrix => Class is ShaderVariableClass.MatrixRows or ShaderVariableClass.MatrixColumns;
    public int ComponentCount => Rows * Columns;

    public CBufferField? FindMember(string name)
    {
        foreach (var m in Members)
            if (m.Name == name)
                return m;
        return null;
    }

    public override string ToString() => $"{TypeName}{(IsArray ? $"[{Elements}]" : "")} {Name} @{Offset}";
}

/// <summary>A reflected constant buffer (or the element layout of a structured buffer).</summary>
public sealed class CBufferLayout
{
    public required string Name { get; init; }
    public required int Size { get; init; }
    public required ConstantBufferType Kind { get; init; }
    /// <summary>Register the buffer is bound at (-1 for structured-buffer element layouts).</summary>
    public required int BindPoint { get; init; }
    public required IReadOnlyList<CBufferField> Variables { get; init; }

    public bool IsConstantBuffer => Kind == ConstantBufferType.ConstantBuffer;

    /// <summary>Every variable path (depth-first, "gScene.sun.wldDir", arrays as "name[]"), for diagnostics.</summary>
    public IEnumerable<(string Path, CBufferField Field)> Walk()
    {
        foreach (var v in Variables)
            foreach (var x in Walk(v.Name, v))
                yield return x;
    }

    private static IEnumerable<(string, CBufferField)> Walk(string path, CBufferField f)
    {
        yield return (path, f);
        foreach (var m in f.Members)
            foreach (var x in Walk(path + (f.IsArray ? "[]." : ".") + m.Name, m))
                yield return x;
    }

    /// <summary>
    /// Resolves "a.b[3].c" to an absolute byte offset + field. If the path does not start with a top-level
    /// variable and the buffer has exactly one struct variable (all engine buffers: gScene, gObject, gPostFx…),
    /// the path is resolved inside it, so "sun.wldDir" works as well as "gScene.sun.wldDir".
    /// </summary>
    public bool TryResolve(string path, out FieldRef result)
    {
        result = default;
        var parts = ParsePath(path);
        if (parts == null || parts.Count == 0)
            return false;

        CBufferField? field = Find(Variables, parts[0].Name);
        if (field == null && Variables.Count == 1 && Variables[0].IsStruct)
            field = Variables[0].FindMember(parts[0].Name);
        if (field == null)
            return false;

        // Member offsets are absolute for element 0 of every enclosing array; array indices accumulate here.
        int arrayOffset = 0;
        for (int i = 0; ; i++)
        {
            int index = parts[i].Index;
            if (index >= 0)
            {
                if (!field.IsArray || index >= field.Elements)
                    return false;
                arrayOffset += index * field.ElementStride;
            }
            if (i == parts.Count - 1)
            {
                result = new FieldRef(field, field.Offset + arrayOffset, index >= 0 || !field.IsArray ? 1 : field.Elements);
                return true;
            }
            var next = field.FindMember(parts[i + 1].Name);
            if (next == null)
                return false;
            // Member offsets are absolute for element 0 of their parent; keep the accumulated array offset.
            field = next;
        }
    }

    public FieldRef Resolve(string path)
        => TryResolve(path, out var r) ? r : throw new KeyNotFoundException($"cbuffer '{Name}' has no variable '{path}'");

    private static CBufferField? Find(IReadOnlyList<CBufferField> list, string name)
    {
        foreach (var f in list)
            if (f.Name == name)
                return f;
        return null;
    }

    private static List<(string Name, int Index)>? ParsePath(string path)
    {
        var parts = new List<(string, int)>();
        foreach (var seg in path.Split('.'))
        {
            int lb = seg.IndexOf('[');
            if (lb < 0)
            {
                parts.Add((seg, -1));
                continue;
            }
            if (!seg.EndsWith(']') || !int.TryParse(seg.AsSpan(lb + 1, seg.Length - lb - 2), out int idx) || idx < 0)
                return null;
            parts.Add((seg[..lb], idx));
        }
        return parts;
    }

    internal static CBufferLayout FromReflection(ID3D11ShaderReflectionConstantBuffer cb, IReadOnlyList<ResourceBinding> resources)
    {
        var desc = cb.Description;
        bool tight = desc.Type != ConstantBufferType.ConstantBuffer && desc.Type != ConstantBufferType.TextureBuffer;
        var vars = new List<CBufferField>((int)desc.VariableCount);
        for (uint i = 0; i < desc.VariableCount; i++)
        {
            var v = cb.GetVariableByIndex(i);
            var vd = v.Description;
            bool used = (vd.Flags & ShaderVariableFlags.Used) != 0;
            vars.Add(BuildField(vd.Name, v.VariableType, (int)vd.StartOffset, used, tight));
        }

        int bind = -1;
        if (!tight)
        {
            foreach (var r in resources)
            {
                if (r.Name == desc.Name && r.Table == BindingTable.ConstantBuffer)
                {
                    bind = r.BindPoint;
                    break;
                }
            }
        }
        return new CBufferLayout { Name = desc.Name, Size = (int)desc.Size, Kind = desc.Type, BindPoint = bind, Variables = vars };
    }

    private static CBufferField BuildField(string name, ID3D11ShaderReflectionType type, int offset, bool used, bool tight)
    {
        var td = type.Description;
        var members = new List<CBufferField>((int)td.MemberCount);
        for (uint m = 0; m < td.MemberCount; m++)
        {
            var mt = type.GetMemberTypeByIndex(m);
            members.Add(BuildField(type.GetMemberTypeName(m), mt, offset + (int)mt.Description.Offset, used, tight));
        }

        int rows = (int)td.RowCount, cols = (int)td.ColumnCount;
        int elementSize = td.Class switch
        {
            ShaderVariableClass.Struct => StructSize(members, offset),
            ShaderVariableClass.MatrixRows => tight ? rows * cols * 4 : (rows - 1) * 16 + cols * 4,
            ShaderVariableClass.MatrixColumns => tight ? rows * cols * 4 : (cols - 1) * 16 + rows * 4,
            _ => Math.Max(1, cols) * Math.Max(1, rows) * 4,
        };
        int stride = tight ? elementSize : (elementSize + 15) & ~15;

        return new CBufferField
        {
            Name = name,
            TypeName = td.Name ?? td.Type.ToString(),
            Offset = offset,
            Class = td.Class,
            Type = td.Type,
            Rows = rows,
            Columns = cols,
            Elements = (int)td.ElementCount,
            ElementSize = elementSize,
            ElementStride = stride,
            Used = used,
            Members = members,
        };
    }

    private static int StructSize(List<CBufferField> members, int structOffset)
    {
        int end = 0;
        foreach (var m in members)
            end = Math.Max(end, m.Offset - structOffset + m.Size);
        return end;
    }
}

/// <summary>A resolved variable location: the field plus its absolute byte offset (array index applied).</summary>
public readonly record struct FieldRef(CBufferField Field, int Offset, int Count);
