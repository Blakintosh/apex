using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using Apex.Render.Assets;
using Apex.Render.Presentation;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// An xanim's preview where the Preview pane docks it (380 px, the default dock width): the transport under the render
/// (slider, Loop, the readout below them) and the chips on it, driven with real input. The chips that need APE's renderer
/// (lighting, first person, joints) are shown with a real environment and model read from the install (read-only): the
/// headless platform has no D3D interop, so the viewports' failure report is taken away for the check, and the render
/// stays black.
/// </summary>
public partial class Program
{
    private const double DockWidth = 380;

    private static void AnimDockedChecks(Window main, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var pane = new PreviewPaneView { DataContext = tab.PreviewPane };
        var backdrop = new Border { Padding = new Thickness(0, 10, 0, 0), Child = pane };
        backdrop.Bind(Border.BackgroundProperty, backdrop.GetResourceObservable("BgPaneBrush"));
        // The window's data is the app's, as the docked pane's is: reference fields find their suggestions through it.
        var host = new Window { Width = DockWidth, Height = 640, DataContext = vm, Content = backdrop };
        host.Show();
        Settle(host);
        var lightingBefore = PreviewLighting.Shared.LightState;
        try
        {
            DockedTransportChecks(host, pane, anim);
            DockedChipChecks(main, host, pane, anim, outDir);
        }
        catch (Exception ex)
        {
            Check($"docked anim: {ex}", false);
        }
        finally
        {
            PreviewLighting.Shared.LightState = lightingBefore;
            anim.IsPlaying = false;
            anim.Gfx.Clear();
            anim.HasViewTag = false;
            anim.ViewmodelInfo = null;
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            host.Close();
            Settle(host);
        }
    }

    // ── P2, P5, P6, P7, P8, P13, P16: the transport ──

