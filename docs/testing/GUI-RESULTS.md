# GUI validation follow-up — 2026-10-05

この追試はユーザーが明示的に許可した `C:\Users\kengo\Downloads\test.txt` の空行とローカルブラウザfixtureを対象とする。初回レポートのGUI未実施記録を、今回実行できた範囲について更新する。

製品コードは変更せず、`Meltype.Tests --gui-validation <directory>` に実際の `MeltypeEngine` / `CompositionService` / Windowsフック / Microsoft IME / 変換ウィンドウを接続した。通常のProgram/Trayを経由せず、config/model/conversion/language/user dictionary/translationの保存先を `artifacts/test-results/gui-session/` に渡す。自動更新・常駐インストール・スタートアップ登録は使用しない。FileLog/LogTypedTextはOFF。Mozc helperは起動せずMicrosoft IMEを使用する。

スクリプトケースはCaptureGateへKeyEventを供給し、その後は本物の変換UIとWindowsへの文字確定経路を使う。`StartsComposition` / OSフックの受信部分は通らないため、物理キーボードE2Eとは区別する。ユーザーによる物理入力は実フックを通す。

| ID | Observation | Result / scope |
|---|---|---|
| GUI-SETUP | テスト専用ホストRelease build、Microsoft IME availability | PASS: build 0 errors / 0 warnings、converter available |
| GUI-VSCODE-FOCUS | 指定test.txtの3行目をクリック。製品FocusInspectorのCanCapture=true | PASS: focus判定のみ。browser専用passive CLIのfixture=false/exit 1は想定どおりで、製品failureではない |
| GUI-GUARD | 最初のJA予約はターゲットに切り替わらずtitle_match=false / CanCapture=false | HARNESS_BLOCKED: 入力先ガードで停止、文字は送っていない |
| GUI-PHYSICAL-JA | ユーザーにkonnichiha → Space → Enterを依頼 | PENDING |

生のセッションイベントは `artifacts/test-results/gui-session/events.jsonl`、受動focus判定は同ディレクトリの `vscode-focus.json` に保存。初回非表示起動は停止し、画面に表示するホストへ切り替えた。起動イベント2件はこの起動方法変更を示し、同時ホスト2件を意味しない。
