# #675 + #707 統合安定化チェックリスト

> 対象: `Fugu0141/Meltype` の `next/v1.2.2-pre.2`。出発点は upstream/main を取り込んだ `ca88a9a`。
>
> **これは完了報告ではありません。** 静的調査で確認した競合を先に直し、原因未確定のものを観測可能な状態で残します。
> 実機の未実施項目がある間は upstream への提出・Merge を行わないでください。

## 守るべき契約

- #675: クリック直後に新しい入力欄を再検査する処理、ブラウザーの自動入力候補の認識、初文字保護を残す。
- #707: 起動中の Microsoft IME 引き継ぎ、アプリ別 OFF、TSF Alt 単押し、WinExe 起動、DPI・配置、TIP 回復、IME 登録チェック、インストーラーの PATH 耐性を残す。
- ゲームなど非入力欄ではフック内の長時間待機を増やさない。パスワード欄や別アプリへ文字が漏れないようにする。
- WinExe を Exe に戻さない。UI 起動時のコンソール点滅を再導入しない。
- 問題の修正に無関係な全体リファクタリング、既存機能の削除、未測定の sleep 増加をしない。

## Issue別の調査・実装順

| Issue | 優先 | 現状と次の検証 |
|---|---|---|
| #1 UIA旧世代の上書き | P0 | **修正案・回帰テスト追加**。Invalidate/ApplyFocus の短いロック、公開結果の世代・HWNDタグ、旧世代イベントの抑制。遅延UIA実機試験は未実施 |
| #3 SendInputの文字順序 | P0 | **未解決**。Windows 11 のメモ帳で Compose の表示あり/なし、CommitText/ReplaceBackward/SendInput を A/B 比較し、原因を切り分けてから修正 |
| #5 非同期Alt操作 | P1 | **修正案・回帰テスト追加**。操作時の HWND/PID/フォーカス HWND/ThreadId を保存し、実行直前に再照合。Windowsアプリ内のWebフォーカスは未保証 |
| #6 非同期IME自動OFF | P1 | **修正案・回帰テスト追加**。キュー投入時の HWND/PID/フォーカス HWND/ThreadId・設定を固定。IME 状態取得後にも照合し、引き継ぎ保留中は触れない |
| #2 入力欄→ボタンの誤横取り | P1 | **部分対策・未解決**。クリックで失効した古いフォーカス状態をタイムアウト後に再利用しない。UIA自体がクリック前の要素を報告する場合は残るため、0/10/30msで実測が必要 |
| #4 クリック直後の初文字欠損 | P1 | **未解決**。#1/#2 の修正後 `--focus-e2e` を各遅延 100 回以上実行。失敗の UIA 世代、HWND とログを保存 |
| #7 Keyboard/TSF除外条件 | P1 | **設計判断待ち**。TSF の DLL から来る process 名のみではフォアグラウンド/権限/全画面状態は再現不能。TIP 通信プロトコルで安全に取得できる条件を整理後、両モードの OFF 契約を揃える |
| #8 WinExe CLI診断 | P1 | **未解決**。`--version`, `--selftest`, `--judge-words`, `--exit` の stdout/stderr/exit-code を cmd, PS5.1, PS7, リダイレクトで測定。安易に Console を割り当てて WinExe 起動点滅を再導入しない |
| #9 Microsoft IME引き継ぎ | P1 | **未解決**。Esc は候補取消であり未確定文字列の終了とは限らない。IME off/Enter/Tab/クリックを実測して解除条件を定義 |
| #10 結合試験 | P1 | **準備中**。下の matrix に実測結果を記録し、全て通ってから完了 |
| #11 TIPタイムアウト | P2 | **未解決**。1回・連続・復旧について 200/600/1200ms 遅延注入。再送重複とバックオフを計測 |
| #12 DPIと配置 | P2 | **未解決**。100/150/200% の物理モニター間の移動、Windows検索とスタートメニューで実測 |

## Windows 実機テスト

**テストを実行していない項目を PASS にしない。**

