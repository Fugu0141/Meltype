// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

#include <iostream>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

#include "base/init_mozc.h"
#include "base/system_util.h"
#include "client/client.h"
#include "client/client_interface.h"
#include "protocol/commands.pb.h"
#include "win32/tip/meltype/responsibility_runtime.h"

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

namespace mozc::win32::tsf::meltype {

std::string PreeditText(const commands::Output& output) {
  std::string text;
  if (!output.has_preedit()) return text;
  for (const auto& segment : output.preedit().segment()) {
    text += segment.value();
  }
  return text;
}

class Bridge {
 public:
  explicit Bridge(std::unique_ptr<client::ClientInterface> client)
      : client_(std::move(client)) {}

  std::string Run(std::string_view raw, std::string* signature) {
    runtime_.Reset();
    rendered_.clear();
    signature->clear();
    latest_preedit_.clear();
    mozc_active_ = false;

    for (char c : raw) {
      Apply(runtime_.Feed(c), signature);
    }
    Apply(runtime_.Flush(), signature);
    CommitMozc();
    return rendered_;
  }

 private:
  void Apply(const std::vector<ResponsibilityAction>& actions,
             std::string* signature) {
    for (const ResponsibilityAction& action : actions) {
      if (!signature->empty()) *signature += "|";
      *signature += action.kind == ResponsibilityKind::kLiteral ? "L:" : "J:";
      *signature += action.raw;

      if (action.kind == ResponsibilityKind::kLiteral) {
        CommitMozc();
        rendered_ += action.raw;
        continue;
      }

      for (unsigned char c : action.raw) {
        commands::KeyEvent key;
        key.set_key_code(c);
        key.set_activated(true);
        key.set_mode(commands::HIRAGANA);

        commands::Output output;
        if (!client_->SendKey(key, &output)) {
          rendered_ += action.raw;
          latest_preedit_.clear();
          mozc_active_ = false;
          break;
        }
        latest_preedit_ = PreeditText(output);
        mozc_active_ = output.has_preedit();
      }
    }
  }

  void CommitMozc() {
    if (!mozc_active_) return;

    commands::SessionCommand command;
    command.set_type(commands::SessionCommand::SUBMIT);
    commands::Output output;
    if (client_->SendCommand(command, &output)) {
      if (output.has_result()) {
        rendered_ += output.result().value();
      } else {
        rendered_ += latest_preedit_;
      }
    } else {
      rendered_ += latest_preedit_;
    }

    latest_preedit_.clear();
    mozc_active_ = false;
  }

  ResponsibilityRuntime runtime_;
  std::unique_ptr<client::ClientInterface> client_;
  std::string rendered_;
  std::string latest_preedit_;
  bool mozc_active_ = false;
};

}  // namespace mozc::win32::tsf::meltype

int main(int argc, char** argv) {
  mozc::InitMozc(argv[0], &argc, &argv);
#ifdef _WIN32
  _setmode(_fileno(stdin), _O_BINARY);
  _setmode(_fileno(stdout), _O_BINARY);
#endif

  if (argc >= 2 && argv[1][0] != '\0') {
    mozc::SystemUtil::SetUserProfileDirectory(argv[1]);
  }

  auto client = mozc::client::ClientFactory::NewClient();
  if (!client) {
    std::cerr << "failed to create Mozc client\n";
    return 1;
  }

  mozc::win32::tsf::meltype::Bridge bridge(std::move(client));
  std::string line;
  while (std::getline(std::cin, line)) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    std::string signature;
    const std::string rendered = bridge.Run(line, &signature);
    std::cout << line << "\t" << signature << "\t" << rendered << "\n";
    std::cout.flush();
  }
  return 0;
}
