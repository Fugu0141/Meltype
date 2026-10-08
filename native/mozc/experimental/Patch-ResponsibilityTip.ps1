# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro
param(
    [Parameter(Mandatory = $true)]
    [string]$MozcSrc
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Replace-Once([string]$Path, [string]$Needle, [string]$Replacement, [string]$AlreadyPresent) {
    $content = Get-Content -Raw -LiteralPath $Path
    if ($AlreadyPresent -and $content.Contains($AlreadyPresent)) { return }
    if (-not $content.Contains($Needle)) {
        throw ('Patch anchor not found: ' + $Path + [Environment]::NewLine + $Needle)
    }
    Write-Utf8NoBom -Path $Path -Text $content.Replace($Needle, $Replacement)
}

$privateHeader = Join-Path $MozcSrc 'win32\tip\tip_private_context.h'

$needle = @'
namespace tsf {

class TipUiElementManager;
'@
$replacement = @'
namespace tsf {

namespace meltype {
class ResponsibilityRuntime;
}  // namespace meltype

class TipUiElementManager;
'@
Replace-Once -Path $privateHeader -Needle $needle -Replacement $replacement -AlreadyPresent 'class ResponsibilityRuntime;'

$needle = @'
  const commands::Output& responsibility_base_output() const;
  commands::Output* mutable_responsibility_base_output();
  const VirtualKey& last_down_key() const;
'@
$replacement = @'
  const commands::Output& responsibility_base_output() const;
  commands::Output* mutable_responsibility_base_output();
  meltype::ResponsibilityRuntime* GetResponsibilityRuntime();
  const VirtualKey& last_down_key() const;
'@
Replace-Once -Path $privateHeader -Needle $needle -Replacement $replacement -AlreadyPresent 'GetResponsibilityRuntime();'

$privateImpl = Join-Path $MozcSrc 'win32\tip\tip_private_context.cc'

$needle = @'
#include "win32/tip/tip_private_context.h"

#include <msctf.h>
'@
$replacement = @'
#include "win32/tip/tip_private_context.h"

#include <msctf.h>
'@
# Keep the normal include position intact and add the experiment include next
# to the other TIP includes below.
$content = Get-Content -Raw -LiteralPath $privateImpl
if (-not $content.Contains('#include "win32/tip/meltype/responsibility_runtime.h"')) {
    $anchor = '#include "win32/tip/tip_text_service.h"'
    if (-not $content.Contains($anchor)) { throw "Patch anchor not found: $anchor" }
    $content = $content.Replace(
        $anchor,
        '#include "win32/tip/meltype/responsibility_runtime.h"' + [Environment]::NewLine + $anchor
    )
    Write-Utf8NoBom -Path $privateImpl -Text $content
}

$needle = @'
  InternalState() : client_(ClientFactory::NewClient()) {}
  std::unique_ptr<client::ClientInterface> client_;
'@
$replacement = @'
  InternalState()
      : client_(ClientFactory::NewClient()),
        responsibility_runtime_(
            std::make_unique<meltype::ResponsibilityRuntime>()) {}
  std::unique_ptr<client::ClientInterface> client_;
  std::unique_ptr<meltype::ResponsibilityRuntime> responsibility_runtime_;
'@
Replace-Once -Path $privateImpl -Needle $needle -Replacement $replacement -AlreadyPresent 'responsibility_runtime_('

$needle = @'
VKBackBasedDeleter* TipPrivateContext::GetDeleter() {
  return &state_->deleter_;
}

const Output& TipPrivateContext::last_output() const {
'@
$replacement = @'
VKBackBasedDeleter* TipPrivateContext::GetDeleter() {
  return &state_->deleter_;
}

meltype::ResponsibilityRuntime* TipPrivateContext::GetResponsibilityRuntime() {
  return state_->responsibility_runtime_.get();
}

const Output& TipPrivateContext::last_output() const {
'@
Replace-Once -Path $privateImpl -Needle $needle -Replacement $replacement -AlreadyPresent 'TipPrivateContext::GetResponsibilityRuntime()'

$build = Join-Path $MozcSrc 'win32\tip\BUILD.bazel'

$needle = @'
mozc_cc_library(
    name = "tip_keyevent_handler",
    srcs = ["tip_keyevent_handler.cc"],
    hdrs = ["tip_keyevent_handler.h"],
    tags = MOZC_TAGS.WIN_ONLY,
    target_compatible_with = ["@platforms//os:windows"],
    deps = [
        ":tip_edit_session",
        ":tip_input_mode_manager",
        ":tip_private_context_h",
'@
$replacement = @'
mozc_cc_library(
    name = "tip_keyevent_handler",
    srcs = ["tip_keyevent_handler.cc"],
    hdrs = ["tip_keyevent_handler.h"],
    tags = MOZC_TAGS.WIN_ONLY,
    target_compatible_with = ["@platforms//os:windows"],
    deps = [
        ":tip_edit_session",
        ":tip_input_mode_manager",
        ":tip_private_context_h",
        "//win32/tip/meltype:responsibility_tsf_router",
'@
Replace-Once -Path $build -Needle $needle -Replacement $replacement -AlreadyPresent '//win32/tip/meltype:responsibility_tsf_router'

$needle = @'
mozc_cc_library(
    name = "tip_private_context",
    srcs = ["tip_private_context.cc"],
    tags = MOZC_TAGS.WIN_ONLY,
    target_compatible_with = ["@platforms//os:windows"],
    deps = [
        ":tip_private_context_h",
        ":tip_text_service",
        ":tip_ui_element_manager",
        "//client",
'@
$replacement = @'
mozc_cc_library(
    name = "tip_private_context",
    srcs = ["tip_private_context.cc"],
    tags = MOZC_TAGS.WIN_ONLY,
    target_compatible_with = ["@platforms//os:windows"],
    deps = [
        ":tip_private_context_h",
        ":tip_text_service",
        ":tip_ui_element_manager",
        "//client",
        "//win32/tip/meltype:responsibility_runtime",
'@
Replace-Once -Path $build -Needle $needle -Replacement $replacement -AlreadyPresent '//win32/tip/meltype:responsibility_runtime'

$keyHandler = Join-Path $MozcSrc 'win32\tip\tip_keyevent_handler.cc'

$content = Get-Content -Raw -LiteralPath $keyHandler
if (-not $content.Contains('#include "win32/tip/meltype/responsibility_tsf_router.h"')) {
    $anchor = '#include "win32/tip/tip_input_mode_manager.h"'
    if (-not $content.Contains($anchor)) { throw "Patch anchor not found: $anchor" }
    $content = $content.Replace(
        $anchor,
        '#include "win32/tip/meltype/responsibility_tsf_router.h"' + [Environment]::NewLine + $anchor
    )
    Write-Utf8NoBom -Path $keyHandler -Text $content
}

$needle = @'
  }

  // Make an immutable snapshot of |private_context->ime_behavior_|, which
'@
$replacement = @'
  }

  // Meltype responsibility split (OnTestKey). Existing deletion/surrogate
  // guards above always get the first chance to consume the event.
  const bool responsibility_enabled =
      open &&
      text_service->GetThreadContext()
              ->GetInputModeManager()
              ->GetEffectiveConversionMode() == commands::HIRAGANA;
  char responsibility_raw = '\0';
  const bool responsibility_character =
      responsibility_enabled &&
      meltype::ResponsibilityTsfRouter::GetInputCharacter(
          vk, keyboard_status, &responsibility_raw);
  if (responsibility_enabled &&
      (responsibility_character ||
       (is_key_down &&
        meltype::ResponsibilityTsfRouter::HasPending(
            private_context->GetResponsibilityRuntime())))) {
    *eaten = TRUE;
    return S_OK;
  }

  // Make an immutable snapshot of |private_context->ime_behavior_|, which
'@
Replace-Once -Path $keyHandler -Needle $needle -Replacement $replacement -AlreadyPresent 'Meltype responsibility split (OnTestKey).'

$needle = @'
    if (ignore_this_keyevent) {
      *eaten = TRUE;
      return S_OK;
    }
  }

  commands::Output temporal_output;
'@
$replacement = @'
    if (ignore_this_keyevent) {
      *eaten = TRUE;
      return S_OK;
    }
  }

  // Meltype responsibility split (OnKey). Existing deletion/surrogate guards
  // above always run first.
  const bool responsibility_enabled =
      open && !use_pending_output &&
      text_service->GetThreadContext()
              ->GetInputModeManager()
              ->GetEffectiveConversionMode() == commands::HIRAGANA;
  meltype::ResponsibilityRuntime* responsibility_runtime =
      private_context->GetResponsibilityRuntime();
  char responsibility_raw = '\0';
  const bool responsibility_character =
      responsibility_enabled &&
      meltype::ResponsibilityTsfRouter::GetInputCharacter(
          vk, keyboard_status, &responsibility_raw);

  if (responsibility_character) {
    *eaten = TRUE;
    if (!is_key_down) {
      return S_OK;
    }

    Context mozc_context;
    FillMozcContextForOnKey(text_service, context, &mozc_context);
    commands::Output responsibility_output;
    if (!meltype::ResponsibilityTsfRouter::Feed(
            responsibility_runtime, responsibility_raw,
            private_context->GetClient(), mozc_context,
            private_context->mutable_responsibility_base_output(),
            &responsibility_output)) {
      return E_FAIL;
    }
    if (!TipEditSession::OnOutputReceivedSync(
            text_service, context, responsibility_output)) {
      return E_FAIL;
    }
    return S_OK;
  }

  if (responsibility_enabled && is_key_down &&
      meltype::ResponsibilityTsfRouter::HasPending(
            private_context->GetResponsibilityRuntime())) {
    commands::Output responsibility_output;

    if (vk.virtual_key() == VK_BACK) {
      *eaten = TRUE;
      if (!meltype::ResponsibilityTsfRouter::Backspace(
              responsibility_runtime,
              private_context->responsibility_base_output(),
              &responsibility_output)) {
        return E_FAIL;
      }
      if (!TipEditSession::OnOutputReceivedSync(
              text_service, context, responsibility_output)) {
        return E_FAIL;
      }
      return S_OK;
    }

    if (vk.virtual_key() == VK_ESCAPE) {
      *eaten = TRUE;
      meltype::ResponsibilityTsfRouter::Cancel(
          responsibility_runtime,
          private_context->responsibility_base_output(),
          &responsibility_output);
      if (!TipEditSession::OnOutputReceivedSync(
              text_service, context, responsibility_output)) {
        return E_FAIL;
      }
      return S_OK;
    }

    // Resolve the local Open suffix first, then continue processing the
    // current boundary key through Mozc in this same OnKey call.
    Context mozc_context;
    FillMozcContextForOnKey(text_service, context, &mozc_context);
    if (!meltype::ResponsibilityTsfRouter::Flush(
            responsibility_runtime, private_context->GetClient(),
            mozc_context,
            private_context->mutable_responsibility_base_output(),
            &responsibility_output)) {
      return E_FAIL;
    }
    if (!TipEditSession::OnOutputReceivedSync(
            text_service, context, responsibility_output)) {
      return E_FAIL;
    }
  }

  commands::Output temporal_output;
'@
Replace-Once -Path $keyHandler -Needle $needle -Replacement $replacement -AlreadyPresent 'Meltype responsibility split (OnKey).'

Write-Host 'Applied Meltype responsibility TSF patch.'