| ケース | 手順・確認内容 | 結果 |
|---|---|---|
| コア | `dotnet run --project src/Meltype.Core.Tests` | 未実施 |
| Windows ユニット | `dotnet run --project src/Meltype.Tests` | 未実施 |
| クリック・初文字 | `dotnet run --project src/Meltype.Tests -- --focus-e2e` (引数の受付は TestRunner で確認) | 未実施 |
| Alt 操作 | 左右単押し、Alt+Tab、Alt+クリック、A→B直後に Alt を離す | 未実施 |
| IME OFF | ON の A→アプリ別 OFF の B へ高速移動、BのIME状態を確認 | 未実施 |
| IME 引き継ぎ | Microsoft IME で未確定のまま起動、Esc/Tab/Enter/クリック | 未実施 |
| TSF除外 | 通常・ゲーム・全画面・アプリ別OFFで TSF/Keyboard を対比 | 未実施 |
| 文字順序 | メモ帳/ブラウザーで連続確定、変換窓表示/非表示 | 未実施 |
| CLI | cmd/Windows PowerShell/PS7でstdout・stderr・exit codeを採取 | 未実施 |
| TIP通信 | 200/600/1200msのUIスレッド遅延で連続打鍵・再接続 | 未実施 |
| DPI | 100/150/200%モニターで変換窓・Windows検索を移動 | 未実施 |
| パッケージ | `install.cmd`/`uninstall.cmd`、TIP登録/解除、PATHからPowerShell除外 | 未実施 |

## 判定ルール

1. ビルド失敗・Windowsテスト失敗があればその時点で修正を止めて調査する。
2. #1/#5/#6 はこの PR では根本的な外部 Win32 呼び出しの原子性までは保証しない。照合と状態管理の追加が狙い。
3. #3、#4、#8、#9、#11、#12 は **Windows 実測が必要**。問題が未再現のままなら証跡を記載し、解決済みと扱わない。
4. upstream/main の取り込み後は #675/#707 の主要な動作を両入力方式で再測定する。
5. 未解決の P0/P1 を残したまま「12件全て修正済み」と表現したり上流提出を進めたりしない。

## PR #13 追加の反例・静的検証 (2026-10-18)

この節の **静的/モデル検証をビルド成功やWindows実行成功と混同しない**。

### 追加した回帰テスト (実行待ち)
- `FocusInspector_StaleGenerationDoesNotPublish`: 旧世代と未来世代の調査結果が現在の情報とイベントを汚さない。
- `FocusInspector_UnresolvedReuseRejectsCounterexamples`: クリック直後、別ウィンドウ、別ネイティブ HWND、パスワード欄を検査。
- `FocusInspector_PendingReuseBooleanMatrix`: 5条件の全32通りを網羅。
- `AsyncIme_RejectsDifferentWindowProcessAndFocus`: 同一ウィンドウの別入力欄・別ThreadIdも拒否。
- `AsyncIme_TargetMatchingBooleanMatrix`: 7条件の全128通りを網羅。
- 修正したフォーカスの再利用判定が、UIA結果公開や新しいクリックと競合しても古い状態を許可しないよう公開結果のidentity/世代を再確認する。

純粋な論理式の **独立したJavaScript参照モデル** では32+128=160通りの期待値との一致を確認。これは C# コードのコンパイル・実行ではない。

### 追加で検討した反例 (既存 Issue を閉じない理由)
| 反例 | 影響・残課題 |
|---|---|
| A→Bへのクリック後にUIAが古いAを *新しい世代* として返す | 世代チェックだけではBを識別できず、#2は未解決。クリックの有無だけで既知の正常入力も止める設計は #675 を後退させる |
| Webアプリで Tab により入力欄→パスワード欄 (同じネイティブ HWND) に移る | UIA未完了中の古いテキスト入力判定の再利用がまだ危険。特にイベント理由のない状態変化を Win32 の HWND のみで完全には検知できない |
| 一つの Electron ウィンドウ内でWeb入力欄だけが変わる | HWND/PID/ThreadId/フォーカスHWNDが同一なので、#5/#6 の対策だけでは区別できない。対象の厳密一致はOSのネイティブ窓まで |
| フォーカスが変化した直後、最後の照合から IMM32 呼び出しまでに変わる | プロセス間 API 呼び出しを原子的にする手段がなく、最終境界の競合余地が残る。ターゲット HWND 自体は既に固定している |
| クリック無しの Discord autocomplete ポップアップ | 今回の保留フォーカス再利用では従来の同じ欄・同じ HWND 条件を維持する。Windows実機で後退の有無を必ず測る |
| Microsoft IME の候補をEscで取り消し、Enterを押さずに次の文字を入力 | #9 の引き継ぎ保留が解除されない可能性。未実測なので強制解除しない |
| TIP応答の遅延/別アプリへの切替を連続させる | #11 の重複入力・打鍵損失についてモックと実機の両方の証跡が必要 |
| CLIのWinExe出力リダイレクト、非既定コンソール | #8 のWindows実測が必要。GUI起動の点滅防止を維持する |

### Windows 実行の再現手順

