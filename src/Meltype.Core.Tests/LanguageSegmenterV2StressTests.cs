// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>
/// Property/stress tests for the experimental raw-keystroke language segmenter.
/// These intentionally go beyond individual bug reports so V2 cannot be tuned to
/// only a handful of known examples.
/// </summary>
internal static class LanguageSegmenterV2StressTests
{
    private static readonly CompositionDetector Detector = CompositionTests.Detector;

    private static string Show(string typed, bool v2, string? before = null, string? after = null)
    {
        var previous = Detector.UseExperimentalLanguageSegmenterV2;
        Detector.UseExperimentalLanguageSegmenterV2 = v2;
        try
        {
            var k = new CompositionTests.Keyboard();
            k.Host.PrecedingText = before;
            k.Host.FollowingText = after;
            k.Type(typed);
            return k.Showing ?? "";
        }
        finally
        {
            Detector.UseExperimentalLanguageSegmenterV2 = previous;
        }
    }

    private static string Commit(string typed, bool v2)
    {
        var previous = Detector.UseExperimentalLanguageSegmenterV2;
        Detector.UseExperimentalLanguageSegmenterV2 = v2;
        try
        {
            var k = new CompositionTests.Keyboard();
            k.Type(typed + "\n");
            return k.Host.Document;
        }
        finally
        {
            Detector.UseExperimentalLanguageSegmenterV2 = previous;
        }
    }

    private static string SegmentSignature(string typed) =>
        string.Join("|", Segments(typed).Select(s =>
            $"{(s.IsEnglish ? "E" : "J")}:{s.Raw}:{s.Kana}"));

    private static string UnitSignature(string typed)
    {
        var text = new CompositionText(Detector);
        foreach (var c in typed) text.Append(c);
        return string.Join("|", text.Units.Select(u => $"{u.Raw}:{u.Kana}")) +
            $"; pending={text.Pending}";
    }

    private static IReadOnlyList<CompositionSegment> Segments(string typed, bool final = false,
        bool? beforeEnglish = null, bool? afterEnglish = null, bool englishSentence = false)
    {
        var previous = Detector.UseExperimentalLanguageSegmenterV2;
        Detector.UseExperimentalLanguageSegmenterV2 = true;
        try
        {
            var text = new CompositionText(Detector);
            foreach (var c in typed) text.Append(c);
            return Detector.Segment(
                text.Units,
                text.Pending,
                beforeEnglish,
                afterEnglish,
                englishSentence: englishSentence,
                final: final);
        }
        finally
        {
            Detector.UseExperimentalLanguageSegmenterV2 = previous;
        }
    }

    [Test]
    public static void V2_MixedEnglishJapanese_Matrix()
    {
        // Strong English heads chosen from technical/common terms that users are
        // realistically likely to mix directly with Japanese without spaces.
        var english = new[]
        {
            "commit", "issue", "reflect", "invite", "github", "google",
            "python", "docker", "branch", "build", "server", "client",
            "debug", "deploy", "merge", "cache", "kernel", "linux",
            "windows", "ubuntu", "typescript", "javascript", "terminal",
            "package", "request", "response", "network", "update",
        };

        var suffixes = new Dictionary<string, string>
        {
            ["ha"] = "は",
            ["ga"] = "が",
            ["wo"] = "を",
            ["ni"] = "に",
            ["de"] = "で",
            ["to"] = "と",
            ["no"] = "の",
            ["suru"] = "する",
            ["shita"] = "した",
            ["shite"] = "して",
            ["shitai"] = "したい",
            ["shimasu"] = "します",
            ["shimashita"] = "しました",
            ["sareta"] = "された",
            ["miru"] = "みる",
            ["tsukau"] = "つかう",
            ["naosu"] = "なおす",
            ["okuru"] = "おくる",
        };

        var failures = new List<string>();
        foreach (var word in english)
        {
            foreach (var (suffix, kana) in suffixes)
            {
                var typed = word + suffix;
                var expected = word + kana;
                var actual = Show(typed, v2: true);
                if (actual != expected)
                    failures.Add($"{typed} => {actual} (expected {expected}); segments={SegmentSignature(typed)}; units={UnitSignature(typed)}");
            }
        }

        Assert.True(failures.Count == 0,
            $"mixed matrix: {failures.Count} failures\n" + string.Join("\n", failures.Take(30)));
    }

    [Test]
    public static void V2_DoesNotStealEnglishLookingSubstringsFromJapanese()
    {
        // These contain many accidental English words/prefixes: ben, red, line,
        // same, go, no, to, me, etc. V2 should not carve those out of Japanese.
        var japanese = new[]
        {
            "nihongowohanasu",
            "watashihagakuseidesu",
            "ashitahaamedesu",
            "kyouhaiitenkidesune",
            "sumimasenkakuninshimasu",
            "kanojohasushigasuki",
            "nihongonobenkyou",
            "arigatougozaimasu",
            "kaishaniikimasu",
            "itsumoarigatou",
            "tomodachitoasobu",
            "shiryouwookurimasu",
            "mondaihaarimasen",
            "korehapenndesu",
            "samuidesune",
            "nimotsuwookuru",
            "benkyouwosuru",
            "minnademiru",
            "sorewomotteiku",
            "kokonimottekite",
            "redowosureba",
            "linewokakuto",
            "makenaiyounisuru",
            "samewomiru",
            "repowotsukawanai",
            "animewomiru",
            "tomatowotaberu",
            "sakewonomu",
            "tokyouniiku",
        };

        var failures = new List<string>();
        foreach (var typed in japanese)
        {
            var baseline = Show(typed, v2: false);
            var actual = Show(typed, v2: true);
            if (actual != baseline)
                failures.Add($"{typed} => V1:{baseline} / V2:{actual}");
        }

        Assert.True(failures.Count == 0,
            $"Japanese baseline mismatches: {failures.Count}\n" + string.Join("\n", failures.Take(30)));
    }

