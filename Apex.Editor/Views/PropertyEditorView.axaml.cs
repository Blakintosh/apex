using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class PropertyEditorView : UserControl
{
    /// <summary>The form's ⚠ / ⊘ slot beside the value, always reserved (a table cell marks problems its own way).</summary>
    public static readonly StyledProperty<bool> ShowMarksProperty = AvaloniaProperty.Register<PropertyEditorView, bool>(nameof(ShowMarks));

    public bool ShowMarks
    {
        get => GetValue(ShowMarksProperty);
        set => SetValue(ShowMarksProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Set here rather than bound up the tree: every row in view has one, and a relative binding per row costs.
        if (change.Property == ShowMarksProperty)
        {
            var show = change.GetNewValue<bool>();
            if (show && Marks.Children.Count == 0)
                BuildMarks();
            Marks.IsVisible = show;
            ApplyWidthCap();
        }
    }

    /// <summary>⚠ (what's wrong with the value) and ⊘ (the deffile disables the row, and why), centred in the slot.</summary>
    private void BuildMarks()
    {
        // The line set's warning and ⊘ drawn the same way: 13 px, in the problem's rose and the faint tone.
        Control Mark(string glyph, string brush, string visible, string tip)
        {
            var mark = new GlyphIcon
            {
                Glyph = glyph,
                Size = 13,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            mark[!GlyphIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brush);
            mark.Bind(IsVisibleProperty, new Avalonia.Data.Binding(visible));
            mark.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(tip));
            ToolTip.SetShowOnDisabled(mark, true);
            return mark;
        }
        Marks.Children.Add(Mark("warning", "DangerBrush", nameof(PropertyItemViewModel.HasProblem), nameof(PropertyItemViewModel.Problem)));
        Marks.Children.Add(Mark("⊘", "TextFaintBrush", nameof(PropertyItemViewModel.ShowDisabledMark), nameof(PropertyItemViewModel.DisabledTip)));
    }

    public PropertyEditorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, Field_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, Field_LostFocus, RoutingStrategies.Bubble);
        AddHandler(Button.ClickEvent, Action_Click, RoutingStrategies.Bubble);
        Root.SizeChanged += (_, e) => Root.Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
    }

    // ── File fields: … picks a file under the field's folder ──

    /// <summary>Below this a cell shows the value alone (the browse button would cover it).</summary>
    private const double NarrowWidth = 150;

    // A swatch click within this long of the picker closing is the click that closed it.
    private const long ReopenGuardMs = 300;

    private void Swatch_Click(Button swatch)
    {
        if (swatch.Parent is not Panel panel || panel.Children.OfType<Popup>().FirstOrDefault() is not { } picker)
            return;
        // The picker dismisses itself on the press of a click on the swatch; that click's release must not reopen it.
        if (picker.IsOpen || picker.Tag is long closedAt && System.Environment.TickCount64 - closedAt < ReopenGuardMs)
        {
            picker.IsOpen = false;
            picker.Tag = null;
            return;
        }
        picker.IsOpen = true;
        picker.Closed += Returned;

        // Closing with the keyboard in the picker (Esc) hands it back to the swatch.
        void Returned(object? sender, System.EventArgs args)
        {
            picker.Closed -= Returned;
            picker.Tag = System.Environment.TickCount64;
            if (picker.Child?.IsKeyboardFocusWithin == true)
                swatch.Focus();
        }
    }

    private async void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Classes: var s } swatch && s.Contains("swatch"))
        {
            e.Handled = true;
            Swatch_Click(swatch);
            return;
        }
        if (e.Source is not Button { Classes: var c } button || !c.Contains("browse")
            || button.DataContext is not FilePropertyViewModel { CanBrowse: true } row
            || TopLevel.GetTopLevel(this) is not { } top)
            return;
        e.Handled = true;
        string? picked;
        string? value, error;
        try
        {
            // An async void handler: anything thrown here (the system picker, a path it hands back) would end the app.
            picked = await FieldFiles.PickFile(top, FieldFiles.RequestFor(row.Def, row.RawValue));
            if (picked is null)
                return;
            (value, error) = FieldFiles.ValueFor(row.Def, picked);
        }
        catch (System.Exception)
        {
            (value, error) = (null, "Couldn't open that file. Pick it again or type its path.");
        }
        if (error is not null)
        {
            if (top.DataContext is MainViewModel shell)
                shell.Status = error;
            return;
        }
        // Whatever was typed and not committed is replaced by the pick, as one edit.
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("pfield") && b.DataContext == row) is { } box)
            box.Text = value;
        row.RawValue = value!;
    }

    // ── Text and reference fields: commit on Enter or focus loss, Esc reverts ──

    private static (TextBox Box, PropertyItemViewModel Row)? FieldOf(object? source) =>
        source is TextBox { Classes: var c } box && c.Contains("pfield") && box.DataContext is PropertyItemViewModel row
            ? (box, row)
            : null;

    private static bool IsTyped(TextBox box, PropertyItemViewModel row) => (box.Text ?? "") != row.RawValue;

    private static void Commit(TextBox box, PropertyItemViewModel row)
    {
        if (IsTyped(box, row))
            row.RawValue = box.Text ?? "";
    }

    private static void Field_KeyDown(object? sender, KeyEventArgs e)
    {
        if (FieldOf(e.Source) is not var (box, row))
            return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                Commit(box, row);
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when IsTyped(box, row):
                Revert(box, row);
                e.Handled = true;
                break;
        }
    }

    private static void Revert(TextBox box, PropertyItemViewModel row)
    {
        box.Text = row.RawValue;
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>
    /// Ctrl+Z with typing pending in the focused field (a text field, or a number being typed)
    /// throws the typing away instead of undoing the last committed edit; returns false when
    /// nothing was pending, and the undo goes on to the history.
    /// </summary>
    public static bool DiscardPending(object? focused)
    {
        if (FieldOf(focused) is var (box, row) && IsTyped(box, row))
        {
            Revert(box, row);
            return true;
        }
        return focused is TextBox { Classes: var c } editor && c.Contains("scrubedit")
            && editor.FindAncestorOfType<ScrubNumberBox>() is { } scrub && scrub.CancelTyping();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyWidthCap();
    }

    /// <summary>
    /// The form keeps every editor to one edge; a card (no marks slot) has no column of edges to keep, so its editors take
    /// the card's width. A column definition has no DataContext to bind through, so the cap is set here.
    /// </summary>
    private void ApplyWidthCap() =>
        Root.ColumnDefinitions[0].MaxWidth = ShowMarks && DataContext is PropertyItemViewModel row ? row.EditorMaxWidth : double.PositiveInfinity;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { DataContext: MainViewModel shell } top)
        {
            shell.DiscardPendingInput ??= () => DiscardPending(top.FocusManager?.GetFocusedElement());
            shell.CommitPendingInput ??= () => CommitPending(top.FocusManager?.GetFocusedElement());
        }
    }

    /// <summary>Ctrl+S with typing pending in the focused field: the typed value is committed first, so it is saved.</summary>
    public static void CommitPending(object? focused)
    {
        if (FieldOf(focused) is var (box, row))
            Commit(box, row);
        else if (focused is TextBox { Classes: var c } editor && c.Contains("scrubedit"))
            editor.FindAncestorOfType<ScrubNumberBox>()?.CommitTyping();
    }

    private static void Field_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (FieldOf(e.Source) is var (box, row))
            Commit(box, row);
    }
}
