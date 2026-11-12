# Trim / TrimLeft / TrimRight の固定 native oracle

## 採取前の失敗条件

`failure-matrix.json` をcapture実装前に定義しました。arity、重複評価、例外・評価順、SP/TAB/CR/LFの正確な集合、None保持、scalar文字列化、直下arrayの浅いmap、nested arrayの非再帰文字列化、空文字・全空白・内部空白・NUL・Unicode空白の保持を確認します。

## 原典と実測

原典commitは `6780924ba35f7f55af0f6f14dd302561175830a6`。`reference-source.zip` と `source-manifest.json` は既存FolderAtOracleの固定7 raw Git blobs・ライセンス（GPL-2.0 / Poco BSL-1.0）を無変更コピーしました。Nodes.cppは `LineTransformArrays/original/FilterExpressionNodes.cpp` と全byte一致を確認しました。採取時にGit履歴や他fixtureは不要です。

`provenance.json` はTrim（2862行）、TrimLeft（2883行）、TrimRight（2903行）、登録（3855–3857行、全て1..1）、getAsString、ToStringValue、template宣言を含むapplyToScalarOrArray、ArrayFunc、型・AST宣言、constructor arity検査の無変更byte spanとSHA-256を固定します。実行対象の本体は原典から無変更includeします。大小文字正規化のconstructor initializerは証拠のみです。

`adapter.cpp` は空context、評価trace付きAST、型付きserializer、入力storageと3関数のdispatchを提供します。直接引数はtrace 0、ArrayFuncの子ASTは10/11/12。例外は原典関数の外側で記録します。arity検査はconstructor内blockのみを実行し、成功時のtrueはadapter sentinelです。

204件をMSVC 14.51.36231 / Windows x64 / Python 3.13で別compile・別child processで採取しました。両processの終了0、stderr空、stdout EOF、保持process handleのsignaledを確認しました。二回のraw stdout SHA-256は `e167284ea9525f4b7c235a4da005521463b7322adbf7f57517375385c01e3646` です。初回のtemplate抽出漏れcompile失敗も外部rootのrun1/run2へ保持し、修正後のrun3/run4だけを固定proofとしました。

case-planは配列で、各caseは`id`、`function`（trim/trimleft/trimright）、`mode`（evaluate/array-expr/arity）、`args`、必要なら`elements`を持ちます。引数は`{value:{type,value?},throws?}`です。goldenは各行`id,function,mode,result,error,evaluated`。resultは型付き値（none/string/boolean/integer/double/regex/content/array）、arity成功true、失敗nullです。

各関数68件で、型・文字列境界58件、引数例外1件、arity 0/1/2/3/16の5件、ArrayFunc成功と全例外位置4件です。文字列はASCII 0–32の端、SP/TAB/CR/LF、VT/FF、NBSP/U+2003/U+3000、日本語ではないUTF-8空白、内部改行、NULを含みます。arrayにはNone・scalar・入れ子arrayを含みます。

## 再採取と独立reader

captureは指定D root配下のfresh childだけを許可し、repository・CODEX_HOME・reparse・既存run・空き1 GiB未満を拒否します。compile、exe、obj、TEMP/TMP、stdout、receiptは全てrunへ出します。

```powershell
$python = (uv python find 3.13)
$fixture = 'tests/Fixtures/FolderTrimOracle'
$root = 'D:/Claude/DiffBeacon/trim-oracle-20261019-r1'
$vc = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC'
$compiler = "$vc/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe"
$vcvars = "$vc/Auxiliary/Build/vcvars64.bat"
& $python -B "$fixture/capture.py" --output "$root/recheck1" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/capture.py" --output "$root/recheck2" --compiler $compiler --vcvars $vcvars
& $python -B "$fixture/verify.py" --observed "$root/recheck1/golden.ndjson"
& $python -B "$fixture/verify.py" --observed "$root/recheck2/golden.ndjson"
```

`verify.py` はcapture・製品をimportしません。固定file全SHA・サイズ、ZIP全entry CRC、raw Git blob、byte span、二回のcompile入力・exe・process receipt・stdoutを検査し、Pythonの独立型変換・正確なtrim set・浅いmap・例外・評価順を全204件照合します。`--observed`は新しいnative stdoutとのraw byte一致です。製品出力のreaderではありません。`freeze.py --run1 <run> --run2 <run>` は初回固定専用で既存golden/proof/manifestを上書きしません。

`native-proof.zip`は二回のexe、compile全入力、stdout/stderr、receiptを保持し、再生成可能なobjを除きます。展開原本rootは `D:/Claude/DiffBeacon/trim-oracle-20261019-r1`。今回の親の清掃判断まで全run・作成scriptを保持します。ZIPから再検証可能になった後に正規清掃できます。

## 検証境界

これはnative原典の関数・helper・arity blockの隔離実測です。managed製品、parser、optimizer、公開`.flt`、loader、metadata、folder scanner、GUI、製品E2Eは実行しません。

Timestampの値/copy/destructor/epoch読取りは原典bindingですが、日時のToStringValue依存（LocalDateTime / DateTimeFormatter）は未使用bindingで、実行すると例外になるようにしています。Timestamp文字列化は未実測です。regex/contentは原典variantとToStringValueを通しますが、adapterのopaque token / UTF-8 pathだけを提供し、regex実行、content読取り、実object構築、OS文字コード変換を検証しません。非有限double、locale差、null array pointer、深度・容量限界、allocation例外は対象外です。Noneを不在metadataと同一視しません。
