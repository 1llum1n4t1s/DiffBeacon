# Folder Replace native oracle

固定 WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の **ReplaceFunc** を直接実行した269ケース。RegexReplaceFunc/ReplaceWithListとは別経路。158ケースは入力nodeにtyped Regexを含む。LineTransformReplaceにも少数のtyped nativeケースは存在するが、本fixtureはfolder移植のための独立した型/評価/bytes oracleであり、既存fixtureを変更しない。

全269ケースを各ケース別processとして、独立MSVC x64 build/runを2回採取（合計538 native case processes）。source/header、Poco RegularExpression ctor/subst/substOne/options、PCRE2 amalgamation、UTF8検査、replacement-token処理は固定原典の無改変body。型/throw node接続だけadapter。通常.NET buildへのC/C++/PCRE依存追加はしない。

## 固定した契約

- null args/arity!=3はNone・評価なし。有効arityはsource→from→toを先に評価し、その後from/toの型を検査する。
- fromのtextはescapeRegexでliteral化し、空textはNone。typed Regexは既にcompileされたPoco objectを受け取り、そのobjectの感度を使う。outer caseSensitiveはtyped objectを上書きしない。toはstring限定、空stringやraw bytesも渡せる。
- source NoneはNone、他scalarはgetAsString/ToStringValue。source arrayだけを浅くmapし、nested要素は文字列化する。from/to arrayはbroadcastしない。
- RE_GLOBALで全置換する。$0..$9は1桁group参照（$10はgroup1と文字0）。$$、$x、${name}はこの実装では文字のまま保持する。存在しない番号groupは空になり、参加しなかったgroupを参照する一部caseはstd::out_of_rangeを送出する。
- 空typed regex/zero-widthは空textと異なる。substOneはzero-widthで1 byte進め、その位置の元byteを保持しない。空sourceではoffset>=lengthで置換しない。末尾anchorによる一致はwhile(pos<len)の外なので挿入されない。これらを.NET/Pythonの通常replacementへ読み替えない。
- regexSubstが捕捉するPoco RegularExpressionExceptionはNone。typed Regex構築時のcompile error、引数throw、std::out_of_rangeは別のerrorType/errorPhase/errorHexで残す。
- 全33 malformed Substr出力をsource/text from/nonUTF8 typed fromとして採取。値はhexで保持しreplacement decodeしない。UTF8 typed Regexは不正sourceをNoneにするが、replacement自体のUTF8検査はしない。PCRE2の次回検査は開始offsetからmax_lookbehind分だけ戻した位置以降（pcre2_match.c:7163–7195）。したがって前方に作った不正replacement bytesを保持でき、text Replace(aba,a,ff)はff62ffを返す。これは全置換後のUTF8完全性を保証しない。

## 型接続と未検証境界

通常typed Regexは固定RegularExpressionLiteral constructor（RE_UTF8と独立した感度指定）で構築する。入力descriptorのlazy=falseはEvaluate前のbinding phaseでcompile、lazy=trueは引数Evaluate時にcompileする。phase/traceはこの明示adapter接続の証拠であり、旧parserの構築順やpublic .flt構文を実行したものではない。

追加のutf8=false descriptorはPoco constructorへoptions=0/RE_CASELESSを直接渡し、native variantが保持可能な非UTF8 Regex objectを表す。このbindingのpublic構文は主張しない。null shared_ptr<Regex>はdereferenceされるため呼ばない。Timestamp/FileContentの実構築・locale変換、全PCRE grammar/case fold/lookbehind/optimizationは未検証。今回のpatternはlookbehindを含まない。Python readerのre利用は固定制御patternのmatch位置/groupを独立照合するためだけで、Poco substOneのbyte処理/global loop/UTF8検査境界は別実装し、一般のPython replacementを期待値に使わない。

## 原典・ライセンス・属性

reference-source.zipは既存LineTransformReplaceの固定source archiveをbytes一致でcopyしたもの。全entryのsize/SHA/CRC、全53原典のGit blobと固定commitの対応をreaderが検査する。originalには無改変FilterExpressionNodes.cpp/h、COPYING、Poco-LICENSEを保持する。PCRE2の著作権・ライセンスはarchive内原典に含まれる。include-specs.jsonは全28 includeのsource byte範囲とhashを固定する。

root .gitattributesの `tests/Fixtures/FolderReplaceOracle/** -text` だけを追加。readerは全fixtureファイルの有効text属性がunsetであることと、`git hash-object --path ... --stdin`（-wなし）がraw bytesのGit blob値に一致することを確認する。stage/commitは行わない。

## 再採取・独立reader

Windows x64 MSVC developer shell、Python 3.13以降。Store aliasではなく実python.exeを使う。reproduce.pyは未使用D外部run、全親のreparse不存在、repo/CODEX_HOME外、空き容量を検査し、TEMP/TMP/source/obj/exe/log/raw/receiptをrunへ揃える。

```powershell
python -B -X utf8 tests/Fixtures/FolderReplaceOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run1 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderReplaceOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run2 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderReplaceOracle/verify.py --candidate-run <作業フォルダー>\DiffBeacon\<unique>\run1 --candidate-run <作業フォルダー>\DiffBeacon\<unique>\run2
python -B -X utf8 tests/Fixtures/FolderReplaceOracle/verify.py
python -B -X utf8 tests/Fixtures/FolderReplaceOracle/verify.py --run <作業フォルダー>\DiffBeacon\folder-replace-regex-oracle-20261020-r1\run1 --run <作業フォルダー>\DiffBeacon\folder-replace-regex-oracle-20261020-r1\run2
```

candidate readerを通した後にだけ新fixtureのgolden/index/manifestを統合した。通常readerは全型/値/trace/error、raw bytes、source/Git blob/属性、compiler/object/EXE、正確なargv、PID/birth、保持handle終了、exit、stdout/stderr EOF/hashと独立2runを照合する。既定readerだけでは外部raw証拠を検証した扱いにしない。再採取時は元goldenと一致を確認してからcapture-index/manifestを明示更新する。

初回証拠の保存先は<作業フォルダー>\DiffBeacon\folder-replace-regex-oracle-20261020-r1のrun1/run2。再検証用に保持し、別runへ完全移行後に清掃できる。今回削除/清掃なし。製品/E2E/managed public typed Regex構文は検証していない。
