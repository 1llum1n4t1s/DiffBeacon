# Independent Text Input Lifetime

Nine actual Button/Task regression cases: Compare gate + Cancel Close; Load gate + Cancel Close; non-token-sensitive injected picker + Cancel Close; successful internal Close for PPP/UUU/AAA/APU/UAP/PUA (Physical/Untitled/Archive). Synthetic UTF-8/no-BOM literal bytes and all stored ZIP entries are pinned in manifest.json; source/reader/LICENSE/C# are pinned in provenance.json. The fixture marker is public and must never flow to observation JSON.

Parent applies input324/hook-and-registration-proposal.json and uses the dedicated App selector:

```powershell
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test-independent-text-input-lifetime <fresh-output>
```

Read ui-report.json fixtures path, retain actual app PID from the external runner, and run:

```powershell
python -B tests/Fixtures/IndependentTextInputLifetime/verify.py --run <fixtures-path>/independent-text-input-lifetime --expected-app-pid <actual-app-pid> --output <fresh-reader-receipt>.json
```

At exact opener continuation, only original Task completion flags and actual array null counts are sampled. Continuations use ExecuteSynchronously/TaskScheduler.Default; no Avalonia controls are read there. Task completion capture sequence is retained as raw observations; completion status is the contract, since a continuation may be scheduled after another continuation. UI fields and parent display are inspected separately on UI after originals drain. Selection local and selection-owned outer/inner array references and pending browser local array references are observed internally; values never serialize. Candidate read clones and event-subscription identity are not directly inspected.

Each case retains PNG and partial-events.json from finally. Overall observations.json is saved in finally before deferred Check(false), preserving all nine baseline cases when checks fail. Baseline early opener completion stays a regression failure/exit2; this reader has no baseline-pass mode. Unexpected operation faults propagate and partial/missing evidence remains a failure. The injected picker is not a native OS picker cancellation test.

Fixture-only validation uses verify.py without --run. It cannot establish app behavior, actual process exit/stream completion, macOS or AOT qualification. Parent owns baseline/fixed execution and PID/exit/stdout/stderr receipts. No product run was performed by the worker. Regenerate into a new copy without existing generated input files; preserve originals and compare SHA first.
