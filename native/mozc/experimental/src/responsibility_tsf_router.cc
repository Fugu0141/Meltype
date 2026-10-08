// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_tsf_router.h"

#include <windows.h>

#include "win32/tip/meltype/responsibility_ime_adapter.h"
#include "win32/tip/meltype/responsibility_runtime.h"

namespace mozc::win32::tsf::meltype {

bool ResponsibilityTsfRouter::GetInputCharacter(
    const VirtualKey& vk, const KeyboardStatus& keyboard_status, char* raw) {
  if (raw == nullptr) return false;

  if (keyboard_status.IsPressed(VK_CONTROL) ||
      keyboard_status.IsPressed(VK_MENU) ||
      keyboard_status.IsPressed(VK_LWIN) ||
      keyboard_status.IsPressed(VK_RWIN)) {
    return false;
  }

  const wchar_t wide = vk.wide_char();
  if (wide != L'\0') {
    if ((wide >= L'a' && wide <= L'z') ||
        (wide >= L'A' && wide <= L'Z') ||
        (wide >= L'0' && wide <= L'9')) {
      *raw = static_cast<char>(wide);
      return true;
    }
    return false;
  }

  const BYTE key = vk.virtual_key();
  const bool shift = keyboard_status.IsPressed(VK_SHIFT);

  if (key >= 'A' && key <= 'Z') {
    const bool upper = shift != keyboard_status.IsToggled(VK_CAPITAL);
    *raw = static_cast<char>((upper ? 'A' : 'a') + (key - 'A'));
    return true;
  }

  if (!shift && key >= '0' && key <= '9') {
    *raw = static_cast<char>(key);
    return true;
  }

  return false;
}

bool ResponsibilityTsfRouter::HasPending(
    const TipPrivateContext& private_context) {
  const ResponsibilityRuntime* runtime =
      private_context.GetResponsibilityRuntime();
  return runtime != nullptr && !runtime->pending().empty();
}

bool ResponsibilityTsfRouter::Feed(
    TipPrivateContext* private_context, char raw,
    const commands::Context& context, commands::Output* display_output) {
  if (private_context == nullptr || display_output == nullptr) return false;
  ResponsibilityRuntime* runtime = private_context->GetResponsibilityRuntime();
  if (runtime == nullptr) return false;

  return ResponsibilityImeAdapter::Feed(
      runtime, raw, private_context->GetClient(), context,
      private_context->mutable_responsibility_base_output(), display_output);
}

bool ResponsibilityTsfRouter::Backspace(
    TipPrivateContext* private_context, commands::Output* display_output) {
  if (private_context == nullptr || display_output == nullptr) return false;
  ResponsibilityRuntime* runtime = private_context->GetResponsibilityRuntime();
  if (runtime == nullptr || !runtime->BackspacePending()) return false;

  ResponsibilityImeAdapter::Render(
      *runtime, private_context->responsibility_base_output(), display_output);
  return true;
}

void ResponsibilityTsfRouter::Cancel(
    TipPrivateContext* private_context, commands::Output* display_output) {
  if (private_context == nullptr || display_output == nullptr) return;
  ResponsibilityRuntime* runtime = private_context->GetResponsibilityRuntime();
  if (runtime == nullptr) return;

  runtime->Reset();
  ResponsibilityImeAdapter::Render(
      *runtime, private_context->responsibility_base_output(), display_output);
}

bool ResponsibilityTsfRouter::Flush(
    TipPrivateContext* private_context, const commands::Context& context,
    commands::Output* display_output) {
  if (private_context == nullptr || display_output == nullptr) return false;
  ResponsibilityRuntime* runtime = private_context->GetResponsibilityRuntime();
  if (runtime == nullptr) return false;

  return ResponsibilityImeAdapter::Flush(
      runtime, private_context->GetClient(), context,
      private_context->mutable_responsibility_base_output(), display_output);
}

}  // namespace mozc::win32::tsf::meltype
