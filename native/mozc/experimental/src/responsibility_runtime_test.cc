// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_runtime.h"

#include <string>
#include <string_view>
#include <vector>

#include "testing/gunit.h"

namespace mozc::win32::tsf::meltype {
namespace {

void Append(std::vector<ResponsibilityAction>* dst,
            std::vector<ResponsibilityAction> src) {
  for (auto& action : src) {
    if (!dst->empty() && dst->back().kind == action.kind) {
      dst->back().raw += action.raw;
    } else {
      dst->push_back(std::move(action));
    }
  }
}

std::string Stream(std::string_view raw, bool flush = true) {
  ResponsibilityRuntime runtime;
  std::vector<ResponsibilityAction> actions;
  for (char c : raw) Append(&actions, runtime.Feed(c));
  if (flush) Append(&actions, runtime.Flush());

  std::string out;
  for (const auto& action : actions) {
    if (!out.empty()) out += "|";
    out += action.kind == ResponsibilityKind::kLiteral ? "L:" : "J:";
    out += action.raw;
  }
  if (!runtime.pending().empty()) {
    if (!out.empty()) out += "|";
    out += "O:";
    out += runtime.pending();
  }
  return out;
}

TEST(ResponsibilityRuntimeTest, DoesNotFlushCommiPrematurely) {
  EXPECT_EQ(Stream("commi", false), "O:commi");
}

TEST(ResponsibilityRuntimeTest, CommitParticleSplit) {
  EXPECT_EQ(Stream("commitha"), "L:commit|J:ha");
}

TEST(ResponsibilityRuntimeTest, NetworkMiruSplit) {
  EXPECT_EQ(Stream("networkmiru"), "L:network|J:miru");
}

TEST(ResponsibilityRuntimeTest, MixedSequenceCanChangeOwnerRepeatedly) {
  EXPECT_EQ(Stream("githubdeissue"), "L:github|J:de|L:issue");
}

TEST(ResponsibilityRuntimeTest, DigitsAreHardLiteralBoundaries) {
  EXPECT_EQ(Stream("commitha12noyatsu"),
            "L:commit|J:ha|L:12|J:noyatsu");
}

TEST(ResponsibilityRuntimeTest, CommandBoundaryResolvesWholeEnglish) {
  EXPECT_EQ(Stream("node.js"), "L:node.js");
  EXPECT_EQ(Stream("the"), "L:the");
}

TEST(ResponsibilityRuntimeTest, JapaneseFallbackIsLossless) {
  EXPECT_EQ(Stream("de-ta"), "J:de-ta");
  EXPECT_EQ(Stream("nihongowohanasu"), "J:nihongowohanasu");
}

}  // namespace
}  // namespace mozc::win32::tsf::meltype
