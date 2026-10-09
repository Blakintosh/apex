using System.Runtime.InteropServices;
using Apex.Render.Constants;
using Apex.Render.Device;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.DXGI;

namespace Apex.Render.Passes;

/// <summary>
/// Device-bound services every preview pass shares: state/layout/shader caches, the fullscreen quad, the
/// window-sized render-target table, the engine constant buffers (b9 scene, b10 object, b11 bones, b13 post-FX;
/// the HemiAO pass owns its b12s), the per-pass instance buffer (t30) and ToolsGfx's default code textures.
/// </summary>
public sealed class RenderContext : IDisposable
{
    public GfxDevice Gfx { get; }
    public StateFactory States { get; }
    public InputLayoutCache Layouts { get; }
    public ShaderCache Programs { get; }
    public CodeShaderLibrary CodeShaders { get; }
    public FullscreenQuad Quad { get; }
    public RenderTargetPool Targets { get; }

    /// <summary>b9 of the main view (the shadow pass uploads its per-cascade copies into <see cref="ShadowSceneCb"/>).</summary>
    public GpuBuffer SceneCb { get; }
    public GpuBuffer ShadowSceneCb { get; }
    /// <summary>b9 re-uploaded for passes that render into smaller targets: ToolsGfx rewrites
    /// <c>renderTargetSize/InvSize</c> whenever the bound render target changes (ExposureDownscale levels).</summary>
    public GpuBuffer TargetSceneCb { get; }
    /// <summary>b10 for scene draws: identity world, zeros (per-object data travels through t30).</summary>
    public GpuBuffer ObjectCb { get; }
    /// <summary>b10 for post-FX / compute passes (<see cref="CodeObjectConsts.ForPostFx"/>).</summary>
    public GpuBuffer PostObjectCb { get; }
    /// <summary>b11 of draws without bones: zeros (immutable). Posed draws get their bones through <see cref="BonesFor"/>.</summary>
    public GpuBuffer BonesCb { get; }
    public GpuBuffer PostFxCb { get; }
    /// <summary><c>gObjectInstanceData</c> t30: <see cref="MaxInstances"/> × 208 B, rewritten per pass.</summary>
    public GpuBuffer InstanceData { get; }
    public const int MaxInstances = 256;

    /// <summary>Dummy GPU-skin textures (t18–t20) bound when no siege/GPU skin animation runs: 1×1 zero.</summary>
    public GpuTexture GpuSkinBase { get; }
    public GpuTexture GpuSkinQuat { get; }
    public GpuTexture GpuSkinPos { get; }
    public GpuTexture Black { get; }
    public GpuTexture White { get; }

    public RenderContext(GfxDevice gfx, Data.Shaders.ShaderCache shaderCache)
    {
        Gfx = gfx;
        States = new StateFactory(gfx);
        Layouts = new InputLayoutCache(gfx);
        Programs = new ShaderCache(gfx);
        CodeShaders = new CodeShaderLibrary(Programs, shaderCache);
        Quad = new FullscreenQuad(gfx);
        Targets = new RenderTargetPool(gfx);
        SceneCb = GpuBuffer.CreateConstant(gfx, CodeSceneConsts.Size, "cb9 scene");
        ShadowSceneCb = GpuBuffer.CreateConstant(gfx, CodeSceneConsts.Size, "cb9 sun shadow view");
        TargetSceneCb = GpuBuffer.CreateConstant(gfx, CodeSceneConsts.Size, "cb9 per render target");
        ObjectCb = GpuBuffer.CreateConstant(gfx, CodeObjectConsts.Size, "cb10 scene object");
        ObjectCb.Update(gfx.Context, CodeObjectConsts.Identity);
        PostObjectCb = GpuBuffer.CreateConstant(gfx, CodeObjectConsts.Size, "cb10 post");
        PostObjectCb.Update(gfx.Context, CodeObjectConsts.ForPostFx());
        BonesCb = GpuBuffer.CreateImmutableConstant(gfx, new byte[CodeObjectBonesConsts.Size], "cb11 zero bones");
        PostFxCb = GpuBuffer.CreateConstant(gfx, CodePostFxConsts.Size, "cb13 postfx");
        PostFxCb.Update(gfx.Context, CodePostFxConsts.PreviewDefaults);
        InstanceData = GpuBuffer.CreateDynamicStructured(gfx, CodeObjectConsts.Size, MaxInstances, "t30 gObjectInstanceData");
        InstanceData.UpdateZeroPadded(gfx.Context, ReadOnlySpan<byte>.Empty, 0);

        GpuSkinBase = Solid(Format.R32G32B32A32_Float, new byte[16], "gpuSkinBase");
        GpuSkinQuat = Solid(Format.R10G10B10A2_UNorm, new byte[4], "gpuSkinQuat");
        GpuSkinPos = Solid(Format.R16G16B16A16_Float, new byte[8], "gpuSkinPos");
        Black = Solid(Format.R8G8B8A8_UNorm, new byte[] { 0, 0, 0, 0 }, "$black");
        White = Solid(Format.R8G8B8A8_UNorm, new byte[] { 255, 255, 255, 255 }, "$white");
    }

