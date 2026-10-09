using System;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Apex.Editor.Controls;

/// <summary>
/// A menu item that UI Automation can invoke. Avalonia's own MenuItem peer offers no Invoke pattern, so a screen reader
/// or an automation client finds the item but can't activate it. Invoking does what a click does (the Click handlers
/// and the command run), then closes the menu. Styled exactly as a MenuItem.
/// </summary>
public class InvokableMenuItem : MenuItem
{
    protected override Type StyleKeyOverride => typeof(MenuItem);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void InvokeFromAutomation()
    {
        if (!IsEffectivelyEnabled)
            return;
        // Raised, not OnClick called: Click= handlers only hear the routed event, and MenuItem's class handler for it
        // runs OnClick (the command) once.
        RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        // Only a context menu closes here; these items are only used in one (a menu bar or flyout would need its own).
        this.FindLogicalAncestorOfType<ContextMenu>()?.Close();
    }

    private sealed class Peer(InvokableMenuItem owner) : MenuItemAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.InvokeFromAutomation();
    }
}
