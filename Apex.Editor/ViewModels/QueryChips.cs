using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>One filter in a search box, shown as a chip: <c>type:xanim</c>, <c>gdt:blak_wpn_mg42_anims</c>, <c>is:changed</c>.</summary>
public sealed partial class SearchChip
{
    private readonly Action<SearchChip> _remove;

    public SearchChip(QueryToken token, Action<SearchChip> remove)
    {
        Token = token;
        _remove = remove;
        (Glyph, GlyphBrush, Label) = token.Kind switch
        {
            TokenKind.Type => (TypeStyles.Glyph(token.Value), TypeStyles.Brush(token.Value), token.Value),
            TokenKind.Gdt => ("▣", TypeStyles.Brush("gdt"), token.Value),
            TokenKind.Prop => ("", TypeStyles.NeutralBrush,
                (token.Key.Length == 0 ? "any property" : token.Key) + (token.Op.Length == 0 ? "" : $" {token.Op} {token.Value}")),
            TokenKind.Changed => ("", TypeStyles.NeutralBrush, "changed"),
            TokenKind.Modified => ("", TypeStyles.NeutralBrush, "off-default"),
            TokenKind.Problems => ("", TypeStyles.NeutralBrush, "problems"),
            _ => ("", TypeStyles.NeutralBrush, token.Raw),
        };
    }

    public QueryToken Token { get; }
    public string Raw => Token.Raw;
    public TokenKind Kind => Token.Kind;
    public string Glyph { get; }
    public bool HasGlyph => Glyph.Length > 0;
    public IBrush GlyphBrush { get; }
    public string Label { get; }
    /// <summary>The ✕'s accessible name and tooltip.</summary>
    public string RemoveName => $"Remove {Raw}";

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>
/// A query as a search box shows it: the filters as chips, the name words as text. Typed syntax turns into a
/// chip once it is finished (a space after it), so typing <c>type:xanim </c> and picking xanim end in the same
/// place. <see cref="Query"/> is the query language <see cref="AssetQuery"/> reads; order never changes its meaning.
/// </summary>
public sealed partial class QueryChips : ObservableObject
{
    private bool _quiet;

    public RangeObservableCollection<SearchChip> Chips { get; } = new();

    /// <summary>The words typed after the chips, exactly as typed.</summary>
    [ObservableProperty]
    private string _words = "";

    /// <summary>Raised after any change to the chips or the words (not for <see cref="SetQuery"/>).</summary>
    public event Action? Changed;

    public bool HasChips => Chips.Count > 0;

    public string Query
    {
        get
        {
            var chips = string.Join(' ', Chips.Select(c => c.Raw));
            return chips.Length == 0 ? Words : Words.Length == 0 ? chips : chips + " " + Words;
        }
    }

    /// <summary>Shows <paramref name="query"/>: its filters become chips, its name words the text.</summary>
    public void SetQuery(string query)
    {
        var chips = new List<SearchChip>();
        var words = new List<string>();
        foreach (var raw in AssetQuery.Tokenize(query))
        {
            if (AssetQuery.Parse(raw) is [{ Kind: not TokenKind.Name } token])
            {
                if (chips.All(c => !c.Raw.Equals(raw, StringComparison.OrdinalIgnoreCase)))
                    chips.Add(new SearchChip(token, Remove));
            }
            else
                words.Add(raw);
        }
        _quiet = true;
        Chips.ReplaceAll(chips);
        // A query with no filters is shown exactly as typed (trailing space and all): it is the text in the box.
        Words = chips.Count == 0 ? query : string.Join(' ', words);
        _quiet = false;
        OnPropertyChanged(nameof(HasChips));
    }

