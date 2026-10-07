// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;

namespace Meltype.Composition;

/// <summary>
/// Experimental V2 language segmenter.
///
/// The legacy detector searches pre-tokenized romaji units greedily. V2 instead
/// builds a lattice on the original keystroke string, scores every plausible
/// English/Japanese span, then uses dynamic programming to choose the best path.
///
/// Working on raw character offsets is intentional: "commitha" must be able to
/// split at "commit|ha" even after the composition layer has recognized "tha"
/// as one valid romaji unit.
/// </summary>
internal sealed class LanguageSegmenterV2
{
    private readonly CompositionDetector _detector;

    private enum Language
    {
        Japanese,
        English,
    }

    private sealed record Edge(int Start, int End, Language Language, string Raw, double Score);

    private sealed class State
    {
        public required double Score { get; init; }
        public required int Switches { get; init; }
        public required int Pieces { get; init; }
        public required Language Language { get; init; }
        public Edge? Edge { get; init; }
        public State? Previous { get; init; }
    }

    private static readonly string[] JapaneseContinuationPrefixes =
    [
        "ha", "wa", "ga", "wo", "ni", "de", "to", "mo", "he", "no",
        "suru", "shita", "shite", "shitai", "shimasu", "shimashita",
        "sare", "sareta", "saseru",
    ];

    public LanguageSegmenterV2(CompositionDetector detector) => _detector = detector;

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

        if (!raw.All(char.IsAsciiLetterLower))
            throw new ArgumentException("LanguageSegmenterV2 currently accepts lower-case ASCII-letter input only.", nameof(raw));

        var n = raw.Length;
        var best = new State?[n + 1, 2];

        foreach (var edge in GenerateEdges(raw, 0, level, final))
            Put(best, edge.End, StartState(edge, precedingEnglish, englishSentence));

        for (var position = 1; position < n; position++)
        {
            for (var languageIndex = 0; languageIndex < 2; languageIndex++)
            {
                var previous = best[position, languageIndex];
                if (previous is null) continue;

                foreach (var edge in GenerateEdges(raw, position, level, final))
                {
                    var switched = previous.Language != edge.Language;
                    // Every extra lattice piece has a small cost. This prevents
                    // hello from winning as he|l|l|o merely by accumulating many
                    // short local scores.
                    var transition = switched ? -4.0 : -1.0;

                    if (previous.Language == Language.English &&
                        edge.Language == Language.Japanese &&
                        IsJapaneseContinuation(edge.Raw))
                    {
                        transition += 5.0;
                    }

                    if (previous.Language == Language.Japanese &&
                        edge.Language == Language.English &&
                        edge.Raw.Length >= 4)
                    {
                        transition += 1.0;
                    }

                    Put(best, edge.End, new State
                    {
                        Score = previous.Score + edge.Score + transition,
                        Switches = previous.Switches + (switched ? 1 : 0),
                        Pieces = previous.Pieces + 1,
                        Language = edge.Language,
                        Edge = edge,
                        Previous = previous,
                    });
                }
            }
        }

        State? winner = null;
        for (var languageIndex = 0; languageIndex < 2; languageIndex++)
        {
            var candidate = best[n, languageIndex];
            if (candidate is null) continue;

            var ending = 0.0;
            if (followingEnglish == true && candidate.Language == Language.English) ending += 2.0;
            if (followingEnglish == false && candidate.Language == Language.Japanese) ending += 1.0;

            var adjusted = new State
            {
                Score = candidate.Score + ending,
                Switches = candidate.Switches,
                Pieces = candidate.Pieces,
                Language = candidate.Language,
                Edge = candidate.Edge,
                Previous = candidate.Previous,
            };
            if (Better(adjusted, winner)) winner = adjusted;
        }

        if (winner is null)
            return [Japanese(raw, 0, raw.Length, units, pending, final)];

        var edges = new List<Edge>();
        for (var state = winner; state?.Edge is { } edge; state = state.Previous)
            edges.Add(edge);
        edges.Reverse();