    private static void DockedTransportChecks(Window host, PreviewPaneView pane, AnimPreviewViewModel anim)
    {
        var slider = Find<Slider>(pane, "FrameSlider");
        var readout = Find<TextBlock>(pane, "TransportReadout");
        var loop = Find<ToggleButton>(pane, "PaneLoopToggle");
        var timeline = Find<NotetrackTimelineBar>(pane, "NotetrackTimeline");
        Check($"docked anim: at the {DockWidth:0} px dock the slider keeps its width ({slider.Bounds.Width:0} px), the readout sits under it and Loop beside it",
            slider.IsEffectivelyVisible && slider.Bounds.Width >= 120 && BoundsIn(readout, host).Top >= BoundsIn(timeline, host).Bottom
            && loop.IsEffectivelyVisible && BoundsIn(loop, host).Left >= BoundsIn(slider, host).Right && readout.Text == anim.TransportLabel);

        // The handle is the playhead's: its accent, 12 px wide.
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
        var accent = Brush(host, "AccentBrush");
        Check($"docked anim: the slider's handle is the playhead's accent and width ({(thumb.Background as ISolidColorBrush)?.Color}, {thumb.Bounds.Width:0} px)",
            (thumb.Background as ISolidColorBrush)?.Color == accent && Math.Abs(thumb.Bounds.Width - 12) < 0.5);
        // The timeline under it spans the same frames: the handle's centre range.
        var track = BoundsIn(slider, host);
        var bar = BoundsIn(timeline, host);
        Check($"docked anim: the notetrack timeline spans the slider's frames ({bar.Left - track.Left:0} and {track.Right - bar.Right:0} px in from its ends)",
            Math.Abs(bar.Left - track.Left - 6) < 1 && Math.Abs(track.Right - bar.Right - 6) < 1);

        // Loop, clicked: the same state as the notetracks dock's.
        Click(host, loop, MouseButton.Left);
        var off = !anim.Loop && loop.IsChecked == false;
        Click(host, loop, MouseButton.Left);
        Check($"docked anim: Loop switches off ({off}) and back on ({anim.Loop}) with a click", off && anim.Loop && loop.IsChecked == true);

        // A drag of the handle to between two frames lands on a whole frame (slider and readout agree), and the slider
        // keeps no keyboard.
        anim.IsPlaying = false;
        anim.CurrentFrame = 0;
        Settle(host);
        var handle = SliderPoint(host, slider, 0);
        var at = SliderPoint(host, slider, 0.37 + 0.5 / Math.Max(anim.LastFrame, 1));
        PressAt(host, handle);
        PointerAt(host, handle + new Point(20, 0));
        PointerAt(host, at);
        ReleaseAt(host, at);
        var focused = host.FocusManager?.GetFocusedElement();
        var shown = ReadoutFrame(anim.TransportLabel);
        Check($"docked anim: a drag on the slider lands on a whole frame (frame {anim.CurrentFrame}, slider {slider.Value}, readout {shown})",
            anim.CurrentFrame > 0 && anim.CurrentFrame == Math.Round(anim.CurrentFrame) && slider.Value == anim.CurrentFrame && shown == (int)anim.CurrentFrame);
        Check($"docked anim: the slider takes no keyboard (focus on {focused?.GetType().Name})",
            focused is not (Slider or Thumb or RepeatButton) && !slider.Focusable);
        Key(host, K.Space);
        Check("docked anim: Space after a click on the slider plays (the preview has the keyboard)", anim.IsPlaying);

        // Scrubbing while playing pauses until the release, then plays on.
        Pump(120);
        var from = SliderPoint(host, slider, slider.Value / Math.Max(slider.Maximum, 1));
        PressAt(host, from);
        var pausedOnPress = !anim.IsPlaying;
        for (var x = 0.2; x <= 0.6; x += 0.05)
            PointerAt(host, SliderPoint(host, slider, x));
        Pump(60);
        var whole = anim.CurrentFrame == Math.Round(anim.CurrentFrame);
        var pausedThrough = !anim.IsPlaying;
        var scrubbedTo = anim.CurrentFrame;
        var to = SliderPoint(host, slider, 0.6);
        host.MouseUp(to, MouseButton.Left);
        Settle(host);
        Check($"docked anim: a scrub while playing pauses on the press ({pausedOnPress}), stays paused through the drag to frame {scrubbedTo} on whole frames ({whole}), and plays on after the release ({anim.IsPlaying})",
            pausedOnPress && pausedThrough && whole && scrubbedTo >= 0.5 * anim.LastFrame && anim.IsPlaying);

        // One formula for the readout: pausing doesn't change the frame it names.
        Pump(90);
        Key(host, K.Space);
        Check($"docked anim: paused, the readout names the frame on the slider ('{anim.TransportLabel}', frame {anim.CurrentFrame})",
            !anim.IsPlaying && ReadoutFrame(anim.TransportLabel) == (int)Math.Round(anim.CurrentFrame) && anim.CurrentFrame == Math.Round(anim.CurrentFrame));
    }

    private static Point SliderPoint(Window host, Slider slider, double fraction)
    {
        // The handle's centre runs 6 px in from either end of the track.
        var x = 6 + fraction * (slider.Bounds.Width - 12);
        return slider.TranslatePoint(new Point(x, slider.Bounds.Height / 2), host)!.Value;
    }

