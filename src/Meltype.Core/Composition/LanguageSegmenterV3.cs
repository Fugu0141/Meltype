// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;

namespace Meltype.Composition;

/// <summary>
/// Experimental V3 stream segmenter.
///
/// V2 only ran when the *entire* raw composition was lower-case ASCII letters.
/// That meant one capital letter, digit or symbol silently sent the whole input
/// back to the legacy detector (Commit... / commit12...). V3 removes that global
/// gate.
///
/// V3 first tokenizes the original raw keystroke stream into alphabetic runs and
/// neutral runs (digits/symbols). Each alphabetic run is analyzed case-insensitively
/// by the language lattice, but the original casing is restored for English output.
/// Neutral runs never disable language detection for their neighbors.
/// </summary>
internal sealed class LanguageSegmenterV3
{
    private readonly CompositionDetector _detector;
    private readonly LanguageSegmenterV2 _letters;

    private sealed record Chunk(
        int Start,
        int End,
        IReadOnlyList<CompositionSegment>? Segments = null,
        bool IsNeutral = false);

    public LanguageSegmenterV3(CompositionDetector detector)
    {
        _detector = detector;
        _letters = new LanguageSegmenterV2(detector);
    }

    public static bool CanHandle(string raw) =>
        raw.Length > 0 &&
        raw.Any(char.IsAsciiLetter) &&
        raw.All(c => c <= 0x7f);

    public IReadOnlyList<CompositionSegment> Segment(
        IReadOnlyList<CompositionUnit> units,
        string pending,
        bool? precedingEnglish,
        bool? followingEnglish,
        DetectionLevel level,
        bool englishSentence,
        bool final)
    {
        var raw = string.Concat(units.Select(u => u.Raw)) + pending;
        if (raw.Length == 0)
            return [new CompositionSegment(false, "", "")];

        var chunks = new List<Chunk>();
        var position = 0;
        bool? previousLanguage = precedingEnglish;
        var firstLetterRun = true;

        while (position < raw.Length)
        {
            var letters = char.IsAsciiLetter(raw[position]);
            var end = position + 1;
            while (end < raw.Length && char.IsAsciiLetter(raw[end]) == letters)
                end++;

            if (!letters)
            {
                chunks.Add(new Chunk(position, end, IsNeutral: true));

                // Digits/symbols are hard lexical boundaries. Do not leak an
                // English decision through "Commit12" into the next Japanese run.
                previousLanguage = null;
                firstLetterRun = false;

                position = end;
                continue;
            }

            var original = raw[position..end];
            var lower = original.ToLowerInvariant();

            // Hard non-letter boundaries terminate a lexical run. The last run
            // keeps the caller's final state because the user may still be typing.
            var runFinal = end < raw.Length || final;
            var runFollowing = end == raw.Length ? followingEnglish : null;
            var runEnglishSentence = firstLetterRun && englishSentence;

            // V2 is used here only as a letter-run lattice engine. A synthetic
            // lower-case unit prevents old romaji tokenization from constraining
            // where a V3 language boundary may fall.
            var analyzed = _letters.Segment(
                [new CompositionUnit("", lower)],
                "",
                previousLanguage,
                runFollowing,
                level,
                runEnglishSentence,
                runFinal);

            var mapped = RestoreOriginalCase(original, analyzed);

            // Capitalization is evidence, not an input-mode switch. If a whole
            // known/proper token begins with a capital and the lattice remained
            // Japanese, prefer the lexical English interpretation.
            if (mapped.Count == 1 &&
                !mapped[0].IsEnglish &&
                original.Length >= 2 &&
                char.IsAsciiLetterUpper(original[0]) &&
                IsCapitalizedEnglish(lower))
            {
                mapped = [new CompositionSegment(true, "", original)];
            }

            chunks.Add(new Chunk(position, end, mapped));

            if (mapped.Count > 0)
                previousLanguage = mapped[^1].IsEnglish;

            firstLetterRun = false;
            position = end;
        }

        return Flatten(raw, units, pending, chunks, precedingEnglish, followingEnglish);
    }

