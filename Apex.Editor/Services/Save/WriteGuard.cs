using System;
using System.IO;

namespace Apex.Editor.Services.Save;

/// <summary>
/// Development safety net for the save path. When <c>APEX_WRITE_ROOT</c> names one or more folders (separated by
/// <c>;</c>), every file the save path writes, renames, replaces or deletes must sit inside one of them, or the
/// operation throws before touching anything. Apex.Shots and every test set it to their temp folder, so no test can
/// write under the BO3 install even by mistake. Unset (the app as users run it), it allows everything.
/// </summary>
public static class WriteGuard
{
    public const string Variable = "APEX_WRITE_ROOT";

    /// <summary>Throws <see cref="WriteOutsideRootException"/> when <paramref name="path"/> is outside the allowed roots.</summary>
    public static void Check(string path)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } roots)
            return;
        var full = Path.GetFullPath(path);
        foreach (var raw in roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new WriteOutsideRootException(full, roots);
    }
}

public sealed class WriteOutsideRootException(string path, string roots)
    : InvalidOperationException($"Refusing to write {path}: {WriteGuard.Variable} allows writes only under {roots}.");
