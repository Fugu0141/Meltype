# Mozc responsibility-split IME experiment

This directory contains the native experiment that follows Meltype's
`language-anchor-v4` idea one level deeper, inside a Mozc-based IME.

The key change is **ownership** rather than post-processing:

- **Japanese**: owned by Mozc. Raw keystrokes are replayed to the normal Mozc
  session and Mozc owns composition/conversion.
- **Literal**: owned by Meltype. Proven English/technical spans are protected
  from kana conversion.
- **Open**: not assigned yet. A prefix such as `commi` is buffered until the
  next input proves `commit` or disproves it.

This lets the stream resolve incrementally:

```text
commi           -> Open(commi)
commitha        -> Literal(commit) + Japanese(ha)
networkmiru     -> Literal(network) + Japanese(miru)
githubdeissue   -> Literal(github) + Japanese(de) + Literal(issue)
node.           -> Open(node.)
node.js [flush] -> Literal(node.js)
de-ta [flush]   -> Japanese(de-ta)
```

Japanese is deliberately the default. Readable/ambiguous English such as
`repo`, `sushi`, `anime`, `same`, `tomato`, `go`, `make`, and
`red` does not become Literal without stronger evidence.

## Layout

- `src/responsibility_decoder.*` — pure ownership policy.
- `src/responsibility_runtime.*` — incremental buffering/draining state.
- `src/*_test.cc` — native regression tests.
- `src/boundary_mozc_bridge.cc` — console harness that routes Japanese spans
  through a real Mozc client/session and leaves Literal spans untouched.
- `Build-ResponsibilityIme.ps1` — copies the experiment into the pinned Mozc
  checkout, generates the native lexicon from Meltype dictionaries, patches the
  TSF private context, runs tests, builds the bridge, and builds
  `mozc_tip64.dll`.

## TSF patch in this stage

The current TSF patch is intentionally behavior-neutral. It adds
`responsibility_base_output_` to `TipPrivateContext::InternalState` and
snapshots the original `commands::Output` in `TipKeyeventHandler::OnKey`.

That fixes the incomplete previous experiment (accessors existed without the
backing state) and provides a rollback/source output for the next stage. The
next stage will wire `ResponsibilityRuntime` into `OnTestKey`/`OnKey` and
apply Literal/Japanese ownership to actual TSF composition.

## Build

From the Meltype repository on Windows:

```powershell
./native/mozc/experimental/Build-ResponsibilityIme.ps1 `
  -MozcSource C:\mozc `
  -Bazelisk C:\tools\bazelisk.exe
```

The build uses the commit pinned by `native/mozc/MOZC_COMMIT`. Generated and
patched files live only in the disposable Mozc checkout. Result binaries are
copied to `native/mozc/experimental/bin/`.
