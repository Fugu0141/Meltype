// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_tsf_router.h"

#include <windows.h>

#include "testing/gunit.h"

namespace mozc::win32::tsf::meltype {
namespace {

TEST(ResponsibilityTsfRouterTest, PlainLettersPreserveCase) {
  KeyboardStatus status;
  char raw = 0;
  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey('A'), status, &raw));
  EXPECT_EQ(raw, 'a');

  status.SetState(VK_SHIFT, 0x80);
  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey('A'), status, &raw));
  EXPECT_EQ(raw, 'A');
}

TEST(ResponsibilityTsfRouterTest, ControlShortcutIsNotCaptured) {
  KeyboardStatus status;
  status.SetState(VK_CONTROL, 0x80);
  char raw = 0;
  EXPECT_FALSE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey('C'), status, &raw));
}

TEST(ResponsibilityTsfRouterTest, DigitsAndCommonTechnicalConnectors) {
  KeyboardStatus status;
  char raw = 0;

  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey('7'), status, &raw));
  EXPECT_EQ(raw, '7');

  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey(VK_OEM_PERIOD), status, &raw));
  EXPECT_EQ(raw, '.');

  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey(VK_OEM_2), status, &raw));
  EXPECT_EQ(raw, '/');

  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey(VK_OEM_MINUS), status, &raw));
  EXPECT_EQ(raw, '-');

  status.SetState(VK_SHIFT, 0x80);
  EXPECT_TRUE(ResponsibilityTsfRouter::GetInputCharacter(
      VirtualKey::FromVirtualKey(VK_OEM_MINUS), status, &raw));
  EXPECT_EQ(raw, '_');
}

}  // namespace
}  // namespace mozc::win32::tsf::meltype
