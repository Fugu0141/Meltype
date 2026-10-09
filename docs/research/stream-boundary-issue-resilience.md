# Stream-boundary minimal integration: Issue resilience and TSF-first policy

2026-10-09. Scope: experiment on the Fugu0141/Meltype fork **only**.
Upstream PR #244 was closed without merge; do not create or reopen a PR
without explicit user approval.

## Integration priority: native Windows IME (TSF), not keyboard hooks

The prototype uses the existing Meltype stack:

- `native/tip/TextService.cpp` (Windows TSF input)
- `src/Meltype/Tip/TipServer.cs` named-pipe adapter
- `src/Meltype/Composition/CompositionService.cs` CreateSession
- `src/Meltype.Core/Composition/MeltypeSession.cs` HandleKey / SessionResult.ToJson
- `src/Meltype.Core/Composition/CompositionController.cs`
- `src/Meltype.Core/Composition/CompositionDetector.cs`
- `src/Meltype.Core/Composition/ScoredSegmentation.cs`

Both the legacy overlay keyboard mode and TSF sessions share the
`CompositionDetector` instance. `MELTYPE_SCORED=1` plus
`MELTYPE_STREAM_BOUNDARY=1` enables the experimental hint inside the
shared segmentation routine.

**Do not replace** the TSF DLL, composition control, the existing Romaji
parser or the Mozc/Windows conversion layer with the stand-alone
IncrementalBoundaryLab decoder. The current v1.2 research addition is
only an optional structured-code-boundary evidence generator, not a
whole input engine or complete Japanese morphology model.

### What has / has not been established

- Code inspection: `TipServer` creates `MeltypeSession` through
  `CompositionService.CreateSession`. The session and overlay use
  the same detector. Therefore the feature is wired into the shared
  managed core and **should be reachable** through TSF.
- A managed-core test probes `MeltypeSession.HandleKey`,
  `SessionResult.ToJson`, the composition view and a commit for the
  uppercase acronym `IME`. **This does not simulate the C++ TSF DLL**.
- Actual end-to-end Windows TSF input, native registration, focus moves,
  shortcuts, composition/candidate UI and host-specific behavior:
  **NOT verified** on the user's device yet.
- The managed IssueResilienceTests additions have **not been executed**
  by GitHub Actions on the fork as of this document. A passing result
  from the previously closed upstream PR #244 applies to an earlier
  revision only.

## Analysis: lowercase `ime` vs uppercase `IME`

Repository inspection:
- `dictionaries/english.txt`, `dictionaries/english-words.txt`,
  `dictionaries/propernouns.txt`, and
  `dictionaries/english-readable.txt` contain no standalone `ime`;
- Meltype recognizes its characters as standard romanization:
  `i` + `me` -> `いめ`;
- `CompositionDetector.IsEnglishSpan` requires language evidence.
  Three-letter romanizable words are deliberately conservative to avoid
  regressions where common Japanese reads become English.
- Capitalized abbreviations are treated differently; the explicit
  `IME` test captures this path.

The lowercase ambiguity is **not evidence of a boundary-method
regression** because the minimal research addition currently only
generates structured-code candidates containing `.`.

Do not add a global `ime -> IME` override or lower short-word
threshold merely to make this example pass. That can convert ordinary
Japanese sequences unexpectedly. Investigate an opt-in domain
abbreviation lexicon or user learning if the issue is widespread,
keeping `ime` and `IME` separately benchmarked. No dictionary
or product conversion rule was changed in this research commit.

## Public GitHub issues sampled by the diagnostic

The managed `IssueResilienceTests` records expected readings and
individually marks preview vs Enter output. It compares the same test
cases with PR #235 scoring only, and scoring + code boundary hint.

| Issue | Example | Scope |
| --- | --- | --- |
| #1 | `kyouhameetinggaarimasu` | mixed English |
| #12 | `feature` | English erroneously treated as kana |
| #65 | `anata` | Japanese wrongly treated as English |
| #77 | `reflectsareta` | English verb + Japanese |
| #79 | `ci` | romanization table, outside new hint |
| #92 | `qo` | romanization table, outside new hint |
| #104 | `moraltute` | Japanese wrongly treated as English |
| #129 | `AInituite` | acronym boundary |
| #130 | `50ccgenntuki` | numeric units |
| #153 | `hosuthingu` | false English in katakana word |
| #154 | `abctodef` | ambiguous English-Japanese-English |
| #207 | `pedia`, `protopedia` | unknown English typing preview |
| #218 | `hotelltu`, `totalltute` | kana parsing after English |
| #220 | `tabde`, `tabga` | correct preview breaks at commit |
| Research | `...node.js...`, `meltypega`, `toomoimasu` | new-method cases |

### Native TSF / OS regression cases not covered by managed tests

| Issue | Required on-device verification |
| --- | --- |
| #242 | TSF `/xyz` then Space then Japanese, without disabling conversion |
| #221 | 101+ uncommitted kana and candidate conversion using Microsoft IME |
| #95 | first character repeated in LINE / Qt-app input |
| #25 | editor composition visibility in Google Docs |
| #227 | commit followed by English not reinserted out of order |
| #243 | backtick/inline-code context; separate proposed feature |

A pass in a managed session is insufficient to close these cases.

## Reproduce without creating a PR

PowerShell from fork checkout `experiment/stream-boundary-core-minimal`:

```powershell
$env:MELTYPE_SCORED = "1"
$env:MELTYPE_STREAM_BOUNDARY = "0"
dotnet run --project src/Meltype.Core.Tests -c Release -- IssueResilienceTests

# Explore the original lower-case abbreviation in the same managed core
dotnet run --project src/Meltype.Core.Tests -c Release -- --repro ime enter
dotnet run --project src/Meltype.Core.Tests -c Release -- --repro IME enter
```

The issue benchmark sets the research hint OFF/ON via delegate;
global `MELTYPE_STREAM_BOUNDARY=1` is deliberately not used during
this comparison.

Test Windows TSF only after ensuring the native TIP DLL is registered
and the local Meltype.exe corresponds to the fork build, not a
simultaneously running separately installed version. Verify:
- `IME` capitalization and `ime` lowercase distinctly;
- `node.js`, `Node.js`, literal sentence punctuation,
  and Japanese surrounding text;
- candidate windows, Space/Enter commits, Delete/BackSpace,
  caret moves, focus changes, and redo/reconversion;
- #242 and #221 edge cases; latency p50/p95 and allocations.

No automated native-TSF integration claim until these are run.

## Safeguards

- Original feature remains **OFF by default**.
- Scored segmentation itself is also OFF by default on Meltype unless
  separately activated.
- Leave original keyboard/TSF logic, crash handling and Mozc intact.
- Benchmark both correctness AND per-key latency; do not describe
  a score-model boost as a production performance improvement.