    private GpuTexture Solid(Format format, byte[] texel, string name)
        => GpuTexture.FromData(Gfx, TextureData.Single2D(format, 1, 1, texel), debugName: name);

    /// <summary>The main-view b9 of the current frame (set by <see cref="UploadScene"/>).</summary>
    public CodeSceneConsts Scene { get; private set; }

    public void UploadScene(in CodeSceneConsts scene)
    {
        Scene = scene;
        SceneCb.Update(Gfx.Context, scene);
    }

    /// <summary>Uploads the current scene b9 with <c>renderTargetSize/InvSize</c> of a <paramref name="width"/>×
    /// <paramref name="height"/> target into <see cref="TargetSceneCb"/> and returns it.</summary>
    public GpuBuffer SceneForTarget(int width, int height)
    {
        var s = Scene;
        s.RenderTargetWidth = (uint)width;
        s.RenderTargetHeight = (uint)height;
        s.RenderTargetInvSize = new System.Numerics.Vector2(1f / width, 1f / height);
        TargetSceneCb.Update(Gfx.Context, s);
        return TargetSceneCb;
    }

    /// <summary>Writes instance elements <c>1..n</c> (element 0 is never referenced by APE's draws: the first
    /// draw of a pass uses StartInstanceLocation 1); element 0 and the rest of the buffer are zeros.</summary>
    public void UploadInstances(ReadOnlySpan<CodeObjectConsts> objects)
    {
        if (objects.Length + 1 > MaxInstances)
            throw new ArgumentException($"at most {MaxInstances - 1} draws per pass");
        InstanceData.UpdateZeroPadded(Gfx.Context, MemoryMarshal.AsBytes(objects), CodeObjectConsts.Size);
    }

    // b11 of posed draws: one buffer per distinct bone memory, uploaded once per scope.
    private readonly List<GpuBuffer> _bonePool = new();
    private readonly Dictionary<ReadOnlyMemory<byte>, GpuBuffer> _bonesUploaded = new();
    private bool _inFrame;

    /// <summary>
    /// The b11 buffer holding <paramref name="bones"/>, uploaded on first use. Between <see cref="BeginFrame"/> and
    /// <see cref="EndFrame"/> an upload serves every pass (a frame's poses do not change while it is drawn); outside a
    /// frame, only the current scene pass.
    /// </summary>
    internal GpuBuffer BonesFor(ReadOnlyMemory<byte> bones)
    {
        if (_bonesUploaded.TryGetValue(bones, out var cb))
            return cb;
        if (_bonesUploaded.Count == _bonePool.Count)
            _bonePool.Add(GpuBuffer.CreateConstant(Gfx, CodeObjectBonesConsts.Size, "cb11 bones"));
        cb = _bonePool[_bonesUploaded.Count];
        cb.Update(Gfx.Context, bones.Span);
        _bonesUploaded[bones] = cb;
        return cb;
    }

    /// <summary>Starts a frame: bone uploads are shared by its passes until <see cref="EndFrame"/>.</summary>
    public void BeginFrame()
    {
        _bonesUploaded.Clear();
        _inFrame = true;
    }

    public void EndFrame()
    {
        _bonesUploaded.Clear();
        _inFrame = false;
    }

    /// <summary>Start of a scene pass (<see cref="SceneDrawer.Draw"/>).</summary>
    internal void BeginScenePass()
    {
        if (!_inFrame)
            _bonesUploaded.Clear();
    }

    private readonly Dictionary<Type, IDisposable> _shared = new();

    /// <summary>A device-lifetime resource of type <typeparamref name="T"/> shared by everything on this context,
    /// created on first use and disposed with the context. Thread-safe.</summary>
    internal T Shared<T>(Func<RenderContext, T> create) where T : class, IDisposable
    {
        lock (_shared)
        {
            if (_shared.TryGetValue(typeof(T), out var existing))
                return (T)existing;
            var created = create(this);
            _shared[typeof(T)] = created;
            return created;
        }
    }

    public void Dispose()
    {
        lock (_shared)
        {
            foreach (var s in _shared.Values)
                s.Dispose();
            _shared.Clear();
        }
        foreach (var b in _bonePool)
            b.Dispose();
        _bonePool.Clear();
        Targets.Dispose();
        foreach (var d in new IDisposable[] { SceneCb, ShadowSceneCb, TargetSceneCb, ObjectCb, PostObjectCb, BonesCb, PostFxCb, InstanceData,
                     GpuSkinBase, GpuSkinQuat, GpuSkinPos, Black, White, Quad, Programs, Layouts, States })
            d.Dispose();
    }
}
