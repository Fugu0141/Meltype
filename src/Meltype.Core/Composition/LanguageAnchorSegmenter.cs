// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;
using Meltype.Detection;

namespace Meltype.Composition;

/// <summary>
/// Experimental anchor-based language segmenter.
///
/// This is intentionally not a V1/V2-style language lattice. Japanese is the
/// default for every alphabetic run. The detector only "protects" spans that have
/// strong lexical English evidence *and* plausible language boundaries on both
/// sides. Everything not protected is reparsed from raw keystrokes as Japanese.
///
/// Examples:
///   commitha        -> [commit] [ha]       -> commitは
///   commitsuru      -> [commit] [suru]     -> commitする
///   nihongowohanasu -> no English anchor   -> にほんごをはなす
///   networkmiru     -> [network] [miru]    -> networkみる
///
/// Digits are hard boundaries, so:
///   commitha12noyatsudayo -> commitは12のやつだよ
/// </summary>
internal sealed class LanguageAnchorSegmenter
{
    private readonly RomajiDetector _romaji;
    private readonly DictionaryDetector _japanese;
    private readonly EnglishDetector _english;
    private readonly ProperNouns _proper;
    private readonly Func<IWordChecker?> _spellChecker;
    private readonly Func<string, bool?> _learned;

    private static readonly Lazy<WordList> ReadableEnglish = new(() =>
    {
        var list = new WordList();
        foreach (var word in DictionarySource.Load("english-readable.txt", null))
            list.Add(word);
        return list;
    });

    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "ha", "wa", "ga", "wo", "ni", "de", "to", "mo", "he", "no",
        "kara", "made", "yori",
    };

    private sealed record Anchor(
        int Start,
        int End,
        int Score,
        string Reason);

    public LanguageAnchorSegmenter(
        RomajiDetector romaji,
        DictionaryDetector japanese,
        EnglishDetector english,
        ProperNouns proper,
        Func<IWordChecker?> spellChecker,
        Func<string, bool?> learned)
    {
        _romaji = romaji;
        _japanese = japanese;
        _english = english;
        _proper = proper;
        _spellChecker = spellChecker;
        _learned = learned;
    }

    public static bool CanHandle(string raw) =>
        raw.Length > 0 &&
        raw.Any(char.IsAsciiLetter) &&
        raw.All(char.IsAsciiLetterOrDigit);

    public IReadOnlyList<CompositionSegment> Segment(
        IReadOnlyList<CompositionUnit> units,
        string pending,
        DetectionLevel level,
        bool final)
    {
        var raw = string.Concat(units.Select(u => u.Raw)) + pending;
        if (raw.Length == 0)
            return [new CompositionSegment(false, "", "")];

        var unitsRawLength = units.Sum(u => u.Raw.Length);
        var result = new List<CompositionSegment>();

        var position = 0;
        while (position < raw.Length)
        {
            if (char.IsAsciiDigit(raw[position]))
            {
                var end = position + 1;
                while (end < raw.Length && char.IsAsciiDigit(raw[end])) end++;
                result.Add(new CompositionSegment(true, "", raw[position..end]));
                position = end;
                continue;
            }

            var runEnd = position + 1;
            while (runEnd < raw.Length && char.IsAsciiLetter(raw[runEnd])) runEnd++;

            var run = raw[position..runEnd];
            var lower = run.ToLowerInvariant();
            var anchors = SelectAnchors(lower, level, final && runEnd == raw.Length);

            var local = 0;
            foreach (var anchor in anchors)
            {
                if (anchor.Start > local)
                {
                    result.Add(Japanese(
                        raw,
                        position + local,
                        position + anchor.Start,
                        unitsRawLength,
                        final && runEnd == raw.Length && anchor.Start == run.Length));
                }

                result.Add(new CompositionSegment(
                    true,
                    "",
                    raw[(position + anchor.Start)..(position + anchor.End)]));

                local = anchor.End;
            }

            if (local < run.Length)
            {
                result.Add(Japanese(
                    raw,
                    position + local,
                    runEnd,
                    unitsRawLength,
                    final && runEnd == raw.Length));
            }

            position = runEnd;
        }

        return MergeAdjacent(result);
    }

    private IReadOnlyList<Anchor> SelectAnchors(
        string raw,
        DetectionLevel level,
        bool final)
    {
        var candidates = GenerateCandidates(raw, level, final).ToList();
        if (candidates.Count == 0) return [];

        // Weighted interval scheduling. The weights are not "English vs Japanese"
        // scores; they only choose between overlapping English protection spans.
        // Japanese remains the implicit default everywhere else.
        candidates.Sort((a, b) =>
        {
            var byEnd = a.End.CompareTo(b.End);
            if (byEnd != 0) return byEnd;
            var byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0) return byStart;
            return b.Score.CompareTo(a.Score);
        });

        var previous = new int[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            previous[i] = -1;
            for (var j = i - 1; j >= 0; j--)
            {
                if (candidates[j].End <= candidates[i].Start)
                {
                    previous[i] = j;
                    break;
                }
            }
        }

        var best = new int[candidates.Count + 1];
        var take = new bool[candidates.Count];

        for (var i = 1; i <= candidates.Count; i++)
        {
            var candidate = candidates[i - 1];
            var with = candidate.Score + best[previous[i - 1] + 1];
            var without = best[i - 1];

            if (with > without)
            {
                best[i] = with;
                take[i - 1] = true;
            }
            else
            {
                best[i] = without;
            }
        }

        var selected = new List<Anchor>();
        var index = candidates.Count - 1;
        while (index >= 0)
        {
            var candidate = candidates[index];
            var with = candidate.Score + best[previous[index] + 1];
            var without = best[index];

            if (with > without)
            {
                selected.Add(candidate);
                index = previous[index];
            }
            else
            {
                index--;
            }
        }

        selected.Reverse();
        return selected;
    }

    private IEnumerable<Anchor> GenerateCandidates(
        string raw,
        DetectionLevel level,
        bool final)
    {
        const int maxWordLength = 48;

        for (var start = 0; start < raw.Length; start++)
        {
            var maxEnd = Math.Min(raw.Length, start + maxWordLength);
            for (var end = start + 2; end <= maxEnd; end++)
            {
                var word = raw[start..end];
                var lexical = LexicalEvidence(word);
                if (lexical <= 0) continue;

                var left = LeftBoundary(raw, start);
                var right = RightBoundary(raw, end);

                if (start > 0 && left < 3) continue;
                if (end < raw.Length && right < 2) continue;

                var profile = RomajiProfile(word);
                if (!AcceptEnglishWord(word, lexical, profile, left, right, start, end, raw.Length, level))
                    continue;

                var score =
                    lexical +
                    left * 10 +
                    right * 12 +
                    Math.Min(word.Length, 12) * 2;

                // Whole-run strong English is useful, but mixed boundaries should
                // beat inflected overlaps when their Japanese continuation is
                // substantially better: commit|suru > commits|uru.
                if (start == 0) score += 8;
                if (end == raw.Length) score += 6;

                yield return new Anchor(
                    start,
                    end,
                    score,
                    $"lex={lexical},left={left},right={right}");
            }
        }
    }

    private int LexicalEvidence(string word)
    {
        var learned = _learned(word);
        if (learned == false) return -1000;
        if (learned == true) return 100;

        if (_proper.Contains(word) && word.Length >= 4) return 88;
        if (_english.Words.ContainsWord(word)) return 78;
        if (word.Length >= 4 && BuiltInWordChecker.Shared.IsWord(word)) return 66;

        var checker = _spellChecker();
        if (word.Length >= 4 && checker?.IsAvailable == true && checker.IsWord(word))
            return 62;

        return 0;
    }

    private sealed record Profile(
        bool StrictValid,
        bool CompleteRomaji,
        bool FragmentValid,
        int Sokuon,
        bool JapaneseExact,
        bool JapanesePrefix,
        bool ReadableEnglish,
        bool Proper);

    private Profile RomajiProfile(string word)
    {
        var strict = _romaji.Analyze(word);
        var fragment = _romaji.AnalyzeFragment(word);

        return new Profile(
            StrictValid: strict.IsValid,
            CompleteRomaji: strict.IsValid && strict.Partial is "" or "n",
            FragmentValid: fragment.IsValid,
            Sokuon: strict.Sokuon,
            JapaneseExact: _japanese.Words.ContainsWord(word),
            JapanesePrefix: _japanese.IsPrefix(word),
            ReadableEnglish: ReadableEnglish.Value.ContainsWord(word),
            Proper: _proper.Contains(word));
    }

    private bool AcceptEnglishWord(
        string word,
        int lexical,
        Profile profile,
        int left,
        int right,
        int start,
        int end,
        int rawLength,
        DetectionLevel level)
    {
        if (lexical >= 100) return true;

        // Very short matches are frequent accidents inside Japanese. A 3-letter
        // curated token is accepted only at a very strong Japanese boundary
        // (api|ha); 2-letter words never become anchors automatically.
        if (word.Length <= 2) return false;
        if (word.Length == 3)
        {
            if (profile.Proper) return false;
            return _english.Words.ContainsWord(word) && right >= 5 && (start == 0 || left >= 5);
        }

        // Listed English that needs a Japanese sokuon to be read as romaji is a
        // classic accidental reading: commit, issue, apple.
        if (profile.Sokuon > 0 &&
            (_english.Words.ContainsWord(word) || profile.Proper) &&
            !profile.JapanesePrefix)
        {
            return true;
        }

        if (profile.ReadableEnglish) return true;

        // Proper nouns are anchors even if their letters form valid romaji
        // (ubuntu, amazon), unless the Japanese dictionary explicitly owns the
        // same reading.
        if (profile.Proper && !profile.JapaneseExact)
            return level != DetectionLevel.Conservative || !profile.CompleteRomaji;

        // Ordinary English spelling that cannot be read as strict romaji is
        // strong evidence. Composition-only small-kana spellings still count as
        // English when the word is a curated dictionary item of 4+ letters.
        if (!profile.StrictValid)
        {
            if (!profile.FragmentValid) return true;
            return _english.Words.ContainsWord(word) && !profile.JapanesePrefix;
        }

        // A trailing unfinished consonant in an exact dictionary word is strong
        // English evidence (git, zoom, reflect-like shapes).
        var strict = _romaji.Analyze(word);
        if (strict.IsValid && strict.Partial is not ("" or "n") &&
            (_english.Words.ContainsWord(word) || lexical >= 66) &&
            !profile.JapanesePrefix)
        {
            return true;
        }

        // Fully readable romaji words (repo, sushi, anime, same, make, tomato)
        // stay Japanese by default. This is the key asymmetry of the anchor
        // model: English must prove itself; Japanese does not.
        return false;
    }

    private int LeftBoundary(string raw, int start)
    {
        if (start == 0) return 6;

        var left = raw[..start];
        if (!IsCompleteJapanese(left)) return 0;

        if (EndsWithParticle(left)) return 6;
        if (_japanese.Words.ContainsWord(left)) return 5;
        if (_japanese.IsPrefix(left)) return 3;

        return 2;
    }

    private int RightBoundary(string raw, int end)
    {
        if (end == raw.Length) return 6;

        var right = raw[end..];
        if (!IsCompleteJapanese(right)) return 0;

        if (StartsWithParticle(right)) return 6;
        if (StartsWithJapaneseWord(right)) return 5;

        // A longer complete romaji suffix is weak but useful evidence. This
        // covers forms not explicitly present in the small Japanese dictionary
        // (issue|tateta) without letting one-letter debris win.
        if (right.Length >= 3) return 2;

        return 1;
    }

    private bool IsCompleteJapanese(string text)
    {
        if (text.Length == 0) return true;
        var analysis = _romaji.AnalyzeFragment(text);
        return analysis.IsValid && analysis.Partial is "" or "n";
    }

    private static bool StartsWithParticle(string text) =>
        Particles.Any(p => text.StartsWith(p, StringComparison.Ordinal));

    private static bool EndsWithParticle(string text) =>
        Particles.Any(p => text.EndsWith(p, StringComparison.Ordinal));

    private bool StartsWithJapaneseWord(string text)
    {
        if (text.Length == 0) return false;
        foreach (var word in _japanese.Words.WordsStartingWith(text[0]))
        {
            if (word.Length < 2) continue;
            if (text.StartsWith(word, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private CompositionSegment Japanese(
        string raw,
        int start,
        int end,
        int unitsRawLength,
        bool final)
    {
        var original = raw[start..end];

        // CompositionText appends PendingText for the final Japanese segment.
        // Therefore Kana contains only the normalized part that is already in
        // CompositionUnit; raw still contains the complete segment.
        var normalizedEnd = Math.Min(end, unitsRawLength);
        if (start >= normalizedEnd)
            return new CompositionSegment(false, "", original);

        var normalized = raw[start..normalizedEnd].ToLowerInvariant();
        var kana = _romaji.ConvertLenient(normalized, final: true);
        return new CompositionSegment(false, kana, original);
    }

    private static List<CompositionSegment> MergeAdjacent(
        IEnumerable<CompositionSegment> source)
    {
        var result = new List<CompositionSegment>();

        foreach (var segment in source)
        {
            if (segment.Raw.Length == 0) continue;

            // Keep digit runs separate even though they are represented as raw
            // segments, because they are hard lexical boundaries.
            var segmentDigits = segment.Raw.All(char.IsAsciiDigit);
            var previousDigits = result.Count > 0 && result[^1].Raw.All(char.IsAsciiDigit);

            if (result.Count > 0 &&
                result[^1].IsEnglish == segment.IsEnglish &&
                !segmentDigits &&
                !previousDigits)
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
