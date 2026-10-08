// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include "win32/tip/meltype/responsibility_ime_adapter.h"

#include <cctype>
#include <string>
#include <string_view>
#include <vector>

namespace mozc::win32::tsf::meltype {

bool ResponsibilityImeAdapter::Feed(
    ResponsibilityRuntime* runtime, char raw, client::ClientInterface* client,
    const commands::Context& context, commands::Output* base_output,
    commands::Output* display_output) {
  return Apply(runtime->Feed(raw), runtime, client, context, base_output,
               display_output);
}

bool ResponsibilityImeAdapter::Flush(
    ResponsibilityRuntime* runtime, client::ClientInterface* client,
    const commands::Context& context, commands::Output* base_output,
    commands::Output* display_output) {
  return Apply(runtime->Flush(), runtime, client, context, base_output,
               display_output);
}

void ResponsibilityImeAdapter::Render(
    const ResponsibilityRuntime& runtime,
    const commands::Output& base_output,
    commands::Output* display_output) {
  ComposeDisplay(runtime, base_output, "", display_output);
}

bool ResponsibilityImeAdapter::Apply(
    const std::vector<ResponsibilityAction>& actions,
    ResponsibilityRuntime* runtime, client::ClientInterface* client,
    const commands::Context& context, commands::Output* base_output,
    commands::Output* display_output) {
  if (runtime == nullptr || client == nullptr || base_output == nullptr ||
      display_output == nullptr) {
    return false;
  }

  std::string committed;

  for (const ResponsibilityAction& action : actions) {
    if (action.kind == ResponsibilityKind::kLiteral) {
      if (!CommitServerComposition(client, context, base_output, &committed)) {
        return false;
      }
      committed += action.raw;
      continue;
    }

    if (action.kind != ResponsibilityKind::kJapanese) {
      continue;
    }

    for (unsigned char raw : action.raw) {
      commands::KeyEvent key;
      const unsigned char lower =
          static_cast<unsigned char>(std::tolower(raw));
      key.set_key_code(lower);
      key.set_activated(true);
      key.set_mode(commands::HIRAGANA);

      commands::Output server_output;
      if (!client->SendKeyWithContext(key, context, &server_output)) {
        return false;
      }
      CaptureServerOutput(server_output, base_output, &committed);
    }
  }

  ComposeDisplay(*runtime, *base_output, committed, display_output);
  return true;
}

bool ResponsibilityImeAdapter::CommitServerComposition(
    client::ClientInterface* client, const commands::Context& context,
    commands::Output* base_output, std::string* committed) {
  if (!base_output->has_preedit()) return true;

  commands::SessionCommand command;
  command.set_type(commands::SessionCommand::SUBMIT);

  commands::Output server_output;
  if (!client->SendCommandWithContext(command, context, &server_output)) {
    return false;
  }
  CaptureServerOutput(server_output, base_output, committed);
  return true;
}

void ResponsibilityImeAdapter::CaptureServerOutput(
    const commands::Output& server_output, commands::Output* base_output,
    std::string* committed) {
  *base_output = server_output;

  // A result is a one-shot client action. Keep the persistent server snapshot
  // result-free and carry the committed text into this event's display output.
  if (base_output->has_result()) {
    committed->append(base_output->result().value());
    base_output->clear_result();
  }
}

void ResponsibilityImeAdapter::ComposeDisplay(
    const ResponsibilityRuntime& runtime,
    const commands::Output& base_output, const std::string& committed,
    commands::Output* display_output) {
  *display_output = base_output;
  display_output->clear_result();

  if (!committed.empty()) {
    commands::Result* result = display_output->mutable_result();
    result->set_type(commands::Result::STRING);
    result->set_key(committed);
    result->set_value(committed);
  }

  const std::string_view pending = runtime.pending();
  if (!pending.empty()) {
    commands::Preedit* preedit = display_output->mutable_preedit();
    commands::Preedit::Segment* segment = preedit->add_segment();
    segment->set_key(std::string(pending));
    segment->set_value(std::string(pending));
    segment->set_value_length(static_cast<uint32_t>(pending.size()));
    segment->set_annotation(commands::Preedit::Segment::UNDERLINE);

    const uint32_t old_cursor = preedit->has_cursor() ? preedit->cursor() : 0;
    preedit->set_cursor(old_cursor + static_cast<uint32_t>(pending.size()));
    preedit->set_is_toggleable(false);

    // Candidates belong to the pure Mozc preedit and become misleading while
    // a local undecided suffix is displayed after it.
    display_output->clear_candidate_window();
    display_output->clear_all_candidate_words();
    display_output->clear_incognito_candidate_words();
    display_output->clear_removed_candidate_words_for_debug();
  }

  display_output->set_consumed(true);
}

}  // namespace mozc::win32::tsf::meltype
