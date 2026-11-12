# Folder side-function fixture

This fixture tests `LineAt`, `LineOffsetAt`, `Column`, `ColumnRange`,
`ColumnAt`, and `ColumnOffsetAt` in the folder provider-null context. Names without
a prefix return two/three side arrays. `left`, `middle`, and `right` select the
native side indexes, including middle in a two-directory comparison.

`native-proof.zip` is the fixed, GPL-2.0 native proof, SHA-256
`ee9f13ee2e8de0684b376d97f8a1467a2591919f9ab1e0d2a49693c5c4e54a21`.
Its source manifest records original paths, Git blob IDs, byte extraction ranges,
SHA-256, and license. Constructor, Optimize, Evaluate, and six function bodies are
unchanged extractions. Both independently compiled native children produced 2,416
cases and stdout SHA-256
`b5382bd3f03302881ea0e8aed98b5840928a69b4cb6c94bf539a91c831d3304d`.
The ZIP preserves compiler receipts, exact inputs, executable hashes, child exit,
EOF, typed values, traces, and phases. The native adapter has no provider and does
not execute the product lexer/parser. License text is `GPL-2.0.txt` and the ZIP's
`COPYING`.

`native-classification.json` assigns every native input exactly once before any
product execution. Internal timestamp/regex/file values and controlled throw nodes
are native-only (368 inputs). The other 2,048 inputs have public notation for the
same operand values or rejection boundary, with `LogInfo` runtime probes where
possible. They do not claim native AST, exception-message, or public internal-phase
equivalence. Another 46 source-derived controls exercise default/@opt/@noopt,
e/de/fe applicability, case-insensitive names, lazy runtime/constant invalid ranges,
unknown aliases, middle in two sides, and skipped runtime invalid At children.
These 2,094 commands have distinct (side-count, expression, rule) tuples.
All 2,048 native projections explicitly use `@noopt` for native Optimize=false
and `@opt` for native Optimize=true, with `nativeOptimize` recorded and checked.
Unprefixed default optimization occurs only in the labeled source-derived controls.

`public-plan.json` is reconstructed by `projection.py` only from fixed native
inputs/golden and explicit source-derived controls. It never reads candidate Core.
`AllEqual(Array(actual, expected))` checks scalar/array, None/empty-string and array
length distinctions through public selection. It is not direct typed-value
observation. Literal Range children cannot emit a public runtime log while retaining
literal cache identity: cached/noncached child traces remain native-only. Runtime
LogInfo Range probes confirm per-side evaluation under @opt and @noopt, but are
not evidence of the literal cache. Constructor arity and invalid range rejection
diagnostics do not reveal internal phases through this CLI.

`required-unimplemented.json` retains the required native child-Optimize folding
gap for `@opt LineAt(ColumnRange(Substr('0',0)))`. It is not included as a passing
case, and reader summaries always retain `completeFeatureVerification:false`.
Resolving and verifying that gap is required before claiming complete parity.
Budget/cancellation injection, provider-present execution, and readonly output
protection are unexercised: this folder CLI reads inputs and has no output-file
option. Existing output sentinel retention is checked without implying a write
attempt. No new public alias or unsupported function is introduced.

Run from repository root, with fixtures linked under the binary's
`tests/Fixtures/FolderSideFunctions`:

```text
<absolute-python> -E -B -X utf8 <fixture>/verify.py --repo-root <repo>
<absolute-python> -E -B -X utf8 <fixture>/verify.py --repo-root <repo> --actual <work>/public-observations.json
```

The first command only validates retained proof/plan/source hashes and Python
expectations; it runs no compiler/app. The second additionally requires all actual
product child observations, every row and side's path/kind/filter/status/size,
input/filter/list/sentinel SHA, stdout/stderr SHA and diagnostic/log count/order,
actual exit, process identity, birth/launch/exit timestamps, elapsed duration, and
real platform. On Windows `CommandResult.LaunchEvidence` is null by design;
`Program.Run` waits for exit then awaits both stdout/stderr tasks before returning
the recorded result. This is source semantics, not separately instrumented cleanup
evidence. On macOS the native launch evidence must additionally prove Terminal,
ActualPid, raw exit, EOF, pipe release, disposal, and no cleanup issues. The reader
binds these platform rules to its actual `sys.platform`; a supplied Windows label
cannot bypass Mac evidence requirements.
The actual JSON and streams are written by `FolderSideFunctionScenarios` beneath
the run's `fixtures/folder-properties/side-functions`, never in the source fixture.

FolderPropertyFilterScenarios and the three helpers use the SDK default Compile items. The E2E project registers every fixture file as Content with its tests/Fixtures link and PreserveNewest metadata. The full folder-property route calls this suite after provider-null. The lists-only selector includes supplementary case-fold coverage and does not call this suite. Native full Optimize remains unimplemented: required-unimplemented.json retains the child-fold-before-provider-guard gap. Fixture preparation, static reader validation, and product runtime evidence are separate validation scopes.
