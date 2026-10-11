using System;
using System.Collections.Generic;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>
/// <c>asset://name.type</c> links (Radiant's asset hyperlinks): Apex started with one, or a later start that handed its
/// links to this window (<see cref="LinkInstance"/>), opens the asset. Links that arrive while the GDTs are still loading
/// wait for the load.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<string> _pendingLinks = new();
    private bool _linksReady;

    /// <summary>A link was taken (opened, or found wanting): the window should come to the front.</summary>
    public event Action? LinkArrived;

    /// <summary>Opens the asset <paramref name="link"/> names, now or once the GDTs have loaded.</summary>
    public void OpenLink(string link)
    {
        LinkArrived?.Invoke();
        if (!_linksReady)
        {
            _pendingLinks.Add(link);
            return;
        }
        if (AssetLink.Parse(link) is not { } parsed)
        {
            Alert($"Apex can't open '{link}': an asset link is asset://<name>.<type>, e.g. asset://ar_standard.bulletweapon.", isError: false);
            return;
        }
        if (FindLinked(parsed) is not { } rec)
        {
            Alert(parsed.Type.Length == 0
                ? $"There's no asset named {parsed.Name} in the loaded GDTs."
                : $"There's no {parsed.Type} named {parsed.Name} in the loaded GDTs.", isError: false);
            return;
        }
        OpenAsset(rec);
        Status = $"Opened {rec.Name} from a link";
    }

    /// <summary>The asset a link names: of its type when there is one; FindAsset's first record of the name otherwise.</summary>
    private Models.AssetRecord? FindLinked(AssetLink link)
    {
        var rec = FindAsset(link.Type, link.Name);
        return rec is not null && link.Type.Length > 0 && !rec.Type.Equals(link.Type, StringComparison.OrdinalIgnoreCase)
            && !(IsWeaponFamilyRef(link.Type) && Services.Extensions.ExtensionManifest.IsWeapon(rec.Type))
            ? null
            : rec;
    }

    /// <summary>The GDTs are in (or there are none to wait for): open the links that came meanwhile.</summary>
    private void LinksReady()
    {
        _linksReady = true;
        var pending = _pendingLinks.ToArray();
        _pendingLinks.Clear();
        foreach (var link in pending)
            OpenLink(link);
    }
}
