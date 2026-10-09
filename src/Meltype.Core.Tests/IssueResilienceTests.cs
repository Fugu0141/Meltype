// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Fugu0141

using System.Diagnostics;
using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>
/// Issue-derived resilience study for the minimal boundary experiment.
/// This is a REPORT, not a claim that every historical issue is fixed.
/// It compares PR #235 scored-only with scored+stream-boundary on the
/// same Meltype session and fake converter, including preview/Enter.
/// No user research JSON or private text is bundled.
/// </summary>
internal static class IssueResilienceTests
{
    private sealed record Case(
        string Issue, string Category, string Input, string Expected,
        bool Preview = false, bool ReadingOnly = false);

    // Grounded in the public issue bodies listed in
    // docs/research/stream-boundary-issue-resilience.md.
    // ReadingOnly cases deliberately compare against a kana reading:
    // fake converters cannot evaluate Mozc's final kanji/katakana choice.
    private static readonly Case[] Cases =
    [
        new("#1", "mixed", "kyouhameetinggaarimasu", "きょうはmeetingがあります"),
        new("#12", "english", "feature", "feature"),
        new("#65", "japanese", "anata", "あなた", Preview: true),
        new("#77", "english-suffix", "reflectsareta", "reflectされた"),
        new("#79", "romaji", "ci", "し"),
        new("#92", "romaji-proposal", "qo", "くぉ", ReadingOnly: true),
        new("#104", "false-english", "moraltute", "もらって"),
        new("#129", "acronym", "AInituite", "AIについて", Preview: true),
        new("#130", "unit", "50ccgenntuki", "50ccげんつき", ReadingOnly: true),
        new("#153", "false-english", "hosuthingu", "ほすてぃんぐ", ReadingOnly: true),
        new("#154", "mixed", "abctodef", "abcとdef", Preview: true),
        new("#207", "english-preview", "pedia", "pedia", Preview: true),
        new("#207", "english-preview", "protopedia", "protopedia", Preview: true),
        new("#218", "romaji-ltu", "hotelltu", "hotelっ"),
        new("#218", "romaji-ltu", "totalltute", "totalって"),
        new("#220", "preview-commit", "tabde", "tabで"),
        new("#220", "preview-commit", "tabga", "tabが"),

        // Previously reproduced in IncrementalBoundaryLab (not reported as
        // GitHub issue numbers); these are protocol-level comparison cases.
        new("research", "structured-code", "konoyouninode.jsnado",
            "このようにnode.jsなど"),
        new("research", "structured-code", "kyouhanode.jsnado",
            "きょうはnode.jsなど"),
        new("research", "structured-code", "Node.jswotukau",
            "Node.jsをつかう"),
        new("research", "unknown-english", "meltypega", "meltypeが"),
        new("research", "unknown-english", "meltypenimo", "meltypeにも"),
        new("research", "japanese", "toomoimasu", "とおもいます"),
        new("research", "uppercase-acronym", "IME", "IME"),
        new("research", "readable-english", "japanese", "japanese"),
        new("research", "readable-english", "japaneseno", "japaneseの"),
        new("research", "readable-english", "tokyo", "tokyo"),
        new("research", "readable-english", "tokyode", "tokyoで"),
        new("research", "japanese-guard", "suzuki", "すずき"),
        new("research", "japanese-guard", "anime", "あにめ"),
        new("research", "negative-control", "densha", "でんしゃ"),
        new("research", "negative-control", "shabushabu", "しゃぶしゃぶ"),
        new("research", "negative-control", "nihongo", "にほんご"),
    ];

    private static CompositionDetector Detector(bool boundary)
    {
        var d = CompositionDetector.CreateDefault();
        d.SpellChecker = Detection.BuiltInWordChecker.Shared;
        d.UseScoredSegmentation = () => true;
        d.UseStreamBoundaryHints = () => boundary;
        return d;
    }

