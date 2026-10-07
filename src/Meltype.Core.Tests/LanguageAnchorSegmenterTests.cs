// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>
/// Integration and adversarial tests for the fresh anchor-based detector.
/// Unlike the previous experiments these tests enter through the real
/// CompositionController/Keyboard path whenever an expected display is checked.
/// </summary>
internal static class LanguageAnchorSegmenterTests
{
    private static readonly CompositionDetector Detector = CompositionTests.Detector;

    private static string Show(string typed)
    {
        var previous = Detector.UseExperimentalLanguageAnchorV4;
        Detector.UseExperimentalLanguageAnchorV4 = true;
        try
        {
            var k = new CompositionTests.Keyboard();
            k.Type(typed);
            return k.Showing ?? "";
        }
        finally
        {
            Detector.UseExperimentalLanguageAnchorV4 = previous;
        }
    }

    private static string Commit(string typed)
    {
        var previous = Detector.UseExperimentalLanguageAnchorV4;
        Detector.UseExperimentalLanguageAnchorV4 = true;
        try
        {
            var k = new CompositionTests.Keyboard();
            k.Type(typed + "\n");
            return k.Host.Document;
        }
        finally
        {
            Detector.UseExperimentalLanguageAnchorV4 = previous;
        }
    }

    [Test]
    public static void AnchorV4_ReportedCases_MustWorkThroughRealKeyboardPath()
    {
        var cases = new Dictionary<string, string>
        {
            ["commitha"] = "commitは",
            ["commitha12noyatsudayo"] = "commitは12のやつだよ",
            ["issuetateta"] = "issueたてた",
            ["reflectsareta"] = "reflectされた",
            ["inviteshimashita"] = "inviteしました",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(
            failures.Count == 0,
            $"reported cases: {failures.Count} failures\n" +
            string.Join("\n", failures));
    }

    [Test]
    public static void AnchorV4_EnglishAnchorMustNotEatJapaneseSuffix()
    {
        var cases = new Dictionary<string, string>
        {
            ["commitsuru"] = "commitする",
            ["commitshita"] = "commitした",
            ["commitshitai"] = "commitしたい",
            ["commitsareta"] = "commitされた",
            ["issuede"] = "issueで",
            ["issuesuru"] = "issueする",
            ["reflectsuru"] = "reflectする",
            ["invitesuru"] = "inviteする",
            ["debugha"] = "debugは",
            ["deploymiru"] = "deployみる",
            ["networkmiru"] = "networkみる",
            ["networktsukau"] = "networkつかう",
            ["linuxde"] = "linuxで",
            ["linuxtsukau"] = "linuxつかう",
            ["serverokuru"] = "serverおくる",
            ["cacheha"] = "cacheは",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(
            failures.Count == 0,
            $"suffix boundary: {failures.Count} failures\n" +
            string.Join("\n", failures.Take(40)));
    }

    [Test]
    public static void AnchorV4_JapaneseMustRemainJapaneseDespiteEnglishSubstrings()
    {
        var cases = new[]
        {
            "nihongowohanasu",
            "watashihagakuseidesu",
            "ashitahaamedesu",
            "samuidesune",
            "nimotsuwookuru",
            "samewomiru",
            "tomatowotaberu",
            "redowosureba",
            "makenaiyounisuru",
            "benkyouwosuru",
            "tomodachitoasobu",
            "kaishaniikimasu",
            "animewomiru",
            "sakewonomu",
            "repoha",
            "sushiha",
            "animeha",
            "goha",
            "noha",
            "toha",
        };

        var failures = new List<string>();
        foreach (var typed in cases)
        {
            var previous = Detector.UseExperimentalLanguageAnchorV4;
            Detector.UseExperimentalLanguageAnchorV4 = false;
            string baseline;
            try
            {
                var k = new CompositionTests.Keyboard();
                k.Type(typed);
                baseline = k.Showing ?? "";
            }
            finally
            {
                Detector.UseExperimentalLanguageAnchorV4 = previous;
            }

            var actual = Show(typed);
            if (actual != baseline)
                failures.Add($"{typed} => V1:{baseline} / Anchor:{actual}");
        }

        Assert.True(
            failures.Count == 0,
            $"Japanese false positives: {failures.Count}\n" +
            string.Join("\n", failures.Take(40)));
    }

    [Test]
    public static void AnchorV4_CanSwitchJapaneseEnglishJapanese()
    {
        var cases = new Dictionary<string, string>
        {
            ["kyouhagooglede"] = "きょうはgoogleで",
            ["githubdeissue"] = "githubでissue",
            ["githubdeissuewokakunin"] = "githubでissueをかくにん",
            ["linuxdekernelwobuildsuru"] = "linuxでkernelをbuildする",
            ["issuewogithubniokuru"] = "issueをgithubにおくる",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Commit(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(
            failures.Count == 0,
            $"multi-language: {failures.Count} failures\n" +
            string.Join("\n", failures.Take(40)));
    }

    [Test]
    public static void AnchorV4_DigitsAreHardBoundaries_NotDetectorKillSwitches()
    {
        var cases = new Dictionary<string, string>
        {
            ["commitha1"] = "commitは1",
            ["commitha12"] = "commitは12",
            ["commitha12noyatsu"] = "commitは12のやつ",
            ["commit2026noyatsu"] = "commit2026のやつ",
            ["github2deissue"] = "github2でissue",
            ["ubuntu24de"] = "ubuntu24で",
            ["win11pro"] = "win11pro",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(
            failures.Count == 0,
            $"digit boundaries: {failures.Count} failures\n" +
            string.Join("\n", failures));
    }

    [Test]
    public static void AnchorV4_RandomLowercaseAndDigits_AreLosslessAndDeterministic()
    {
        var previous = Detector.UseExperimentalLanguageAnchorV4;
        Detector.UseExperimentalLanguageAnchorV4 = true;
        try
        {
            uint state = 0xA14C_4026;
            static uint Next(ref uint x)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                return x;
            }

            for (var sample = 0; sample < 10_000; sample++)
            {
                var length = 1 + (int)(Next(ref state) % 24);
                var chars = new char[length];
                var hasLetter = false;

                for (var i = 0; i < chars.Length; i++)
                {
                    if ((Next(ref state) % 10) < 8)
                    {
                        chars[i] = (char)('a' + Next(ref state) % 26);
                        hasLetter = true;
                    }
                    else
                    {
                        chars[i] = (char)('0' + Next(ref state) % 10);
                    }
                }

                if (!hasLetter) chars[0] = 'a';
                var raw = new string(chars);

                var a = new CompositionText(Detector);
                var b = new CompositionText(Detector);
                foreach (var c in raw)
                {
                    a.Append(c);
                    b.Append(c);
                }

                IReadOnlyList<CompositionSegment> sa;
                IReadOnlyList<CompositionSegment> sb;
                try
                {
                    sa = a.Segments();
                    sb = b.Segments();
                }
                catch (Exception ex)
                {
                    Assert.True(false, $"{raw}: threw {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                Assert.Equal(raw, string.Concat(sa.Select(s => s.Raw)),
                    $"raw preservation: {raw}");

                var sigA = string.Join("|", sa.Select(s =>
                    $"{(s.IsEnglish ? "E" : "J")}:{s.Raw}:{s.Kana}"));
                var sigB = string.Join("|", sb.Select(s =>
                    $"{(s.IsEnglish ? "E" : "J")}:{s.Raw}:{s.Kana}"));

                Assert.Equal(sigA, sigB, $"determinism: {raw}");
                Assert.True(!sa.Any(s => s.Raw.Length == 0), $"empty segment: {raw}");
            }
        }
        finally
        {
            Detector.UseExperimentalLanguageAnchorV4 = previous;
        }
    }
}
