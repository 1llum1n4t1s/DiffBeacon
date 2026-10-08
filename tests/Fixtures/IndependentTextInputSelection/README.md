# 通常GUIの独立三側入力: 検証草案

所有はこのinput266だけ。製品・tests・docs・Gitの変更なし。失敗条件をfailure-spec.mdへ先に書いてから草案を作成した。

親への配置候補:

- `HeadlessIndependentTextInputSelectionChecks.cs` → `Src/DiffBeacon.App/`。
- `IndependentTextInputSelectionScenarios.cs` → `tests/DiffBeacon.E2E/`。
- `fixture/inputs`・`fixture/manifest.json`・`fixture/LICENSE`、`verify.py`、本書、failure-spec.md、coverage.md → 新規 `tests/Fixtures/IndependentTextInputSelection/`。
- `exact-edit-proposal.json` の18置換は親が適用する。7sourceのraw SHA/BOM/newlineと各exact-old一箇所を記録し、browser/dialog/mainの元実ButtonTask観測と、新しい限定selector/全体登録だけを追加する。

fixtureはsynthetic literalをPython標準ライブラリで生成した。3sideはUTF-8 BOM / UTF-16 LE BOM / UTF-8 no BOM、CRLF/LF/CRを混在させた。nested ZIPの全entryをmanifestへ記録。同root別leaf用ZIPと同size別SHAの差替え用inputを含む。製品outputからexpectedを作成していない。
runへコピーした全入力は初回browser/候補read/backup前および各caseのreset直後に `2024-01-01T00:00:00.1234567Z` へ固定する。差替えはcase別 `tamper/<case-id>/before.raw` / `after.raw` に両rawを残し、shared inputを次caseで復元しても証跡を保持する。readerはraw全byte/固定SHA/sizeと実 `stat.st_mtime_ns == 1704067200123456700` の両file一致を検査する。Ticksからnsを推定しない。固定sourcefixtureのmtimeは変更しない。
licenseはMIT、既存synthetic `tests/Fixtures/IndependentArchiveText/LICENSE` と同じ許諾を新fixtureへ同梱。WinMerge goldenの代替とは扱わない。
既存 `tests/Fixtures/IndependentArchiveText/fixed-sha256.json` の42pinsは読取り再使用し、manifestに原SHA・size・sourceとlicenseを記録した。固定reader/fixture/golden/既存assertionは編集しない。

今回実行したのはfixture-onlyの独立読取り確認:

```powershell
python -B artifacts/local/folder-threeway-next/text-next73/input266/verify.py --fixture artifacts/local/folder-threeway-next/text-next73/input266/fixture --repo .
```

親の配置/build後の別process検証候補:

```powershell
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test-independent-text-inputs artifacts/verification/input-selection
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/input-selection --independent-text-inputs-only
```

後者は実appを既存Run/MacCommandLauncher経路で起動し、stdout/stderr/終了証跡を保持してから別Python processへ `--gui-report <ui-report.json>` を渡す。新readerはscope `independent-text-inputs-only`、32exact case、原文全byte、全ZIP entry、route/pair/readonly、parent state/候補数/採用結果/既存output、PNG CRC/scanline/boundsを照合する。PNG目視は親が別途行う。
全体登録も提案したが、旧限定163/B5882295等のschema/reader/数を変更しない。新readerと既存B588 readerの合格は別々に報告する。

復元: input266内の編集前ファイルは `backups/` に保持する。同名の草案へCopy-Itemする。源は全てここで新規作成した成果物で、Src復元用backupではない。`source-reconciliation.md` と `coverage.md` を読んでから親が採否を決める。
Mac時刻修正前のrawbackupと元SHA/attributes/UTC timestamp/復元方法は `mac-time-review-backup/ledger.json` に保持する。修正後の静的receiptは `static-validation-mac-time-review.json`、fixture-only receiptは `fixture-verification-mac-time-review.json`。旧receiptは過去状態の証跡として保持する。
用途を終えた一時展開の直接削除は行っていない。input266は草案/fixture/再開に必要な成果物として保持する。今後の清掃はユーザー指定ごみ箱経路で親が実施する。
