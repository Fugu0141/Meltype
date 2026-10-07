# Language Segmenter V2 experiment

This branch contains an experimental replacement for the English/Japanese
composition segmentation logic.

## Goal

Instead of greedily deciding whether each pre-tokenized romaji unit is English
or Japanese, V2 evaluates the original raw keystroke string.

That allows language boundaries to exist inside a legacy romaji unit. The
motivating example is:

- `commitha` -> `commit|ha` -> `commitは`

In the legacy tokenizer, `tha` is a valid romaji unit, so the desired boundary
falls inside an already-created unit.

## Algorithm

V2 builds candidate English/Japanese spans over raw character offsets and scores
whole-input segmentation paths with dynamic programming.

High-confidence `English + Japanese continuation` boundaries are resolved
before the general lattice when:

1. the English head has strong lexical/spelling evidence, and
2. the remainder is valid Japanese romaji beginning with a particle or common
   Japanese continuation.

Romaji-readable English matches remain ambiguous so words found accidentally
inside ordinary Japanese input do not automatically become English.

## Enable the experiment

The experiment is opt-in so the existing Meltype behavior and test suite remain
unchanged by default.

PowerShell:

```powershell
$env:MELTYPE_LANGUAGE_SEGMENTER_V2 = "1"
dotnet run --project .\src\Meltype\Meltype.csproj -c Release
```

Remove the environment variable or set it to `0` to return to the legacy
segmenter.

## Initial regression cases

V2 currently has explicit regression coverage for:

- `commitha` -> `commitは`
- `issuetateta` -> `issueたてた`
- `reflectsareta` -> `reflectされた`
- `inviteshimashita` -> `inviteしました`
- `githubnipush` -> `githubにpush`
- `commitshitai` -> `commitしたい`

Japanese controls are also included:

- `nihongowohanasu` -> `にほんごをはなす`
- `koreha` -> `これは`
- `repo` -> `れぽ`

## Status

The branch keeps V2 behind an opt-in switch while the scoring model is expanded.
The normal Windows test job and the cross-platform Core tests currently pass.