    partial void OnWordsChanged(string value)
    {
        if (_quiet)
            return;
        // The chip appears at once; the text it came from leaves once the box has finished its own update (a text box
        // ignores a new value pushed back while it is still writing the one just typed).
        if (MoveFinishedFilters(value, out _) > 0)
        {
            OnPropertyChanged(nameof(HasChips));
            Dispatcher.UIThread.Post(StripFinishedFilters);
        }
        Changed?.Invoke();
    }

    /// <summary>Makes chips of the finished filters (followed by a space) in <paramref name="text"/>; the word still being typed stays.</summary>
    private int MoveFinishedFilters(string text, out List<string> rest)
    {
        var parts = AssetQuery.Tokenize(text).ToList();
        var finished = text.Length > 0 && char.IsWhiteSpace(text[^1]) ? parts.Count : parts.Count - 1;
        var moved = 0;
        for (var i = 0; i < finished; i++)
        {
            if (AssetQuery.Parse(parts[i]) is not [{ Kind: not TokenKind.Name } token])
                continue;
            AddQuietly(token);
            parts[i] = "";
            moved++;
        }
        rest = parts.Where(p => p.Length > 0).ToList();
        return moved;
    }

    private void StripFinishedFilters()
    {
        if (MoveFinishedFilters(Words, out var rest) == 0)
            return;
        _quiet = true;
        Words = string.Join(' ', rest);
        _quiet = false;
        OnPropertyChanged(nameof(HasChips));
        Changed?.Invoke();
    }

    /// <summary>Adds the filter <paramref name="raw"/> as a chip unless one like it is there.</summary>
    public void Add(string raw)
    {
        if (AssetQuery.Parse(raw) is not [{ Kind: not TokenKind.Name } token] || !AddQuietly(token))
            return;
        OnPropertyChanged(nameof(HasChips));
        Changed?.Invoke();
    }

    /// <summary>Adds <paramref name="raw"/>, or removes every chip meaning the same when it is there.</summary>
    public void Toggle(string raw)
    {
        if (Find(raw) is { } existing)
            Remove(existing);
        else
            Add(raw);
    }

    public bool Contains(string raw) => Find(raw) is not null;

    public void Remove(SearchChip chip)
    {
        if (!Chips.Remove(chip))
            return;
        OnPropertyChanged(nameof(HasChips));
        Changed?.Invoke();
    }

    /// <summary>Backspace with nothing typed: the last chip goes (only of <paramref name="kind"/>, when given).</summary>
    public bool RemoveLast(TokenKind? kind = null)
    {
        var last = Chips.LastOrDefault(c => kind is null || c.Kind == kind);
        if (last is null)
            return false;
        Remove(last);
        return true;
    }

    public void Clear()
    {
        if (Chips.Count == 0 && Words.Length == 0)
            return;
        _quiet = true;
        Chips.ReplaceAll(Array.Empty<SearchChip>());
        Words = "";
        _quiet = false;
        OnPropertyChanged(nameof(HasChips));
        Changed?.Invoke();
    }

    /// <summary>The chip meaning the same as <paramref name="raw"/>: <c>is:modified</c> is <c>is:off-default</c>, <c>t:fx</c> is <c>type:fx</c>.</summary>
    private SearchChip? Find(string raw) =>
        AssetQuery.Parse(raw) is [{ } token] ? Chips.FirstOrDefault(c => SameFilter(c.Token, token)) : null;

    private static bool SameFilter(QueryToken a, QueryToken b) =>
        a.Kind == b.Kind && a.Kind switch
        {
            TokenKind.Changed or TokenKind.Modified or TokenKind.Problems => true,
            TokenKind.Prop => a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Op == b.Op
                              && a.Value.Equals(b.Value, StringComparison.OrdinalIgnoreCase),
            _ => a.Value.Equals(b.Value, StringComparison.OrdinalIgnoreCase),
        };

    private bool AddQuietly(QueryToken token)
    {
        if (Chips.Any(c => SameFilter(c.Token, token)))
            return false;
        Chips.Add(new SearchChip(token, Remove));
        return true;
    }
}
