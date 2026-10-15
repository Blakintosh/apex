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
        if (parsed.IsStockClone)
        {
            OpenStockClone(parsed);
            return;
        }
        if (FindLinked(parsed) is not { } rec)
        {
            // Radiant's model browser lists the shipped models too, and its Open in Apex sends them as plain links
            if (FindStock(parsed) is { } hit)
            {
                OpenAsset(StockRecordFor(hit));
                Status = $"Opened {parsed.Name} from a link";
                return;
            }
            Alert(Stock.State == Services.Stock.StockState.Indexing
                ? $"{parsed.Name} isn't in the loaded GDTs, and the stock library is still being indexed. Open the link again when it is done."
                : parsed.Type.Length == 0
                    ? $"There's no asset named {parsed.Name} in the loaded GDTs or the stock library."
                    : $"There's no {parsed.Type} named {parsed.Name} in the loaded GDTs or the stock library.", isError: false);
            return;
        }
        OpenAsset(rec);
        Status = $"Opened {rec.Name} from a link";
    }

    /// <summary>The shipped asset a link names, or null when there is none or the stock library can't be searched.</summary>
    private Services.Stock.StockHit? FindStock(AssetLink link)
    {
        if (!Stock.CanSearch || Stock.Db is not { } db)
            return null;
        try
        {
            return db.Find(link.Type, link.Name, LinkKinds);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            return null;
        }
    }

    private static readonly string[] LinkKinds = ["xmodel", "material", "image", "weapon", "xanim", "attachmentunique"];

    /// <summary>
    /// <c>asset://name.type?stock=clone&amp;map=zm_repl</c> (Radiant's "create a local copy of this stock asset"): opens the stock
    /// asset and the Create local copy dialog on it, with the map the link names as the target.
    /// </summary>
    private void OpenStockClone(AssetLink link)
    {
        if (!Stock.CanSearch || Stock.Db is null)
        {
            Alert(Stock.State == Services.Stock.StockState.Indexing
                ? "The stock library is still being indexed. Open the link again when it is done."
                : "The stock library isn't available, so Apex can't offer a copy of a shipped asset.", isError: false);
            return;
        }
        if (FindStock(link) is not { } hit)
        {
            Alert(link.Type.Length == 0
                ? $"The game doesn't ship an asset named {link.Name}."
                : $"The game doesn't ship a {link.Type} named {link.Name}.", isError: false);
            return;
        }
        var record = StockRecordFor(hit);
        OpenAsset(record);
        Status = $"Opened {link.Name} from a link";
        OpenCloneDialog(record, link.Map);
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
