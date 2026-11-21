# Independent Text Input Selection Critical

Synthetic literal inputs for 16 critical cases. No original WinMerge golden is claimed. `generate.py` creates flat stored ZIP roots with complete literal leaf/alternate entries, UTF-8 BOM / UTF-16LE BOM / UTF-8 without BOM, mixed CRLF/LF/CR. `manifest.json` pins every input and raw metadata JSON; `provenance.json` pins source, independent reader, LICENSE and C# check source. MIT license is included.

The independent standard-library reader imports no DiffBeacon/B588/D99 implementation. Fixture-only validation:

```powershell
python -B tests/Fixtures/IndependentTextInputSelectionCritical/verify.py
```

Parent registration adds the critical checks to the existing input-selection self-test and E2E reader pipeline. After an actual self-test, inspect its `ui-report.json` fixtures path and run:

```powershell
python -B tests/Fixtures/IndependentTextInputSelectionCritical/verify.py --run <fixtures-path>/independent-text-input-selection-critical --output <new-result-path>.json
```

Run evidence includes the actual opening/submission/browser/copy/save Tasks, literal saved bytes, readonly diagnostics, all three cached texts and pending-Hex applied bytes, callback lifetime and nonempty public marker field clear, parent before/after/mutation snapshots, PNGs, existing output, and before/after raw ZIPs. Both raw ZIP timestamps must independently stat to 1704067200123456700 ns; product ticks are not proof. Raw capture uses File.Copy and is never retimed. Fresh run sources are fixed before first reads, restored after tampering, and must retain their original SHA and actual ns.

Private credential arrays and subscriptions are not directly observable. The public fixture marker must not flow into observation JSON. Full six-direction copy/all side saves/workspace packaging, encrypted-password semantics, unsafe entry/depth/complete output-guard matrix, native picker/OS pointer, macOS runtime/AOT are separate. Reader status is critical-subset-only; fixture-only success is never product success.

Regenerate only into a new copy of this directory with no existing generated `inputs` or metadata. The generator refuses to replace inputs; preserve the current immutable fixtures and compare full SHA before considering replacement. The parent applies the required root .gitattributes `tests/Fixtures/IndependentTextInputSelectionCritical/** -text` proposal, then checks effective attributes and Git blob SHA; this worker does not stage or alter Git attributes.
