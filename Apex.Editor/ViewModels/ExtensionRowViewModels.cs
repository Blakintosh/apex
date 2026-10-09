using System;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// The line a form starts with while an extension is off for its asset: the manifest's own words (<c>offNotice</c>) and
/// one action, Turn on, which flips the extension's switch as a click on it would (one undo step).
/// </summary>
public sealed class ExtensionOffRowViewModel : Controls.IRowStandIn
{
    public ExtensionOffRowViewModel(string text, Action turnOn)
    {
        Text = text;
        TurnOnCommand = new RelayCommand(turnOn);
    }

    public string Text { get; }

    public IRelayCommand TurnOnCommand { get; }

    object? Controls.IRowStandIn.CreateStandIn() => new ExtensionOffRowViewModel("", () => { });
}

/// <summary>A sub-section's title inside a section ("LOD 1" under LODs: the deffile's <c>BeginCategory("LODs.LOD 1")</c>).</summary>
public sealed class SubsectionRowViewModel(string title) : Controls.IRowStandIn
{
    public string Title { get; } = title;

    object? Controls.IRowStandIn.CreateStandIn() => new SubsectionRowViewModel("");
}

/// <summary>A manifest's notice line under a section header (<c>notice</c>), while the extension is on.</summary>
public sealed class ExtensionNoticeRowViewModel(string text) : Controls.IRowStandIn
{
    public string Text { get; } = text;

    object? Controls.IRowStandIn.CreateStandIn() => new ExtensionNoticeRowViewModel("");
}
