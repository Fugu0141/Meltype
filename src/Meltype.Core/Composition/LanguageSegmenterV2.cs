// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;

namespace Meltype.Composition;

/// <summary>
/// Experimental V2 language segmenter.
///
/// Unlike the legacy detector, this works on the raw keystroke string instead of
/// pre-tokenized romaji units. That is important because a language boundary can
/// exist inside one romaji unit (for example, "commitha" should split as
/// "commit|ha", even though "tha" is a valid romaji unit).
///
/// Every possible English/Japanese span becomes an edge in a lattice. Dynamic
/// programming then selects the highest-scoring path for the whole input.
/// Adjacent spans of the same language are merged before returning.
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
        string raw,
        bool? precedingEnglish,
        bool? followingEnglish,
        DetectionLevel level,
        bool englishSentence,
        bool final)
    {
        if (raw.Length == 0)
            return [new CompositionSegment(false, "", "")];

        // V2 currently owns the alphabetic mixed-language path. Symbols, digits
        // and kana input still use the mature legacy path at the caller.
        if (!raw.All(char.IsAsciiLetter))
            throw new ArgumentException("LanguageSegmenterV2 accepts ASCII-letter input only.", nameof(raw));

        var n = raw.Length;
        var best = new State?[n + 1, 2];

        foreach (var edge in GenerateEdges(raw, 0, final))
        {
            var state = StartState(edge, precedingEnglish, englishSentence);
            Put(best, edge.End, state);
        }

        for (var position = 1; position < n; position++)
        {
            for (var languageIndex = 0; languageIndex < 2; languageIndex++)
            {
                var previous = best[position, languageIndex];
                if (previous is null) continue;

                foreach (var edge in GenerateEdges(raw, position, final))
                {
                    var switched = previous.Language != edge.Language;
                    var transition = switched ? -3.5 : 0.0;

                    // A known English word followed by a Japanese particle or
                    // suru-form is a very natural boundary: commit|ha,
                    // reflect|sareta, push|shita.
                    if (previous.Language == Language.English &&
                        edge.Language == Language.Japanese &&
                        IsJapaneseContinuation(edge.Raw))
                    {
                        transition += 3.5;
                    }

                    // A sufficiently long English word embedded in Japanese is
                    // also natural: kyouha|google|de.
                    if (previous.Language == Language.Japanese &&
                        edge.Language == Language.English &&
                        edge.Raw.Length >= 4)
                    {
                        transition += 1.5;
                    }

                    var state = new State
                    {
                        Score = previous.Score + edge.Score + transition,
                        Switches = previous.Switches + (switched ? 1 : 0),
                        Pieces = previous.Pieces + 1,
                        Language = edge.Language,
                        Edge = edge,
                        Previous = previous,
                    };
                    Put(best, edge.End, state);
                }
            }
        }

        State? winner = null;
        for (var languageIndex = 0; languageIndex < 2; languageIndex++)
        {
            var candidate = best[n, languageIndex];
            if (candidate is null) continue;

            var score = candidate.Score;
            if (followingEnglish == true && candidate.Language == Language.English) score += 2.0;
            if (followingEnglish == false && candidate.Language == Language.Japanese) score += 1.0;

            var adjusted = new State
            {
                Score = score,
                Switches = candidate.Switches,
                Pieces = candidate.Pieces,
                Language = candidate.Language,
                Edge = candidate.Edge,
                Previous = candidate.Previous,
            };
            if (Better(adjusted, winner)) winner = adjusted;
        }

        if (winner is null)
        {
            return [Japanese(raw, final)];
        }

        var edges = new List<Edge>();
        for (var state = winner; state?.Edge is { } edge; state = state.Previous)
            edges.Add(edge);
        edges.Reverse();

        // Merge same-language edges. DP may split Japanese into dictionary-sized
        // pieces only for scoring; the user should still see one continuous span.
        var merged = new List<(Language Language, string Raw)>();
        foreach (var edge in edges)
        {
            if (merged.Count > 0 && merged[^1].Language == edge.Language)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Language, previous.Raw + edge.Raw);
            }
            else
            {
                merged.Add((edge.Language, edge.Raw));
            }
        }

        var result = new List<CompositionSegment>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var segment = merged[i];
            if (segment.Language == Language.English)
            {
                result.Add(new CompositionSegment(true, "", segment.Raw));
            }
            else
            {
                result.Add(Japanese(segment.Raw, final: i == merged.Count - 1 ? final : true));
            }
        }
        return result;
    }

    private State StartState(Edge edge, bool? precedingEnglish, bool englishSentence)
    {
        var context = 0.0;
        if (edge.Language == Language.English)
        {
            if (englishSentence) context += 7.0;
            else if (precedingEnglish == true) context += 4.0;
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

    private IEnumerable<Edge> GenerateEdges(string raw, int start, bool final)
    {
        var maxEnd = Math.Min(raw.Length, start + 48);
        var yieldedJapanese = false;

        // Japanese candidates. Interior boundaries must finish a romaji sound;
        // an unfinished consonant is only allowed at the end while typing.
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
            yield return new Edge(start, end, Language.Japanese, span, JapaneseScore(lower, start == 0, atEnd));
        }

        // English candidates. Exact dictionary hits are strongest. Prefixes are
        // considered only for the unfinished tail so words can become English
        // before the final letter (goog -> google).
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
            var explicitCase = char.IsAsciiLetterUpper(span[0]) || span.All(char.IsAsciiLetterUpper);
            var obviousSingle = span.Length == 1 && lower[0] is 'q' or 'l' or 'v' or 'x';

            if (!listed && !known && !prefix && !properPrefix && !explicitCase && !obviousSingle)
                continue;

            yield return new Edge(start, end, Language.English, span,
                EnglishScore(lower, listed, known, proper, prefix, properPrefix, explicitCase, obviousSingle, atEnd));
        }

        // Always leave a path for an unfinished/unknown character.
        if (!yieldedJapanese)
        {
            var one = raw[start..(start + 1)];
            yield return new Edge(start, start + 1, Language.Japanese, one, -8.0);
        }
    }

    private double JapaneseScore(string lower, bool atStart, bool atEnd)
    {
        var score = lower.Length * 2.0;
        var strict = _detector.Romaji.Analyze(lower);

        // Standard romaji is a stronger Japanese signal than composition-only
        // spellings such as co/tha/va that also occur inside English words.
        score += strict.IsValid ? 4.0 : -5.0;

        if (_detector.IsKnownJapaneseRomaji(lower)) score += 6.0;
        if (_detector.IsCommonJapanese?.Invoke(lower) == true) score += 6.0;

        // A complete all-Japanese interpretation gets a small continuity prior.
        if (atStart && atEnd) score += 2.0;

        // Common particles are useful short Japanese edges after English.
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
        bool explicitCase,
        bool obviousSingle,
        bool atEnd)
    {
        double score;
        if (listed || proper)
            score = 10.0 + lower.Length * 1.5;
        else if (known)
            score = 8.0 + lower.Length * 1.25;
        else if (properPrefix)
            score = 13.0 + lower.Length;
        else if (prefix)
            score = 9.0 + lower.Length;
        else if (explicitCase)
            score = 9.0 + lower.Length;
        else if (obviousSingle)
            score = 8.0;
        else
            score = 0.0;

        var strict = _detector.Romaji.Analyze(lower);

        // If the word is also perfectly good romaji, Japanese remains the
        // default unless there is stronger evidence. This protects repo/sushi.
        if (strict.IsValid && strict.Partial is "" or "n")
            score -= 10.0;
        else if (!strict.IsValid)
            score += 5.0;

        // issue/apple-like words are valid romaji only by using a sokuon; a
        // listed English word with that pattern is much more likely English.
        if ((listed || proper) && strict.IsValid && strict.Sokuon > 0)
            score += 12.0;

        // Curated words such as feature/remote are known false friends: they can
        // be read as romaji but are intended as English in normal mixed input.
        if (_detector.IsReadableEnglishWord(lower))
            score += 12.0;

        if (proper) score += 4.0;
        if (properPrefix) score += 5.0;
        if (explicitCase) score += 5.0;

        // Tiny English matches occur constantly inside Japanese romaji.
        if (lower.Length <= 2 && !explicitCase && !obviousSingle) score -= 12.0;
        else if (lower.Length == 3 && !proper && !explicitCase) score -= 3.0;

        // Prefix evidence is intentionally weaker once the user finishes.
        if (prefix && !atEnd) score -= 4.0;

        return score;
    }

    private CompositionSegment Japanese(string raw, bool final) =>
        new(false, _detector.Romaji.ConvertLenient(raw.ToLowerInvariant(), final), raw);

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
