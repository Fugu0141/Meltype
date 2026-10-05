# 実アプリ・OS検証の残項目

2026-10-05。以下は未実施。空欄をPASSと扱わない。

## テスト環境

ビルド/自己診断は成功済み。Microsoft IME / Windows候補 / UIA / SpellCheckerは確認済み。
常駐インストールはしていない。実ユーザー保存先を変更しない計画のため、実入力検証は別WindowsユーザーまたはVMで行う。
`Install-Meltype.ps1` は旧プロセス停止とスタートアップ登録を行うので、この普段使いの環境で無条件に実行しない。

computer-useは「current browser URL on Windows with enough confidence to enforce policy」を確認できず、入力操作前に停止した。
さらに自動SendInputは製品の `StartsComposition` で `Injected` として無視される。自動typingの成功だけで実フックの成功を記録しない。

## 実入力ケース

各ケースについて入力全文（合成文字だけ）、前後文脈、実際の出力、focus先、ログ、version、OS、DPI、配列、頻度を記録する。

| App | JA | EN | Mixed | Focus switch | Fast typing | Status |
|---|---|---|---|---|---|---|
| Notepad | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 物理入力が必要 |
| Edge/Chrome | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | browser URL検証停止 |
| Firefox | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 標準Firefoxなし、Tor代用せず |
| VS Code/Cursor | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 実エディター |
| Visual Studio | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 実エディター |
| Windows Terminal | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 手動検証 |
| PowerShell/cmd | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 手動検証 |
| Discord/Slack | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 送信せずローカルdraft |
| Word/Excel/PowerPoint | 未実施 | 未実施 | 未実施 | 未実施 | 未実施 | 空のtest document |

- [ ] `konnnichiha` → Enter、`hello world`、`kyouhagoogledekensaku` → Enter/Space
- [ ] composition中Alt+Tab、別inputクリック、別appクリック、window/app close、desktop/monitor移動
- [ ] 新しいtargetへ文字が流れない。元target復帰後にstale stateが残らない
- [ ] 高速Backspace、Space/Enter連打、key repeat、Ctrl/Alt/Shift同時押し
- [ ] code/comment/string/doc comment/shell/AI prompt。C/C++/C#/Python/Java/JS/TS/Rust/Go/HTML/CSS/SQL/Shell/PowerShell/Markdown/JSON/YAML
- [ ] JIS/US配列、100/125/150/175/200% DPI、multi-monitor、negative coordinates、taskbar位置、dark/light
- [ ] renderedフォーム画像で候補paging、長い語、下端control、UIA label

## Password/privacy

外部ログインフォームや実パスワードを使わない。ローカル `artifacts/test-results/e2e-fixture.html` を使用。
準備済みのtest-only readerは `Meltype.Tests --validate-focus <output.json> password`（通常欄なら末尾 `normal`）。fixtureのaria-label prefix以外では値を保存しない。

- [ ] Win32/WPF/WinUI/WebView/Chromiumのpassword focusで `IsPassword=true` / `CanCapture=false`
- [ ] 合成canaryを物理入力。compositionが現れず、学習/通常log/保存fileに残らない
- [ ] 通常欄→password、password→通常欄、更新待ちfocusの競合
- [ ] FileLog OFF / ON × RecordText OFF / ONの4通り。設定OFFでも学習logに全文が出る現象は今回再現済み
- [ ] 開発ログ/Issue添付前に実データ不在を確認

## Network/update

- [ ] Windows VMでMeltype PIDと子update PowerShell PIDを区別してETW/packet capture
- [ ] 無入力 / 通常入力 / 異なる合成入力を比較。connection/destination/timing/bytesを記録
- [ ] 起動2分後/6時間ごとの更新チェックを分離
- [ ] install/update/restart回帰。通常入力内容の外部送信なし

## 追加の残項目

- [ ] learningへのACL拒否/rename失敗、保存途中kill、同時プロセスsave、失敗後restartで元data保持
- [ ] dictionary 10万〜100万entry load、prefix/miss/normalization、huge line
- [ ] detector/composition/persistenceの各専用benchmark
- [ ] Linux/macOS上で同一seed/corpus、OS spellchecker無効時の結果比較
- [ ] parallel hook/worker/timer競合fuzz、長時間soak、GC後handleの持続増加
