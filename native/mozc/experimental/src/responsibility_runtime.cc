// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_runtime.h"

#include <string_view>
#include <utility>
#include <vector>

namespace mozc::win32::tsf::meltype {

std::vector<ResponsibilityAction> ResponsibilityRuntime::Feed(char raw) {
  pending_.push_back(raw);
  return Drain(false);
}

std::vector<ResponsibilityAction> ResponsibilityRuntime::Flush() {
  return Drain(true);
}

void ResponsibilityRuntime::Reset() {
  pending_.clear();
  bias_ = BoundaryBias::kNeutral;
}

std::vector<ResponsibilityAction> ResponsibilityRuntime::Drain(bool final) {
  std::vector<ResponsibilityAction> actions;

  // Draining can expose a new boundary at the beginning of the remaining
  // suffix, so keep decoding until no additional prefix becomes stable.
  while (!pending_.empty()) {
    const ResponsibilityPlan plan = decoder_.Decode(pending_, final, bias_);
    if (plan.stable_end == 0) break;

    for (const ResponsibilitySpan& span : plan.stable) {
      const std::string_view value =
          std::string_view(pending_).substr(span.begin, span.end - span.begin);
      AppendAction(&actions, span.kind, value);
      bias_ = span.kind == ResponsibilityKind::kLiteral
                  ? BoundaryBias::kAfterLiteral
                  : BoundaryBias::kNeutral;
    }

    pending_.erase(0, plan.stable_end);
    if (!final && plan.has_open_suffix) break;
  }

  // final=true is a command boundary.  The decoder is deliberately
  // conservative, so any residue that still cannot prove English belongs to
  // Japanese/Mozc rather than being silently lost.
  if (final && !pending_.empty()) {
    AppendAction(&actions, ResponsibilityKind::kJapanese, pending_);
    pending_.clear();
    bias_ = BoundaryBias::kNeutral;
  }

  return actions;
}

void ResponsibilityRuntime::AppendAction(
    std::vector<ResponsibilityAction>* actions, ResponsibilityKind kind,
    std::string_view raw) {
  if (raw.empty()) return;
  if (!actions->empty() && actions->back().kind == kind) {
    actions->back().raw.append(raw);
    return;
  }
  actions->push_back({kind, std::string(raw)});
}

}  // namespace mozc::win32::tsf::meltype
