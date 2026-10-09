using System.Security.Cryptography;
using Apex.Render.Device;

namespace Apex.Render.Shaders;

/// <summary>
/// Per-device cache of created shader variants. Keys are whatever identifies a variant to the caller —
/// normally the cache-relative blob name (<c>"tonemap_lut.hlsl/ps_main_ps_5_0_1d96c0ec…"</c>) or a
/// (techset, prefix, technique, stage) tuple the data layer resolves. Identical bytecode reached through
/// different keys (4,719 deps files map to 3,938 blobs) is created once.
/// </summary>
public sealed class ShaderCache : IDisposable
{
    private readonly GfxDevice _gfx;
    private readonly Dictionary<string, ShaderProgram> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderProgram> _byHash = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ShaderCache(GfxDevice gfx) => _gfx = gfx;

    public int Count
    {
        get { lock (_lock) return _byHash.Count; }
    }

    public bool TryGet(string key, out ShaderProgram program)
    {
        lock (_lock)
            return _byKey.TryGetValue(key, out program!);
    }

    /// <summary>Returns the cached variant for <paramref name="key"/>, loading and creating it on first use.
    /// <paramref name="loadBytecode"/> returns the raw DXBC (the data layer's shader-cache reader).</summary>
    public ShaderProgram GetOrCreate(string key, Func<ReadOnlyMemory<byte>> loadBytecode, ShaderStage? expected = null)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var existing))
                return existing;
        }
        return Add(key, loadBytecode(), expected);
    }

    /// <summary>Registers bytecode under <paramref name="key"/> (deduplicated by content).</summary>
    public ShaderProgram Add(string key, ReadOnlyMemory<byte> dxbc, ShaderStage? expected = null)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(dxbc.Span));
        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var existing))
                return existing;
            if (!_byHash.TryGetValue(hash, out var program))
            {
                program = ShaderProgram.Create(_gfx, dxbc, hash, key, expected);
                _byHash[hash] = program;
            }
            else if (expected is { } e && program.Stage != e)
            {
                throw new InvalidDataException($"{key}: expected a {e} shader, bytecode is {program.Stage}");
            }
            _byKey[key] = program;
            return program;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _byHash.Values)
                p.Dispose();
            _byHash.Clear();
            _byKey.Clear();
        }
    }
}
