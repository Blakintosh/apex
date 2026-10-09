using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Apex.Editor.Commands;

/// <summary>
/// A catalog command bound to what it does in one window. It is the <see cref="ICommand"/> every menu item and button
/// for that command uses; <see cref="IsAvailable"/> is its context (the palette hides it when false, menus grey it).
/// </summary>
public sealed partial class AppCommand : ObservableObject, ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _available;
    private readonly Func<bool>? _checked;

    internal AppCommand(CommandInfo info, Action execute, Func<bool>? available, Func<bool>? isChecked)
    {
        Info = info;
        _execute = execute;
        _available = available ?? (() => true);
        _checked = isChecked;
        _isAvailable = _available();
        _isChecked = _checked?.Invoke() ?? false;
    }

    public CommandInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;

    /// <summary>Evaluated now, not the cached value: keys and the palette always see the current context.</summary>
    public bool CanRun => _available();

    public bool CurrentlyChecked => _checked?.Invoke() ?? false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>What its button says: the name and keys, or for a toggle that is on, what pressing it does now.</summary>
    public string Tip => IsChecked && Info.OnName is { } on ? Info.TipFor(on) : Info.Tip;

    public void Execute()
    {
        if (_available())
            _execute();
        Refresh();
    }

    internal void Refresh()
    {
        IsChecked = CurrentlyChecked;
        var available = _available();
        if (available != IsAvailable)
        {
            IsAvailable = available;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;
    bool ICommand.CanExecute(object? parameter) => _available();
    void ICommand.Execute(object? parameter) => Execute();
}

/// <summary>Every catalog command bound for one main window, looked up by id.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, AppCommand> _byId = new();
    private readonly List<AppCommand> _all = new();
    private bool _refreshQueued;

    public IReadOnlyList<AppCommand> All => _all;

    public AppCommand this[string id] => _byId[id];

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>While this holds, no command runs or shows as available (the window is waiting on something modal).</summary>
    public Func<bool>? Suspended { get; set; }

    /// <summary>
    /// The command run last and when (UTC): a handler that fails inside it is reported by that command's name, not as
    /// "something" (see MainViewModel.ReportRecovered).
    /// </summary>
    public (CommandInfo Command, DateTime At)? LastRun { get; private set; }

    public void Bind(string id, Action execute, Func<bool>? available = null, Func<bool>? isChecked = null)
    {
        var info = CommandCatalog.Get(id);
        var command = new AppCommand(info, () =>
            {
                LastRun = (info, DateTime.UtcNow);
                execute();
            },
            () => Suspended?.Invoke() != true && (available?.Invoke() ?? true), isChecked);
        _byId.Add(id, command);
        _all.Add(command);
    }

    /// <summary>Catalog commands nothing has bound: a command that is named but can't be run is a bug.</summary>
    public IEnumerable<CommandInfo> Unbound() => CommandCatalog.All.Where(c => !_byId.ContainsKey(c.Id));

    /// <summary>
    /// Re-reads every command's availability and check state once the current burst of changes is over, so a
    /// keystroke that raises ten property changes costs one pass.
    /// </summary>
    public void RequestRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(RefreshNow, DispatcherPriority.Background);
    }

    public void RefreshNow()
    {
        _refreshQueued = false;
        foreach (var c in _all)
            c.Refresh();
    }
}

/// <summary>What only the window can do for a command: move keyboard focus, open a menu at a control, use the clipboard.</summary>
public interface IShellView
{
    void FocusAssetSearch();
    void FocusPropertyFilter();

    /// <summary>Opens the Set view's Add property list.</summary>
    void OpenAddProperty();
    void ShowAllTabs();
    void CyclePaneFocus(int delta);
    void FramePreview();
    void CopyText(string text);

    /// <summary>About Apex: the version and what Apex is, under the mark.</summary>
    void ShowAbout();

    /// <summary>Puts text on the system clipboard; never throws (a clipboard another program holds is skipped).</summary>
    System.Threading.Tasks.Task CopyTextAsync(string text);

    /// <summary>The system clipboard's text (null: it holds none); Read is false when it couldn't be read.</summary>
    System.Threading.Tasks.Task<(bool Read, string? Text)> GetClipboardTextAsync();
}
