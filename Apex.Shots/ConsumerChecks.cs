using System;
using System.IO;
using System.Linq;
using Apex.Editor.Services.Extensions;

namespace Apex.Shots;

/// <summary>
/// The consumer probe: a manifest names a file in the install that carries a marker while the tool that reads the
/// extension's values is installed, and its notice goes while it does. Pure and on temp folders; nothing is written
/// outside them.
/// </summary>
public partial class Program
{
    private static void ConsumerChecks()
    {
        const string Marker = "generated:apex-gdtx";

        ExtensionManifest Load(string consumerJson, out string said)
        {
            var dir = NewScratch("pass-consumer-load");
            Directory.CreateDirectory(Path.Combine(dir, "c"));
            File.WriteAllText(Path.Combine(dir, "c", ExtensionLoader.FileName),
                "{ \"apexSchema\": 1, \"id\": \"c\", \"version\": \"1\", \"targets\": [\"weapon\"], \"notice\": \"Not installed.\"" + consumerJson
                + ", \"sections\": [ { \"title\": \"S\", \"fields\": [ { \"key\": \"cNum\", \"kind\": \"number\" } ] } ] }");
            var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
            said = string.Join(" | ", diagnostics.Select(d => $"{d.Problem}: {d.Message}"));
            return manifests.Single();
        }

        // ── The manifest member ──
        var good = Load(", \"consumer\": { \"file\": \"bin/hook.dll\", \"contains\": \"" + Marker + "\" }", out var goodSaid);
        Check($"consumer: a file in the install and a text to look for in it ({good.Consumer})",
            good.Consumer is { File: "bin/hook.dll", Contains: Marker } && goodSaid.Length == 0);
        Check("consumer: a manifest without one has none, and its notice is always its own",
            Load("", out _) is { Consumer: null } plain && ExtensionConsumerProbe.NoticeFor(plain, Path.GetTempPath()) == "Not installed.");

        string[] bad =
        {
            ", \"consumer\": 5",
            ", \"consumer\": { \"file\": \"C:/Windows/x.dll\", \"contains\": \"a\" }",
            ", \"consumer\": { \"file\": \"../x.dll\", \"contains\": \"a\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll:stream\", \"contains\": \"a\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll.\", \"contains\": \"a\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll\", \"contains\": \"\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll\", \"contains\": \"caf\u00e9\" }",
            ", \"consumer\": { \"file\": \"bin/x.dll\", \"contains\": \"" + new string('a', ExtensionConsumerProbe.MaxTextLength + 1) + "\" }",
        };
        foreach (var json in bad)
        {
            var m = Load(json, out var said);
            Check($"consumer: '{json[..Math.Min(json.Length, 70)]}' leaves the consumer out, says so, and loads the rest ({said.Length > 0})",
                m.Consumer is null && said.Length > 0 && m.Sections.Count == 1 && ExtensionConsumerProbe.NoticeFor(m, null) == "Not installed.");
        }
        var extra = Load(", \"consumer\": { \"file\": \"bin/hook.dll\", \"contains\": \"" + Marker + "\", \"activeIn\": { \"glob\": \"usermaps/*/linker.json\" } }", out var extraSaid);
        Check($"consumer: a member Apex doesn't know is noted and ignored ({extraSaid})",
            extra.Consumer is not null && extraSaid.Contains("doesn't know the consumer member 'activeIn'"));

        // ── The probe ──
        var root = NewScratch("pass-consumer-root");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var hook = Path.Combine(bin, "hook.dll");
        ExtensionConsumerProbe.Reset();
        Check("consumer: no install to look in is unknown, and the notice shows",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, null) == ConsumerState.Unknown
            && ExtensionConsumerProbe.Evaluate(good.Consumer!, Path.Combine(root, "nope")) == ConsumerState.Unknown
            && ExtensionConsumerProbe.NoticeFor(good, null) == "Not installed.");
        Check("consumer: no file is missing, and the notice shows",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, root) == ConsumerState.Missing && ExtensionConsumerProbe.NoticeFor(good, root) == "Not installed.");
        File.WriteAllBytes(hook, new byte[] { 1, 2, 3, 4 });
        Check("consumer: a file without the text is missing too (another build of the tool, or something else)",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, root) == ConsumerState.Missing);

        var withMarker = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes(Marker).CopyTo(withMarker, 1000);
        File.WriteAllBytes(hook, withMarker);
        File.SetLastWriteTimeUtc(hook, DateTime.UtcNow.AddSeconds(2));
        Check("consumer: the text in the file is present, and the notice goes",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, root) == ConsumerState.Present && ExtensionConsumerProbe.NoticeFor(good, root) is null);

        File.WriteAllBytes(hook, new byte[4096]);
        File.SetLastWriteTimeUtc(hook, DateTime.UtcNow.AddSeconds(4));
        Check("consumer: a changed file is read again, so removing the tool brings the notice back",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, root) == ConsumerState.Missing && ExtensionConsumerProbe.NoticeFor(good, root) == "Not installed.");

        var big = new byte[ExtensionConsumerProbe.MaxFileBytes + 1];
        System.Text.Encoding.ASCII.GetBytes(Marker).CopyTo(big, 10);
        File.WriteAllBytes(hook, big);
        File.SetLastWriteTimeUtc(hook, DateTime.UtcNow.AddSeconds(6));
        Check("consumer: a file over the size cap isn't read (unknown, so the notice stays)",
            ExtensionConsumerProbe.Evaluate(good.Consumer!, root) == ConsumerState.Unknown && ExtensionConsumerProbe.NoticeFor(good, root) == "Not installed.");
        File.Delete(hook);

        Check("consumer: a path that leaves the install is refused by the probe too",
            ExtensionConsumerProbe.Resolve(root, "../x") is null && ExtensionConsumerProbe.Resolve(root, "a/../../x") is null
            && ExtensionConsumerProbe.Resolve(root, "C:/x") is null && ExtensionConsumerProbe.Resolve(root, "/x") is null
            && ExtensionConsumerProbe.Resolve(root, "bin\\hook.dll") == hook && ExtensionConsumerProbe.Resolve(root, "bin/hook.dll") == hook);
        Check("consumer: an unreadable path in a manifest handed straight to the probe is unknown, not an exception",
            ExtensionConsumerProbe.Evaluate(new ExtensionConsumer("../outside.dll", "a"), root) == ConsumerState.Unknown);
    }
}