    private static string Evaluate(Case input, CompositionDetector detector)
    {
        var session = new MeltypeSession(
            detector, new CompositionTests.FakeConverter(),
            new CompositionOptions(), () => new Settings());
        var committed = "";
        string? preview = null;
        foreach (var character in input.Input + (input.Preview ? "" : "\n"))
        {
            var enter = character == '\n';
            var vk = enter ? VirtualKeys.Return :
                char.ToUpperInvariant(character);
            var response = session.HandleKey(
                vk, enter ? null : character, char.IsAsciiLetterUpper(character),
                false, false, false);
            foreach (var edit in response.Commits)
            {
                if (edit.DeleteBefore > 0)
                    committed = committed[..(committed.Length -
                        Math.Min(edit.DeleteBefore, committed.Length))];
                committed += edit.Text;
            }
            if (!response.Consumed && !enter)
                committed += character;
            if (response.View is not null)
                preview = response.View.Text;
        }
        return input.Preview ? preview ?? committed : committed;
    }

    [Test]
    public static void ImeAbbreviation_TsfSessionContractAndCaseProbe()
    {
        // Windows TSF TipServer.CreateSession uses this very MeltypeSession
        // and exchanges its SessionResult.ToJson() with the native TSF DLL.
        // This is a managed protocol smoke test, NOT a native TSF E2E test.
        var detector = Detector(true);
        // Inspect the underlying raw stream directly so a test failure
        // distinguishes changed key input from failed segmentation.
        var composition = new CompositionText(detector);
        foreach (var character in "IME")
        {
            composition.Append(character);
            Console.WriteLine(
                $"IME_RAW_TRACE key={character} raw={composition.Raw} " +
                $"units={string.Join("|", composition.Units.Select(u => u.Raw + "=" + u.Kana))} " +
                $"pending={composition.Pending} preview={composition.Display(final: false)}");
        }
        Assert.Equal("IME", composition.Raw, "raw uppercase key sequence must remain untouched");
        Assert.Equal("IME", composition.Display(final: false),
            "raw composition should remain a single acronym in preview");
        Assert.Equal("IME", composition.Display(final: true),
            "raw composition should remain a single acronym on Enter");

        var explicitEnglish = new Case(
            "research", "ime-acronym", "IME", "IME");
        var uppercase = Evaluate(explicitEnglish, detector);
        var lowercase = Evaluate(new Case(
            "research", "ime-ambiguous", "ime", "ime"), detector);
        Console.WriteLine(
            $"IME_CASE_PROBE upper={uppercase} lower={lowercase} " +
            $"dictionaryHint=IME_not_in_bundled_english_lexicon");
        Assert.Equal("IME", uppercase,
            "explicit uppercase acronym should be retained by the session");

        var settings = new Settings();
        var session = new MeltypeSession(
            detector, new CompositionTests.FakeConverter(),
            new CompositionOptions(), () => settings);
        SessionResult? last = null;
        foreach (var c in "IME")
        {
            last = session.HandleKey(
                char.ToUpperInvariant(c), c,
                true, false, false, false);
            Console.WriteLine(
                $"IME_TSF_TRACE key={c} consumed={last.Consumed} " +
                $"view={last.View?.Text ?? "<null>"} " +
                $"commits={string.Join("|", last.Commits.Select(e => e.Text))}");
            using var responseJson = System.Text.Json.JsonDocument.Parse(
                last.ToJson());
        }
        Assert.True(last is not null && last.View is not null,
            "managed TSF-facing session must provide a composition view");
        var committed = session.CommitPending();
        using var resultJson = System.Text.Json.JsonDocument.Parse(
            committed.ToJson());
        Assert.True(resultJson.RootElement.TryGetProperty(
            "commits", out _),
            "TSF-facing response must serialize the commits array");
        Assert.Equal("IME",
            string.Concat(committed.Commits.Select(c => c.Text)),
            "TSF-facing session must preserve explicitly uppercase IME");
    }

