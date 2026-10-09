using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Editor;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before anything draws: MonoFont's first family resolves through it.
        Services.MonoFace.Register();
        AvaloniaXamlLoader.Load(this);
        // Per-type hues have a dark-surface and a light-surface ink; re-tint them with the variant.
        // The app can start light (Windows, or the saved theme): apply once now, then on every change.
        ApplyTypeVariant();
        ActualThemeVariantChanged += (_, _) => ApplyTypeVariant();
    }

    private void ApplyTypeVariant() =>
        TypeStyles.ApplyVariant(Services.AppTheme.IsLight(ActualThemeVariant));

    public override void OnFrameworkInitializationCompleted()
    {
        // The platform's light/dark setting is only known once the platform is up.
        ApplyTypeVariant();

        // Anything nothing else caught: log it, get the session onto disk, and keep going when it's safe to.
        Services.CrashGuard.Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            // Before the window exists, so it opens in the saved theme with no flash of the Windows one.
            Services.AppTheme.Apply(vm.Theme);
            Services.CrashGuard.FlushSession = vm.FlushForCrash;
            Services.CrashGuard.Recovered = vm.ReportRecovered;
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            // Install now swaps the files, then closes Apex; it starts again from OnExit.
            vm.Updates.RestartRequested += (_, _) => window.Close();
            // After the first frame: the check and its download run in the background and only ever show a ready update.
            window.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(vm.CheckForUpdatesIfDue, Avalonia.Threading.DispatcherPriority.Background);
            desktop.Exit += (_, _) =>
            {
                vm.Dispose();
                // Preview devices outlive their last viewport for a grace period; release them now.
                Apex.Render.Presentation.SharedGfxDevices.DisposeIdle();
                vm.Updates.OnExit();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
