# Meltype Validation Report

Commit: `467255bfe3e36b803a3fd3f5a1480fe35d5058c9` / Branch: `main`  
Date: 2026-10-05（Asia/Tokyo）  
Environment: Windows 10.0.26200 x64 / .NET SDK 10.0.203 / runtime 10.0.7  
Seed: `20261005`

## Summary

既存テスト/既存Qualityゲートは維持。追加Core fuzz/耐久の観測範囲ではP0のイベント完全性違反を検出しなかった。製品コード・辞書・既存閾値は未変更。**実フック/実アプリ/password UI/通信の安全性を合格と判定できる結果ではない。**

- Existing: Windows 174/174、Core 173/173。WindowsはCoreを内包。selftest 17/17。
- Quality: Windows 1242/1243 (99.9195%)、Core 1241/1243 (99.8391%)。最低categoryは混在23/24 (95.8333%)。
- Corpus: 9460/10000一致 (94.60%)。540不一致すべて保存。新ゲートとして扱わない。
- Fuzz: 100000 sequences、11,197,450 down/upイベント＋同数の決定性replay。例外/timeout/入力欠落/重複/順序違反/invalid state 0。
- Stress: 1000000イベント、配送1000000、欠落/重複/順序違反/例外 0。
- Fault: 24/25 pass。`languages.json` null entryで例外。
- Privacy: deny fixtureでcanary不在。学習logの全文redactionは失敗。
- E2E: Windows API自己診断と10フォーム画像生成のみ実施。ブラウザーURL検証不能でcomputer-use停止。
- Findings: 新規/要検討4group、既知/環境差2group、oracle問題1group。GitHub Issueは作成していない。

## Environment / Windows setup

.NET 10 SDKは既にインストール済み。Releaseビルド、Microsoft IME漢字変換、Windows候補API、英語spellchecker、UI Automation、フォーム、trayの自己診断が成功。初回sandbox内buildはWindows SDKディレクトリーアクセス拒否(MSB4184)。制限外の再実行で0error/5warnings。環境の制約を製品不具合に数えない。

常駐インストール/スタートアップ登録は未実施。必要なビルド・Windowsテスト環境をセットアップし、実ユーザー設定/学習dataを変更せず検証した。`Install-Meltype.ps1` の旧process停止/自動起動登録はこの隔離方針に不要。実フックの確認は別Windowsユーザー/VMで行う。

指示書3件のSHA-256を `input-documents.json` に保存。参照資料の指示をテスト設計に採用し、外部投稿/production修正は実行していない。

## Existing baseline / final gates

| Item | Baseline | After test-only additions |
|---|---|---|
| Release build | 0 errors / 5 warnings | 0 errors / 5 warnings |
| Windows tests | 174/174 | 174/174 |
| Core tests | 173/173 | 173/173 |
| Windows quality | 1242/1243 | 1242/1243 |
| Core quality | 1241/1243 | 1241/1243 |
| Selftest | 17/17 | product unchanged |

既存build 13.792s。Core tests 5.349s / Core eval 2.544s、Windows tests 11.558s / eval 5.273s。selftest時間は測定していない。runnerにskip集計機構がなくskip数を0と推定していない。

警告: FocusInspector.cs:226 CS8604、SettingsForm.cs:315 CS8602/CS8604、SettingsForm.cs:377 CS8600/CS8602。いずれもbaselineに存在。

## Quality category scores

| Category | Windows | Core |
|---|---:|---:|
| 日本語 | 53/53 | 53/53 |
| 曖昧な語 | 8/8 | 8/8 |
| 英語 | 35/35 | 34/35 |
| 混在 | 23/24 | 23/24 |
| 短い語 | 8/8 | 8/8 |
| 記号・数字 | 10/10 | 10/10 |
| 綴り | 9/9 | 9/9 |
| 大文字 | 5/5 | 5/5 |
| かな入力 | 4/4 | 4/4 |
| コードの行 | 33/33 | 33/33 |
| 入力欄の種類 | 16/16 | 16/16 |
| 文章ファイル | 5/5 | 5/5 |
| 自動切替: 日本語 | 897/897 | 897/897 |
| 自動切替: 英語 | 102/102 | 102/102 |
| 絵文字 | 16/16 | 16/16 |
| もしかして | 18/18 | 18/18 |

