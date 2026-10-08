// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#ifndef MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_
#define MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_

#include "client/client_interface.h"
#include "protocol/commands.pb.h"
#include "win32/base/keyboard.h"
#include "win32/tip/meltype/responsibility_runtime.h"

namespace mozc::win32::tsf::meltype {

// Thin TSF-facing facade. It intentionally does not depend on
// TipPrivateContext because that target is private to //win32/tip.
class ResponsibilityTsfRouter {
 public:
  ResponsibilityTsfRouter() = delete;

  static bool GetInputCharacter(const VirtualKey& vk,
                                const KeyboardStatus& keyboard_status,
                                char* raw);

  static bool HasPending(const ResponsibilityRuntime* runtime);

  static bool Feed(ResponsibilityRuntime* runtime, char raw,
                   client::ClientInterface* client,
                   const commands::Context& context,
                   commands::Output* base_output,
                   commands::Output* display_output);

  static bool Backspace(ResponsibilityRuntime* runtime,
                        const commands::Output& base_output,
                        commands::Output* display_output);

  static void Cancel(ResponsibilityRuntime* runtime,
                     const commands::Output& base_output,
                     commands::Output* display_output);

  static bool Flush(ResponsibilityRuntime* runtime,
                    client::ClientInterface* client,
                    const commands::Context& context,
                    commands::Output* base_output,
                    commands::Output* display_output);
};

}  // namespace mozc::win32::tsf::meltype

#endif  // MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_TSF_ROUTER_H_
