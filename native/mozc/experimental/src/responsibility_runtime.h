// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#ifndef MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_RUNTIME_H_
#define MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_RUNTIME_H_

#include <string>
#include <string_view>
#include <vector>

#include "win32/tip/meltype/responsibility_decoder.h"

namespace mozc::win32::tsf::meltype {

struct ResponsibilityAction {
  ResponsibilityKind kind = ResponsibilityKind::kOpen;
  std::string raw;
};

// Stateful front-end used by the TSF experiment.
//
// It intentionally does not talk to the Mozc client itself.  The host can map
// kJapanese actions to Mozc SendKey calls and kLiteral actions to direct TSF
// result text.  Keeping the policy pure makes the difficult ownership rules
// testable without a Windows text service.
class ResponsibilityRuntime {
 public:
  ResponsibilityRuntime() = default;

  // Adds one raw ASCII byte and returns spans whose ownership became stable.
  std::vector<ResponsibilityAction> Feed(char raw);

  // Resolves the remaining open suffix at a command boundary (Space, Enter,
  // focus change, etc.).
  std::vector<ResponsibilityAction> Flush();

  bool BackspacePending();
  void Reset();

  std::string_view pending() const { return pending_; }
  BoundaryBias bias() const { return bias_; }

 private:
  std::vector<ResponsibilityAction> Drain(bool final);
  static void AppendAction(std::vector<ResponsibilityAction>* actions,
                           ResponsibilityKind kind, std::string_view raw);

  ResponsibilityDecoder decoder_;
  std::string pending_;
  BoundaryBias bias_ = BoundaryBias::kNeutral;
};

}  // namespace mozc::win32::tsf::meltype

#endif  // MOZC_WIN32_TIP_MELTYPE_RESPONSIBILITY_RUNTIME_H_
