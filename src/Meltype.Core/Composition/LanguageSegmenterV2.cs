// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;

namespace Meltype.Composition;

/// <summary>
/// Experimental V2 language segmenter.
///
/// V1 greedily searches pre-tokenized romaji units. V2 instead builds a lattice
/// on the original keystroke string, gives every plausible English/Japanese span
/// a score, then chooses the highest-scoring whole-input path with dynamic
/// programming.
///
/// Raw character offsets are intentional. A language boundary can exist inside a
/// V1 romaji unit: "commitha" must be able to split as "commit|ha" even though
/// "tha" itself is a valid romaji spelling.
/// </summary>
internal sealed class LanguageSegmenterV2
{
    private readonly CompositionDetector _detector;

    private enum Language
    {
        Japanese,
        English,
    }

    /// <param name="Ambiguous">
    /// True when the English spelling is also a plausible Japanese-romaji span.
    /// Such edges need context and should not interrupt an otherwise Japanese path.
    /// </param>
    /// <param name="StrongEnglish">
    /// True when spelling/dictionary evidence strongly favors English: unreadable
    /// romaji, a curated technical word, an English word that needs a sokuon to
    /// read as Japanese, etc.
    /// </param>
    private sealed record Edge(
        int Start,
        int End,
        Language Language,
        string Raw,
        double Score,
        bool Ambiguous = false,
        bool StrongEnglish = false);

