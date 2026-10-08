// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#ifndef MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_
#define MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_

#include "protocol/commands.pb.h"
#include "win32/base/keyboard.h"
#include "win32/tip/tip_private_context.h"

namespace mozc::win32::tsf::meltype {

class ResponsibilityTsfRouter {
 public:
  ResponsibilityTsfRouter() = delete;

  // Returns true only for plain ASCII letters/digits that the experiment can
  // safely own without keyboard-layout-specific OEM-key interpretation.
  static bool GetInputCharacter(const VirtualKey& vk,
                                const KeyboardStatus& keyboard_status,
                                char* raw);

  static bool HasPending(const TipPrivateContext& private_context);

  static bool Feed(TipPrivateContext* private_context, char raw,
                   const commands::Context& context,
                   commands::Output* display_output);

  static bool Backspace(TipPrivateContext* private_context,
                        commands::Output* display_output);

  static void Cancel(TipPrivateContext* private_context,
                     commands::Output* display_output);

  static bool Flush(TipPrivateContext* private_context,
                    const commands::Context& context,
                    commands::Output* display_output);
};

}  // namespace mozc::win32::tsf::meltype

#endif  // MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_
