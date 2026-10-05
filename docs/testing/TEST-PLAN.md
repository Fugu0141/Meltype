# Meltype 追加検証

2026-10-05 / seed `20261005`。製品コードと既存Qualityゲートは変更しない。

## 再実行

```powershell
./tools/test-validation.ps1 -Profile Quick
./tools/test-validation.ps1 -Profile Deep -Output artifacts/test-results-new
python tools/test-validation-report.py
dotnet run --project src/Meltype.Core.Tests -c Release -- --validate corpus --case 456 --out artifacts/replay
dotnet run --project src/Meltype.Core.Tests -c Release -- --validate fuzz --seed 20261005 --case 48391 --out artifacts/replay
dotnet run --project src/Meltype.Core.Tests -c Release -- --validate fuzz --replay artifacts/test-results/smoke/fuzz-repro-2.json --out artifacts/replay
dotnet run --project src/Meltype.Core.Tests -c Release -- --validate diff --baseline before/corpus.jsonl --current after/corpus.jsonl --out artifacts/diff
```

縮小JSONの `sequence` を使って再生する。smoke/fuzz-repro-2.jsonは修正前のtest-oracle問題の記録であり、現在のharnessでは再現しないことが期待値。report generatorは今回のbaseline/確認probeを含む `artifacts/test-results` 全体を集約する専用scriptで、Quick出力単独には適用しない。

## 既存機構の再利用

- `TestHost` / `[Test]`: Core173件、Windows版174件。Windows runnerはCore testsも含むので独立347件とは数えない。
- `Quality.Run`, `--eval`, `--eval-json`: 1243採点ケース、全体95%/カテゴリー80%の既存ゲートを保持。
- `CompositionTests.Keyboard`, FakeHost, FakeConverter, CaptureGate, CompositionControllerを追加コーパス・fuzzに再利用。
- `FakeEnvironment`, InputSession, ScoreEngineをイベント完全性に再利用。
- 既存 `--repro`, `--segments`, `--explain`, `--type`, `--convert`, `--context`, `--henkan`, `--jht`, `--jht-batch`, `--render-forms`, `--selftest` を調査に使用。
- `node tools/check-dictionaries.mjs` を辞書の形式検査に使用。
- `.github/workflows/build.yml` のWindows/Core CI、pr-checks品質比較を確認。追加Quick workflowはローカルに追加しただけでリモート実行していない。

## 追加の観測範囲

コーパス: repository内Quality.Typingの種から連結/数字/文脈を合成。日本語2000、英語2000、混在3000、曖昧/context1000、数字/記号1000、コード行1000。一意の入力/contextとIDを保存。合成後の期待値は仮説であり、境界で文脈が変わった例をそのまま製品バグと断定しない。外部辞書や市販IME辞書からのコピーはなし。由来はGPL-3.0-or-laterの本repository。

Fuzz: SplitMix64でseed/caseに独立な10〜200 down/upイベント。英字、Shift大文字、数字、Space/Enter/Esc/Backspace/Delete/矢印/Tab/F6/F7/F9/F10/モード切替、修飾キー、timer、遅延flush、abort、context changeを合成。各sequenceを2回実行して結果digestを比較。候補/文節index、確定後state、InputSession入出力イベントのID/順序を検査。代表failureはchunk除去、single-event除去、繰り返し圧縮（同じ除去操作）、文字/時間簡約で縮小。

Stress: managed InputSessionへ100万down/upイベント。再送consumerでイベントIDを照合。指定checkpointのメモリ/GC/thread/handle/CPU、各OnKeyと定期drainのp50/p95/p99/maxを記録。強制GCを含むthroughputのため、実フック遅延とは比較しない。

Fault: explicit test paths内でconfig破損/空/null/型不正/巨大値/負値/深いJSON/BOM/whitespace/大ファイル、learning破損/null entry/巨大Count、readonly、temp leftover、save/load/reset。既存Backup/File testsも実行。ACL拒否、突然終了、同時プロセス保存は未実行。

Unicode/長さ: CompositionText.Raw保存propertyで0〜10000 UTF-16 code units。サロゲート片割れも意図的に含む。実キーボードによるUnicode入力の正解率には含めない。

Privacy: fake deny permissionに対する入力非捕捉、学習なし、isolated log/modelへのcanary不在。Log.Text OFF/ON。これは実password UI/keyboard-hook E2Eを置き換えない。

## 制限

- 全追加runnerは60秒/case watchdog（timeout.jsonにcaseを保存、exit124）。0件のtimeoutを記録する場合もwatchdogがプロセスを停止しなかった範囲に限定。
- Fuzz/Stressはsingle-threadのCoreモデル。Windows SendInput、実キーボードフック、focus target、key-up実配送、parallel raceを未保証。
- computer-useのSendInputは `Injected=true` となり、製品 `StartsComposition` が明示的に無視する。疑似UI打鍵を物理入力E2Eとして合格に数えない。
- 実ユーザーの `%LOCALAPPDATA%/Meltype` を変更せず、スタートアップ登録も行わない。Windowsビルドと自己診断で必要SDK/IME/APIを確認する。
- OSをまたぐCore比較、DPI/multi-monitor全組合せ、ネットワークETW/packet観測はこのWindows単体環境で未実施。

詳細結果と未実行checklistは `RESULTS.md` / `MANUAL-CHECKLIST.md` を参照。