    private static int ReadoutFrame(string? label) =>
        int.TryParse(label?.Split('/')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : -1;

    private static Color? Brush(Visual on, string key) =>
        on is StyledElement e && e.TryFindResource(key, e.ActualThemeVariant, out var r) && r is ISolidColorBrush b ? b.Color : null;

    // ── P1, P3, P4, P9, P10, P12, P14, P15, P18, U5: on the render ──

    private static void DockedChipChecks(Window main, Window host, PreviewPaneView pane, AnimPreviewViewModel anim, string outDir)
    {
        anim.IsPlaying = false;
        if (RealGfx() is not var (env, model))
        {
            Console.WriteLine("docked anim chips: BO3 install not found — skipped");
            return;
        }
        // Every viewport showing this anim (the editor's and this one) keeps the chips up despite the missing interop.
        foreach (var gfx in new[] { main, host }.SelectMany(w => w.GetVisualDescendants().OfType<ToolsGfxPreviewViewport>()))
            typeof(ToolsGfxPreviewViewport).GetField("Failed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(gfx, null);
        anim.Gfx.Environment = env;
        anim.Gfx.Model = model;
        anim.HasViewTag = false;
        anim.ViewmodelInfo = "hands c_t7_mp_specialist_viewhands (handModel of wpn_t7_ar_an94) · gun wpn_t7_loot_ar_an94_view (wpn_t7_ar_an94 uses vm_ar_an94_idle) · wpn_t7_attach_reflex_view at tag_reflex";
        Settle(host);

        var viewportFill = pane.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("viewport") && b.IsEffectivelyVisible);
        var lighting = pane.GetVisualDescendants().OfType<Button>().First(b => b.Name == "LightingChip" && b.IsEffectivelyVisible);
        var chips = Find<StackPanel>(pane, "ViewChips");
        var group = Find<Border>(pane, "FirstPersonGroup");
        var toggle = Find<ToggleButton>(pane, "FirstPersonToggle");
        var options = Find<Button>(pane, "ViewmodelOptionsButton");
        var frame = Find<Button>(pane, "FrameChip");
        var fill = BoundsIn(viewportFill, host);
        Check($"docked anim: at {DockWidth:0} px the lighting chip and the view chips fit on the render, side by side (lighting {BoundsIn(lighting, host).Right:0}, chips {BoundsIn(chips, host).Left:0}–{BoundsIn(chips, host).Right:0} in {fill.Left:0}–{fill.Right:0})",
            lighting.IsEffectivelyVisible && group.IsEffectivelyVisible && BoundsIn(lighting, host).Right + 4 <= BoundsIn(chips, host).Left
            && BoundsIn(chips, host).Right <= fill.Right - 8 && BoundsIn(lighting, host).Left >= fill.Left + 8
            && chips.DesiredSize.Width <= chips.Bounds.Width + 0.5);

        // U5: one group, one disabled state. No camera tag: both halves off, and the group says why.
        Check($"docked anim: with no camera tag the First person group is disabled as one (toggle {toggle.IsEffectivelyEnabled}, options {options.IsEffectivelyEnabled}); its tip says why",
            !group.IsEnabled && !toggle.IsEffectivelyEnabled && !options.IsEffectivelyEnabled && (ToolTip.GetTip(group) as string)?.Contains("no camera tag") == true);
        anim.HasViewTag = true;
        Settle(host);
        Check("docked anim: with a camera tag both halves are live", group.IsEnabled && toggle.IsEffectivelyEnabled && options.IsEffectivelyEnabled);
        var segments = group.GetVisualDescendants().OfType<Button>().ToList();
        Check($"docked anim: the group owns the fill and the radius: one bordered group, its halves flat ({segments.Count} halves, one divider)",
            segments.Count == 2 && group.Classes.Contains("overlay") && group.BorderThickness.Left == 1
            && segments.All(s => s.BorderThickness == default) && group.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("divider")) == 1);

        // P14: looking through the camera, Frame goes (there is nothing to frame) and the wheel is the field of view.
        Click(host, toggle, MouseButton.Left);
        Settle(host);
        var through = anim.LooksThroughCamera && toggle.IsChecked == true && !frame.IsEffectivelyVisible;
        var fov = anim.ViewmodelFov;
        var viewport = pane.GetVisualDescendants().OfType<ToolsGfxPreviewViewport>().First(v => v.IsEffectivelyVisible);
        var mid = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), host)!.Value;
        host.MouseWheel(mid, new Vector(0, -1));
        Settle(host);
        var wheeled = anim.ViewmodelFov;
        Click(host, toggle, MouseButton.Left);
        Settle(host);
        Check($"docked anim: in first person Frame is gone ({through}); out of it Frame is back ({frame.IsEffectivelyVisible})",
            through && !anim.LooksThroughCamera && frame.IsEffectivelyVisible);
        Console.WriteLine($"info  docked anim: the wheel in first person took the field of view {fov:0} → {wheeled:0} (live only with a pose that has a view tag)");
        anim.ViewmodelFov = fov;

        // P1/P4/P12: lighting is one chip with a menu; the state in use is always the one checked.
        Click(host, lighting, MouseButton.Left);
        var menu = pane.LightingMenu;
        var items = menu?.Items.OfType<MenuItem>().ToList() ?? new();
        Check($"docked anim: the lighting chip opens the four states, the one in use checked ({string.Join(", ", items.Select(i => $"{i.Header}{(i.IsChecked ? "✓" : "")}"))})",
            menu is { IsOpen: true } && items.Count == 4 && items.Count(i => i.IsChecked) == 1 && items.Single(i => i.IsChecked).Header as string == PreviewLighting.Shared.LightState.ToString());
        PickMenuItem(items.First(i => i.Header as string == "Night"));
        var label = lighting.GetVisualDescendants().OfType<TextBlock>().First();
        var picked = PreviewLighting.Shared.LightState.ToString() == "Night" && label.Text == "Night" && menu?.IsOpen != true;
        Click(host, lighting, MouseButton.Left);
        items = pane.LightingMenu?.Items.OfType<MenuItem>().ToList() ?? new();
        PickMenuItem(items.First(i => i.Header as string == "Night"));
        Click(host, lighting, MouseButton.Left);
        items = pane.LightingMenu?.Items.OfType<MenuItem>().ToList() ?? new();
        Check($"docked anim: picking Night lights with Night and the chip says so ({picked}); picking it again leaves it the one checked ({string.Join(", ", items.Where(i => i.IsChecked).Select(i => i.Header))})",
            picked && items.Count(i => i.IsChecked) == 1 && items.Single(i => i.IsChecked).Header as string == "Night");
        pane.LightingMenu?.Hide();
        Settle(host);

        // P3: the caption and the stats stack, each with the width of the render; the stats lead with the motion's origin.
        var caption = Find<Border>(pane, "CaptionChip");
        var stats = Find<Border>(pane, "StatsChip");
        Check($"docked anim: the caption sits above the stats, both from the left edge, neither cut by the other ('{anim.Stats}')",
            caption.IsEffectivelyVisible && stats.IsEffectivelyVisible && BoundsIn(caption, host).Bottom <= BoundsIn(stats, host).Top
            && Math.Abs(BoundsIn(caption, host).Left - BoundsIn(stats, host).Left) < 0.5 && anim.Stats?.StartsWith("export file", StringComparison.Ordinal) == true);

        // P9: everything on the render carries the hairline.
        var edge = Brush(host, "OverlayEdgeBrush");
        var onRender = viewportFill.GetVisualDescendants().OfType<TemplatedControl>().Where(c => c.IsEffectivelyVisible && (c.Classes.Contains("chip"))).ToList();
        var pills = viewportFill.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("overlay")).ToList();
        Check($"docked anim: every chip ({onRender.Count}) and pill ({pills.Count}) on the render has the inset hairline",
            onRender.Count >= 3 && pills.Count >= 3
            && onRender.All(c => c.BorderThickness.Left == 1 && (c.BorderBrush as ISolidColorBrush)?.Color == edge)
            && pills.All(b => b.BorderThickness.Left == 1 && (b.BorderBrush as ISolidColorBrush)?.Color == edge));

        // P18: no glyph icons left on the render or the pane's header.
        var glyphs = pane.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is "▾" or "⧉" or "⤢").Count();
        Check($"docked anim: the icons are drawn, not glyphs ({glyphs} glyphs)", glyphs == 0);

        // P15: the hands and gun boxes are xmodel fields with suggestions and a line that says what's wrong.
        Click(host, options, MouseButton.Left);
        var flyout = options.Flyout as Flyout;
        var panel = flyout?.Content as StackPanel;
        Settle(host);
        var handsBox = panel?.GetVisualDescendants().OfType<PropertyEditorView>().FirstOrDefault(p => p.Name == "HandsBox");
        var field = handsBox?.GetVisualDescendants().OfType<RefField>().FirstOrDefault();
        Check($"docked anim: ▾ opens the field of view and, for an anim with no previewModel, the hands and gun as xmodel fields ({field is not null})",
            flyout?.IsOpen == true && field is { IsEffectivelyVisible: true } && anim.ShowViewmodelChoice);
        if (field is not null && panel is not null)
        {
            field.Focus();
            Settle(host);
            // Alt+↓ lists every xmodel, as in any reference field (the fixture install has none, and says so).
            Key(host, K.Down, RawInputModifiers.Alt);
            for (var i = 0; i < 20 && !field.IsSuggesting; i++)
                Pump(50);
            var list = field.Suggestions;
            var message = list?.FindControl<TextBlock>("Message");
            Check($"docked anim: the hands box lists xmodels from inside the flyout ('{message?.Text}')",
                field.IsSuggesting && message is { IsVisible: true } && message.Text?.Contains("xmodel") == true);
            // (Esc here closes the list in the app, where the list is a window of its own; headless popups route keys
            // differently, so it is closed directly.)
            field.Close();
            field.Focus();
            field.SelectAll();
            TypeText(host, "no_such_hands");
            Key(host, K.Enter);
            var note = panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HandsNote");
            Check($"docked anim: a hands name that isn't an xmodel says so under the box ('{note.Text}')",
                anim.HandsField.HasProblem && note.Text == "no_such_hands isn't an xmodel in the loaded GDTs" && note.Classes.Contains("fielderror"));
            var gunNote = panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "GunNote");
            var box = BoundsIn(Find<PropertyEditorView>(panel, "GunBox"), panel);
            Capture(host, Path.Combine(outDir, "89-anim-viewmodel-options-dark.png"));
            field.SelectAll();
            Key(host, K.Back);
            Key(host, K.Enter);
            Check($"docked anim: emptied, the note says where the hands come from instead ('{note.Text}'); the gun box didn't move ({BoundsIn(Find<PropertyEditorView>(panel, "GunBox"), panel).Top:0} = {box.Top:0})",
                !anim.HandsField.HasProblem && !note.Classes.Contains("fielderror") && note.Text?.StartsWith("Empty finds the hands", StringComparison.Ordinal) == true
                && Math.Abs(BoundsIn(Find<PropertyEditorView>(panel, "GunBox"), panel).Top - box.Top) < 0.5 && gunNote.Text?.Length > 0);
        }
        flyout?.Hide();
        Settle(host);

        Capture(host, Path.Combine(outDir, "87-anim-docked-dark.png"));
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Settle(host);
        // P10: in the light theme every word on the render is still light on dark.
        var viewportBg = Brush(host, "ViewportBgBrush") ?? Colors.Black;
        var worst = viewportFill.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => (t.Text, Ratio: Contrast((t.Foreground as ISolidColorBrush)?.Color ?? Colors.Black, viewportBg)))
            .OrderBy(t => t.Ratio).FirstOrDefault();
        Check($"docked anim: light theme, the text on the render reads (lowest contrast {worst.Ratio:0.0}:1, '{worst.Text}')", worst.Ratio >= 7);
        Capture(host, Path.Combine(outDir, "88-anim-docked-light.png"));
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Settle(host);
    }

    /// <summary>A real click on a menu item, in the popup it is drawn in.</summary>
    private static void PickMenuItem(MenuItem item)
    {
        Pump();
        if (TopLevel.GetTopLevel(item) is not { } popup)
        {
            item.Command?.Execute(item.CommandParameter);
            return;
        }
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
        popup.MouseMove(at);
        popup.MouseDown(at, MouseButton.Left);
        popup.MouseUp(at, MouseButton.Left);
        Pump();
        System.Threading.Thread.Sleep(550);
    }


    private static (PreviewEnvironment Env, PreparedPreviewModel Model)? _realGfx;
    private static bool _realGfxTried;

    /// <summary>APE's lighting environment and one prepared model from the install, read-only (a copy of one GDT indexes it).</summary>
    private static (PreviewEnvironment Env, PreparedPreviewModel Model)? RealGfx()
    {
        if (_realGfxTried)
            return _realGfx;
        _realGfxTried = true;
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(InstallRoot);
        var gdt = Path.Combine(InstallRoot, "source_data", "ar_an94.gdt");
        if (!install.IsAvailable || !File.Exists(gdt))
            return null;
        var gdts = NewScratch("docked-anim-gdt");
        File.Copy(gdt, Path.Combine(gdts, "ar_an94.gdt"));
        var data = new Apex.Render.Data.ToolsGfxData(install, new Apex.Render.Data.Gdt.GdtIndex(gdts));
        var model = PreviewModelLoader.Prepare(data, "wpn_t7_loot_ar_an94_view", new PreviewModelOptions { SkipBrokenMaterials = true });
        return _realGfx = (PreviewEnvironment.Load(data), model);
    }
}
