# [P2] Coreのtaro誤判定はWindows spellchecker有効時には通過する

Classification: ENVIRONMENT_DEPENDENT
Finding ID: KNOWN-002
Commit: `467255bfe3e36b803a3fd3f5a1480fe35d5058c9`
Environment: Windows 10.0.26200 x64 / SDK 10.0.203 / runtime 10.0.7
Seed: 20261005

## 再現手順

合成データと明示的test output pathで実行する。

```powershell
dotnet run --project src/Meltype.Core.Tests -c Release -- --repro "my name is taro" enter
```

Input / 最小再現例: `my name is taro`

Context: Core built-in checker vs WindowsSpellChecker

## 期待結果

my name is taro

## 実際の結果

Core: my name is たろ / Windows quality: pass

## 証拠・頻度

- Frequency: existing quality 1 case; synthetic English derivatives 125 cases
- Relevant logs: baseline-core-eval.txt / baseline-windows-eval-unrestricted.txt
- Suspected component: OS-dependent spellchecker
- Notes: 新Issueとして大量提出しない。Windows/Coreの構成差。

製品コードは未変更。AIをテスト基盤の作成・実行と報告整理に使用した。GitHubへ未投稿。
