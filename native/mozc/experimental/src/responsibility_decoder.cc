// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_decoder.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <string>
#include <string_view>
#include <unordered_set>

#include "win32/tip/meltype/generated_lexicon.h"

namespace mozc::win32::tsf::meltype {
namespace {

using Set = std::unordered_set<std::string>;

struct Lexicon {
  Set english;
  Set english_prefixes;
  Set readable_english;
  Set proper;
  Set japanese;
  Set japanese_prefixes;

  Lexicon() {
    auto add_with_prefixes = [](std::string_view word, Set* words,
                                Set* prefixes) {
      std::string lower(word);
      std::transform(lower.begin(), lower.end(), lower.begin(),
                     [](unsigned char c) {
                       return static_cast<char>(std::tolower(c));
                     });
      if (lower.empty()) return;
      words->insert(lower);
      for (std::size_t i = 1; i < lower.size(); ++i) {
        prefixes->insert(lower.substr(0, i));
      }
    };

    for (std::string_view word : kEnglishWords) {
      add_with_prefixes(word, &english, &english_prefixes);
    }
    for (std::string_view word : kProperNouns) {
      add_with_prefixes(word, &proper, &english_prefixes);
    }
    for (std::string_view word : kReadableEnglishWords) {
      readable_english.emplace(word);
      english.emplace(word);
      for (std::size_t i = 1; i < word.size(); ++i) {
        english_prefixes.emplace(word.substr(0, i));
      }
    }
    for (std::string_view word : kJapaneseWords) {
      add_with_prefixes(word, &japanese, &japanese_prefixes);
    }
  }
};

const Lexicon& GetLexicon() {
  static const Lexicon* const lexicon = new Lexicon();
  return *lexicon;
}

std::string Lower(std::string_view text) {
  std::string out(text);
  std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) {
    return static_cast<char>(std::tolower(c));
  });
  return out;
}

constexpr std::array<std::string_view, 18> kParticles = {
    "ha", "wa", "ga", "wo", "ni", "de", "to", "mo", "he",
    "no", "kara", "made", "yori", "tte", "ya", "ne", "yo", "ka"};

constexpr std::array<std::string_view, 20> kGrammar = {
    "suru", "shita", "shite", "shitai", "shimasu", "shimashita",
    "sare", "sareta", "saseru", "shinai", "shiyou", "sureba",
    "miru", "mita", "mite", "tsukau", "okuru", "kakunin",
    "tateta", "dekiru"};

constexpr std::array<std::string_view, 16> kAmbiguousEnglish = {
    "repo", "sushi", "anime", "same", "tomato", "go", "make", "red",
    "sake", "radio", "kana", "sumo", "ramen", "manga", "ninja", "koi"};

bool StartsWithAny(std::string_view text,
                   const auto& candidates) {
  return std::any_of(candidates.begin(), candidates.end(),
                     [text](std::string_view value) {
                       return text.starts_with(value);
                     });
}

}  // namespace

const char* ResponsibilityDecoder::KindName(ResponsibilityKind kind) {
  switch (kind) {
    case ResponsibilityKind::kJapanese:
      return "Japanese";
    case ResponsibilityKind::kLiteral:
      return "Literal";
    case ResponsibilityKind::kOpen:
      return "Open";
  }
  return "Unknown";
}

