// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#ifndef MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_IME_ADAPTER_H_
#define MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_IME_ADAPTER_H_

#include <string>
#include <vector>

#include "client/client_interface.h"
#include "protocol/commands.pb.h"
#include "win32/tip/meltype/responsibility_runtime.h"

namespace mozc::win32::tsf::meltype {

// Connects the pure responsibility runtime to one real Mozc client session.
//
// Only stable Japanese bytes are sent to Mozc. Literal bytes are committed as
// raw text. The unresolved Open suffix is rendered as an additional local
// preedit segment, so input is visible even though ownership is not decided.
class ResponsibilityImeAdapter {
 public:
  ResponsibilityImeAdapter() = delete;

  static bool Feed(ResponsibilityRuntime* runtime, char raw,
                   client::ClientInterface* client,
                   const commands::Context& context,
                   commands::Output* base_output,
                   commands::Output* display_output);

  static bool Flush(ResponsibilityRuntime* runtime,
                    client::ClientInterface* client,
                    const commands::Context& context,
                    commands::Output* base_output,
                    commands::Output* display_output);

  static void Render(const ResponsibilityRuntime& runtime,
                     const commands::Output& base_output,
                     commands::Output* display_output);

 private:
  static bool Apply(const std::vector<ResponsibilityAction>& actions,
                    ResponsibilityRuntime* runtime,
                    client::ClientInterface* client,
                    const commands::Context& context,
                    commands::Output* base_output,
                    commands::Output* display_output);

  static bool CommitServerComposition(client::ClientInterface* client,
                                      const commands::Context& context,
                                      commands::Output* base_output,
                                      std::string* committed);

  static void CaptureServerOutput(const commands::Output& server_output,
                                  commands::Output* base_output,
                                  std::string* committed);

  static void ComposeDisplay(const ResponsibilityRuntime& runtime,
                             const commands::Output& base_output,
                             const std::string& committed,
                             commands::Output* display_output);
};

}  // namespace mozc::win32::tsf::meltype

#endif  // MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_IME_ADAPTER_H_
