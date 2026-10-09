namespace Apex.Render.Data;

/// <summary>
/// Locates the ToolsGfx data APE (asseteditor_modtools.exe) reads from a Black Ops III install:
/// the converted asset caches under <c>share\assetconvert\ToolsGfx</c> and the ToolsGfx techsetdefs
/// under <c>share\raw\techsetdefs_stable_toolsgfx</c>. Everything is read-only; nothing from the game
/// is ever copied into Apex.
///
/// Root resolution (same order as the editor's GameEnvironment):
///   1. an explicit root passed to <see cref="FromRoot"/>,
///   2. the <c>APEX_BO3_ROOT</c> environment variable,
///   3. <c>TA_TOOLS_PATH</c> (the root or one of its subdirectories),
///   4. the default Steam install path.
/// </summary>
public sealed class ToolsGfxInstall
{
    /// <summary>Default Steam install location used when no environment override is set.</summary>
    public const string DefaultRoot = @"I:\SteamLibrary\steamapps\common\Call of Duty Black Ops III";

    /// <summary>Shader cache version directory (APE hard-codes v14).</summary>
    public const int ShaderCacheVersion = 14;

    /// <summary>D3DCompile flags the cache was built with (<c>D3DCOMPILE_PACK_MATRIX_ROW_MAJOR</c> = 8 -> <c>f8</c>).</summary>
    public const int ShaderCacheFlags = 8;

    public const int ImageCacheVersion = 20;
    public const int XMeshCacheVersion = 39;
    public const int XAnimCacheVersion = 11;
    public const int XBinCacheVersion = 1;

    /// <summary>The Black Ops III install root.</summary>
    public string Root { get; }

    /// <summary><c>share\assetconvert\ToolsGfx</c>.</summary>
    public string ToolsGfxDir { get; }

    /// <summary><c>ToolsGfx\shaders_modtools\v14\f8</c>: one folder per HLSL source file.</summary>
    public string ShaderCacheDir { get; }

    /// <summary><c>ToolsGfx\images\v20</c>: one folder per image settings string.</summary>
    public string ImageCacheDir { get; }

    /// <summary><c>ToolsGfx\xmeshes\v39</c>: one folder per xmodel_bin stem.</summary>
    public string XMeshCacheDir { get; }

    /// <summary><c>ToolsGfx\xanims\v11</c>: one folder per xanim.</summary>
    public string XAnimCacheDir { get; }

    /// <summary><c>ToolsGfx\xbins\v1</c>: one folder per xmodel_bin stem (material lists).</summary>
    public string XBinCacheDir { get; }

    /// <summary><c>share\raw\techsetdefs_stable_toolsgfx</c>.</summary>
    public string TechsetdefDir { get; }

    /// <summary><c>model_export</c>: where GDT xmodel <c>filename</c> values are rooted.</summary>
    public string ModelExportDir { get; }

    /// <summary><c>xanim_export</c>: where GDT xanim <c>filename</c> values are rooted.</summary>
    public string XAnimExportDir { get; }

    /// <summary><c>texture_assets</c>.</summary>
    public string TextureAssetsDir { get; }

    /// <summary>True when the shader cache and the techsetdef directory both exist.</summary>
    public bool IsAvailable => Directory.Exists(ShaderCacheDir) && Directory.Exists(TechsetdefDir);

    private ToolsGfxInstall(string root)
    {
        Root = Path.GetFullPath(root);
        ToolsGfxDir = Path.Combine(Root, "share", "assetconvert", "ToolsGfx");
        ShaderCacheDir = Path.Combine(ToolsGfxDir, "shaders_modtools", $"v{ShaderCacheVersion}", $"f{ShaderCacheFlags:x}");
        ImageCacheDir = Path.Combine(ToolsGfxDir, "images", $"v{ImageCacheVersion}");
        XMeshCacheDir = Path.Combine(ToolsGfxDir, "xmeshes", $"v{XMeshCacheVersion}");
        XAnimCacheDir = Path.Combine(ToolsGfxDir, "xanims", $"v{XAnimCacheVersion}");
        XBinCacheDir = Path.Combine(ToolsGfxDir, "xbins", $"v{XBinCacheVersion}");
        TechsetdefDir = Path.Combine(Root, "share", "raw", "techsetdefs_stable_toolsgfx");
        ModelExportDir = Path.Combine(Root, "model_export");
        XAnimExportDir = Path.Combine(Root, "xanim_export");
        TextureAssetsDir = Path.Combine(Root, "texture_assets");
    }

    /// <summary>Uses <paramref name="root"/> as the install root (no probing).</summary>
    public static ToolsGfxInstall FromRoot(string root) => new(root);

    /// <summary>
    /// Resolves the install from the environment (see class remarks). Returns null when no candidate
    /// directory exists; check <see cref="IsAvailable"/> on the result before using the caches.
    /// </summary>
    public static ToolsGfxInstall? Locate()
    {
        var root = ResolveRoot();
        return root is null ? null : new ToolsGfxInstall(root);
    }

    private static string? ResolveRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("APEX_BO3_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return explicitRoot;

        var toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
        if (!string.IsNullOrWhiteSpace(toolsPath))
        {
            var dir = toolsPath.Trim().TrimEnd('\\', '/');
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "share", "assetconvert")))
                    return dir;
                dir = Path.GetDirectoryName(dir) ?? string.Empty;
            }
        }

        return Directory.Exists(DefaultRoot) ? DefaultRoot : null;
    }

    public override string ToString() => Root;
}
