using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Services.Extensions.Simulation;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The recoil preview's controls against what a user does between them: clicking a segment then holding Space,
/// switching windows mid-burst, closing a weapon while its module question is open, the palette with nothing to fire.
/// Real input on the live app over a temp install, as <see cref="RecoilAppChecks"/>.
/// </summary>
public partial class Program
{
    private static void RecoilHardeningChecks(string outDir)
    {
        if (RecoilInstall("recoil-hard") is not { } setup)
        {
            Console.WriteLine("recoil hardening: BO3 deffiles not found — skipped");
            return;
        }
        var saved = (Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            (vm, window) = StartRecoilApp(setup)!.Value;
            RecoilQuestionWithdrawnCheck(vm, window, setup);
            RecoilPaletteCheck(vm, window);
            RecoilFocusChecks(vm, window);
            RecoilDeactivateCheck(vm, window);
            RecoilMinimisedCheck(vm, window);
        }
        catch (Exception ex)
        {
            Check($"recoil hardening: {ex}", false);
        }
        finally
        {
            StopRecoilApp(vm, window, saved);
        }
    }

    private static DockPanel RecoilRoot(Window window, WeaponPreviewViewModel recoil) =>
        window.GetVisualDescendants().OfType<DockPanel>().First(p => p.DataContext == recoil && p.Classes.Contains("animroot"));