```powershell
# クリックなしのビルド・論理テストのみ (インストール/アンインストールは行わない)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-675707-Integration.ps1
# UI操作あり、Focus E2Eを6回実施 (操作を一時中断する)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-675707-Integration.ps1 -RunFocusE2E -FocusIterations 6
```

実行ログは %TEMP% 下のタイムスタンプ付きディレクトリに保存される。**手元で実行していないため、現状はすべて未実施**。

## 外部レビュー（Claude）独立照合と対処（2026-10-18）

外部レビューの結論を鵜呑みにせず、現行PRの実コードと照合した。以下の「対処」はコミット済みのコードまたは追加テストを指し、Windowsでの動作確認完了を意味しない。

| 指摘 | 判定と処理 |
|---|---|
| XMLコメントの誤配置 | 確認。連続する二つの summary を分離し、SetImeOpenAsync に本来の説明を戻した |
| target! と Nullable 型 | 確認。まず target is null でガードし、以後 non-null として利用するよう統一した。実コンパイル時の警告件数は未確認 |
| Actions 0件、ビルド未実行 | 確認。取得可能な Actions 実行履歴が0件であり、成功を主張しない。参照モデルの判定と実際のC#実行は区別する |
| FocusWindow==0 の許容 | 事実。ネイティブHWNDを取れないブラウザー等では、クリック以外の仮想フォーカス移動を検出できない。安易な「0を常に不一致とする」変更は #675 の入力候補・先頭文字を後退させる可能性があるため未修正・ブロッカーとして維持 |
| 解決完了競合時の false | 確認。保留状態を最終判定する前に新しい結果の解決を再確認し、解決済みなら CanCapture の最新結果を読む。完全な原子性は保証しない |

### 追加で発見・対処した問題

**クリック世代の記録時点**: 以前は `ApplyFocus` 公開時の `_clickVersion` をそのまま結果に付けていた。UIA調査 *中* のクリックが完了結果の世代に取り込まれ、旧フォーカスがクリック後の正しい結果に見える可能性があった。

- `Inspect` 開始時に `_inspectedClickVersion` を取得する。
- `ApplyFocus` は調査開始時と公開時のクリック世代が違えば棄却する。
- `CanCapture` と Affinity 高速経路も公開結果のクリック世代を照合する。
- `FocusInspector_RejectsInspectionFinishedAfterClick` でクリック中の調査完了を模擬。

### 未解決の安全性と回帰リスク

- #675 のクリック直後再調査は維持する一方、`CanCaptureWaiting` の古い値を使う経路でネイティブフォーカスが不明なら、DOM内のTab移動などを区別できない。
- UIA がクリック後に古い要素を *新しい世代として* 報告する場合も、純粋な世代検査では検出できない。
- `ImeTargetMatchesOrigin` のHWND/PID/Focus/ThreadId一致は、同一ネイティブ入力欄でのWeb内部のフォーカス差を表現しない。
- これらを機械的に fail-closed 化すると #675 の初文字欠損が増える可能性があるため、Windows上のA/B/E2Eと測定結果を条件に設計を変更する。

### 状態

変更をコミットし、C#回帰テストも追加したが **Windows上のビルドと実行は依然未実施**。そのため #1・#2・#5・#6 は完全解決とは扱わず、PRをDraftに据え置く。

## フォーカス通知・Alt順序・有効スナップショットの修正（2026-10-18 続報）

前回の追加レビューで挙がった4点をPR #13の作業ブランチへ修正した。**ここでの修正済みとはコードへの反映を指し、Windowsビルド・実機試験での合格を意味しない。**

### (1) 世代に結び付いた通知

- `FocusInspector.NotificationToken` に Sequence / ClickVersion / Foreground / Publication Revision を保持。
- `ApplyFocus` は状態とトークンを同じロック内で確定。通知前と、`CompositionService` の受け側でも `IsCurrent` を使って失効を検査。
- `CompositionService.Abandon` はUIスレッドに積んだ時点ではなく **実行する時点** に旧フォーカスの破棄通知を拒否する。
- `TextInputEntered` もトークンを受け取り、現行フォーカスなら入力モード表示に使う。
- この照合では Windows のフォーカス変更と処理実行を原子的にはできないため、最後の照合直後の変更は残存リスクとする。

### (2) Alt切替を入力順に処理

- 左Alt=英数、右Alt=日本語を `_altImeQueue` へFIFOで積み、単一ワーカーが順に処理する。
- キーボードフックは同期的な外部IME待機を行わない。
- HWND/PID/Focus/ThreadId/設定の再照合は維持。
- 同じ入力先へ「左Alt→右Alt」の順で届いたのにワーカーだけ逆順で完了するレースを抑止。
- IMM32側のタイムアウト後に外部OS操作が遅延完了するケースは別途実機測定する。

