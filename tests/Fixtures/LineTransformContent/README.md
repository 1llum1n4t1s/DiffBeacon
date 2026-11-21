# Content / LineCount / Sublines / contains のスナップショット契約

`contract.json` は手定義の engineering 契約です。各ケースをopt／nooptの実App CLIで実行します。旧C++の実行結果を採取したgoldenではありません。JSONのbytesは `contract.sha256` と近い `.gitattributes` の `-text` で固定します。

根拠は `Src/FilterEngine/FilterExpressionNodes.cpp:1913-1949` のLineCount/Sublinesと、`FileContentRef.cpp:66-99,131-193` の行内検索・物理行抽出です。製品はpath再読込みを移植せず、Coreへ渡された比較前の原文を不変の内容値として扱います。空fileは0行、文字またはEOLのある行を数え、末尾EOLの後へ合成行を追加しません。Tableでも引用内改行を含む原文の物理行を数えます。

`Content contains "needle"` はsnapshotの各物理行を検索し、行末のCR／LF／CRLFを含めず、行をまたぐ一致を作りません。空needleはfalseです。式の`@cs`／`@ci`を尊重し、`@ci`は既存のscalar `contains` と同じ決定的なASCII大小文字foldを使います。fileを再読込みせず、行走査・文字比較を共有作業予算に課金し、取消も検査します。このcontains契約も手定義であり、旧Windows localeの大小文字変換goldenではありません。

`Sublines(Content,start[,len])` は0始まりで、負startは末尾から数えて0へclampします。len省略／非整数は-1、負lenは`remaining + len + 1`です。範囲外の正lenは残りへclampし、負の選択量は空文字列にします。整数極値でもoverflowや範囲外iteratorを使いません。選択行のCR／LF／CRLF、最終改行なし、空の実行をそのまま保持します。content／start／lenの順に全て評価した後でstartを型検査し、不正startと非Contentはnoneです。配列は直下一段だけに適用し、入れ子はnoneになります。Sublinesの結果は内容値ではなく文字列です。

比較キーは原文と別配列です。Sublinesを含む式の複数行結果、配列、文字列合成は一つのopaqueキーとして保持し、原文の行数・座標・EOL・保存本文を変更しません。実際にCR/LFを含むキーを受け取った後続ルールにもこの扱いを引き継ぎます。Content／LineCountだけを含む式では従来の生成CR/LF拒否境界を緩めません。旧file filterの生成行を物理行へ分割する処理と原行写像の移植を意味しません。共有作業量・出力量・配列slots・取消と、既存の最適化／lazy分岐の境界を維持します。

集計cacheは値とともに複数行キーを読んだ由来も保持します。原文`a\nb`で同じmaskの`Sublines(Content,0,1)`→`Minimum(Line)`を順に実行し、2行目のcache hitでも`a\n`を一つのキーとして維持します。nested Minimumと文字列／配列合成、実CLIのAll mask、実paneへの再比較も回帰確認へ含めます。nested集計のcacheに外側の式の読込み由来を混入させません。

内容値は通常stringと異なるinternal型です。原典の`<FileContent:path>`に対応し、元の絶対pathが渡された比較では`<FileContent:{absolute-path}>`、pathlessなsnapshotでは`<Content>`へ文字列化します。pathは表示値に使うだけで、fileを再読込みしません。`LineCount("<Content>")`と`Sublines("<Content>",0)`はnoneです。Prop、Contentに対する旧ファイル属性比較、任意fileの読込み、pathを引数にしたLineCount、一般`.flt` file filterの完全移植は対象外です。公開`LineTransformPlan.Transform(string[])`の直接利用では渡した行をLFで結合したsnapshotを使います。製品のText／Table経路は元の全文字列を明示し、元EOLを保持します。

限定実行は `--line-transform-content-only`、一般行変換限定／全体E2Eにも含めます。CLIは原文全行・行番号・入力bytes、逐次変換前のsnapshot、三者、project-copy／report-project／包装、二者のleft／rightおよび三者のleft／base／right各paneにおける実ファイルpath markerを確認します。headless UIは実dialog・比較・原文editor・workspace再読込み・PNG、pathless APIの内容値、left／right実path marker、共有予算と取消に加え、ancestor／independent FourPane materializationのopaque複数行キーと、行ごとに異なる複数行キーのpair境界を確認します。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output <fresh-external-output> --line-transform-content-only
py -3 -B tests/Fixtures/LineTransformContent/verify.py --actual <run-fixtures>/line-transform-content/observations.json
```

独立readerは固定JSONのSHA、全IDと式・命令、実stdoutの分類と原文全行・行番号、入力全bytes、paneごとの完全一致path markerを照合します。Sublinesの算法を再実装して期待値を生成しません。空文書では通常CLIが行式を評価しないため、0行の値は公開planを使うheadless確認と区別します。NativeAOT、通常desktop、ネイティブ保存dialog、macOS／他RIDは別資格です。今回のsource edit時点ではbuild／E2E／UIをまだ実行していません。
