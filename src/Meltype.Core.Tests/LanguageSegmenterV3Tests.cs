// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>
/// End-to-end tests for the V3 stream dispatcher. These specifically cover the
/// gap V2 missed: capitals, digits and neutral characters must not silently
/// route the whole composition back to the legacy detector.
/// </summary>
internal static class LanguageSegmenterV3Tests
{
    private static readonly CompositionDetector Detector = CompositionTests.Detector;

    private static string Show(string typed)
    {
        var oldV2 = Detector.UseExperimentalLanguageSegmenterV2;
        var oldV3 = Detector.UseExperimentalLanguageSegmenterV3;
        Detector.UseExperimentalLanguageSegmenterV2 = false;
        Detector.UseExperimentalLanguageSegmenterV3 = true;
        try
        {
            var k = new CompositionTests.Keyboard();
            k.Type(typed);
            return k.Showing ?? "";
        }
        finally
        {
            Detector.UseExperimentalLanguageSegmenterV2 = oldV2;
            Detector.UseExperimentalLanguageSegmenterV3 = oldV3;
        }
    }

    [Test]
    public static void V3_ActualReported_CapitalCommitAndDigits()
    {
        var cases = new Dictionary<string, string>
        {
            ["Commitha"] = "Commitは",
            ["COMMITha"] = "COMMITは",
            ["Commit12noyatsudayo"] = "Commit12のやつだよ",
            ["commit12noyatsudayo"] = "commit12のやつだよ",
            ["Commit12ha"] = "Commit12は",
            ["Ubuntu24de"] = "Ubuntu24で",
            ["version2ha"] = "version2は",
            ["win11pro"] = "win11pro",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(failures.Count == 0,
            $"V3 dispatch failures: {failures.Count}\n" + string.Join("\n", failures));
    }

    [Test]
    public static void V3_CaseVariants_DoNotChangeLanguageBoundaries()
    {
        var variants = new Dictionary<string, string>
        {
            ["commitha"] = "commitは",
            ["Commitha"] = "Commitは",
            ["COMMITha"] = "COMMITは",
            ["issuetateta"] = "issueたてた",
            ["Issuetateta"] = "Issueたてた",
            ["ISSUEtateta"] = "ISSUEたてた",
            ["githubde"] = "githubで",
            ["GitHubde"] = "GitHubで",
            ["GITHUBde"] = "GITHUBで",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in variants)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(failures.Count == 0,
            $"case-variant failures: {failures.Count}\n" + string.Join("\n", failures));
    }

    [Test]
    public static void V3_Digits_AreHardBoundaries_NotGlobalFallbacks()
    {
        var cases = new Dictionary<string, string>
        {
            ["commit1ha"] = "commit1は",
            ["commit12ha"] = "commit12は",
            ["commit2026noyatsu"] = "commit2026のやつ",
            ["github2deissue"] = "github2でissue",
            ["ubuntu24tsukau"] = "ubuntu24つかう",
            ["12noyatsudayo"] = "12のやつだよ",
            ["2026nenha"] = "2026ねんは",
        };

        var failures = new List<string>();
        foreach (var (typed, expected) in cases)
        {
            var actual = Show(typed);
            if (actual != expected)
                failures.Add($"{typed} => {actual} (expected {expected})");
        }

        Assert.True(failures.Count == 0,
            $"digit-boundary failures: {failures.Count}\n" + string.Join("\n", failures));
    }

    [Test]
    public static void V3_RandomCaseAndDigits_NeverLoseRawASCII()
    {
        uint state = 0xC0FFEE42;
        static uint Next(ref uint x)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }

        var oldV2 = Detector.UseExperimentalLanguageSegmenterV2;
        var oldV3 = Detector.UseExperimentalLanguageSegmenterV3;
        Detector.UseExperimentalLanguageSegmenterV2 = false;
        Detector.UseExperimentalLanguageSegmenterV3 = true;
        try
        {
            for (var sample = 0; sample < 2_000; sample++)
            {
                var length = 1 + (int)(Next(ref state) % 24);
                var chars = new char[length];
                var hasLetter = false;
                for (var i = 0; i < chars.Length; i++)
                {
                    var kind = Next(ref state) % 10;
                    if (kind < 7)
                    {
                        var c = (char)('a' + Next(ref state) % 26);
                        if ((Next(ref state) & 3) == 0)
                            c = char.ToUpperInvariant(c);
                        chars[i] = c;
                        hasLetter = true;
                    }
                    else
                    {
                        chars[i] = (char)('0' + Next(ref state) % 10);
                    }
                }

                if (!hasLetter)
                    chars[0] = 'a';

                var raw = new string(chars);
                var text = new CompositionText(Detector);
                foreach (var c in raw) text.Append(c);

                IReadOnlyList<CompositionSegment> segments;
                try
                {
                    segments = text.Segments();
                }
                catch (Exception ex)
                {
                    Assert.True(false, $"{raw}: threw {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                var reconstructed = string.Concat(segments.Select(s => s.Raw));
                Assert.Equal(raw, reconstructed, $"raw preservation: {raw}");
            }
        }
        finally
        {
            Detector.UseExperimentalLanguageSegmenterV2 = oldV2;
            Detector.UseExperimentalLanguageSegmenterV3 = oldV3;
        }
    }
}
