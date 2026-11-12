# Replace の固定原典と公開経路の検証

固定 Git commit `6780924ba35f7f55af0f6f14dd302561175830a6` の C++ Replace を実際に compile・実行した100件です。`cases.json`、`golden.ndjson`、`serialized-golden.ndjson` は採取 stdout／入力の bytes を変更していません。88件は function-body-noopt、12件は optimizer-selected-branch。serialized sidecar は93件で原典 `ToStringValue` を実行し、7件の原典例外には serialization を適用しません。PythonやC#の擬似 formatter はありません。

`reference-source.zip` を正式な唯一の再生成原本とします。53個の raw Git blob、31個の正確な抽出範囲＋外側LF、adapter・compile資料を格納しています。reader は全90 entry のサイズ・SHA・CRC、固定commitのGit blob ID／bytes、全抽出範囲と行番号を読み取り専用で照合します。struct／enum の終端semicolonを含む修正済み `provenance.json` を採用しています。旧metadataは採用していません。ライセンスは原本 `LICENSE.md` (GPL-2.0)、`Externals/poco/LICENSE` (BSL-1.0)、PCRE2 source headerのBSD条件、Unicode noticesをZIP内でそのまま保持しています。展開原本を重複して保存しません。

## 固定ファイルと採取の境界

`fixed-manifest.json` はそれ自身以外のこのfamilyの全ファイルのサイズ・SHA-256を記載します。入力、stdout golden、source provenance、初回producer原本script／README、実process receipts、実Core公開経路の凍結ZIPを含みます。producer metadataやreceiptsを期待値へ書き換えません。初回scriptは `capture-evidence/original-*.py` に原本のまま保持し、通常再現はportableな `capture.py`／`reproduce.py` を使います。`serialized-adapter.cpp` は原典serializerを追加実行するadapterだけの変更です。`serialized-wrapper-patch.json` に差分を記録しています。

旧whole parser／whole optimizer、typed regex、ReplaceWithListは製品の移植・検証対象として追加していません。selected optimizerのReplace分岐だけを原典のまま実行し、外側AST／所有権／文脈をadapterで与えています。Poco例外表示の全階層同等性、Timestamp／FileContentの実構築、旧LineTransform全体、provider／CSVはこの採取から主張しません。純粋Coreの予算・取消証拠は原典同等性とは別です。

## 独立reader

Python 3.12以降と、固定commitを持つcheckoutのGitを使用します。通常 .NET build にC compiler／PCRE DLLを追加しません。作業出力は必ず新しい外部Tempを明示し、repository／Codex home／名前artifactsのfolderへ出力しません。

```powershell
python tests/Fixtures/LineTransformReplace/verify.py --verify
python tests/Fixtures/LineTransformReplace/verify.py --actual <実Appの100行NDJSON> --public-plan tests/Fixtures/LineTransformReplace/public-case-plan.json
python tests/Fixtures/LineTransformReplace/verify.py --legacy-proof
```

`--legacy-proof` は以前に採取した純粋Core observerの実process証拠専用です。現在のAppの実行、E2E、GUIを証明しません。通常 `--actual` では省略されたcontext／source metadataを拒否します。`--public-plan` は凍結planとbytes完全一致が必要です。未登録fixtureのGit blob SHAは未採取です。`.gitattributes` の `* -text` と近い属性、raw worktree SHAを別に確認し、stage後のblob確認は親の担当です。

## App diagnostics 契約

全100 IDを原順序で一度だけemitします。行の必須fieldsは `id`, `classification`, `expression`, `context`, `sourceCommit`, `sourceSha256`, `originalHex`, `keyHex`, `sourceLineCount`, `rowIndex`, `status`, `errorPhase`, `exceptionType`。`context` は固定planのcontext object完全一致、`sourceCommit` は上記commit、`sourceSha256` はplanの `originalNodesSha256` (`A2FCCA950BD5E5CFA48F3FF43E4436AE0F5FD55EFF9661DA1D5A979CC9051135`)。expression・classification・入力hex・行数・rowもplanと一致が必要です。初期EOL=0、1行、row=0、EOF空行もこのcontextを維持します。optionalの人向けerror messageはoracleの例外文字列同等性を主張しません。

| 分類 | 必須結果 |
| --- | --- |
| oracle-match-candidate 78件 | status=ok、errorPhaseとexceptionTypeは空文字、keyHexは実C++sidecarのserializedUtf8Hex完全一致 |
| 公開式のある拒否10件 | status=error、exceptionType=ArgumentException、generated-crlf-boundaryだけevaluate／他はcompile、keyHex=originalHex |
| 式がnullの表現不能12件 | status=unsupported、errorPhase=unrepresentable、exceptionType=UnrepresentableAdapterType、keyHex=originalHex。compile／evaluateは未実行 |

表現不能IDは `arity-4`, `eager-throw-0`, `eager-throw-1`, `eager-throw-2`, `invalid-needle-eager-throw`, `regex-group`, `regex-zero-width`, `regex-empty`, `invalid-regex-rejection`, `invalid-utf8-subject`, `invalid-utf8-needle`, `raw-replacement-bytes`。これらを成功・skip・compile実行として数えません。原文keys、originalBytesと保持EOLの文脈を維持します。100件全部のoracle一致とは報告しません。dynamic source／needleはplanの非literalなLine入力文脈を維持します。

## 原典の再compile

現在検証済みのnative captureはWindows x64／MSVCのみです。既存MSVCを初期化したshellでは `--compiler cl`、未初期化shellではcompilerとvcvarsを明示します。自動インストール・ユーザー固定path探索はしません。macOS compilerによるcaptureは未検証で、scriptは停止します。

```powershell
python tests/Fixtures/LineTransformReplace/reproduce.py --output <新規外部Temp>/replace-native --compiler <cl.exe絶対path> --vcvars <vcvars64.bat絶対path>
```

2回独立compileしnative／serializedのstdoutをそれぞれ固定goldenとbytes比較します。個別再現は `capture.py --output <新規外部Temp>/native --compiler ...`、sidecarは `--serialized` を追加します。元capturesは上書きしません。今回の統合は保持済みの独立2実process receipts（PID／birth／held-handle終了／EOF／exit0）を再検証しており、追加のnative再compileはしていません。

## CaseFold の再生成

```powershell
python tests/Fixtures/LineTransformReplace/Generate-CaseFold.py --reference-zip tests/Fixtures/LineTransformReplace/reference-source.zip --output <既存の外部Temp>/LineTransformCaseFold.cs
```

`--reference-zip` は省略時もこのfixtureの正式ZIPを使います。UCD原本SHA `6EBDC9043DCB13832189A45818F767AFE9C4D6DF078FB37DB6D55D80126DCE92` を強制し、Unicode16.0／PCRE2 10.47の全0x110000 scalarの関係を検査して2938 relations／1484 pairsを生成します。Turkish特例は無効。全BSD headerを生成出力へ保持します。元採取のcompiler／PCRE実プローブは `casefold-generation.json` と親の保持証拠に記録し、再生成script単独を新しいcompiler probe成功とは扱いません。生成profileを直接編集せず、親が生成出力をCoreへ採用します。固定proposalとの差は再生成先を示す先頭commentだけです。licenseの通常／AOT binary同梱はCore側の担当です。

保持理由は移植の独立再現・原典同等性・ライセンス確認です。このfamilyは正式fixtureとして保持します。外部検証証拠の削除条件は同等の証拠が正式検証へ移行し、今回の再開・復元が不要になったときです。