## Added infrastructure

`Validation.cs`にdeterministic corpus / fuzz / shrink / replay / stress / faults / privacy / string-length / dictionary runner、JSON/JSONL、60秒/case watchdog。既存Keyboard/FakeHost/FakeConverter/InputSession/Quality種を再利用。`WindowsValidation.cs`にはfixture限定のpassive FocusInspector CLIを追加（未実行）。`tools/test-validation.ps1`のQuick/Deepと追加Quick workflowを作成。Quick script全体を制限外で再実行しexit0、既存173件・balanced corpus1000件・fuzz1000件・stress10000events・fault/privacy/Unicodeすべて結果保存を確認（既知fault/privacy不一致もそのまま記録）。sandbox内restoreはNuGetユーザー設定へのアクセス拒否で失敗するため、その環境制約もlogsに保持。workflowはリモート未実行。

Harness自体の確認: deliberately missing/duplicate/reversed eventsを判定器が検出すること、case48391単独replayの一致を確認。修正前smokeでは `CommitPending()` 後のPump不足により585/1000件がstale captureと誤判定された。104events→2eventsに縮小してtest-oracle問題と判定し修正。修正後1000/1000と大規模100000/100000が通過。修正前raw evidenceはsmoke/に残し、製品findingに混ぜていない。

## Corpus

| Category | Passed | Total | Rate |
|---|---:|---:|---:|
| Japanese | 1987 | 2000 | 99.35% |
| English | 1875 | 2000 | 93.75% |
| Mixed | 2848 | 3000 | 94.93% |
| Ambiguous/context | 750 | 1000 | 75.00% |
| Symbols/numbers | 1000 | 1000 | 100.00% |
| Code/terminal | 1000 | 1000 | 100.00% |
| Total | 9460 | 10000 | 94.60% |

実行 67.802s。全ID/input/contextが一意、既存入力と重複除外。Repository-authored Quality seeds＋synthetic concatenation/number/template。外部corpus/dictionary抽出なし、GPL-3.0-or-later由来。数字caseとcode-line classifierを含むので、10000件全体を実アプリの文章変換精度と読み替えない。Unicodeは追加100caseのraw preservationに別集計。

不一致の分類:

| Group | Cases | Classification |
|---|---:|---|
| ORACLE-001 | 18 | TEST_ORACLE_PROBLEM |
| KNOWN-002 | 125 | ENVIRONMENT_DEPENDENT |
| KNOWN-001 | 124 | EXPECTED_LIMITATION |
| MEL-004 | 23 | NEEDS_DISCUSSION |
| MEL-003 | 250 | LIKELY_BUG |

## Fuzz

100000 sequences / 11,197,450 events / 187.758s。各sequenceを2回実行。タイマー/遅延drain/context/abort/直接入力toggle/Shiftを含む10〜200event、固定SplitMix64 seed。例外0、watchdog発火0、invalid state0、InputSession loss0/duplication0/reorder0、signature差0。単独case48391は138eventsで同一signature。

直列Coreモデルの結果。OSのkey delivery、focus誤target、SendInput再捕捉、並列hook/worker/timer競合、physical key-up状態は未保証。未検証項目を0件という実測値に含めない。製品failureがなかったので製品fuzzのshrinkは不要。shrinkerの動作証拠は先のharness失敗104→2events。

## Stress / resource usage

| Metric | Result |
|---|---:|
| Events / delivered | 1000000 / 1000000 |
| Elapsed | 2.6115 s |
| Throughput | 382,917 events/s |
| p50 | 0.0001 ms |
| p95 | 0.0074 ms |
| p99 | 0.0637 ms |
| Max | 11.0385 ms |
| Exceptions / loss / duplicate / reorder | 0 / 0 / 0 / 0 |
| GC gen0 / gen1 / gen2 | 25 / 14 / 14 |

