using System.Globalization;
using System.Text.RegularExpressions;

namespace Apex.Render.Data.Gdt;

/// <summary>
/// Field defaults of an asset type, read from its deffile script (<c>deffiles\&lt;type&gt;.awi</c>). A GDT only stores
/// values that differ from these, so anything evaluating GDT fields (<see cref="Techsets.MaterialEvaluator"/>) needs
/// them underneath. The scripts are not executed: every <c>AddEntry_*</c> call is scraped for its default argument
/// (or <c>.SetDefaultValue(…)</c>), combos default to their first choice (string variables resolved), check boxes to
/// 0/1 and vector entries expand into their per-component fields. When an entry is declared in several branches the
/// last declaration wins. This reproduces the <c>material.awi</c> defaults APE evaluates materials with (verified
/// through the RenderCheck captures).
/// </summary>
public static partial class DeffileDefaults
{
    [GeneratedRegex(@"string\s+(\w+)\s*=\s*""([^""]*)""")]
    private static partial Regex StringVarRx();

    [GeneratedRegex(@"AddEntry_(\w+)\s*\((.*?)\)\s*(\..*)?$")]
    private static partial Regex EntryRx();

    [GeneratedRegex(@"SetDefaultValue\(\s*""([^""]*)""\s*\)")]
    private static partial Regex DefaultRx();

    /// <summary>Defaults of <c>deffiles\&lt;type&gt;.awi</c> under <paramref name="install"/>; empty when the file is missing.</summary>
    public static IReadOnlyDictionary<string, string> ForType(ToolsGfxInstall install, string type)
    {
        var path = Path.Combine(install.Root, "deffiles", type + ".awi");
        return File.Exists(path) ? Load(path) : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Scrapes one .awi script.</summary>
    public static Dictionary<string, string> Load(string awiPath) => Parse(File.ReadLines(awiPath));

    /// <summary>Scrapes .awi script lines.</summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;
            var sv = StringVarRx().Match(line);
            if (sv.Success && !vars.ContainsKey(sv.Groups[1].Value))
                vars[sv.Groups[1].Value] = sv.Groups[2].Value;

            var m = EntryRx().Match(line);
            if (!m.Success)
                continue;
            var kind = m.Groups[1].Value;
            var args = SplitArgs(m.Groups[2].Value);
            // Names built at run time ("cg" + Digit + "_x") cannot be scraped.
            if (args.Count == 0 || !IsLiteral(args[0]))
                continue;
            var name = args[0].Trim('"');
            var def = DefaultRx().Match(m.Groups[3].Value);
            switch (kind)
            {
                case "Color" when args.Count >= 5:
                    result[name] = string.Join(' ', args.Skip(1).Take(4).Select(Num));
                    break;
                case "Float" or "Int" when args.Count >= 2:
                    result[name] = Num(args[1]);
                    break;
                case "CheckBox" when args.Count >= 2:
                    result[name] = args[1].Trim() == "true" ? "1" : "0";
                    break;
                case "Vector2" when args.Count >= 4 && IsLiteral(args[1]):
                    result[name] = Num(args[2]);
                    result[args[1].Trim('"')] = Num(args[3]);
                    break;
                case "Vector3" when args.Count >= 6 && IsLiteral(args[1]) && IsLiteral(args[2]):
                    result[name] = Num(args[3]);
                    result[args[1].Trim('"')] = Num(args[4]);
                    result[args[2].Trim('"')] = Num(args[5]);
                    break;
                case "Vector4" when args.Count >= 8 && IsLiteral(args[1]) && IsLiteral(args[2]) && IsLiteral(args[3]):
                    result[name] = Num(args[4]);
                    result[args[1].Trim('"')] = Num(args[5]);
                    result[args[2].Trim('"')] = Num(args[6]);
                    result[args[3].Trim('"')] = Num(args[7]);
                    break;
                case "Combo" when args.Count >= 2:
                    if (def.Success)
                    {
                        result[name] = def.Groups[1].Value;
                    }
                    else
                    {
                        var list = args[1].StartsWith('"') ? args[1].Trim('"') : vars.GetValueOrDefault(args[1].Trim()) ?? "";
                        result[name] = list.Split('|')[0].Trim();
                    }
                    break;
                case "String" when args.Count >= 2 && IsLiteral(args[1]):
                    result[name] = args[1].Trim('"');
                    break;
            }
            if (def.Success && kind != "Combo")
                result[name] = def.Groups[1].Value;
        }
        return result;

        static string Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : s.Trim();
    }

    private static bool IsLiteral(string arg) =>
        arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"' && arg.IndexOf('"', 1) == arg.Length - 1;

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') depth--;
            else if (!quoted && depth == 0 && c == ',')
            {
                list.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        list.Add(s[start..].Trim());
        return list;
    }
}