    [Test]
    public static void V2_AmbiguousWords_UseContextInsteadOfSubstringLuck()
    {
        var ambiguous = new[] { "sushi", "repo", "same", "make", "anime", "sake", "demo", "home", "name", "note" };
        var failures = new List<string>();

        foreach (var word in ambiguous)
        {
            var baseline = Show(word, v2: false);
            var neutral = Show(word, v2: true);
            if (neutral != baseline)
                failures.Add($"neutral {word}: V1:{baseline} / V2:{neutral}");

            var english = Show(word, v2: true, before: "I really like ");
            if (english != word)
                failures.Add($"English context {word}: {english}");

            var japanese = Show(word, v2: true, before: "これは");
            var japaneseBaseline = Show(word, v2: false, before: "これは");
            if (japanese != japaneseBaseline)
                failures.Add($"Japanese context {word}: V1:{japaneseBaseline} / V2:{japanese}");
        }

        Assert.True(failures.Count == 0,
            $"ambiguous context failures: {failures.Count}\n" + string.Join("\n", failures.Take(30)));
    }

    [Test]
    public static void V2_MultipleLanguageSwitches()
    {
        var cases = new Dictionary<string, string>
        {
            ["githubnipush"] = "githubにpush",
            ["githubdeissuewokakunin"] = "githubでissueをかくにん",
            ["commitshitapush"] = "commitしたpush",
            ["serverdebuildsuru"] = "serverでbuildする",
            ["dockerdepythonwotsukau"] = "dockerでpythonをつかう",
            ["issuewogithubniokuru"] = "issueをgithubにおくる",
            ["branchwomerge shita".Replace(" ", "")] = "branchをmergeした",
            ["linuxdekernelwobuildsuru"] = "linuxでkernelをbuildする",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Commit(typed, v2: true);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(failures.Count == 0,
            $"multi-switch failures: {failures.Count}\n" + string.Join("\n", failures));
    }

    [Test]
    public static void V2_AdversarialBoundaries()
    {
        var cases = new Dictionary<string, string>
        {
            // Short English matches must not steal ordinary Japanese.
            ["goha"] = Show("goha", v2: false),
            ["noha"] = Show("noha", v2: false),
            ["toha"] = Show("toha", v2: false),
            ["sushiha"] = Show("sushiha", v2: false),
            ["animeha"] = Show("animeha", v2: false),
            ["repoha"] = Show("repoha", v2: false),

            // Curated technical short words should still work at a real boundary.
            ["apiha"] = "apiは",
            ["apinoerror"] = "apiのerror",
            ["oknotasuku"] = "okのたすく",

            // Repetition / awkward but legal input should remain lossless.
            ["commitcommit"] = "commitcommit",
            ["issueissue"] = "issueissue",
            ["commitcommitha"] = "commitcommitは",
            ["issueissuewo"] = "issueissueを",
            ["commithahaha"] = "commitははは",

            // Boundary inside what V1 would make one romaji unit.
            ["commitha"] = "commitは",
            ["issueha"] = "issueは",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed, v2: true);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(failures.Count == 0,
            $"adversarial failures: {failures.Count}\n" + string.Join("\n", failures.Take(30)));
    }

    [Test]
    public static void V2_RandomizedStrings_AreLosslessDeterministicAndWellFormed()
    {
        // Deterministic xorshift: 10,000 arbitrary strings, including inputs no
        // human would intentionally type. The goal is robustness, not linguistic
        // correctness: never throw, never lose/reorder raw keys, never return
        // unstable or structurally malformed segmentation.
        uint state = 0x51A7C0DE;
        static uint Next(ref uint x)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }

        var failures = new List<string>();
        for (var sample = 0; sample < 10_000; sample++)
        {
            var length = 1 + (int)(Next(ref state) % 20);
            var chars = new char[length];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)('a' + Next(ref state) % 26);
            var raw = new string(chars);

            IReadOnlyList<CompositionSegment> a;
            IReadOnlyList<CompositionSegment> b;
            try
            {
                a = Segments(raw);
                b = Segments(raw);
            }
            catch (Exception ex)
            {
                failures.Add($"{raw}: threw {ex.GetType().Name}: {ex.Message}");
                if (failures.Count >= 30) break;
                continue;
            }

            var joined = string.Concat(a.Select(s => s.Raw));
            if (joined != raw)
                failures.Add($"{raw}: raw became {joined}");

            if (a.Count == 0 || a.Any(s => s.Raw.Length == 0))
                failures.Add($"{raw}: empty segment");

            if (a.Zip(a.Skip(1), (x, y) => x.IsEnglish == y.IsEnglish).Any(same => same))
                failures.Add($"{raw}: adjacent same-language segments");

            var signatureA = string.Join("|", a.Select(s => $"{(s.IsEnglish ? "E" : "J")}:{s.Raw}:{s.Kana}"));
            var signatureB = string.Join("|", b.Select(s => $"{(s.IsEnglish ? "E" : "J")}:{s.Raw}:{s.Kana}"));
            if (signatureA != signatureB)
                failures.Add($"{raw}: non-deterministic {signatureA} != {signatureB}");

            if (failures.Count >= 30) break;
        }

        Assert.True(failures.Count == 0,
            $"randomized structural failures: {failures.Count}\n" + string.Join("\n", failures));
    }
}