    private List<CompositionSegment> RestoreOriginalCase(
        string original,
        IReadOnlyList<CompositionSegment> analyzed)
    {
        var result = new List<CompositionSegment>(analyzed.Count);
        var offset = 0;

        foreach (var segment in analyzed)
        {
            var length = segment.Raw.Length;
            if (offset + length > original.Length)
                length = Math.Max(0, original.Length - offset);

            var originalRaw = original.Substring(offset, length);
            result.Add(segment.IsEnglish
                ? new CompositionSegment(true, "", originalRaw)
                : new CompositionSegment(false, segment.Kana, originalRaw));
            offset += length;
        }

        if (offset < original.Length)
        {
            var rest = original[offset..];
            result.Add(new CompositionSegment(
                false,
                _detector.Romaji.ConvertLenient(rest.ToLowerInvariant(), final: false),
                rest));
        }

        return MergeAdjacent(result);
    }

    private bool IsCapitalizedEnglish(string lower) =>
        _detector.IsListedEnglishWord(lower) ||
        _detector.IsKnownEnglishWord(lower) ||
        _detector.IsBroadEnglishWord(lower) ||
        _detector.ProperNouns.Contains(lower);

    private IReadOnlyList<CompositionSegment> Flatten(
        string raw,
        IReadOnlyList<CompositionUnit> units,
        string pending,
        IReadOnlyList<Chunk> chunks,
        bool? precedingEnglish,
        bool? followingEnglish)
    {
        var result = new List<CompositionSegment>();

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (!chunk.IsNeutral)
            {
                result.AddRange(chunk.Segments!);
                continue;
            }

            var text = raw[chunk.Start..chunk.End];

            // Digits are language-neutral and always stay as typed. Keeping them
            // as an English/raw segment prevents accidental full-width/kana
            // conversion without making surrounding letter runs English.
            if (text.All(char.IsAsciiDigit))
            {
                result.Add(new CompositionSegment(true, "", text));
                continue;
            }

            var left = LastLanguage(result) ?? precedingEnglish;
            var right = NextLanguage(chunks, index + 1) ?? followingEnglish;

            // ASCII punctuation between English spans stays ASCII. Otherwise use
            // the legacy unit's already-normalized Japanese punctuation when the
            // neutral range lines up with CompositionUnit boundaries.
            if (left == true && right == true)
            {
                result.Add(new CompositionSegment(true, "", text));
            }
            else
            {
                result.Add(new CompositionSegment(
                    false,
                    KanaForRange(raw, units, pending, chunk.Start, chunk.End) ?? text,
                    text));
            }
        }

        return MergeAdjacent(result);
    }

    private static bool? LastLanguage(IReadOnlyList<CompositionSegment> segments)
    {
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            if (segments[i].Raw.All(char.IsAsciiDigit))
                continue;
            return segments[i].IsEnglish;
        }
        return null;
    }

    private static bool? NextLanguage(IReadOnlyList<Chunk> chunks, int start)
    {
        for (var i = start; i < chunks.Count; i++)
        {
            if (chunks[i].IsNeutral) continue;
            var segments = chunks[i].Segments!;
            if (segments.Count == 0) continue;
            return segments[0].IsEnglish;
        }
        return null;
    }

    private static string? KanaForRange(
        string raw,
        IReadOnlyList<CompositionUnit> units,
        string pending,
        int start,
        int end)
    {
        var builder = new System.Text.StringBuilder();
        var offset = 0;

        foreach (var unit in units)
        {
            var next = offset + unit.Raw.Length;
            if (next <= start)
            {
                offset = next;
                continue;
            }
            if (offset >= end) break;

            // Only reuse unit rendering when this neutral range owns the whole
            // unit. A partial overlap would be ambiguous, so fall back to raw.
            if (offset < start || next > end)
                return null;

            builder.Append(unit.Kana);
            offset = next;
        }

        if (end > offset && pending.Length > 0)
        {
            var pendingStart = offset;
            if (start < pendingStart || end > pendingStart + pending.Length)
                return null;
            builder.Append(raw[start..end]);
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static List<CompositionSegment> MergeAdjacent(
        IEnumerable<CompositionSegment> source)
    {
        var result = new List<CompositionSegment>();
        foreach (var segment in source)
        {
            if (segment.Raw.Length == 0) continue;

            if (result.Count > 0 &&
                result[^1].IsEnglish == segment.IsEnglish &&
                (segment.IsEnglish || result[^1].Kana.Length > 0))
            {
                var previous = result[^1];
                result[^1] = new CompositionSegment(
                    previous.IsEnglish,
                    previous.Kana + segment.Kana,
                    previous.Raw + segment.Raw);
            }
            else
            {
                result.Add(segment);
            }
        }
        return result;
    }
}
