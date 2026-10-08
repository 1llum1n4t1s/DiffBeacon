# 独立三側Text入力: Archive browser追加検証

`failure-spec.md` を先に作成したinput301の15case。通常GUI入口と実Load/Open/Back/Compareから通知された元TaskをPumpする。固定原本・期待値・readerの独立性は `coverage.md` を参照する。

既存CC0 [Sources README](../Archives/Sources/README.md) と原本SHAをmanifestへpinする。新siblings ZIPは既存暗号化inner containerの全bytesをそのまま二つ格納する。危険ZIPは../leaf.txtを持つ最小入力。本文期待値はreader内の独立literalであり、製品decoderの結果を期待生成に使わない。全てZipCryptoでPython標準libraryが復号でき、未復号の形式はこのsetにない。

```powershell
python -B tests/Fixtures/IndependentTextInputSelectionArchives/verify.py --output artifacts/input301/fixture-reader-proof.json
python -B tests/Fixtures/IndependentTextInputSelectionArchives/verify.py --gui-report <run>/ui-report.json --output <run>/reader-proof.json
```

生成は空のinputsとmanifestがない状態だけで `python -B generate.py`。通常検証で生成しない。上書きや自動削除はしない。source_license.txt、manifest.json、pins.json、近い.gitattributesで原本・出典・CC0・SHAを保持する。新fixtureだけのraw backupと復元手順はartifact/input301へ記録する。

親がsource freezeして製品build/runを行うまで、上記コマンドのfixture-only結果をGUI合格と扱わない。登録のexact-old-newと別process runnerの新file内容はintegration-proposal.json／IndependentTextInputSelectionArchiveScenarios.cs.proposalに保存する。