ResponsibilityPlan ResponsibilityDecoder::Decode(std::string_view input,
                                                 bool final,
                                                 BoundaryBias bias) const {
  ResponsibilityPlan plan;
  const std::string raw = Lower(input);
  std::size_t pos = 0;
  BoundaryBias local_bias = bias;

  while (pos < raw.size()) {
    const std::string_view rest(raw.data() + pos, raw.size() - pos);

    // After an English literal, grammar wins over a coincidental English
    // prefix.  This is the important commit|ha / network|miru fast path.
    if (local_bias == BoundaryBias::kAfterLiteral) {
      const std::size_t jp = LeadingJapaneseBoundaryLength(rest);
      if (jp > 0 && jp <= rest.size()) {
        AppendSpan(&plan, ResponsibilityKind::kJapanese, pos, pos + jp);
        pos += jp;
        local_bias = BoundaryBias::kNeutral;
        continue;
      }
    }

    // An English technical token may legally contain '.', '/', '_' or '-'.
    // We keep node. / the/ open until the right side is known, then protect
    // node.js / the/then as one literal span.
    const std::size_t technical = TechnicalLiteralLength(rest, final);
    if (technical > 0) {
      AppendSpan(&plan, ResponsibilityKind::kLiteral, pos, pos + technical);
      pos += technical;
      local_bias = BoundaryBias::kAfterLiteral;
      continue;
    }

    if (IsAsciiDigit(rest.front())) {
      std::size_t end = 1;
      while (end < rest.size() && IsAsciiDigit(rest[end])) ++end;
      AppendSpan(&plan, ResponsibilityKind::kLiteral, pos, pos + end);
      pos += end;
      local_bias = BoundaryBias::kNeutral;
      continue;
    }

    if (!IsAsciiAlpha(rest.front())) {
      // Symbols that are not part of a proven technical literal stay on the
      // Japanese/Mozc side.  This keeps de-ta as Japanese rather than turning
      // '-' into an English-token switch.
      AppendSpan(&plan, ResponsibilityKind::kJapanese, pos, pos + 1);
      ++pos;
      local_bias = BoundaryBias::kNeutral;
      continue;
    }

    const Match english = LongestEnglish(rest);
    if (english.length > 0) {
      const bool at_end = english.length == rest.size();
      const std::string_view tail = rest.substr(english.length);
      const bool japanese_boundary =
          !tail.empty() && StartsJapaneseBoundary(tail);

      if (english.strong && (japanese_boundary || (at_end && final))) {
        AppendSpan(&plan, ResponsibilityKind::kLiteral, pos,
                   pos + english.length);
        pos += english.length;
        local_bias = BoundaryBias::kAfterLiteral;
        continue;
      }

      // A complete English word at the end is not enough while typing.  The
      // next letters may prove a Japanese continuation or a longer English
      // token, so retain ownership as Open until a boundary or command arrives.
      if (at_end && !final) {
        plan.stable_end = pos;
        plan.has_open_suffix = true;
        return plan;
      }
    }

    // If the whole remaining suffix can still grow into an English word, hold
    // it.  commi must not be sent to Mozc before we know whether it is commit.
    if (!final && CouldStartEnglish(rest)) {
      plan.stable_end = pos;
      plan.has_open_suffix = true;
      return plan;
    }

    // Japanese is the default.  Release one byte immediately and re-evaluate
    // from the next position so an English anchor can still begin later.
    AppendSpan(&plan, ResponsibilityKind::kJapanese, pos, pos + 1);
    ++pos;
    local_bias = BoundaryBias::kNeutral;
  }

  plan.stable_end = pos;
  return plan;
}

ResponsibilityDecoder::Match ResponsibilityDecoder::LongestEnglish(
    std::string_view raw) const {
  Match result;
  const std::size_t max = std::min<std::size_t>(raw.size(), 64);
  for (std::size_t len = 2; len <= max; ++len) {
    const std::string_view word = raw.substr(0, len);
    if (!IsEnglishExact(word)) continue;
    result.length = len;
    result.strong = IsStrongEnglish(word);
  }
  return result;
}

std::size_t ResponsibilityDecoder::TechnicalLiteralLength(
    std::string_view raw, bool final) const {
  const Match head = LongestEnglish(raw);
  if (head.length == 0 || !head.strong || head.length >= raw.size() ||
      !IsTechnicalConnector(raw[head.length])) {
    return 0;
  }

  std::size_t end = head.length;
  bool saw_connector = false;
  while (end < raw.size()) {
    const char c = raw[end];
    if (IsAsciiAlpha(c) || IsAsciiDigit(c)) {
      ++end;
      continue;
    }
    if (IsTechnicalConnector(c)) {
      saw_connector = true;
      ++end;
      continue;
    }
    break;
  }
  if (!saw_connector) return 0;

  if (end == raw.size()) {
    if (!final) return 0;
    // Do not protect a dangling connector such as "node." on finalization.
    return IsTechnicalConnector(raw[end - 1]) ? 0 : end;
  }

  if (StartsJapaneseBoundary(raw.substr(end))) return end;
  return 0;
}

