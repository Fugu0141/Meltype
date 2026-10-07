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
                    var transition = switched ? -10.0 : -2.0;

                    if (previous.Language == Language.English &&
                        edge.Language == Language.Japanese &&
                        IsJapaneseContinuation(edge.Raw))
                    {
                        transition += 8.0;
                    }

                    if (previous.Language == Language.Japanese &&
                        edge.Language == Language.English &&
                        edge.Raw.Length >= 4)
                    {
                        transition += 3.0;
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
            if (englishSentence) context += 30.0;
            else if (precedingEnglish == true) context += 15.0;
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
            var contextShortWord = raw.Length == 1 && start == 0 && span.Length == 1 && lower[0] is 'a' or 'i' or 'u' or 'r';

            if (!listed && !known && !prefix && !properPrefix && !obviousSingle && !contextShortWord)
                continue;

            var japaneseRemainder = end < raw.Length && RemainderLooksJapanese(raw[end..]);
            var continuation = end < raw.Length && IsJapaneseContinuation(raw[end..]);

            yield return new Edge(start, end, Language.English, span,
                EnglishScore(lower, listed, known, proper, prefix, properPrefix,
                    obviousSingle, contextShortWord, level, japaneseRemainder, continuation));
        }

        if (!yieldedJapanese)
        {
            var one = raw[start..(start + 1)];
            yield return new Edge(start, start + 1, Language.Japanese, one, -8.0);
        }
    }

    private double JapaneseScore(string lower, bool atStart, bool atEnd)
    {
        // Keep the Japanese prior mostly length-based. Large bonuses on every
        // short dictionary hit made normal sentences fragment around accidental
        // English words (kid, sam, red, ...).
        var score = lower.Length * 3.0;
        var strict = _detector.Romaji.Analyze(lower);

        if (strict.IsValid && strict.Partial is "" or "n") score += 8.0;
        else if (!strict.IsValid) score -= 6.0;

        if (_detector.IsCommonJapanese?.Invoke(lower) == true) score += 8.0;

        var learned = _detector.LearnedLanguage(lower);
        if (learned == false) score += 30.0;
        else if (learned == true) score -= 30.0;

        if (atStart && atEnd) score += 6.0;

        // Explicit small-kana spellings such as xa/la/ltu are Japanese input.
        if (lower.Length >= 2 && lower[0] is 'x' or 'l' &&
            _detector.Romaji.AnalyzeFragment(lower) is { IsValid: true })
            score += 14.0;

        if (lower is "ha" or "wa" or "ga" or "wo" or "ni" or "de" or "to" or "mo" or "he" or "no")
            score += 2.0;

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
        bool contextShortWord,
        DetectionLevel level,
        bool japaneseRemainder,
        bool continuation)
    {
        double score;
        if (listed || proper)
            score = lower.Length * 3.0 + 10.0;
        else if (known)
            score = lower.Length * 3.0 + 6.0;
        else if (properPrefix)
            score = lower.Length * 3.0 + 12.0;
        else if (prefix)
            score = lower.Length * 3.0 + 8.0;
        else if (obviousSingle)
            score = 8.0;
        else if (contextShortWord)
            score = 5.0;
        else
            score = 0.0;

        var strict = _detector.Romaji.Analyze(lower);
        var completeRomaji = strict.IsValid && strict.Partial is "" or "n";

        // Words that are also ordinary romaji are ambiguous by default.
        if (completeRomaji) score -= 18.0;
        else if (!strict.IsValid) score += 8.0;
        else score += 2.0;

        // issue/apple-like words only become Japanese through a sokuon. For a
        // listed English word this is strong evidence for the English reading.
        if ((listed || proper) && strict.IsValid && strict.Sokuon > 0)
            score += 30.0;

        if (_detector.IsReadableEnglishWord(lower))
            score += 25.0;

        if (proper) score += 25.0;
        if (properPrefix) score += 8.0;

        var learned = _detector.LearnedLanguage(lower);
        if (learned == true) score += 30.0;
        else if (learned == false) score -= 50.0;

        // Generic mixed-language evidence: a known English token followed by a
        // valid Japanese continuation is a plausible language boundary.
        if ((listed || known || proper) && lower.Length >= 3 && japaneseRemainder)
            score += 5.0;
        if ((listed || known || proper) && lower.Length >= 3 && continuation)
            score += 15.0;
        if ((listed || known || proper) && lower.Length == 3 && continuation)
            score += 12.0;

        // Do not steal the beginning of a longer Japanese romaji word (fair|u).
        if (_detector.IsJapaneseRomajiPrefix(lower) && japaneseRemainder && !proper)
            score -= 12.0;

        if (level == DetectionLevel.Aggressive && (listed || known || proper))
            score += lower.Length <= 2 ? 35.0 : 8.0;
        if (level == DetectionLevel.Conservative)
        {
            if (completeRomaji && !proper) score -= 4.0;
            if (proper && completeRomaji) score -= 15.0;
        }

        if (lower.Length <= 2 && learned != true && !obviousSingle && !contextShortWord) score -= 20.0;
        else if (lower.Length == 3 && learned != true && !proper) score -= 5.0;

        if (lower.Length >= 2 && lower[0] is 'x' or 'l' &&
            _detector.Romaji.AnalyzeFragment(lower) is { IsValid: true } && !proper && learned != true)
            score -= 20.0;

        return score;
    }

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

        // Pending is rendered separately by CompositionText.RenderSegments.
        if (firstUnit >= 0 && end == unitsRawLength + pending.Length && start <= unitsRawLength)
        {
            var kana = string.Concat(units.Skip(firstUnit).Select(u => u.Kana));
            return new CompositionSegment(false, kana, raw);
        }

        if (start >= unitsRawLength)
            return new CompositionSegment(false, "", raw);

        // Boundary lies inside a legacy unit. Convert only the normalized part;
        // the pending tail is appended later by CompositionText.
        var convertedEnd = Math.Min(end, unitsRawLength);
        var convertedLength = Math.Max(0, convertedEnd - start);
        var convertedRaw = convertedLength > 0 ? raw[..Math.Min(raw.Length, convertedLength)] : "";
        return new CompositionSegment(false,
            _detector.Romaji.ConvertLenient(convertedRaw.ToLowerInvariant(), final: true), raw);
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