### (3) 操作用フォーカスと診断用フォーカス

- `Current` は最新の世代・クリック番号・前面ウィンドウと一致する場合だけ返し、それ以外は未確認（非入力欄）。
- `LastInspected` は UIA が最後に公開した生の結果を診断用に保持。
- これによりコード入力欄分類や表示位置で失効した結果を読む可能性を下げるが、#675の候補表示・クリック後初文字保護に影響し得るのでWindows E2Eが必要。

### (4) テスト実行補助

- `tools/Test-675707-Integration.ps1` は `Start-Process` で .NETのstdout/stderrを分離保存し、PowerShell 5.1の `NativeCommandError` 依存をなくした。
- Focusトークンのクリックによる失効・通知の識別テストを追加。
- 左右Altを高速に連続押下する実機テストも必要（純粋な32/128条件の参照モデルでは競合を検証できない）。

**未実施**: Windows .NETビルド、C#テスト、Alt高速切替の実機E2E、WinPS5.1/PS7のスクリプト動作、候補表示中の初文字とクリック0/10/30ms。


## フォーカス失効の追加観測 (2026-10-19)

Windows 実機で通常の統合テスト (Core 515/515、Windows 550/550) と既存の Focus E2E が通過した。一方、既存E2Eでは、入力欄からボタン・パスワード欄へのクリック直後に、`CanCaptureWaiting` 側が入力欄として扱う試行があった。「クリックが UI スレッドに届く前に判定した」のか「新しいネイティブフォーカスに移った後も旧結果を使った」のかは、従来のログだけでは区別できない。

### 新しい診断モード (テストコードのみ変更)

`MELTYPE_FOCUS_DIAGNOSTICS=1` を付けて `--focus-e2e` を実行すると、入力欄→ボタンおよび入力欄→パスワード欄に対して、遅延別の `FOCUS_DIAG` と横取り判断試行の `FOCUS_TRACE` を出力する。Win32 `GetGUIThreadInfo` で確認したフォーカス HWND を「キー判定の直前」「判定終了直後」に記録する。UIAへの追加問い合わせやキー文字列の記録は行わない。

`captured_with_target_focus_at_key` は、すでに目的の非入力欄にネイティブフォーカスが移った状態で横取りを許可した試行数。診断モードでこれが1件でもあれば、独立の期待条件により FAIL とする。HWND=0等の取得不能な試行は「安全」と推定せず、`captured_with_unknown_focus_at_key` として分ける。`captured_with_target_focus_after_check` は測定後に移動した可能性もあり、単独ではFAILにしない。

Windows cmd.exe での起動例:

```bat
git pull --ff-only origin fix/675-707-integration-safety
set "MELTYPE_FOCUS_DIAGNOSTICS=1"
dotnet run --project src/Meltype.Tests -c Release -- --focus-e2e
set "MELTYPE_FOCUS_DIAGNOSTICS="
```

検証は入力を実際に送信するものではなく、キャプチャ判定と HWND の観測である。上記の診断モードは Win32 を余分に問い合わせるため、通常の性能比較には環境変数なしの E2Eを使用する。

### 統合前との A/B 比較

対象PR のベースは `ca88a9addd689c1181a6459ff94797b69eb06232`。このリビジョンでは `FocusEndToEnd.cs` が同じ既存実装であるため、診断モードなしの通常 E2E をベースとHEADの両方で複数回実施し、入力欄への復帰率・非入力欄の横取り許可数・最大フック待機時間を比較する。比較中は端末負荷・ウィンドウ状態・入力操作条件をなるべく揃える。

```bat
REM 現在の Meltype リポジトリから、親ディレクトリへ別の worktree を追加
git worktree add --detach ..\Meltype-675707-baseline ca88a9addd689c1181a6459ff94797b69eb06232
cd ..\Meltype-675707-baseline
dotnet run --project src/Meltype.Tests -c Release -- --focus-e2e
cd ..\Meltype
REM 診断環境変数が解除されていることを確認したうえでHEAD側でも同じコマンドを実行
dotnet run --project src/Meltype.Tests -c Release -- --focus-e2e
```

既存の E2E の `captured` は、アプリに文字が実際に入力されたことや機密漏えいが生じたことを意味しない。旧コードからの回帰かどうかは A/B 実測を確認して判定する。製品コードのIME・フォーカス処理はこの診断追加では変更しない。
