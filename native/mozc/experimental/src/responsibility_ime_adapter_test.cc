// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_ime_adapter.h"

#include <string_view>

#include "testing/gunit.h"

namespace mozc::win32::tsf::meltype {
namespace {

TEST(ResponsibilityImeAdapterTest, OpenSuffixIsRenderedAfterMozcPreedit) {
  ResponsibilityRuntime runtime;
  for (char c : std::string_view("commi")) {
    (void)runtime.Feed(c);
  }
  ASSERT_EQ(runtime.pending(), "commi");

  commands::Output base;
  base.set_consumed(true);
  commands::Preedit* preedit = base.mutable_preedit();
  preedit->set_cursor(1);
  commands::Preedit::Segment* segment = preedit->add_segment();
  segment->set_key("ha");
  segment->set_value("は");
  segment->set_value_length(1);
  segment->set_annotation(commands::Preedit::Segment::UNDERLINE);

  commands::Output display;
  ResponsibilityImeAdapter::Render(runtime, base, &display);

  ASSERT_TRUE(display.has_preedit());
  ASSERT_EQ(display.preedit().segment_size(), 2);
  EXPECT_EQ(display.preedit().segment(0).value(), "は");
  EXPECT_EQ(display.preedit().segment(1).value(), "commi");
  EXPECT_EQ(display.preedit().cursor(), 6);
  EXPECT_TRUE(display.consumed());
}

TEST(ResponsibilityImeAdapterTest, EmptyOpenSuffixKeepsPureMozcPreedit) {
  ResponsibilityRuntime runtime;
  commands::Output base;
  base.set_consumed(true);
  commands::Preedit* preedit = base.mutable_preedit();
  preedit->set_cursor(1);
  commands::Preedit::Segment* segment = preedit->add_segment();
  segment->set_key("ha");
  segment->set_value("は");
  segment->set_value_length(1);
  segment->set_annotation(commands::Preedit::Segment::UNDERLINE);

  commands::Output display;
  ResponsibilityImeAdapter::Render(runtime, base, &display);
  ASSERT_TRUE(display.has_preedit());
  EXPECT_EQ(display.preedit().segment_size(), 1);
  EXPECT_EQ(display.preedit().segment(0).value(), "は");
}

}  // namespace
}  // namespace mozc::win32::tsf::meltype
