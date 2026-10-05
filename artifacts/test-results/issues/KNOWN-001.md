# [P2] 既存Qualityでもapinoerrorがあぴのerrorになる

Classification: EXPECTED_LIMITATION
Finding ID: KNOWN-001
Commit: `467255bfe3e36b803a3fd3f5a1480fe35d5058c9`
Environment: Windows 10.0.26200 x64 / SDK 10.0.203 / runtime 10.0.7
Seed: 20261005

## 再現手順

合成データと明示的test output pathで実行する。

```powershell
dotnet run --project src/Meltype.Core.Tests -c Release -- --repro apinoerror enter
```

Input / 最小再現例: `apinoerror`

Context: Core/Windows baseline

## 期待結果

apiのerror

## 実際の結果

あぴのerror

## 証拠・頻度

- Frequency: existing quality 1 case; synthetic derivatives 124 cases (one n-boundary derivative classified as oracle)
- Relevant logs: baseline quality logs / corpus.jsonl
- Suspected component: CompositionDetector
- Notes: 今回のregressionではない。既存Issueの有無を確認してから提出。

製品コードは未変更。AIをテスト基盤の作成・実行と報告整理に使用した。GitHubへ未投稿。