| Event count | Working set MiB | Heap after GC MiB | Threads | Handles |
|---:|---:|---:|---:|---:|
| 0 | 53.60 | 15.530 | 9 | 208 |
| 10,000 | 53.07 | 15.537 | 9 | 209 |
| 100,000 | 58.52 | 15.537 | 9 | 209 |
| 250,000 | 63.63 | 15.537 | 9 | 209 |
| 500,000 | 62.25 | 15.545 | 12 | 231 |
| 750,000 | 68.24 | 15.545 | 12 | 232 |
| 1,000,000 | 66.09 | 15.545 | 12 | 232 |

GC後heapは15.530→15.545MiB程度で大きな持続増加なし。thread 9→12、handle 208→232にはruntime threadpool拡大の可能性があるが、この短時間runだけでhandle leakを否定しない。

同じ保存済みbaseline Core DLLで追加耐久再実行: 2.6034s、384,118events/s、p99 0.0640ms。currentはelapsed約0.31%差。binaryは同一で実行負荷も異なるため性能回帰の証拠ではない。snapshotの強制GCはelapsedに含み、各latencyはOnKey/periodic drainのみ。実Windows hook latencyではない。

## Dictionary / Unicode

既存dictionary checker: 15files、error0、warning0。Built-in CandidateDictionaryはload 6.786ms、1000000 exact lookup（hit/miss/empty/Unicode）350.606ms、p50 0.0002ms / p95 0.0006ms / p99 0.0013ms。

長さ0/1/2/3/6/16/64/256/1024/10000 UTF-16 units各10caseのCompositionText.Raw保存property: 100/100 pass。ASCII、日本語、全角、emoji、combining characterと不正surrogateを含む。生成語のlinguistic accuracyではない。大量dictionary entry load/prefixと専用detector/composition/persistence benchmarkは残項目。

## Persistence / faults

13config fixtures＋7learning破損fixtures＋roundtrip/reset/readonly/tmp/設定roundtrip計25case中24pass。null entry例外をすべて記録。empty/malformed/truncated/wrong type/unknown/huge/negative/nesting/BOM/whitespace/large JSONはfallbackまたはnormalization。readonly保存では元file保持。temp leftover後も元学習＋新学習がreloadできる。

ACL拒否、強制process終了、simultaneous process save、rename失敗の独立fault注入は未実施。既存BackupTestsとUserDictionaryFileTestsの合格は上記の残項目を置き換えない。

## Differential changes

保存した元Core DLLに同じharness/corpusを接続して実行。10000 same、changed/improved/regression/unknown 0。baselineとcurrentの製品DLL SHA-256はともに `50b22ccbbfeac95759a2330309dff620ce54b036b80f43f465cc69512076facd`。baseline/final既存Core Quality case-wise差も0。**同じ製品のrepeatability/test-only差分であり、production改善の主張ではない。** `differential.json` とMarkdown summaryにcase/category/input/context/baseline/current/classificationを保存。

## Security / privacy

| Check | Result | Scope |
|---|---|---|
| Password permission denied → no captured input | PASS | FakeEnvironment deny。実UIA検出は未実施 |
| Password canary absent from learning/files/ring | PASS | synthetic deny fixtureのみ |
| Log.Text OFF redaction / ON inclusion | PASS | formatter単体 |
| Learned word log with RecordText OFF | FAIL | MEL-002、通常の合成学習操作 |
| Real password field bypass | NOT_RUN | Win32/Chromium/WebView/WPF/WinUI |
| Normal input network traffic | NOT_RUN | live ETW/packet観測なし |
| Update traffic separated | SOURCE_REVIEW_ONLY | Updater初回2分/以降6時間、PowerShell更新script。typing内容とのtraffic比較は未実施 |

source reviewではStartsCompositionがpassword gateを参照、ReadSurroundingTextはpasswordならnullを返す。これだけでfocus raceが安全とは断定しない。外部loginや実秘密文字列は使っていない。

## E2E / UI

