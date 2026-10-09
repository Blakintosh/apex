using Avalonia;

namespace Apex.Editor.Controls;

/// <summary>
/// Details on demand: whether the controls classed <c>.live</c> under a property row show. The row sets it (off, on
/// while hovered or focused) and it inherits down to them; outside a row it stays on. A descendant selector
/// (<c>Border.prow:pointerover .live</c>) did the same, but it has every control in a row subscribe to the row's classes
/// and pointer state, and those subscriptions outlived the editor views they belonged to.
/// </summary>
public sealed class RowDetails
{
    private RowDetails()
    {
    }

    public static readonly AttachedProperty<bool> ShowProperty =
        AvaloniaProperty.RegisterAttached<RowDetails, AvaloniaObject, bool>("Show", defaultValue: true, inherits: true);

    public static bool GetShow(AvaloniaObject o) => o.GetValue(ShowProperty);

    public static void SetShow(AvaloniaObject o, bool value) => o.SetValue(ShowProperty, value);
}
