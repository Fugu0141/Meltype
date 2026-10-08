// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_decoder.h"

#include <string>
#include <string_view>

#include "testing/gunit.h"

namespace mozc::win32::tsf::meltype {
namespace {

std::string Signature(const ResponsibilityPlan& plan, std::string_view raw) {
  std::string out;
  for (const ResponsibilitySpan& span : plan.stable) {
    if (!out.empty()) out += "|";
    out += span.kind == ResponsibilityKind::kLiteral ? "L:" : "J:";
    out += span.View(raw);
  }
  if (plan.has_open_suffix) {
    if (!out.empty()) out += "|";
    out += "O:";
    out += raw.substr(plan.stable_end);
  }
  return out;
}

TEST(ResponsibilityDecoderTest, KeepsPotentialEnglishPrefixOpen) {
  ResponsibilityDecoder decoder;
  const ResponsibilityPlan plan = decoder.Decode("commi", false);
  EXPECT_EQ(Signature(plan, "commi"), "O:commi");
}

TEST(ResponsibilityDecoderTest, ResolvesEnglishBeforeJapaneseParticle) {
  ResponsibilityDecoder decoder;
  const ResponsibilityPlan plan = decoder.Decode("commitha", false);
  EXPECT_EQ(Signature(plan, "commitha"), "L:commit|J:ha");
}

TEST(ResponsibilityDecoderTest, ResolvesEnglishBeforeJapaneseVerb) {
  ResponsibilityDecoder decoder;
  const ResponsibilityPlan plan = decoder.Decode("networkmiru", false);
  EXPECT_EQ(Signature(plan, "networkmiru"), "L:network|J:miru");
}

TEST(ResponsibilityDecoderTest, TechnicalLiteralWaitsForRightBoundary) {
  ResponsibilityDecoder decoder;
  EXPECT_EQ(Signature(decoder.Decode("node.", false), "node."), "O:node.");
  EXPECT_EQ(Signature(decoder.Decode("node.js", true), "node.js"),
            "L:node.js");
  EXPECT_EQ(Signature(decoder.Decode("the/then", true), "the/then"),
            "L:the/then");
}

TEST(ResponsibilityDecoderTest, HyphenWithoutEnglishAnchorStaysJapanese) {
  ResponsibilityDecoder decoder;
  const ResponsibilityPlan plan = decoder.Decode("de-ta", true);
  EXPECT_EQ(Signature(plan, "de-ta"), "J:de-ta");
}

TEST(ResponsibilityDecoderTest, MixedTechnicalSentencesKeepAlternatingOwnership) {
  ResponsibilityDecoder decoder;
  EXPECT_EQ(Signature(decoder.Decode("linuxdekernelwobuildsuru", true),
                      "linuxdekernelwobuildsuru"),
            "L:linux|J:de|L:kernel|J:wo|L:build|J:suru");
  EXPECT_EQ(Signature(decoder.Decode("githubdeissue", true),
                      "githubdeissue"),
            "L:github|J:de|L:issue");
  EXPECT_EQ(Signature(decoder.Decode("issuetateta", true), "issuetateta"),
            "L:issue|J:tateta");
  EXPECT_EQ(Signature(decoder.Decode("inviteshimashita", true),
                      "inviteshimashita"),
            "L:invite|J:shimashita");
}

TEST(ResponsibilityDecoderTest, AmbiguousEnglishDefaultsToJapanese) {
  ResponsibilityDecoder decoder;
  for (std::string_view raw :
       {"repo", "sushi", "anime", "same", "tomato", "go", "make", "red"}) {
    EXPECT_EQ(Signature(decoder.Decode(raw, true), raw),
              std::string("J:") + std::string(raw))
        << raw;
  }
}

}  // namespace
}  // namespace mozc::win32::tsf::meltype