    private sealed class State
    {
        public required double Score { get; init; }
        public required int Switches { get; init; }
        public required int Pieces { get; init; }
        public required Language Language { get; init; }
        public Edge? Edge { get; init; }
        public State? Previous { get; init; }
    }

    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "ha", "wa", "ga", "wo", "ni", "de", "to", "mo", "he", "no",
    };

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

        // Explicit user learning is stronger than automatic segmentation when the
        // complete current token matches what was taught.
        if (_detector.LearnedLanguage(raw) is { } learnedWhole)
        {
            if (learnedWhole)
                return [new CompositionSegment(true, "", raw)];
            return [Japanese(raw, 0, raw.Length, units, pending, final)];
        }

        var contextEnglish = englishSentence || precedingEnglish == true || followingEnglish == true;

        // If the complete raw input is ordinary Japanese romaji, keep it Japanese
        // unless the string contains at least one genuinely strong English ->
        // Japanese boundary. This prevents incidental words such as red/mod/you
        // from being carved out of otherwise normal Japanese sentences.
        var wholeJapanese = _detector.Romaji.AnalyzeFragment(raw) is
            { IsValid: true, Partial: "" or "n" };
        if (!contextEnglish && wholeJapanese && !HasStrongMixedBoundary(raw, level))
            return [Japanese(raw, 0, raw.Length, units, pending, final)];

        // High-confidence whole-token paths are still part of V2's model: in
        // these cases one lattice edge is overwhelmingly better than every
        // fragmented alternative. Keeping the decision explicit also prevents
        // accidental proper-noun fragments such as Sam/Nim from stealing it.
        if (ContextSaysWholeEnglish(raw, precedingEnglish, followingEnglish, englishSentence, level) ||
            IsStrongWholeEnglish(raw, level))
        {
            return [new CompositionSegment(true, "", raw)];
        }

        var n = raw.Length;
        var best = new State?[n + 1, 2];

        foreach (var edge in GenerateEdges(raw, 0, level, final, contextEnglish))
            Put(best, edge.End, StartState(edge, precedingEnglish, englishSentence, level));

        for (var position = 1; position < n; position++)
        {
            for (var languageIndex = 0; languageIndex < 2; languageIndex++)
            {
                var previous = best[position, languageIndex];
                if (previous is null) continue;

                foreach (var edge in GenerateEdges(raw, position, level, final, contextEnglish))
                {
                    var switched = previous.Language != edge.Language;
                    var transition = switched ? -14.0 : -2.0;

                    if (previous.Language == Language.English && edge.Language == Language.Japanese)
                    {
                        // An ambiguous romaji-readable English word followed by
                        // Japanese is usually one continuous Japanese expression:
                        // sushi|ga -> すしが, matte, anime...
                        if (previous.Edge?.Ambiguous == true)
                        {
                            if (previous.Edge.StrongEnglish && IsParticle(edge.Raw))
                                transition += 8.0;
                            else
                                transition -= 16.0;
                        }
                        else if (IsJapaneseContinuation(edge.Raw))
                        {
                            // Strong English + Japanese is exactly the mixed-input
                            // pattern V2 is designed for: commit|ha, reflect|sareta.
                            transition += 10.0;
                        }
                        else if (previous.Edge?.StrongEnglish == true)
                        {
                            // A strong English head can also be followed directly
                            // by an ordinary Japanese verb/noun: invite|miru,
                            // cache|naosu, server|okuru.
                            transition += 8.0;
                        }
                    }
                    else if (previous.Language == Language.Japanese && edge.Language == Language.English)
                    {
                        // Do not let short/proper English matches appear randomly
                        // inside ordinary romaji (na|nim|o, ...|sam|a...).
                        if (edge.Ambiguous) transition -= 12.0;

                        // A Japanese particle followed by an unambiguous English
                        // word is a natural boundary: python|no|bug, api|no|error.
                        if (previous.Edge is { Language: Language.Japanese } previousEdge &&
                            IsParticle(previousEdge.Raw) &&
                            edge.StrongEnglish)
                        {
                            transition += 16.0;
                        }
                        else if (edge.StrongEnglish && edge.Raw.Length >= 4)
                        {
                            transition += 4.0;
                        }
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
            if (followingEnglish == true)
            {
                if (candidate.Language == Language.English)
                    ending += level == DetectionLevel.Conservative ? 15.0 : 30.0;
                else
                    ending -= 2.0;
            }
            else if (followingEnglish == false)
            {
                if (candidate.Language == Language.Japanese)
                    ending += 8.0;
                else if (candidate.Edge?.Ambiguous == true)
                    ending -= 30.0;
            }

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

        // DP may split one language into several dictionary-sized edges only for
        // scoring. Expose continuous same-language spans to the rest of Meltype.
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
                result.Add(Japanese(
                    text,
                    segment.Start,
                    segment.End,
                    units,
                    pending,
                    final: i == merged.Count - 1 ? final : true));
            }
        }

        return result;
    }

    private State StartState(
        Edge edge,
        bool? precedingEnglish,
        bool englishSentence,
        DetectionLevel level)
    {
        var context = 0.0;

        if (edge.Language == Language.English)
        {
            if (englishSentence)
            {
                context += level switch
                {
                    DetectionLevel.Conservative => 20.0,
                    DetectionLevel.Aggressive => 60.0,
                    _ => 50.0,
                };
            }
            else if (precedingEnglish == true)
            {
                context += level switch
                {
                    DetectionLevel.Conservative => 8.0,
                    DetectionLevel.Aggressive => 40.0,
                    _ => 30.0,
                };
            }
            else if (precedingEnglish == false)
            {
                context -= 3.0;
            }
        }
        else if (precedingEnglish == false)
        {
            context += 2.0;
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

    private IEnumerable<Edge> GenerateEdges(
        string raw,
        int start,
        DetectionLevel level,
        bool final,
        bool contextEnglish)
    {
        var maxEnd = Math.Min(raw.Length, start + 48);
        var yieldedJapanese = false;

        // Japanese edges.
        for (var end = start + 1; end <= maxEnd; end++)
        {
            var span = raw[start..end];
            var analysis = _detector.Romaji.AnalyzeFragment(span);
            if (!analysis.IsValid) continue;

            var atEnd = end == raw.Length;
            var complete = analysis.Partial is "" or "n";

            // Interior Japanese edges must end on a complete romaji sound. The
            // unfinished tail is permitted only while the user is still typing.
            if (!atEnd && !complete) continue;
            if (atEnd && final && !complete) continue;

            yieldedJapanese = true;
            yield return new Edge(
                start,
                end,
                Language.Japanese,
                span,
                JapaneseScore(span, start == 0, atEnd));
        }

        // English edges.
        for (var end = start + 1; end <= maxEnd; end++)
        {
            var span = raw[start..end];
            var atEnd = end == raw.Length;
            var atInputStart = start == 0;

            var listed = _detector.IsListedEnglishWord(span);
            var known = _detector.IsKnownEnglishWord(span);
            var broad = _detector.IsBroadEnglishWord(span);
            var proper = _detector.ProperNouns.Contains(span);
            var prefix = atEnd && !final && span.Length >= 2 && _detector.IsEnglishPrefix(span);
            var properPrefix = atEnd && !final && span.Length >= 2 && _detector.IsProperNounPrefix(span);

            var obviousSingle = raw.Length == 1 && atInputStart &&
                span.Length == 1 && span[0] is 'q' or 'l' or 'v' or 'x';
            var contextShortWord = raw.Length == 1 && atInputStart &&
                span.Length == 1 && span[0] is 'a' or 'i' or 'u' or 'r';

            // Unknown names/words are allowed as a whole-token English candidate
            // only when surrounding text already strongly says "English".
            var contextOnly = atInputStart && atEnd && contextEnglish &&
                span.Length >= 2 && !listed && !known && !broad && !proper;

            if (!listed && !known && !broad && !proper &&
                !prefix && !properPrefix && !obviousSingle &&
                !contextShortWord && !contextOnly)
            {
                continue;
            }

            var strict = _detector.Romaji.Analyze(span);
            var fragment = _detector.Romaji.AnalyzeFragment(span);
            var completeRomaji = strict.IsValid && strict.Partial is "" or "n";
            var japanesePrefix = _detector.IsJapaneseRomajiPrefix(span);
            var japaneseExact = _detector.IsKnownJapaneseRomaji(span);
            var readableEnglish = _detector.IsReadableEnglishWord(span);
            var smallKanaSpelling = !strict.IsValid &&
                fragment is { IsValid: true, Partial: "" };

            var consumedBeforeInvalid = strict.Tokens.Sum(t => t.Romaji.Length);

            // A complete ordinary romaji reading is ambiguous by definition.
            // Sokuon-heavy listed words (issue/commit) are excluded because that
            // reading is an accidental by-product of English spelling. Short
            // lower-case proper names such as Sam/Nim are also ambiguous inside
            // Japanese even when they end in an unfinished consonant.
            var ambiguous =
                !readableEnglish &&
                strict.Sokuon == 0 &&
                (
                    completeRomaji ||
                    proper && span.Length <= 4 && strict.IsValid
                );

            var japaneseRemainder = end < raw.Length && RemainderLooksJapanese(raw[end..]);
            var continuation = end < raw.Length && IsJapaneseContinuation(raw[end..]);

            var strongEnglish = IsStrongEnglishBoundaryHead(
                span,
                listed,
                known,
                broad,
                proper,
                strict,
                fragment,
                japanesePrefix,
                japaneseExact,
                readableEnglish,
                japaneseRemainder,
                continuation);

            if (!strongEnglish && atEnd && start > 0 &&
                StartsAfterParticle(raw, start) &&
                IsStrongWholeEnglish(span, level))
            {
                strongEnglish = true;
            }

            if (level == DetectionLevel.Conservative && proper && completeRomaji)
                strongEnglish = false;

            var whole = atInputStart && atEnd;

            var englishScore = EnglishScore(
                span,
                listed,
                known,
                broad,
                proper,
                prefix,
                properPrefix,
                obviousSingle,
                contextShortWord,
                contextOnly,
                level,
                final,
                japaneseRemainder,
                continuation,
                atEnd,
                whole,
                completeRomaji,
                japanesePrefix,
                japaneseExact,
                readableEnglish,
                smallKanaSpelling,
                strict.Sokuon,
                ambiguous,
                strongEnglish);

            // Boundary position is evidence. Prefer a long strong word at the
            // beginning (debug|ha, deploy|miru) over a shorter word found later
            // inside it (de|bug|ha). Likewise, a strong English word immediately
            // after a Japanese particle is a natural switch (github|ni|push,
            // linux|de|kernel).
            if (strongEnglish && (japaneseRemainder || continuation || atEnd))
            {
                if (start == 0 && (japaneseRemainder || continuation))
                    englishScore += 32.0 + Math.Min(span.Length, 12) * 1.5;
                else if (start > 0 && StartsAfterParticle(raw, start))
                    englishScore += 28.0 + Math.Min(span.Length, 12);
            }

            if (start > 0 && HasDominatingEnglishSpan(raw, start, end, level))
                englishScore -= 80.0;

            if (HasLongerDominatingEnglishSpan(raw, start, end, level))
                englishScore -= 80.0;

            if (CrossesProtectedJapaneseBoundary(raw, start, end, level))
                englishScore -= 120.0;

            yield return new Edge(
                start,
                end,
                Language.English,
                span,
                englishScore,
                Ambiguous: ambiguous,
                StrongEnglish: strongEnglish);
        }

        // Unknown/unreadable bytes still need a path through the lattice. A low
        // Japanese score preserves the typed character without claiming it is an
        // English word.
        if (!yieldedJapanese)
        {
            var one = raw[start..(start + 1)];
            yield return new Edge(start, start + 1, Language.Japanese, one, -8.0);
        }
    }

    private double JapaneseScore(string lower, bool atStart, bool atEnd)
    {
        var score = lower.Length * 3.0;
        var strict = _detector.Romaji.Analyze(lower);

        if (strict.IsValid && strict.Partial is "" or "n")
            score += 8.0;
        else if (!strict.IsValid)
            score -= 6.0;

        if (_detector.IsCommonJapanese?.Invoke(lower) == true)
            score += 8.0;

        var learned = _detector.LearnedLanguage(lower);
        if (learned == false) score += 30.0;
        else if (learned == true) score -= 30.0;

        if (atStart && atEnd) score += 6.0;

        // x/l spellings explicitly request small kana in Japanese IMEs.
        if (lower.Length >= 2 && lower[0] is 'x' or 'l' &&
            _detector.Romaji.AnalyzeFragment(lower) is { IsValid: true })
        {
            score += 14.0;
        }

        if (IsParticle(lower)) score += 2.0;

        return score;
    }

    private double EnglishScore(
        string lower,
        bool listed,
        bool known,
        bool broad,
        bool proper,
        bool prefix,
        bool properPrefix,
        bool obviousSingle,
        bool contextShortWord,
        bool contextOnly,
        DetectionLevel level,
        bool final,
        bool japaneseRemainder,
        bool continuation,
        bool atEnd,
        bool whole,
        bool completeRomaji,
        bool japanesePrefix,
        bool japaneseExact,
        bool readableEnglish,
        bool smallKanaSpelling,
        int sokuon,
        bool ambiguous,
        bool strongEnglish)
    {
        double score;

        if (listed || proper)
            score = lower.Length * 3.0 + 10.0;
        else if (known)
            score = lower.Length * 3.0 + 7.0;
        else if (broad)
            score = lower.Length * 2.4 + 6.0;
        else if (properPrefix)
            score = lower.Length * 2.5 + 10.0;
        else if (prefix)
            score = lower.Length * 2.2 + 7.0;
        else if (obviousSingle)
            score = 8.0;
        else if (contextShortWord)
            score = 5.0;
        else if (contextOnly)
            score = lower.Length * 1.5;
        else
            score = 0.0;

        if (ambiguous)
            score -= 18.0;
        else if (strongEnglish)
            score += 7.0;

        if (sokuon > 0 && (listed || proper))
            score += 30.0;

        if (readableEnglish)
            score += 25.0;

        // Proper nouns are strong only when they do not also appear as a Japanese
        // romaji word. This keeps amazon English while avoiding ...nikon... being
        // pulled out of ordinary Japanese.
        if (proper && !japaneseExact)
            score += 25.0;
        if (properPrefix)
            score += 6.0;

        var learned = _detector.LearnedLanguage(lower);
        if (learned == true) score += 35.0;
        else if (learned == false) score -= 55.0;

        // A known English word followed by a valid Japanese continuation is the
        // central mixed-input signal. Ambiguous Japanese-looking English words
        // deliberately do not get this bonus.
        var exactEnglish = listed || known || broad || proper;
        if (exactEnglish && strongEnglish && japaneseRemainder)
            score += 10.0;
        if (exactEnglish && strongEnglish && continuation)
            score += lower.Length <= 3 ? 40.0 : 35.0;

        // Very short curated tokens can be useful before a Japanese particle
        // even when they are romaji-readable (api|no, ok|no). Ordinary particles
        // themselves are explicitly excluded.
        if (listed && lower.Length <= 3 && continuation && !IsParticle(lower))
            score += 25.0;

        // A strong exact English word at the current input tail should beat a
        // fragmented kana path (push, update, bug).
        if (atEnd && exactEnglish && strongEnglish)
            score += 15.0;

        if (broad && strongEnglish)
            score += 12.0;

        // Whole-token exact English is stronger than a path made of several
        // smaller pieces, but not when the word is deliberately ambiguous.
        if (whole && exactEnglish && strongEnglish && (!ambiguous || proper))
            score += 20.0;

        // A prefix of a known Japanese-romaji word should not turn English just
        // because an English dictionary contains the same letters (kit..., fairu).
        if (japanesePrefix && (japaneseRemainder || !whole) && !readableEnglish)
            score -= 24.0;
        else if (japanesePrefix && whole && !final && !strongEnglish)
            score -= 16.0;

        if (smallKanaSpelling && learned != true && !readableEnglish && !strongEnglish)
            score -= 40.0;

        if (level == DetectionLevel.Aggressive && exactEnglish)
            score += lower.Length <= 2 ? 35.0 : 8.0;

        if (level == DetectionLevel.Conservative)
        {
            if (completeRomaji && !proper) score -= 5.0;

            // Conservative mode intentionally keeps romaji-readable lower-case
            // proper nouns such as amazon as Japanese until context says English.
            if (proper && completeRomaji) score -= 50.0;
        }

        // Short matches are common inside Japanese. Only curated strong short
        // tokens (api/ok/bug) or explicit context should survive this penalty.
        if (lower.Length <= 2 && learned != true && !obviousSingle && !contextShortWord)
            score -= strongEnglish ? 10.0 : 22.0;
        else if (lower.Length == 3 && learned != true && !proper)
            score -= strongEnglish ? 2.0 : 8.0;

        return score;
    }

    private bool HasStrongMixedBoundary(string raw, DetectionLevel level)
    {
        for (var start = 0; start < raw.Length - 1; start++)
        {
            // A new English span in the middle of Japanese normally needs a
            // Japanese particle immediately before it. This keeps random
            // dictionary hits inside Japanese from becoming language switches.
            var startAllowed = start == 0 || StartsAfterParticle(raw, start);
            if (!startAllowed) continue;

            for (var end = raw.Length - 1; end > start; end--)
            {
                var span = raw[start..end];
                var rest = raw[end..];
                if (!RemainderLooksJapanese(rest)) continue;
                if (IsStrongEnglishBoundaryHead(span, rest, level)) return true;
            }
        }

        return false;
    }

    private bool IsStrongEnglishBoundaryHead(string span, string rest, DetectionLevel level)
    {
        var listed = _detector.IsListedEnglishWord(span);
        var known = _detector.IsKnownEnglishWord(span);
        var broad = _detector.IsBroadEnglishWord(span);
        var proper = _detector.ProperNouns.Contains(span);
        if (!listed && !known && !broad && !proper) return false;

        var strict = _detector.Romaji.Analyze(span);
        var fragment = _detector.Romaji.AnalyzeFragment(span);
        return IsStrongEnglishBoundaryHead(
            span,
            listed,
            known,
            broad,
            proper,
            strict,
            fragment,
            _detector.IsJapaneseRomajiPrefix(span),
            _detector.IsKnownJapaneseRomaji(span),
            _detector.IsReadableEnglishWord(span),
            japaneseRemainder: RemainderLooksJapanese(rest),
            continuation: IsJapaneseContinuation(rest));
    }

    private bool IsStrongEnglishBoundaryHead(
        string span,
        bool listed,
        bool known,
        bool broad,
        bool proper,
        Meltype.Detection.RomajiAnalysis strict,
        Meltype.Detection.RomajiAnalysis fragment,
        bool japanesePrefix,
        bool japaneseExact,
        bool readableEnglish,
        bool japaneseRemainder,
        bool continuation)
    {
        if (!japaneseRemainder && !continuation) return false;

        if (_detector.LearnedLanguage(span) == false) return false;
        if (_detector.LearnedLanguage(span) == true) return true;

        if (readableEnglish) return true;
        if (strict.IsValid && strict.Sokuon > 0 && (listed || proper)) return true;

        var completeRomaji = strict.IsValid && strict.Partial is "" or "n";
        var consumedBeforeInvalid = strict.Tokens.Sum(t => t.Romaji.Length);
        var smallKanaSpelling = !strict.IsValid &&
            fragment is { IsValid: true, Partial: "" };

        // Most normal English spellings are not valid Japanese romaji. They are
        // safe boundaries even when the following Japanese starts with a verb
        // rather than a particle: invite|miru, server|okuru.
        if (!strict.IsValid)
        {
            // ca/cu/co and similar composition-only C-row spellings are a
            // special case: an exact English word such as cache should win,
            // while arbitrary Japanese C-row input should not.
            if (smallKanaSpelling && consumedBeforeInvalid == 0)
                return span.Length >= 4 && (listed || broad || proper);

            return span.Length >= 4 || listed || known || proper;
        }

        if (strict.Partial is not ("" or "n") && !japanesePrefix &&
            (span.Length >= 4 || continuation && (listed || known)))
            return true;

        // Lower-case proper names that are also exact Japanese readings (samui)
        // are not strong without context.
        if (proper && span.Length >= 4 && !japaneseExact)
            return true;

        if (continuation)
        {
            // Curated 3-letter technical tokens such as api are useful before a
            // particle. Two-letter complete romaji words such as go/no remain
            // Japanese; incomplete abbreviations such as ok may be English.
            if (span.Length == 3 && listed && !IsParticle(span))
                return true;

            if (span.Length == 2 && (listed || known) && !completeRomaji &&
                !IsParticle(span))
                return true;

            // Longer complete-romaji words (sushi/repo/anime) stay ambiguous.
            if (!completeRomaji && span.Length >= 4 &&
                (listed || known || broad || proper))
                return true;
        }

        return false;
    }

    private bool CrossesProtectedJapaneseBoundary(
        string raw,
        int start,
        int end,
        DetectionLevel level)
    {
        // Do not let an inflected English word consume the beginning of a
        // well-formed Japanese continuation:
        // build|suru beats builds|uru, merge|shita beats merges|hita.
        for (var boundary = start + 2; boundary < end; boundary++)
        {
            var rest = raw[boundary..];
            if (!IsJapaneseContinuation(rest))
                continue;

            var head = raw[start..boundary];
            if (IsStrongWholeEnglish(head, level))
                return true;
        }

        return false;
    }

    private bool HasLongerDominatingEnglishSpan(
        string raw,
        int start,
        int end,
        DetectionLevel level)
    {
        // A known Japanese continuation boundary is stronger than maximal munch.
        // commit|suru must beat commits|uru, issue|de must beat issued|e.
        var currentRest = raw[end..];
        if (IsJapaneseContinuation(currentRest) ||
            _detector.IsKnownJapaneseRomaji(currentRest))
            return false;

        // Same-start maximal-munch rule. If a longer strong English token starts
        // here and leaves a plausible Japanese continuation, do not let a short
        // prefix (term/pack/net) split it prematurely.
        for (var longerEnd = end + 1; longerEnd < raw.Length; longerEnd++)
        {
            var longer = raw[start..longerEnd];
            var rest = raw[longerEnd..];
            if (!RemainderLooksJapanese(rest) && !IsJapaneseContinuation(rest))
                continue;

            if (IsStrongEnglishBoundaryHead(longer, rest, level))
                return true;
        }

        return false;
    }

    private bool HasDominatingEnglishSpan(
        string raw,
        int start,
        int end,
        DetectionLevel level)
    {
        // Prefer the longest strong lexical span ending at the same point.
        // This is a lattice dominance rule, not a word-specific exception:
        // debug dominates de|bug, deploy dominates de|ploy.
        for (var earlier = 0; earlier < start; earlier++)
        {
            if (earlier > 0 && !StartsAfterParticle(raw, earlier))
                continue;

            var longer = raw[earlier..end];
            if (longer.Length <= raw[start..end].Length)
                continue;

            if (IsStrongWholeEnglish(longer, level))
                return true;
        }

        return false;
    }

    private static bool StartsAfterParticle(string raw, int start)
    {
        foreach (var particle in Particles)
        {
            if (start >= particle.Length &&
                raw.AsSpan(start - particle.Length, particle.Length)
                    .SequenceEqual(particle.AsSpan()))
                return true;
        }
        return false;
    }

    private bool HasKnownEnglishJapaneseSplit(string raw)
    {
        for (var k = 1; k < raw.Length; k++)
        {
            var head = raw[..k];
            var rest = raw[k..];
            if (!IsJapaneseContinuation(rest)) continue;

            if (_detector.IsListedEnglishWord(head) ||
                _detector.IsKnownEnglishWord(head) ||
                _detector.IsBroadEnglishWord(head) ||
                _detector.ProperNouns.Contains(head))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContextSaysWholeEnglish(
        string raw,
        bool? precedingEnglish,
        bool? followingEnglish,
        bool englishSentence,
        DetectionLevel level)
    {
        // Explicit Japanese text after the caret breaks the English-context tie.
        if (followingEnglish == false) return false;

        var exact = _detector.IsListedEnglishWord(raw) ||
                    _detector.IsKnownEnglishWord(raw) ||
                    _detector.IsBroadEnglishWord(raw) ||
                    _detector.ProperNouns.Contains(raw);

        // Two or more preceding English words are strong sentence context. This
        // also allows unknown names such as "taro" to stay Latin.
        if (englishSentence)
            return raw.Length > 0 && !HasKnownEnglishJapaneseSplit(raw);

        if (followingEnglish == true && exact) return true;

        if (precedingEnglish != true || level == DetectionLevel.Conservative)
            return false;

        if (raw.Length == 1)
            return raw[0] is 'a' or 'i' or 'u' or 'r';

        if (!exact) return false;

        // After only one English token, short Japanese particles remain Japanese
        // (GitHub + no/to/ga), while "is"/"at" and 3+ letter words may be English.
        if (raw.Length >= 3) return true;

        var analysis = _detector.Romaji.Analyze(raw);
        return !(analysis.IsValid && analysis.Partial is "" or "n");
    }

    private bool IsStrongWholeEnglish(string raw, DetectionLevel level)
    {
        var listed = _detector.IsListedEnglishWord(raw);
        var known = _detector.IsKnownEnglishWord(raw);
        var broad = _detector.IsBroadEnglishWord(raw);
        var proper = _detector.ProperNouns.Contains(raw);
        var exact = listed || known || broad || proper;
        if (!exact) return false;

        var strict = _detector.Romaji.Analyze(raw);
        var fragment = _detector.Romaji.AnalyzeFragment(raw);
        var completeRomaji = strict.IsValid && strict.Partial is "" or "n";
        var smallKanaSpelling = !strict.IsValid &&
            fragment is { IsValid: true, Partial: "" };
        var consumedBeforeInvalid = strict.Tokens.Sum(t => t.Romaji.Length);
        var japaneseExact = _detector.IsKnownJapaneseRomaji(raw);

        if (level == DetectionLevel.Conservative && proper && completeRomaji)
            return false;

        if (_detector.IsReadableEnglishWord(raw)) return true;
        if (strict.Sokuon > 0 && (listed || proper)) return true;

        if (!strict.IsValid)
        {
            // who/va-like composition-only spellings start failing immediately
            // and are intentional Japanese input. hello fails only after "he",
            // which is much stronger English evidence.
            if (smallKanaSpelling && consumedBeforeInvalid == 0) return false;
            return true;
        }

        if (strict.Partial is not ("" or "n") &&
            !_detector.IsJapaneseRomajiPrefix(raw))
            return true;

        if (proper && !japaneseExact && level != DetectionLevel.Conservative)
            return true;

        return false;
    }

    /// <summary>
    /// Prefer already-normalized kana when V2 boundaries coincide with old
    /// CompositionUnit boundaries. Only re-run romaji conversion when a new
    /// boundary cuts through an old unit (the commit|ha case).
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

        if (firstUnit >= 0 && lastUnitExclusive >= firstUnit && end <= unitsRawLength)
        {
            var kana = string.Concat(
                units.Skip(firstUnit).Take(lastUnitExclusive - firstUnit).Select(u => u.Kana));
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
        // pending is appended by CompositionText after the segment.
        var convertedEnd = Math.Min(end, unitsRawLength);
        var convertedLength = Math.Max(0, convertedEnd - start);
        var convertedRaw = convertedLength > 0
            ? raw[..Math.Min(raw.Length, convertedLength)]
            : "";

        return new CompositionSegment(
            false,
            _detector.Romaji.ConvertLenient(convertedRaw, final: true),
            raw);
    }

    private bool RemainderLooksJapanese(string raw)
    {
        var analysis = _detector.Romaji.AnalyzeFragment(raw);
        return analysis.IsValid && analysis.Partial is "" or "n";
    }

    private bool IsJapaneseContinuation(string raw)
    {
        // Verb/auxiliary forms are strong boundaries on their own.
        foreach (var prefix in JapaneseContinuationPrefixes)
        {
            if (Particles.Contains(prefix)) continue;
            if (raw.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        // A two-letter particle is a boundary only if what follows can itself
        // continue as Japanese or as a strong English token. This prevents
        // network from becoming net|work merely because work starts with "wo".
        foreach (var particle in Particles)
        {
            if (!raw.StartsWith(particle, StringComparison.Ordinal))
                continue;

            var tail = raw[particle.Length..];
            if (tail.Length == 0) return true;
            if (RemainderLooksJapanese(tail)) return true;
            if (StartsWithStrongEnglishWord(tail)) return true;
        }

        return false;
    }

    private bool StartsWithStrongEnglishWord(string raw)
    {
        for (var end = raw.Length; end >= 2; end--)
        {
            var head = raw[..end];
            if (!IsStrongWholeEnglish(head, DetectionLevel.Balanced))
                continue;

            var tail = raw[end..];
            if (tail.Length == 0 ||
                RemainderLooksJapanese(tail) ||
                JapaneseContinuationPrefixes
                    .Where(p => !Particles.Contains(p))
                    .Any(p => tail.StartsWith(p, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsParticle(string raw) => Particles.Contains(raw);

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
        if (candidate.Switches != current.Switches)
            return candidate.Switches < current.Switches;
        return candidate.Pieces < current.Pieces;
    }
}