    private static void HoldSpace(Window window, int frames)
    {
        window.KeyPress(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Frames(frames, 25);
        window.KeyRelease(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();
    }

    /// <summary>A minimised window draws nothing, so its preview steps nothing: not even a held trigger burns frames.</summary>
    private static void RecoilMinimisedCheck(MainViewModel vm, MainWindow window)
    {
        var recoil = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
        recoil.ResetCommand.Execute(null);
        recoil.PressTrigger();
        Frames(3, 20);
        window.WindowState = WindowState.Minimized;
        Pump(50);
        var stepped = recoil.FramesStepped;
        Frames(10, 20);
        var whileMinimised = recoil.FramesStepped - stepped;
        window.WindowState = WindowState.Normal;
        Pump(50);
        Frames(5, 20);
        Check($"recoil hardening: minimised, the preview steps nothing even with the trigger held, and goes on when restored ({whileMinimised} steps while minimised, {recoil.FramesStepped - stepped} after)",
            window.WindowState == WindowState.Normal && whileMinimised == 0 && recoil.FramesStepped > stepped);
        recoil.ReleaseTrigger();
        Frames(20);
    }

    /// <summary>The module question belongs to the previews waiting on it: the last one closing withdraws it, remembering nothing.</summary>
    private static void RecoilQuestionWithdrawnCheck(MainViewModel vm, MainWindow window, (string Install, string Ext, string Settings, string Dll) setup)
    {
        vm.OpenByName("apex_recoil_base");
        vm.ActiveTab!.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
        Pump(400);
        var question = (vm.ActiveTab.PreviewPane?.Content as WeaponPreviewViewModel)?.Question;
        vm.CloseActiveTabCommand.Execute(null);
        Pump(100);
        var answers = Path.Combine(setup.Settings, SimulatorConsentStore.FileName);
        Check($"recoil hardening: closing the only weapon waiting on the module question withdraws it and remembers nothing (asked {question is not null}, still open {question?.IsOpen})",
            question is { IsOpen: false, Answer.IsCompletedSuccessfully: true } && question.Answer.Result is null && vm.ModuleQuestions.For(SimId) is null
            && !vm.IsConfirmOpen && !File.Exists(answers) && !IsLoaded(setup.Dll));
        question?.LoadCommand.Execute(null);
        Pump(100);
        Check("recoil hardening: Load on a question that went away counts for nothing (no answer kept, nothing loaded)",
            question?.Answer.Result is null && !File.Exists(answers) && !IsLoaded(setup.Dll));
        vm.OpenByName("apex_recoil_base");
        Pump(400);
        var again = (vm.ActiveTab!.PreviewPane?.Content as WeaponPreviewViewModel)?.Question;
        Check("recoil hardening: opening it again asks again, a new question", again is { IsOpen: true, Title: "Load sim-fixture's preview module?" } && !ReferenceEquals(again, question));
    }

    /// <summary>Fire and Reset are offered only when something can fire.</summary>
    private static void RecoilPaletteCheck(MainViewModel vm, MainWindow window)
    {
        vm.OpenByName("apex_recoil_base");
        vm.ActiveTab!.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
        Pump(400);
        var recoil = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
        AnswerQuestion(window, recoil, load: false);
        Pump(100);
        var declined = (Fire: vm.Registry[CommandCatalog.Fire].CanRun, Reset: vm.Registry[CommandCatalog.ResetRecoil].CanRun, Ads: vm.Registry[CommandCatalog.AimDownSights].CanRun);
        Check($"recoil hardening: with the module declined the palette doesn't offer Fire or Reset (fire {declined.Fire}, reset {declined.Reset}; ADS stays {declined.Ads})",
            !recoil.CanFire && !declined.Fire && !declined.Reset && declined.Ads);
        Click(window, Named<Button>(window, "LoadModuleButton"), MouseButton.Left);
        AnswerQuestion(window, recoil, load: true);
        WaitUntil(() => recoil.CanFire, 5000);
        Pump(100);
        Check("recoil hardening: loaded, the palette offers Fire and Reset", vm.Registry[CommandCatalog.Fire].CanRun && vm.Registry[CommandCatalog.ResetRecoil].CanRun);
    }

    /// <summary>
    /// Space fires after a click on Hip, ADS or Reset (a click doesn't leave the keyboard on them); reached with Tab,
    /// a segment still takes Space itself.
    /// </summary>
    private static void RecoilFocusChecks(MainViewModel vm, MainWindow window)
    {
        var recoil = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
        foreach (var name in new[] { "AdsToggle", "ResetButton", "HipToggle" })
        {
            recoil.ResetCommand.Execute(null);
            Frames(2);
            Control target = Named<Button>(window, name);
            Click(window, target, MouseButton.Left);
            var focused = window.FocusManager?.GetFocusedElement() as Control;
            HoldSpace(window, 8);
            Check($"recoil hardening: after a click on {name}, holding Space fires ({recoil.Driver.Rounds} rounds; focus on {focused?.Name ?? focused?.GetType().Name})",
                recoil.Driver.Rounds > 0 && !recoil.Driver.IsTriggerHeld);
            Frames(20);
        }
        Check("recoil hardening: the clicks still did their work (Hip last)", !recoil.IsAds && Named<Button>(window, "HipToggle").Classes.Contains("checked"));

        // The keyboard path: Tab reaches the segment, and Space on it is the segment's, not the trigger.
        recoil.ResetCommand.Execute(null);
        Named<Button>(window, "AdsToggle").Focus(NavigationMethod.Tab);
        Pump();
        HoldSpace(window, 2);
        Check($"recoil hardening: reached by Tab, Space on ADS switches to ADS and fires nothing (ads {recoil.IsAds}, rounds {recoil.Driver.Rounds})",
            recoil.IsAds && recoil.Driver.Rounds == 0);
        Key(window, K.A);
        Frames(20);
    }

    /// <summary>The window losing focus mid-burst (Alt+Tab, a minimise) lets the trigger go: the key's release goes elsewhere.</summary>
    private static void RecoilDeactivateCheck(MainViewModel vm, MainWindow window)
    {
        var recoil = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
        recoil.ResetCommand.Execute(null);
        RecoilRoot(window, recoil).Focus();
        Pump();
        window.KeyPress(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Frames(3, 20);
        var held = recoil.Driver.IsTriggerHeld;
        var impl = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window)!;
        (impl.GetType().GetProperty("Deactivated")?.GetValue(impl) as Action)?.Invoke();
        Pump(50);
        var rounds = recoil.Driver.Rounds;
        Frames(20, 25);
        Check($"recoil hardening: the window deactivating while Space is held lets the trigger go (held {held} → {recoil.Driver.IsTriggerHeld}, rounds {rounds} → {recoil.Driver.Rounds})",
            held && !recoil.Driver.IsTriggerHeld && recoil.Driver.Rounds == rounds);
        (impl.GetType().GetProperty("Activated")?.GetValue(impl) as Action)?.Invoke();
        window.KeyRelease(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();
    }
}
