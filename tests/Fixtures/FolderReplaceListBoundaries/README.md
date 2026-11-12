# FolderReplaceListBoundaries 補足native境界原本（未統合候補）

既存 `LineTransformReplaceLists` の70件を維持したまま、`ReplaceWithListFunc` と `RegexReplaceWithListFunc` の例外・部分bytes保持の境界を補足する10件です。固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の関数・Poco・PCRE2を無変更で実行した観測原本であり、製品や公開式の同等性を証明するものではありません。製品のbuild/test/実行は行っていません。

## 出典と無変更範囲

原典sourceは相対参照 `../LineTransformRegex/reference-source.zip` と `../LineTransformReplaceLists/additional-original`、source manifest・provenance・元adapterは `../LineTransformReplaceLists` を参照します。既存ZIPのSHA-256は `882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf`。既存ZIPの57 blobsとadditional-original 4 blobs（合計61）のsize・SHA-256・Git blob IDをreaderで照合します。

原関数は `Src/FilterEngine/FilterExpressionNodes.cpp` の1996行 `RegexReplaceWithListFunc` と2046行 `ReplaceWithListFunc`。Literal側はPoco `subst` を直接呼び例外を伝播し、Regex側は `regexSubst` wrapperが例外をcatchするため、置換途中bytesを返す場合があります。関数・wrapper・Poco・loaderの抽出spanは既存provenanceと全bytes一致し、元sourceの範囲SHA・行番号と照合します。adapter.cppも元fixtureから無変更です。

GPL-2.0、Poco BSL-1.0、PCRE2の著作権・ライセンス原文は既存ZIPの `original/LICENSE.md`、`original/Externals/poco/LICENSE`、`original/Externals/poco/dependencies/pcre2/src/` 各source headerに保持されています。原典ZIPとadditional-originalを保持して参照してください。

## 観測とadapter境界

- file list `TAB X LF` の空needleに対し、UTF-8 source `é`／`aé` はLiteralが `bad offset into UTF string` 例外、RegexがString bytes `58a9`／`5858a9` を返しました。ASCII `ab` のcontrolは両関数とも `5858`（XX）です。
- file list `a TAB X LF` とraw source `c3` はLiteralが `UTF-8 error: 1 byte missing at end` 例外、Regexが `c3` を保持しました。
- inline String pair `(a,X)` とraw source `c3` はLiteralがpairを無視して保持し、Regexはwrapper内の失敗をcatchして保持しました。両結果はString bytes `c3` です。

`cases.json`／`golden.ndjson` は採取済み補足10件のbytesをそのまま複製し、既存70件のgoldenとは混ぜません。例外caseのtyped value／serialized bytesはnull、通常caseはStringと実serializerのhexを保持します。Regex側の出力には不正UTF-8 bytesが残り得るため、Unicode文字列へ復号して比較しません。

raw sourceを表す追加bindingは生成 `cases.inc` の `new StringLiteral(bytes({195}))` だけです。native関数spanとadapter.cppは変えていません。unsafeな非array-list／null pointer listは実行していません。引数評価・AST・logger・Exceptionのhost型は既存adapterです。UniMemFile、encoding推定、環境変数展開は既存の明示UTF-8 adapterであり、原典OS I/O／任意codepage／path正規化の同等性を主張しません。file list・raw byte入力はnative境界の証拠であり、製品の公開literal式からの到達性や全例外の同等性は別途検証が必要です。

## 採取環境と証拠

既存MSVC `14.51.36231` を使用しました。最初のvcvars64だけのcompileはUCRT `ctype.h` 不在で停止し、既存SDK `10.0.26100.0` のINCLUDE／LIBとMSVC PATHを採取processだけへ明示して復旧しました。インストール・global環境変更はありません。失敗時compile receipt／stdout／stderr、成功時の実compiler SHA・rsp/cmd・SDK環境cmd・TEMP/TMP・child stdout/stderr・PID/birth/保持handle/exit/EOF/timeout/elapsedをproof内へ保持します。

二回の独立compile・別childの全80行stdoutが一致し、先頭70行は既存固定golden全bytes、末尾10行は本golden全bytesと一致しました。childはexit 0／stderr空／両EOF／保持handle signaled／timeoutなし。raw採取場所のD絶対pathは履歴であり、readerはその場所の実在を要求しません。source参照は `--repo-root` から相対解決します。

`native-boundary-proof.zip` は270 indexed entriesとindex自身を保持。ZIP SHA-256は `75ea1db305f68b6c6db657d91a365b742b42f7d865e6ee0802da9507a0ed9b0c`。重複PCRE2 sourceと再生成可能なobj/exeは含めず、固定原典とcompile receiptの全source SHAで復元・照合します。

## 独立検証

```powershell
& $python -B <candidate>/verify.py --repo-root D:/Dev/DiffBeacon --self-check-rejections
```

readerは固定fileset/size/SHA、ZIP exactset/重複/全entry SHA・size・CRC、原典source、全native span、元adapter、compiler入力、入力list bytes、両child receipt、80行stdoutと70+10行のbyte一致を検査します。`--self-check-rejections` は実原本の正常検証後、メモリー内でgolden／proof entry／receipt exit／entry欠落を改変し拒否を確認します。native再compile・製品実行は行いません。既定repo-rootは統合後の配置に対応し、外部候補では明示してください。

`.gitattributes` は近い既存fixtureと同じ `* -text`。まだリポジトリへ統合していないため、統合時の近い属性による上書きと実Git blobの原本SHA照合は未実行です。候補単独では既存参照fixtureを置換せず、原典ZIP・additional-original・既存70件を必要とします。