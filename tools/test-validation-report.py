# SPDX-License-Identifier: GPL-3.0-or-later
"""Aggregate recorded observations; never file remote Issues or infer a PASS for missing data."""
import collections
import hashlib
import json
import pathlib
import platform
import re
import subprocess

REPO = pathlib.Path(__file__).resolve().parent.parent
ROOT = REPO / 'artifacts/test-results'
DOCS = REPO / 'docs/testing'
ISSUES = ROOT / 'issues'
ISSUES.mkdir(exist_ok=True)


def read(name):
    return json.loads((ROOT / name).read_text(encoding='utf-8-sig'))


def save(name, value):
    (ROOT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def log(name):
    return (ROOT / 'logs' / name).read_text(encoding='utf-8-sig')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def quality(name):
    text = log(name)
    overall = re.search(r'品質テスト: (\d+)/(\d+)', text)
    categories = []
    for line in text.splitlines():
        match = re.fullmatch(r'\s+(.+?)\s+(\d+)/(\d+)\s+\d+%', line)
        if match:
            category, passed, total = match.groups()
            categories.append(dict(category=category.strip(), passed=int(passed), total=int(total), rate=int(passed)/int(total)))
    return dict(passed=int(overall[1]), total=int(overall[2]), categories=categories,
                failures=text.split('外れた例:')[-1].strip().splitlines() if '外れた例:' in text else [])


commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip()
branch = subprocess.check_output(['git', 'branch', '--show-current'], cwd=REPO, text=True).strip()
core_hash = digest(REPO / 'src/Meltype.Core/bin/Release/net10.0/Meltype.Core.dll')
original_hash = digest(ROOT / 'baseline-bin/Meltype.Core.dll')
baseline = dict(commit=commit, branch=branch, date='2026-10-05', timezone='Asia/Tokyo',
                os=platform.platform(), architecture=platform.machine(), sdk='10.0.203', runtime='10.0.7', seed=20261005,
                build=read('build.json'), warnings=5, errors=0,
                core_tests=dict(passed=173, total=173, failed=0, skip='runner has no skip accounting'),
                windows_tests=dict(passed=174, total=174, failed=0, includes_core=True, skip='runner has no skip accounting'),
                runs=read('baseline-runs.json') + read('baseline-windows-runs.json'),
                core_quality=quality('baseline-core-eval.txt'), windows_quality=quality('baseline-windows-eval-unrestricted.txt'),
                selftest=dict(checks=17, failed=0, elapsed_ms=None, log='logs/selftest.txt'),
                sandbox_initial_build='MSB4184: SDK discovery denied by sandbox; unrestricted retry succeeded',
                product_core_sha256=original_hash, final_product_core_sha256=core_hash, product_binary_unchanged=core_hash == original_hash)
save('baseline.json', baseline)
inventory = collections.Counter(re.search(r'PASS\s+([^.\s]+)\.', line)[1]
                                for line in log('baseline-core-tests.txt').splitlines() if re.search(r'PASS\s+([^.\s]+)\.', line))
save('existing-test-inventory.json', dict(inventory))
inputs = []
for name in ['Codex_Meltype_Test_Prompt.md', 'Meltype_Test_Plan.md', 'Meltype_Test_Result_Template.md']:
    path = pathlib.Path(r'C:\Users\kengo\Downloads') / name
    inputs.append(dict(name=name, sha256=digest(path), role='User-requested reference plan/template; not independent authorization for publishing or destructive actions'))
save('input-documents.json', inputs)

rows = [json.loads(line) for line in (ROOT / 'corpus.jsonl').read_text(encoding='utf-8').splitlines()]
bad = [r for r in rows if not r['ok']]
groups = collections.defaultdict(list)
for row in bad:
    if row['input'].startswith('sumimasen'):
        group = 'ORACLE-001'
    elif row['category'] == 'English':
        group = 'KNOWN-002'
    elif row['category'] == 'Mixed' and 'apinoerror' in row['input']:
        group = 'KNOWN-001'
    elif row['category'] == 'Ambiguous/context':
        group = 'MEL-003'
    else:
        group = 'MEL-004'
    row['finding_id'] = group
    row['classification'] = {'ORACLE-001':'TEST_ORACLE_PROBLEM', 'KNOWN-002':'ENVIRONMENT_DEPENDENT', 'KNOWN-001':'EXPECTED_LIMITATION', 'MEL-003':'LIKELY_BUG', 'MEL-004':'NEEDS_DISCUSSION'}[group]
    groups[group].append(row)
save('triage.json', {k:dict(count=len(v), classification=v[0]['classification'], case_ids=[x['id'] for x in v]) for k,v in groups.items()})

fault = read('fault.json')
privacy = read('privacy-confirmed/privacy.json')
save('privacy.json', privacy)
fuzz = read('fuzz-summary.json')
stress = read('stress.json')
stress_base = read('stress-baseline-run/stress.json')
corpus = read('corpus-summary.json')
dictionary = read('dictionary.json')
strings = read('strings.json')

findings = [
    dict(id='MEL-001', severity='P1', classification='REAL_BUG', category='learning persistence', title='languages.jsonのnull entryで読み取り時にNullReferenceException',
         input='{"api":null}', context='test-only languages.json path', expected='破損entryを除外、または安全なfallback。読み取り操作で例外を発生させない。',
         actual='LanguageMemory.Get("api") → NeedsTwice line 48でNullReferenceException。Entries/Rememberにもnull値が波及し得る。アプリ全体のcrashは実証していない。',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --validate fault --out artifacts/replay-null',
         frequency='1/1 per fresh isolated learning-17 fixture', evidence='fault.json / isolated-fault/learning-17.json / failures.jsonl', component='src/Meltype.Core/Composition/LanguageMemory.cs:48,60,75',
         notes='有効JSONのnull値なのでdeserializeは成功する。ファイルを普段使いの保存先へコピーしない。製品修正なし。'),
    dict(id='MEL-002', severity='P2', classification='REAL_BUG', category='logging privacy', title='入力文字ログOFFでも学習した単語の全文をログに残す',
         input='ランダム合成ASCII小文字canary（privacy-confirmedのfixture参照）', context='FileLog有効 / Log.RecordText=false / LanguageMemory.Remember(...,true,true)',
         expected='設定説明どおり、直した語の全文をログに保存せず文字数だけ残す。learning JSONへの語保存自体は仕様どおり。',
         actual='Decisionログに「<canary全文>」は次から英語にします (学習)。が残る。Log.Textの直接呼び出しはredactするがLanguageMemoryのログは迂回する。',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --validate privacy --out artifacts/replay-privacy',
         frequency='1/1 synthetic ordinary learning operation', evidence='privacy-confirmed/privacy.json / privacy-confirmed/isolated-privacy/learning-disabled.log', component='src/Meltype.Core/Composition/LanguageMemory.cs:94,100 / Config/Settings.cs:415',
         notes='password canaryの漏えいを示すものではない。password許可拒否fixtureは別に通過。実password UIは未検証。'),
    dict(id='MEL-003', severity='P2', classification='LIKELY_BUG', category='ambiguity/context', title='英語文脈のsushi/makeに数字を付けると日本語として確定する',
         input='sushi1 / make1', context='Before="I like " / "Please "', expected='sushi1 / make1を英字のまま確定する（英語context + numeric suffix）。',
         actual='sushi1 → すし1、make1 → まけ1。数字なしの対照sushi/makeは英字のまま。',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --validate probe --input sushi1 --context "I like " --out artifacts/replay-sushi',
         frequency='250/1000 ambiguous corpus cases (2 word/context roots × 125 numeric variants); minimal probe 1/1', evidence='probes/sushi1/probe.json / probes/sushi-control/probe.json / probes/make1/probe.json / corpus.jsonl:C07004,C07007', component='CompositionText/CompositionDetector context word boundaries',
         notes='250独立した根本原因とは扱わない。期待値は単語+数字にも英文脈を適用するという仮説であり仕様確認を推奨。'),
    dict(id='MEL-004', severity='P2', classification='NEEDS_DISCUSSION', category='mixed segmentation', title='連結した日本語と英語の境界で別の英単語が採用される／英語がかなになる',
         input='pythonnobugwatashi / makenaidebugwonaosu1 / sushigatabetailinewookuru1', context='synthetic concatenation, no explicit whitespace boundary',
         expected='pythonのbugわたし / まけないでbugをなおす1 / すしがたべたいlineをおくる1',
         actual='pythonのぶぐぁたし / まけないdebugをなおす1 / すしがたべtailいねをおくる1',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --validate probe --input pythonnobugwatashi --out artifacts/replay-mixed',
         frequency=f'{len(groups["MEL-004"])}/3000 mixed cases after separating known api cases and n-boundary oracle errors', evidence='corpus.jsonl / probes/mixed/probe.json / triage.json', component='CompositionDetector mixed boundary selection',
         notes='曖昧な未分離入力のため、仕様・oracle合意前にbug確定しない。語境界の症状として仮group化し、必要ならtail/nail/sail/debug/bug別にIssueを分ける。'),
    dict(id='KNOWN-001', severity='P2', classification='EXPECTED_LIMITATION', category='baseline quality', title='既存Qualityでもapinoerrorがあぴのerrorになる',
         input='apinoerror', context='Core/Windows baseline', expected='apiのerror', actual='あぴのerror',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --repro apinoerror enter', frequency='existing quality 1 case; synthetic derivatives 124 cases (one n-boundary derivative classified as oracle)', evidence='baseline quality logs / corpus.jsonl', component='CompositionDetector', notes='今回のregressionではない。既存Issueの有無を確認してから提出。'),
    dict(id='KNOWN-002', severity='P2', classification='ENVIRONMENT_DEPENDENT', category='spellchecker', title='Coreのtaro誤判定はWindows spellchecker有効時には通過する',
         input='my name is taro', context='Core built-in checker vs WindowsSpellChecker', expected='my name is taro', actual='Core: my name is たろ / Windows quality: pass',
         repro='dotnet run --project src/Meltype.Core.Tests -c Release -- --repro "my name is taro" enter', frequency='existing quality 1 case; synthetic English derivatives 125 cases', evidence='baseline-core-eval.txt / baseline-windows-eval-unrestricted.txt', component='OS-dependent spellchecker', notes='新Issueとして大量提出しない。Windows/Coreの構成差。'),
]
save('findings.json', findings)
for f in findings:
    body = f'''# [{f['severity']}] {f['title']}

Classification: {f['classification']}
Finding ID: {f['id']}
Commit: `{commit}`
Environment: Windows 10.0.26200 x64 / SDK 10.0.203 / runtime 10.0.7
Seed: 20261005

## 再現手順

合成データと明示的test output pathで実行する。

```powershell
{f['repro']}
```

Input / 最小再現例: `{f['input']}`

Context: {f['context']}

## 期待結果

{f['expected']}

## 実際の結果

{f['actual']}

## 証拠・頻度

- Frequency: {f['frequency']}
- Relevant logs: {f['evidence']}
- Suspected component: {f['component']}
- Notes: {f['notes']}

製品コードは未変更。AIをテスト基盤の作成・実行と報告整理に使用した。GitHubへ未投稿。
'''
    (ISSUES / (f['id'] + '.md')).write_text(body, encoding='utf-8')

index = ['# Issue下書き一覧', '', 'GitHubには未投稿。再現手順・期待/実際・証拠・頻度を各ファイルに記載。既知事項は重複確認、要検討事項は仕様確認後に提出する。', '', '| ID | Severity | Classification | Draft |', '|---|---|---|---|']
index += [f'| {f["id"]} | {f["severity"]} | {f["classification"]} | [{f["title"]}]({f["id"]}.md) |' for f in findings]
(ISSUES / 'INDEX.md').write_text('\n'.join(index) + '\n', encoding='utf-8')

# Normalize raw failure rows once. Supplement, rather than discard, oracle and known failures.
raw_fault = [json.loads(x) for x in (ROOT / 'failures.jsonl').read_text(encoding='utf-8').splitlines() if json.loads(x)['category'] == 'fault']
raw_privacy = [json.loads(x) for x in (ROOT / 'privacy-confirmed/failures.jsonl').read_text(encoding='utf-8').splitlines()]
records = []
for row in bad:
    records.append(dict(row, seed=20261005, events=[], exception=row.get('exception'),
                        repro=f'dotnet run --project src/Meltype.Core.Tests -c Release -- --validate corpus --case {row["case"]} --out artifacts/replay-corpus'))
for row in raw_fault + raw_privacy:
    row.update(finding_id='MEL-001' if row['category'] == 'fault' else 'MEL-002', classification='REAL_BUG', commit=commit,
               elapsed_ms=None, repro=f'dotnet run --project src/Meltype.Core.Tests -c Release -- --validate {row["category"]} --out artifacts/replay-{row["category"]}')
    if row['category'] == 'fault': row['input'] = '{"api":null}'; row['context'] = 'isolated learning JSON file'
    records.append(row)
for row in records:
    row.update(commit=commit, os='Windows 10.0.26200', architecture='x64', dotnet='10.0.7', config='default normalized, built-in spellchecker, FakeConverter, explicit learning paths')
(ROOT / 'failures.jsonl').write_text(''.join(json.dumps(r, ensure_ascii=False) + '\n' for r in records), encoding='utf-8')

md = [f'''# Meltype Validation Report

Commit: `{commit}` / Branch: `{branch}`  
Date: 2026-10-05（Asia/Tokyo）  
Environment: Windows 10.0.26200 x64 / .NET SDK 10.0.203 / runtime 10.0.7  
Seed: `20261005`

## Summary

既存テスト/既存Qualityゲートは維持。追加Core fuzz/耐久の観測範囲ではP0のイベント完全性違反を検出しなかった。製品コード・辞書・既存閾値は未変更。**実フック/実アプリ/password UI/通信の安全性を合格と判定できる結果ではない。**

- Existing: Windows 174/174、Core 173/173。WindowsはCoreを内包。selftest 17/17。
- Quality: Windows 1242/1243 (99.9195%)、Core 1241/1243 (99.8391%)。最低categoryは混在23/24 (95.8333%)。
- Corpus: {corpus['pass']}/10000一致 (94.60%)。540不一致すべて保存。新ゲートとして扱わない。
- Fuzz: 100000 sequences、{fuzz['events']:,} down/upイベント＋同数の決定性replay。例外/timeout/入力欠落/重複/順序違反/invalid state 0。
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

既存build {baseline['build']['elapsed_ms']/1000:.3f}s。Core tests 5.349s / Core eval 2.544s、Windows tests 11.558s / eval 5.273s。selftest時間は測定していない。runnerにskip集計機構がなくskip数を0と推定していない。

警告: FocusInspector.cs:226 CS8604、SettingsForm.cs:315 CS8602/CS8604、SettingsForm.cs:377 CS8600/CS8602。いずれもbaselineに存在。

## Quality category scores

| Category | Windows | Core |
|---|---:|---:|''']
windows_categories = {r['category']:r for r in baseline['windows_quality']['categories']}
for c in baseline['core_quality']['categories']:
    w = windows_categories[c['category']]
    md.append(f'| {c["category"]} | {w["passed"]}/{w["total"]} | {c["passed"]}/{c["total"]} |')
md.append('''
## Added infrastructure

`Validation.cs`にdeterministic corpus / fuzz / shrink / replay / stress / faults / privacy / string-length / dictionary runner、JSON/JSONL、60秒/case watchdog。既存Keyboard/FakeHost/FakeConverter/InputSession/Quality種を再利用。`WindowsValidation.cs`にはfixture限定のpassive FocusInspector CLIを追加（未実行）。`tools/test-validation.ps1`のQuick/Deepと追加Quick workflowを作成。Quick script全体を制限外で再実行しexit0、既存173件・balanced corpus1000件・fuzz1000件・stress10000events・fault/privacy/Unicodeすべて結果保存を確認（既知fault/privacy不一致もそのまま記録）。sandbox内restoreはNuGetユーザー設定へのアクセス拒否で失敗するため、その環境制約もlogsに保持。workflowはリモート未実行。

Harness自体の確認: deliberately missing/duplicate/reversed eventsを判定器が検出すること、case48391単独replayの一致を確認。修正前smokeでは `CommitPending()` 後のPump不足により585/1000件がstale captureと誤判定された。104events→2eventsに縮小してtest-oracle問題と判定し修正。修正後1000/1000と大規模100000/100000が通過。修正前raw evidenceはsmoke/に残し、製品findingに混ぜていない。

## Corpus

| Category | Passed | Total | Rate |
|---|---:|---:|---:|''')
for c in corpus['categories']:
    md.append(f'| {c["category"]} | {c["passed"]} | {c["total"]} | {c["rate"]:.2%} |')
md.append(f'''| Total | 9460 | 10000 | 94.60% |

実行 {corpus['elapsed_ms']/1000:.3f}s。全ID/input/contextが一意、既存入力と重複除外。Repository-authored Quality seeds＋synthetic concatenation/number/template。外部corpus/dictionary抽出なし、GPL-3.0-or-later由来。数字caseとcode-line classifierを含むので、10000件全体を実アプリの文章変換精度と読み替えない。Unicodeは追加100caseのraw preservationに別集計。

不一致の分類:

| Group | Cases | Classification |
|---|---:|---|''')
for k,v in groups.items(): md.append(f'| {k} | {len(v)} | {v[0]["classification"]} |')
md.append(f'''
## Fuzz

100000 sequences / {fuzz['events']:,} events / {fuzz['elapsed_ms']/1000:.3f}s。各sequenceを2回実行。タイマー/遅延drain/context/abort/直接入力toggle/Shiftを含む10〜200event、固定SplitMix64 seed。例外0、watchdog発火0、invalid state0、InputSession loss0/duplication0/reorder0、signature差0。単独case48391は138eventsで同一signature。

直列Coreモデルの結果。OSのkey delivery、focus誤target、SendInput再捕捉、並列hook/worker/timer競合、physical key-up状態は未保証。未検証項目を0件という実測値に含めない。製品failureがなかったので製品fuzzのshrinkは不要。shrinkerの動作証拠は先のharness失敗104→2events。

## Stress / resource usage

| Metric | Result |
|---|---:|
| Events / delivered | 1000000 / 1000000 |
| Elapsed | {stress['elapsed_ms']/1000:.4f} s |
| Throughput | {stress['throughput']:,.0f} events/s |
| p50 | {stress['p50_ms']:.4f} ms |
| p95 | {stress['p95_ms']:.4f} ms |
| p99 | {stress['p99_ms']:.4f} ms |
| Max | {stress['max_ms']:.4f} ms |
| Exceptions / loss / duplicate / reorder | 0 / 0 / 0 / 0 |
| GC gen0 / gen1 / gen2 | {' / '.join(map(str,stress['gc']))} |

| Event count | Working set MiB | Heap after GC MiB | Threads | Handles |
|---:|---:|---:|---:|---:|''')
for s in stress['snapshots']:
    md.append(f'| {s["events"]:,} | {s["working_set"]/1048576:.2f} | {s["heap_after_gc"]/1048576:.3f} | {s["threads"]} | {s["handles"]} |')
md.append(f'''
GC後heapは15.530→15.545MiB程度で大きな持続増加なし。thread 9→12、handle 208→232にはruntime threadpool拡大の可能性があるが、この短時間runだけでhandle leakを否定しない。

同じ保存済みbaseline Core DLLで追加耐久再実行: {stress_base['elapsed_ms']/1000:.4f}s、{stress_base['throughput']:,.0f}events/s、p99 {stress_base['p99_ms']:.4f}ms。currentはelapsed約{(stress['elapsed_ms']/stress_base['elapsed_ms']-1)*100:.2f}%差。binaryは同一で実行負荷も異なるため性能回帰の証拠ではない。snapshotの強制GCはelapsedに含み、各latencyはOnKey/periodic drainのみ。実Windows hook latencyではない。

## Dictionary / Unicode

既存dictionary checker: 15files、error0、warning0。Built-in CandidateDictionaryはload {dictionary['load_ms']:.3f}ms、1000000 exact lookup（hit/miss/empty/Unicode）{dictionary['elapsed_ms']:.3f}ms、p50 {dictionary['p50_ms']:.4f}ms / p95 {dictionary['p95_ms']:.4f}ms / p99 {dictionary['p99_ms']:.4f}ms。

長さ0/1/2/3/6/16/64/256/1024/10000 UTF-16 units各10caseのCompositionText.Raw保存property: {sum(x['ok'] for x in strings)}/100 pass。ASCII、日本語、全角、emoji、combining characterと不正surrogateを含む。生成語のlinguistic accuracyではない。大量dictionary entry load/prefixと専用detector/composition/persistence benchmarkは残項目。

## Persistence / faults

13config fixtures＋7learning破損fixtures＋roundtrip/reset/readonly/tmp/設定roundtrip計25case中24pass。null entry例外をすべて記録。empty/malformed/truncated/wrong type/unknown/huge/negative/nesting/BOM/whitespace/large JSONはfallbackまたはnormalization。readonly保存では元file保持。temp leftover後も元学習＋新学習がreloadできる。

ACL拒否、強制process終了、simultaneous process save、rename失敗の独立fault注入は未実施。既存BackupTestsとUserDictionaryFileTestsの合格は上記の残項目を置き換えない。

## Differential changes

保存した元Core DLLに同じharness/corpusを接続して実行。10000 same、changed/improved/regression/unknown 0。baselineとcurrentの製品DLL SHA-256はともに `{core_hash}`。baseline/final既存Core Quality case-wise差も0。**同じ製品のrepeatability/test-only差分であり、production改善の主張ではない。** `differential.json` とMarkdown summaryにcase/category/input/context/baseline/current/classificationを保存。

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
|---|---|---|---|---|''')
for f in findings: md.append(f'| {f["id"]} | {f["severity"]} | {f["classification"]} | Yes in stated fixture | {f["title"]} |')
md.append('''
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
''')
gui_report = DOCS / 'GUI-RESULTS.md'
if gui_report.exists():
    md.append('\n---\n\n' + gui_report.read_text(encoding='utf-8'))
report = '\n'.join(md)
(ROOT / 'summary.md').write_text(report, encoding='utf-8')
(DOCS / 'RESULTS.md').write_text(report, encoding='utf-8')
print(json.dumps(dict(report=str(DOCS / 'RESULTS.md'), failures=len(records), new_findings=4,
                      grouped_findings={k:len(v) for k,v in groups.items()}, binary_unchanged=core_hash == original_hash), ensure_ascii=False))
