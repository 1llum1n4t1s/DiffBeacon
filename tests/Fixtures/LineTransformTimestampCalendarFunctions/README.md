# startofweek / startofmonth / startofyear 固定原典

原典ZIPは byte一致の1本コピー。SHA-256 `1965de864574bf4c00da5484ba18e3dba6d459d9ceb1ec04906aa17a64dc8d21`、1,036,937 bytes、66 entries。固定manifest SHA-256 `e8feeccd3cb79435f0b4f90e944e60f99470ef993d6f5fc67cd8b4fa249aa08e`。元commit `6780924ba35f7f55af0f6f14dd302561175830a6`、Nodes SHA `a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135`。内包reference-source.zipの394 entry、原典span・adapter・licensesを保持する。再配布条件は内包ライセンスを参照する。

自然なWindows LCID1041/ja-JP/Tokyo/Tzd32400でraw126行×2、108本体、18除外。四childの実OS exit0・held job・birth・両EOFは固定receiptの証拠であり、今回再実行していない。全108のtypedGolden/body例外をrawへjoinする。serializerが返した14件のSystem文字列は、成功した負epoch Timestampの表示catchであり、本体例外にも正常日付文字列にも置換しない。

除外は各関数のnull-array/no-args/dynamic-NUL/int64-min/int64-max/unsafe1900の6件×3。全AST/parser/Optimize/zeroarity/NUL/nullarray/unsafe extremes/other profile/macOS/DST/最小浮動年はNative未資格。公開arity/NUL/共有予算はengineering。旧Literal/System serializer互換を変更する資料ではない。

`python -B verify.py` はZIPを展開せず、全closure entryのSHA/size/CRC、394原典entryのSHA/size/CRC、Nodes/span、4 receipt、全raw/golden/input/provenanceを照合する。絶対採取パスは証跡の内容としてのみ保持し探索しない。外部Temp・Native・実製品・依存・bytecodecache・自動削除を使わない。任意の実製品selection JSONを明示引数で渡せば母数と除外を照合するが、selection単独で全CLI結果合格とは宣言しない。

一般行変換の実アプリscenarioは `tests/DiffBeacon.E2E/LineTransformTimestampCalendarScenarios.cs`。全108本体はTextで保存epochまたはnone/配列要素を照合する。Table/report-projectは各関数・配列・parse/System例外・負epoch・serializerを覆う代表を照合する。全測定Stringはnormal/@noopt/runtimeLineの接続を照合し、Timestamp serializerは保存epochと別途照合する。配列直下/中途失敗、全原入力/project JSON/既存HTML保持、代表共有予算、選択Text/Table/workspaceのproject-copy再保存とreport再読込みを確認する。profile不一致は理由付きskip、期待値を再計算しない。macOSは対応ホストであり、Windowsと異なるNative profileというskip理由にする。missing-hostはWindows/macOS以外だけ。selected HTMLはdata-different=falseの分類と元の左右本文、復元したText/Tableの元本文保持を確認する。採用済みの製品検証実績と未資格の範囲は [移行記録](../../../Docs/MIGRATION.md) の一般行変換の週・月・年の初めを参照する。

フォルダー `.flt` の投影は `FolderPropertyFilterScenarios` の `StartOfValues` から `--folder-properties-only` で実行する。固定108本体（70成功／38例外）を二者／三者へ計216投影し、成功は `AllEqual(Array(actual,expected))` で型・epoch・直下arrayを照合、失敗は実CLIの終了2・空stdout・非空diagnosticで照合する。18静的除外はgoldenを生成しない。自然な固定profileに一致しなければ理由付きskipとし、別profile・DST・macOSへ原典資格を一般化しない。

`.flt` では既存の日時環境adapterと `LineTransformTimestampParser.StartOf` を再利用する。週だけがUTC時間軸で曜日分を減算してローカル日時を再構築するため、epoch近傍でWeekは例外、Month／Yearは `-32400000000us` を正常に返す。引数一度評価・大小文字・arity・短絡・metadata／Prop配列・全側で共有する採否・共有65,536予算・入力／filter／既存sentinelのSHAも確認する。一度評価は公開式の作業予算対照であり、Nativeのcallback／副作用注入資格ではない。非負Timestampは固定表示bytesも照合する。負epochの14 serializer観測は本体成功と分離して投影JSONへ記録し、既存 `.flt` のLiteral／System serializer契約は変更しない。

独立reader結果 `independent-startof-oracle.json`、全投影 `startof-projections.json`、実CLIのstdout／stderr、入力fixtureとassertionsは実行runのE2E出力先に保持する。固定ZIPとmanifestは原典・再検証用の正本であり、生成物ではない。Nativeの再実行・原典全AST／parser／Optimize・未採取profile／unsafe値はこの固定readerによって検証済みとはならない。

2026-10-20のWindows x64 `.flt` 採用検証はRelease build警告0／エラー0、`--folder-properties-only` が16,182成功／0失敗／2環境skip（5,311命令）。StartOf追加516命令・1,787チェックと全216原典投影（140正常／76期待例外）が成功した。run `<作業フォルダー>\DiffBeacon\startof-20261020-impl01` の別CLI readerはrawへのjoin、全JSON entry／側の採否・streams・exit・入力13ファイルのSHAも照合した。固定profile一致でStartOfのskipはなく、2環境skipは既存macOS FIFOとWindows case-sensitive distinct file。全体E2E／UI／Native AOT／他OSはこの限定結果に含めない。
