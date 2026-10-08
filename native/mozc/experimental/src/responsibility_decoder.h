// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#ifndef MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_DECODER_H_
#define MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_DECODER_H_

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace mozc::win32::tsf::meltype {

enum class ResponsibilityKind {
  kJapanese,
  kLiteral,
  kOpen,
};

enum class BoundaryBias {
  kNeutral,
  // A literal English span has just been resolved.  Short Japanese particles
  // and grammatical continuations are therefore much stronger boundary
  // evidence than an unrelated English prefix (commit|ha, network|miru).
  kAfterLiteral,
};

struct ResponsibilitySpan {
  ResponsibilityKind kind = ResponsibilityKind::kOpen;
  std::size_t begin = 0;
  std::size_t end = 0;

  std::string_view View(std::string_view raw) const {
    return raw.substr(begin, end - begin);
  }
};

struct ResponsibilityPlan {
  std::vector<ResponsibilitySpan> stable;
  std::size_t stable_end = 0;
  bool has_open_suffix = false;
};

// Incremental, left-to-right owner selection.
//
// The important asymmetry is the same as Meltype's language-anchor-v4:
// Japanese is the default.  English must be proven by lexical evidence and a
// plausible right boundary.  If the current suffix is still a prefix of a
// possible English token we keep it open instead of prematurely feeding it to
// Mozc.  Once that possibility disappears, Japanese bytes can be released
// immediately, which avoids the old "wait for a whole word" latency.
class ResponsibilityDecoder {
 public:
  ResponsibilityDecoder() = default;

  ResponsibilityPlan Decode(std::string_view raw, bool final,
                            BoundaryBias bias = BoundaryBias::kNeutral) const;

  static const char* KindName(ResponsibilityKind kind);

 private:
  struct Match {
    std::size_t length = 0;
    bool strong = false;
  };

  Match LongestEnglish(std::string_view raw) const;
  std::size_t TechnicalLiteralLength(std::string_view raw, bool final) const;

  bool CouldStartEnglish(std::string_view prefix) const;
  bool IsEnglishExact(std::string_view word) const;
  bool IsStrongEnglish(std::string_view word) const;
  bool IsJapaneseExact(std::string_view word) const;
  bool IsJapanesePrefix(std::string_view word) const;
  bool IsAmbiguousEnglish(std::string_view word) const;

  static bool IsAsciiAlpha(char c);
  static bool IsAsciiDigit(char c);
  static bool IsTechnicalConnector(char c);
  static bool StartsJapaneseBoundary(std::string_view text);
  static std::size_t LeadingJapaneseBoundaryLength(std::string_view text);
  static void AppendSpan(ResponsibilityPlan* plan, ResponsibilityKind kind,
                         std::size_t begin, std::size_t end);
};

}  // namespace mozc::win32::tsf::meltype

#endif  // MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_DECODER_H_
