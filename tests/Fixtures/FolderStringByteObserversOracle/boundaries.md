# Native string byte observers — 検証境界

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の固定Git blobをoriginalへ無改変copy。元は FolderStrSearchOracle と LineTransformSearch の固定原典。provenance.jsonに全source/license SHAとGit blobを保持する。通常.NET buildの依存ではない。GPLはoriginal/COPYING、Pocoはoriginal/Poco-LICENSE。

1226 typed cases。FolderSubstrOracle/golden.ndjsonの33 malformed出力（26種類のhex）をすべて採用し、元case IDとgolden SHAをmalformed-inputs.jsonに保持する。各入力に30 caseを割り当て、14 observer operationを実行する。各不正列をreplacement decodeして得た有効UTF-8列とも比較するが、このdecodeは対照入力の作成だけに用い、native入力/結果を置換しない。Len/Strlen、無効needle、None、入れ子配列、NUL、有効UTF-8、投機評価/short circuit/throw traceの対照54 caseを加え、さらに異なる不正列同士でreplacement decode結果が一致する14組へ13比較ずつ（182 case）を追加する。collision-inputs.jsonに元hex両側と共通replacementのUTF-8 hexを固定する。

| observer | 実行した原典/意味論 | TCHAR境界 |
| --- | --- | --- |
| AllEqual | valueEquals/AllEqualFunc、variant厳密型、std::string全bytes equality、配列再帰。firstがarrayなら残りの引数は未評価 | 変換なし |
| runtime Eq/Ne/Lt/Le/Gt/Ge | BinaryOpNode::Evaluate→compute。case-sensitive std::string byte比較、case-insensitive固定Poco icompare/ASCII lower | 変換なし |
| Len/Strlen | ucr::stringlen_of_utf8→Utf8len_fromLeadByte。先頭byte幅で前進、継続byteや切断をstrict検査しない。Lenのarrayは要素数、Strlenは浅いmap | 変換なし |
| StrFind/StrCount | 固定関数、byte位置と非重複一致、固定Poco toLowerはASCIIのみ | 変換なし |
| Substr | 既存oracleの出力bytesを新しいliteralとして再入力し、byte切出しを再実行 | 変換なし |
| ToString | 固定ToStringValue、配列の構造ASCIIを追加して内部bytesを保持 | 選択した型には変換なし |
| Plus | runtime string+stringのraw byte連結 | 変換なし |

case-insensitive orderingはMSVC既定signed charのPoco template比較を固定している（adapter static_assert）。非ASCII byteがASCIIより前に並ぶ場合があり、case-sensitive std::stringのunsigned byte比較順と異なる。Poco toLowerもUnicode case foldではない。Len/StrlenはUnicode scalar countの保証ではなく、C0/C1やF8–FDも先頭幅として受け、truncated leadは幅が入力を越えても1文字として数える。数え上げが成功してもvalid UTF-8の証拠にはならない。

入力/結果 `type:string` の `value` は生bytesのlowercase hex。None/bool/int64/arrayと混同しない。不正列とそのreplacement decode後の列はAllEqual/Eqで区別され、後続Substr/検索/連結/配列文字列化にも元bytesが残る。そのため置換復号だけのUnicode string modelはこの観測集合と同等ではない。最小のsound model候補は、通常文字列と同じ値種別にraw UTF-8 bytesを持てる表現と、各演算の固有byte意味論である。これは製品の採用判断ではない。

ToStringValueのtimestamp/regex/FileContent分岐やcomputeのregex/Glob等はadapterの実行拒否stubで、実装/同等性を主張しない。timestamp・locale・TCHAR変換の成否は採取していない。採取された例外はliteral probeの `probe-N` のみで、固定native関数が不正UTF-8を拒否したという証拠ではない。全1226 casesで転送処理のTCHAR変換は起きない。

parser/FunctionNode constructor/Optimize、複合式ASTの自動実行、folder loader/scanner、managed実装、公開flt、GUI/CLI、OS locale跨ぎを検証していない。Substrの連続実行は固定Substr出力のbytesをliteralとして再入力する境界であり、旧parserの合成式受理を意味しない。operatorはruntime bodyを無改変抽出して直接実行し、Optimizeによるconstant foldingとは区別する。

## 再採取とreader

Windows x64 MSVC developer shell、Python 3.13以降。実python.exeを使い、未使用のD外部runを2個指定する。reproduce.pyはD/CODEX_HOME/repo/linked ancestor/既存先/free-space guardを検査し、TEMP/TMP、obj/exe、raw stdout/stderrとreceiptをrunへ集約する。

```powershell
python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run3 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run4 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/verify.py
python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/verify.py --run <作業フォルダー>\DiffBeacon\string-bytes-oracle-20261020-r1\run3 --run <作業フォルダー>\DiffBeacon\string-bytes-oracle-20261020-r1\run4
```

verify.pyはcollector/managed codeをimportしない独立Python byte model。exact型/値/trace、全33入力の被覆、元Substr golden、source/Git blob、全fixture file、compiler/EXE/include hashes、正確なcompile/run引数、PID/birth、保持handle終了、exit、時刻、stdout/stderr EOFとhash、別2processとgolden一致を照合する。既定readerは外部process証拠を照合した扱いにしない。capture-indexは初回receipt SHAを固定し、再採取ではraw/goldenを照合してからindex/manifestを明示更新する。receiptは観測記録で暗号署名された環境証明ではない。

初回証拠は<作業フォルダー>\DiffBeacon\string-bytes-oracle-20261020-r1に保持。別runの完全照合とcapture-index移行後に旧runを清掃できる。最終captureはrun3/run4。collision対照追加前の予備capture run1/run2も作業証拠として保持し、最終fixtureの検証対象とは区別する。今回Git操作/削除/清掃なし。.gitattributesは依頼境界により作成も変更もしていないため、後にGitへ登録する際は原典/hex goldenを改行変換しない属性の判断とblob照合を別担当で行う必要がある。