    [Test]
    public static void HistoricalIssues_BaselineVersusStreamBoundaryReport()
    {
        // Environment override is process-wide and overrides the delegate.
        // Require an uncontaminated OFF/ON comparison in this test process.
        if (Environment.GetEnvironmentVariable("MELTYPE_STREAM_BOUNDARY") == "1")
        {
            Console.WriteLine("ISSUE_RESILIENCE_SKIPPED: " +
                "set MELTYPE_STREAM_BOUNDARY=0 to compare OFF vs ON");
            return;
        }

        var baseDetector = Detector(false);
        var streamDetector = Detector(true);
        var baseCorrect = 0;
        var streamCorrect = 0;
        var improved = 0;
        var regressed = 0;
        var sameWrong = 0;
        var sameRight = 0;

        foreach (var row in Cases)
        {
            var before = Evaluate(row, baseDetector);
            var after = Evaluate(row, streamDetector);
            var beforeOk = before == row.Expected;
            var afterOk = after == row.Expected;
            if (beforeOk) baseCorrect++;
            if (afterOk) streamCorrect++;
            if (!beforeOk && afterOk) improved++;
            else if (beforeOk && !afterOk) regressed++;
            else if (beforeOk) sameRight++;
            else sameWrong++;

            Console.WriteLine(
                $"ISSUE_RESILIENCE issue={row.Issue} category={row.Category} " +
                $"mode={(row.Preview ? "preview" : "enter")} " +
                $"readingOnly={row.ReadingOnly} " +
                $"baseline={(beforeOk ? "PASS" : "MISS")} " +
                $"stream={(afterOk ? "PASS" : "MISS")} " +
                $"input={row.Input} expected={row.Expected} " +
                $"baselineOutput={before} streamOutput={after}");
        }
        Console.WriteLine(
            $"ISSUE_RESILIENCE_SUMMARY total={Cases.Length} " +
            $"baseline={baseCorrect} stream={streamCorrect} " +
            $"improved={improved} regressed={regressed} " +
            $"sameRight={sameRight} sameWrong={sameWrong}");

        // This test is informative: pre-existing and unrelated failures
        // must not be mistaken for regressions caused by this opt-in hook.
        // True regressions are printed prominently for manual triage.
        // Assert only invariant properties of the experimental harness.
        Assert.True(Cases.Length >= 20,
            "expected a representative issue-derived test sample");
        Assert.Equal(Cases.Length,
            improved + regressed + sameRight + sameWrong);
    }

    [Test]
    public static void HistoricalIssues_LongCompositionLatencyProbe()
    {
        // Report only, not a timing assertion on shared CI runners.
        // #114 concerns repeated spelling lookups on macOS;
        // #221 requires Windows Microsoft IME. Neither can be proven
        // fixed by this managed-core timing diagnostic.
        const string sample =
            "kyouhanode.jsnadowotukattebenkyousiteimasu" +
            "konoyounimeltypewotukatte" +
            "kyouhanode.jsnadowotukattebenkyousiteimasu" +
            "konoyounimeltypewotukatte";
        foreach (var useBoundary in new[] { false, true })
        {
            var detector = Detector(useBoundary);
            var session = new MeltypeSession(
                detector, new CompositionTests.FakeConverter(),
                new CompositionOptions(), () => new Settings());
            var samples = new List<double>(sample.Length);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            foreach (var c in sample)
            {
                var start = Stopwatch.GetTimestamp();
                session.HandleKey(
                    char.ToUpperInvariant(c), c,
                    char.IsAsciiLetterUpper(c), false, false, false);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() -
                allocatedBefore;
            samples.Sort();
            var p95 = samples[Math.Min(samples.Count - 1,
                (int)Math.Ceiling(samples.Count * 0.95) - 1)];
            var median = samples[samples.Count / 2];
            Console.WriteLine(
                $"ISSUE_LATENCY boundary={(useBoundary ? "on" : "off")} " +
                $"length={sample.Length} p50Ms={median:F3} " +
                $"p95Ms={p95:F3} maxMs={samples[^1]:F3} " +
                $"allocatedBytes={bytes}");
        }
    }
}
