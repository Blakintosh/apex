using Avalonia;
using Avalonia.Logging;
using Avalonia.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Apex.Editor;

class Program
{
    private const uint LoadLibrarySearchUserDirs = 0x400;
    private const uint LoadLibrarySearchSystem32 = 0x800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Apex ships in <BO3>in, and installs often keep other copies of dxgi.dll and d3d11.dll
        // there for the Mod Tools (DXVK, or old Windows 10 builds). Rendering through those crashes,
        // so Windows' search skips the exe folder, and .NET's own probe of it, which runs first for
        // [DllImport], goes to System32 for Avalonia and Vortice. Libraries loaded by full path
        // (ispc_texcomp, openexr, simulator DLLs) are unaffected.
        SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs);
        // After Install now, wait for the version that started this one to exit and remove what the swap left.
        Gscode.Updates.Updater.FinishPreviousUpdate(Services.AppUpdates.Options);
        foreach (var assembly in new[]
                 {
                     typeof(Win32PlatformOptions).Assembly,
                     typeof(Vortice.Direct3D11.ID3D11Device).Assembly,
                     typeof(Vortice.DXGI.IDXGIFactory1).Assembly,
                     typeof(Vortice.D3DCompiler.Compiler).Assembly,
                 })
            NativeLibrary.SetDllImportResolver(assembly, LoadFromSystem32);

        // --mock: generated sample assets instead of an install (development only; settings aren't saved).
        if (Array.Exists(args, a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", "1");
        // Logged from the first line: a failure setting up the platform, the renderer or the XAML (before the app hooks
        // its full handlers) still leaves a log and a message instead of a window that never appears.
        AppDomain.CurrentDomain.UnhandledException += OnEarlyCrash;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Services.CrashGuard.StartupFailed(ex);
            throw;
        }
    }

    private static void OnEarlyCrash(object? sender, UnhandledExceptionEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnEarlyCrash;
        // Once the app is up, CrashGuard's own handler reports it; before that this is the only one.
        if (!Services.CrashGuard.IsInstalled && e.ExceptionObject is Exception ex)
            Services.CrashGuard.StartupFailed(ex);
    }

    private static IntPtr LoadFromSystem32(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (Path.IsPathRooted(libraryName))
            return IntPtr.Zero;

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        var systemPath = Path.Combine(Environment.SystemDirectory, fileName);
        return File.Exists(systemPath) && NativeLibrary.TryLoad(systemPath, out var handle) ? handle : IntPtr.Zero;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
#if DEBUG
            .LogToTrace();
#else
            // Warnings include every failed binding evaluation, formatted on the UI thread for nobody.
            .LogToTrace(LogEventLevel.Error);
#endif
}