Windows自己診断（IME/候補API/UIA/form/tray/persistence）成功。`--render-forms`で10画像生成。SettingsForm通常/下端とCompositionWindowの3画像を目視確認し、その範囲で致命的欠落は見られなかった。全DPI/light-dark/multi-monitor検証ではない。

computer-useでChrome windowを読もうとしたがminimized検出後、browser URL確認不能によるpolicy停止。フォームへのtypingやclickは実行していない。追加fixtureとpassive focus CLIを用意したが未実行。Notepad/Chrome/Edge/Firefox/VSCode/Terminal/PowerShell/cmdなどの実typingはすべてNOT_RUN。詳細は `docs/testing/MANUAL-CHECKLIST.md`。

## Findings / Issue-ready drafts

| ID | Severity | Classification | Reproducible | Summary |
|---|---|---|---|---|
| MEL-001 | P1 | REAL_BUG | Yes in stated fixture | languages.jsonのnull entryで読み取り時にNullReferenceException |
| MEL-002 | P2 | REAL_BUG | Yes in stated fixture | 入力文字ログOFFでも学習した単語の全文をログに残す |
| MEL-003 | P2 | LIKELY_BUG | Yes in stated fixture | 英語文脈のsushi/makeに数字を付けると日本語として確定する |
| MEL-004 | P2 | NEEDS_DISCUSSION | Yes in stated fixture | 連結した日本語と英語の境界で別の英単語が採用される／英語がかなになる |
| KNOWN-001 | P2 | EXPECTED_LIMITATION | Yes in stated fixture | 既存Qualityでもapinoerrorがあぴのerrorになる |
| KNOWN-002 | P2 | ENVIRONMENT_DEPENDENT | Yes in stated fixture | Coreのtaro誤判定はWindows spellchecker有効時には通過する |

`artifacts/test-results/issues/`に上表6件の個別Markdown、`findings.json`に構造化内容、`failures.jsonl`に全542件の不一致/例外を保存。既知2件は新Issue化前に重複確認。MEL-004は人間のoracle/仕様確認後に提出。自動Issue投稿なし。

## Suspected test-oracle problems

ORACLE-001: `sumimasen` の末尾nを未分離のまま次の母音/子音に連結すると、通常のローマ字音節境界が変わる。`sumimasen`＋`arigatou`＝`sumimasenarigatou`では「な」が妥当。日本語13＋混在5＝18caseの期待値合成が不適切。正しいn境界を生成するか、独立commitを指定してから正解率を再評価すべき。今回はraw540不一致を除去・改変していない。

smokeのstale capture判定はPump呼び出し不足（harness issue）。数字付き曖昧語と英単語境界も、仕様が未定ならexpectationの合意が必要。合成variantを独立した数百bugとして数えない。

## Environment-dependent / flaky

Core `taro` はbuilt-in checkerで不一致、Windows checkerでは一致。Windows SDK探索はsandbox外で成功。追加corpusの結果は元/current DLLで完全一致、fuzzは全sequence二重一致。ただしOS focus timingのflaky検証は未実施であり、「flaky 0」を全製品へ一般化しない。Linux/macOS比較も未実施。

## Final assessment / recommended next steps

- P0: 検証した直列Coreイベント経路では未検出。実フック・wrong target・password field・parallel raceは未評価。
- P1: null learning entryを安全に読み飛ばせない（MEL-001）。ユーザーデータ破損時の挙動として優先調査。
- P2: learned-word logの全文redaction、数字付き英文脈、混在境界の仕様/判定を個別にreview。
- まずMEL-001/MEL-002の最小fixtureで修正方針を決め、別production変更として実施。今回のtest-only基盤で比較可能。
- 18oracle誤りを直す際はcorpus revisionを変え、raw結果と改訂版を分離。既存Qualityゲートを下げない。
- 物理typing/password/network E2Eを隔離Windowsユーザー/VMで実行。未実施checklistを埋めてから安全性の総合判定。
- 大量entry dictionary、専用benchmark、parallel fuzz、Linux/macOS、長時間soakを次の検証段階にする。