bool ResponsibilityDecoder::CouldStartEnglish(std::string_view prefix) const {
  const auto& lexicon = GetLexicon();
  const std::string lower = Lower(prefix);
  return lexicon.english_prefixes.contains(lower) ||
         lexicon.english.contains(lower) || lexicon.proper.contains(lower);
}

bool ResponsibilityDecoder::IsEnglishExact(std::string_view word) const {
  const auto& lexicon = GetLexicon();
  const std::string lower = Lower(word);
  return lexicon.english.contains(lower) || lexicon.proper.contains(lower);
}

bool ResponsibilityDecoder::IsStrongEnglish(std::string_view word) const {
  const auto& lexicon = GetLexicon();
  const std::string lower = Lower(word);

  if (lexicon.proper.contains(lower) ||
      lexicon.readable_english.contains(lower)) {
    return true;
  }
  if (!lexicon.english.contains(lower)) return false;
  if (IsAmbiguousEnglish(lower)) return false;

  // If Japanese owns the exact spelling or uses it as a productive prefix,
  // English must not steal it without additional context.
  if (IsJapaneseExact(lower) || IsJapanesePrefix(lower)) return false;
  return true;
}

bool ResponsibilityDecoder::IsJapaneseExact(std::string_view word) const {
  return GetLexicon().japanese.contains(Lower(word));
}

bool ResponsibilityDecoder::IsJapanesePrefix(std::string_view word) const {
  return GetLexicon().japanese_prefixes.contains(Lower(word));
}

bool ResponsibilityDecoder::IsAmbiguousEnglish(std::string_view word) const {
  return std::find(kAmbiguousEnglish.begin(), kAmbiguousEnglish.end(), word) !=
         kAmbiguousEnglish.end();
}

bool ResponsibilityDecoder::IsAsciiAlpha(char c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

bool ResponsibilityDecoder::IsAsciiDigit(char c) {
  return c >= '0' && c <= '9';
}

bool ResponsibilityDecoder::IsTechnicalConnector(char c) {
  return c == '.' || c == '/' || c == '_' || c == '-' || c == '+';
}

bool ResponsibilityDecoder::StartsJapaneseBoundary(std::string_view text) {
  if (text.empty()) return true;
  return StartsWithAny(text, kParticles) || StartsWithAny(text, kGrammar) ||
         GetLexicon().japanese.contains(std::string(text));
}

std::size_t ResponsibilityDecoder::LeadingJapaneseBoundaryLength(
    std::string_view text) {
  std::size_t best = 0;
  for (std::string_view particle : kParticles) {
    if (text.starts_with(particle)) best = std::max(best, particle.size());
  }
  for (std::string_view grammar : kGrammar) {
    if (text.starts_with(grammar)) best = std::max(best, grammar.size());
  }

  // Exact dictionary words are also useful after a protected literal.
  const auto& lexicon = GetLexicon();
  for (std::size_t len = 2; len <= text.size(); ++len) {
    if (lexicon.japanese.contains(std::string(text.substr(0, len)))) {
      best = std::max(best, len);
    }
  }
  return best;
}

void ResponsibilityDecoder::AppendSpan(ResponsibilityPlan* plan,
                                       ResponsibilityKind kind,
                                       std::size_t begin, std::size_t end) {
  if (begin >= end) return;
  if (!plan->stable.empty() && plan->stable.back().kind == kind &&
      plan->stable.back().end == begin) {
    plan->stable.back().end = end;
  } else {
    plan->stable.push_back({kind, begin, end});
  }
  plan->stable_end = end;
}

}  // namespace mozc::win32::tsf::meltype