        // DP is free to split one language into dictionary-sized pieces only for
        // scoring. Merge those pieces again before exposing composition segments.
        var merged = new List<(Language Language, int Start, int End)>();
        foreach (var edge in edges)
        {
            if (merged.Count > 0 && merged[^1].Language == edge.Language && merged[^1].End == edge.Start)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Language, previous.Start, edge.End);
            }
            else
            {
                merged.Add((edge.Language, edge.Start, edge.End));
            }
        }

        var result = new List<CompositionSegment>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var segment = merged[i];
            var text = raw[segment.Start..segment.End];
            if (segment.Language == Language.English)
            {
                result.Add(new CompositionSegment(true, "", text));
            }
            else
            {
                result.Add(Japanese(text, segment.Start, segment.End, units, pending,
                    final: i == merged.Count - 1 ? final : true));
            }
        }
        return result;
    }

    private State StartState(Edge edge, bool? precedingEnglish, bool englishSentence)
    {
        var context = 0.0;
        if (edge.Language == Language.English)
        {
            // Once surrounding text says "this is an English sentence", that is
            // stronger evidence than the fact that sushi/make can also be romaji.
            if (englishSentence) context += 22.0;
            else if (precedingEnglish == true) context += 10.0;
            else if (precedingEnglish == false) context -= 1.5;
        }
        else if (precedingEnglish == false)
        {
            context += 1.0;
        }

        return new State
        {
            Score = edge.Score + context,
            Switches = 0,
            Pieces = 1,
            Language = edge.Language,
            Edge = edge,
            Previous = null,
        };
    }

    private IEnumerable<Edge> GenerateEdges(string raw, int start, DetectionLevel level, bool final)
    {
        var maxEnd = Math.Min(raw.Length, start + 48);
        var yieldedJapanese = false;

        for (var end = start + 1; end <= maxEnd; end++)
        {
            var span = raw[start..end];
            var lower = span.ToLowerInvariant();
            var analysis = _detector.Romaji.AnalyzeFragment(lower);
            if (!analysis.IsValid) continue;

            var atEnd = end == raw.Length;
            var complete = analysis.Partial is "" or "n";
            if (!atEnd && !complete) continue;
            if (atEnd && final && !complete) continue;

            yieldedJapanese = true;
            yield return new Edge(start, end, Language.Japanese, span,
                JapaneseScore(lower, start == 0, atEnd));
        }

        for (var end = start + 1; end <= maxEnd; end++)
        {
            var span = raw[start..end];
            var lower = span.ToLowerInvariant();
            var atEnd = end == raw.Length;

            var listed = _detector.IsListedEnglishWord(lower);
            var known = _detector.IsKnownEnglishWord(lower);
            var proper = _detector.ProperNouns.Contains(lower);
            var prefix = atEnd && !final && lower.Length >= 2 && _detector.IsEnglishPrefix(lower);
            var properPrefix = atEnd && !final && lower.Length >= 2 && _detector.IsProperNounPrefix(lower);
            var obviousSingle = raw.Length == 1 && start == 0 && span.Length == 1 && lower[0] is 'q' or 'l' or 'v' or 'x';

            if (!listed && !known && !prefix && !properPrefix && !obviousSingle)
                continue;

            var japaneseRemainder = end < raw.Length && RemainderLooksJapanese(raw[end..]);
            var continuation = end < raw.Length && IsJapaneseContinuation(raw[end..]);

            yield return new Edge(start, end, Language.English, span,
                EnglishScore(lower, listed, known, proper, prefix, properPrefix,
                    obviousSingle, level, japaneseRemainder, continuation));
        }

        if (!yieldedJapanese)
        {
            var one = raw[start..(start + 1)];
            yield return new Edge(start, start + 1, Language.Japanese, one, -8.0);
        }
    }

    private double JapaneseScore(string lower, bool atStart, bool atEnd)
    {
        var score = lower.Length * 1.8;
        var strict = _detector.Romaji.Analyze(lower);

        score += strict.IsValid ? 4.0 : -4.0;
        if (_detector.IsKnownJapaneseRomaji(lower)) score += 5.0;
        if (_detector.IsCommonJapanese?.Invoke(lower) == true) score += 6.0;

        if (_detector.LearnedLanguage(lower) == false) score += 25.0;
        else if (_detector.LearnedLanguage(lower) == true) score -= 15.0;

        if (atStart && atEnd) score += 2.0;

        if (lower is "ha" or "wa" or "ga" or "wo" or "ni" or "de" or "to" or "mo" or "he" or "no")
            score += 2.5;

        return score;
    }

    private double EnglishScore(
        string lower,
        bool listed,
        bool known,
        bool proper,
        bool prefix,
        bool properPrefix,
        bool obviousSingle,
        DetectionLevel level,
        bool japaneseRemainder,
        bool continuation)
    {
        double score;
        if (listed || proper)
            score = 12.0 + lower.Length * 2.2;
        else if (known)
            score = 9.0 + lower.Length * 1.8;
        else if (properPrefix)
            score = 16.0 + lower.Length * 1.5;
        else if (prefix)
            score = 11.0 + lower.Length * 1.3;
        else if (obviousSingle)
            score = 8.0;
        else
            score = 0.0;

        var strict = _detector.Romaji.Analyze(lower);
        var completeRomaji = strict.IsValid && strict.Partial is "" or "n";

        // Ambiguous dictionary words are Japanese by default (repo/same/sushi).
        if (completeRomaji) score -= 18.0;
        else if (!strict.IsValid) score += 5.0;
        else score += 2.0; // ends in an unfinished consonant: weak English evidence

        if ((listed || proper) && strict.IsValid && strict.Sokuon > 0)
            score += 14.0;
        if (_detector.IsReadableEnglishWord(lower))
            score += 15.0;

        // Proper nouns are intentionally strong even when they are readable as
        // romaji (amazon/korea/youtube).
        if (proper) score += 15.0;
        if (properPrefix) score += 6.0;

        var learned = _detector.LearnedLanguage(lower);
        if (learned == true) score += 30.0;
        else if (learned == false) score -= 50.0;

        // General mixed-language boundary evidence: a complete known English word
        // followed by a valid Japanese remainder is better than reading the whole
        // string as one unusual romaji sequence.
        if ((listed || known || proper) && lower.Length >= 3 && japaneseRemainder)
            score += 6.0;
        if ((listed || known || proper) && lower.Length >= 3 && continuation)
            score += 8.0;

        if (level == DetectionLevel.Aggressive && (listed || known || proper)) score += 8.0;
        if (level == DetectionLevel.Conservative && completeRomaji && !proper) score -= 2.0;

        if (lower.Length <= 2 && learned != true && !obviousSingle) score -= 14.0;
        else if (lower.Length == 3 && learned != true && !proper) score -= 4.0;

        return score;
    }

    /// <summary>
    /// Prefer the already-normalized kana when a V2 boundary coincides with
    /// CompositionUnit boundaries. Only re-run romaji conversion when the new
    /// language boundary cuts through an old unit (the commi[t|ha] case).
    /// </summary>
    private CompositionSegment Japanese(
        string raw,
        int start,
        int end,
        IReadOnlyList<CompositionUnit> units,
        string pending,
        bool final)
    {
        var offset = 0;
        var firstUnit = -1;
        var lastUnitExclusive = -1;

        for (var i = 0; i < units.Count; i++)
        {
            var next = offset + units[i].Raw.Length;
            if (offset == start) firstUnit = i;
            if (next == end) lastUnitExclusive = i + 1;
            offset = next;
        }

        var unitsRawLength = units.Sum(u => u.Raw.Length);

        // Fully aligned inside normalized units.
        if (firstUnit >= 0 && lastUnitExclusive >= firstUnit && end <= unitsRawLength)
        {
            var kana = string.Concat(units.Skip(firstUnit).Take(lastUnitExclusive - firstUnit).Select(u => u.Kana));
            return new CompositionSegment(false, kana, raw);
        }

        // Aligned at a unit boundary and extending through all pending letters.
        if (firstUnit >= 0 && end == unitsRawLength + pending.Length && start <= unitsRawLength)
        {
            var kana = string.Concat(units.Skip(firstUnit).Select(u => u.Kana));
            if (pending.Length > 0)
                kana += _detector.Romaji.ConvertLenient(pending.ToLowerInvariant(), final);
            return new CompositionSegment(false, kana, raw);
        }

        // The interesting V2 case: boundary lies inside a legacy unit.
        return new CompositionSegment(false,
            _detector.Romaji.ConvertLenient(raw.ToLowerInvariant(), final), raw);
    }

    private bool RemainderLooksJapanese(string raw)
    {
        var analysis = _detector.Romaji.AnalyzeFragment(raw.ToLowerInvariant());
        return analysis.IsValid && analysis.Partial is "" or "n";
    }

    private static bool IsJapaneseContinuation(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return JapaneseContinuationPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal));
    }

    private static void Put(State?[,] best, int position, State candidate)
    {
        var languageIndex = candidate.Language == Language.Japanese ? 0 : 1;
        if (Better(candidate, best[position, languageIndex]))
            best[position, languageIndex] = candidate;
    }

    private static bool Better(State candidate, State? current)
    {
        if (current is null) return true;
        if (candidate.Score > current.Score + 0.0001) return true;
        if (candidate.Score < current.Score - 0.0001) return false;
        if (candidate.Switches != current.Switches) return candidate.Switches < current.Switches;
        return candidate.Pieces < current.Pieces;
    }
}
