using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CallOfFile;

namespace Apex.Editor.Services.Preview.Formats;

/// <summary>
/// Loads XANIM_EXPORT (v3 text) and XANIM_BIN (LZ4) files into a <see cref="PreviewAnim"/> using the
/// vendored CallOfFile token reader. Data is left in BO3 space (Z-up, inches).
/// </summary>
/// <remarks>
/// The CallOfFile text reader resolves ambiguous token names by first match, so <c>PART i "name"</c>
/// and notetrack <c>FRAME i "name"</c> lines lose their trailing string. For <c>*_export</c> files the
/// string data (part names, notetracks) is recovered with a light text pre-scan; <c>*_bin</c> files are
/// hash-addressed and carry the names directly.
/// </remarks>
public static class AnimReader
{
    private enum Section { Header, Frames, Notes }

    private static readonly Regex PartNameRegex =
        new(@"^\s*PART\s+(\d+)\s+""([^""]*)""", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex NoteFrameRegex =
        new(@"^\s*FRAME\s+(\d+)\s+""([^""]*)""", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex FrameLineRegex = new(@"^\s*FRAME\b", RegexOptions.Compiled);

    public static Task<PreviewAnim?> LoadAsync(string path, CancellationToken ct)
        => Task.Run(() => Load(path, ct), ct);

    /// <summary><see cref="LoadAsync"/> on the calling thread (already a worker).</summary>
    public static PreviewAnim? Load(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        using var reader = ExportReaderSupport.OpenReader(path);
        if (reader is null)
            return null;

        var isExport = path.EndsWith(".xanim_export", StringComparison.OrdinalIgnoreCase);

        Dictionary<int, string>? textPartNames = null;
        List<(string Name, int Frame)>? textNotes = null;
        if (isExport)
            PreScanText(path, out textPartNames, out textNotes);

        var anim = new PreviewAnim();
        var section = Section.Header;

        int currentPart = -1;
        Vector3 rowX = Vector3.UnitX, rowY = Vector3.UnitY, rowZ = Vector3.UnitZ;

        try
        {
            TokenData? td;
            while ((td = reader.RequestNextToken()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                var name = td.Token.Name;

                switch (name)
                {
                    case "NUMPARTS":
                        section = Section.Header;
                        break;

                    case "PART":
                        {
                            int index = td switch
                            {
                                TokenDataUIntString s => (int)s.IntegerValue,
                                TokenDataUInt u => (int)u.Value,
                                _ => -1,
                            };
                            if (section == Section.Header)
                            {
                                var partName = td is TokenDataUIntString ts && ts.StringValue.Length > 0
                                    ? ts.StringValue
                                    : textPartNames is not null && index >= 0 && textPartNames.TryGetValue(index, out var n)
                                        ? n
                                        : $"part_{Math.Max(index, anim.PartNames.Count)}";
                                while (anim.PartNames.Count <= index && index >= 0)
                                    anim.PartNames.Add(string.Empty);
                                if (index >= 0 && index < anim.PartNames.Count)
                                    anim.PartNames[index] = partName;
                                else
                                    anim.PartNames.Add(partName);
                            }
                            else
                            {
                                currentPart = index;
                                rowX = Vector3.UnitX;
                                rowY = Vector3.UnitY;
                                rowZ = Vector3.UnitZ;
                            }
                        }
                        break;

                    case "FRAMERATE":
                        if (td is TokenDataUInt fr && fr.Value > 0)
                            anim.FrameRate = fr.Value;
                        break;

                    case "NUMFRAMES":
                        if (td is TokenDataUInt nf)
                            anim.FrameCount = (int)nf.Value;
                        break;

                    case "FRAME":
                        if (td is TokenDataUIntString note)
                        {
                            // A notetrack key (bin only). Bins carry no NOTETRACKS section token: the keys follow the
                            // last frame's parts directly, so the key itself is what ends the frames.
                            section = Section.Notes;
                            if (textNotes is null)
                                anim.Notetracks.Add((note.StringValue, (int)note.IntegerValue));
                        }
                        else if (section != Section.Notes)
                        {
                            section = Section.Frames;
                            anim.Frames.Add(new PreviewAnimFrame(Math.Max(anim.PartNames.Count, 1)));
                            currentPart = -1;
                        }
                        break;

                    case "OFFSET":
                        if (section == Section.Frames && td is TokenDataVector3 off &&
                            anim.Frames.Count > 0 && currentPart >= 0 && currentPart < anim.Frames[^1].Positions.Length)
                        {
                            anim.Frames[^1].Positions[currentPart] = off.Value;
                        }
                        break;

                    case "X":
                        if (section == Section.Frames && td is TokenDataVector3 rx) rowX = rx.Value;
                        break;
                    case "Y":
                        if (section == Section.Frames && td is TokenDataVector3 ry) rowY = ry.Value;
                        break;
                    case "Z":
                        if (section == Section.Frames && td is TokenDataVector3 rz &&
                            anim.Frames.Count > 0 && currentPart >= 0 && currentPart < anim.Frames[^1].Rotations.Length)
                        {
                            rowZ = rz.Value;
                            anim.Frames[^1].Rotations[currentPart] = RowsToMatrix(rowX, rowY, rowZ);
                        }
                        break;

                    case "NOTETRACKS":
                        section = Section.Notes;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lenient: keep whatever parsed before a malformed/truncated token, and say what was lost.
            anim.ReadProblem = section == Section.Notes ? "notetracks could not be read"
                : anim.Frames.Count == 0 ? "file unreadable"
                : $"file unreadable after frame {anim.Frames.Count}";
        }

        if (textNotes is not null)
            anim.Notetracks.AddRange(textNotes);

        if (anim.FrameCount == 0)
            anim.FrameCount = anim.Frames.Count;

        return anim;
    }

    private static void PreScanText(string path, out Dictionary<int, string> partNames, out List<(string, int)> notes)
    {
        partNames = new Dictionary<int, string>();
        notes = new List<(string, int)>();
        try
        {
            // One streamed pass. Only the part-declaration header (before the first FRAME) is scanned for part names,
            // so that notetrack "PART i" lines (which have no quoted name) do not overwrite entries; notetrack keys are
            // the only FRAME lines carrying a quoted string.
            bool header = true;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.StartsWith("PART") && header)
                {
                    if (PartNameRegex.Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var idx))
                        partNames[idx] = m.Groups[2].Value;
                }
                else if (trimmed.StartsWith("FRAME"))
                {
                    header &= !FrameLineRegex.IsMatch(line);
                    if (NoteFrameRegex.Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var frame))
                        notes.Add((m.Groups[2].Value, frame));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Matrix4x4 RowsToMatrix(Vector3 x, Vector3 y, Vector3 z) => new(
        x.X, x.Y, x.Z, 0f,
        y.X, y.Y, y.Z, 0f,
        z.X, z.Y, z.Z, 0f,
        0f, 0f, 0f, 1f);
}
