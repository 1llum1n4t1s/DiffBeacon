# recontains の固定原典と公開境界

実装前に固定する失敗条件:

- string の途中一致と長さ0の一致を拒否する、または matches の開始固定／非空制約を流用する。
- Content を一つの文字列として検索し、CR／LF／CRLFを一致へ含める、行をまたぐ、最後の非EOL行を落とす。
- UTF-8／UCS-2 reader の cleared EOF buffer を落とす。空file／BOMだけ／空行／最終EOLの有無を混同する。
- 不正な定数patternを最適化時に拒否しない、動的不正patternを例外にする、処理上限・timeout・取消をfalseへ隠す。
- 配列の対応、none、notのbool集約、左右評価順、pane mask、原文snapshotを変える。
- 比較キーをContentとして検索する、pathを再読込みする、RAW TableとTextで原文を取り違える、workspace／HTMLで式を失う。

原典はローカルGitの固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の
`Src/FilterEngine/FilterExpressionNodes.cpp`、`FileContentRef.cpp`、`Src/Common/UniFile.cpp`。
既存Regex fixtureの同commitのPoco／PCRE2を使い、C++実関数の無改変spanを抽出して実行する。
期待値をPython／.NET regexで計算しない。C++ compiler／PCRE2は検証専用で通常buildへ追加しない。

encoding境界: 製品のContentは復号済み原文の不変snapshotとreader分類を使う。
UTF-8とUCS-2のreaderは最終非EOL行でもtrueを返し、次の空EOFも検索する。
legacy codepageのNONE readerは最終非EOL行でfalseを返すため、その場合だけ空EOFを検索しない。
この原典差をfixtureで別採取する。ANSIの自動判定、任意codepage、
不正Unicode、UTF-32の旧reader互換性、任意PCRE2構文、全Unicode casefoldは対象外。
製品では既存snapshot契約を維持し、検索用に外部pathを再読込みしない。

原典の追加4ファイルは `content-source.zip`（SHA-256 `492581a3e1803f4ec6f3a06b0cfeedf2574f25cd24d8182fff4698fb64b0c0db`）。
固定commitのGit blobをそのまま格納し、`LICENSE.md` のGPL-2.0と `UniFile.cpp` のMIT条件を保持する。
PocoのBSL-1.0、PCRE2のBSD／Unicode noticesは既存 `LineTransformRegex/reference-source.zip`
（SHA-256 `882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf`）に含まれる。

`capture.py` はBinaryOpNodeのOptimize／Evaluate、compute、NotNode、bool判定、serializer、Pocoのcompile／matchと
FileContentRef::REContains、UniMemFile::ReadString、UTF-8長・復号、maketcharの無改変spanを抽出する。
adapterは明示したbytes／encodingをメモリreaderへ注入し、BOMの除外とCP1252のWindows API変換を接続する。
実ファイルのOpenReadOnly／mmap／GuessEncoding／ReadBom全体、無効入力のlossy fallback、非Unicode buildは未採取。
NONE分岐の変換adapterは同じMultiByteToWideCharを使う有効CP1252だけで、任意codepage converterの同等性ではない。
未使用の日時・glob・Content等値・containsは呼ばれると失敗するstub。旧parser／whole optimizerの同等性ではない。

1140ケース（成功1081・例外59）を別directoryで2回compile・実行し、stdout全bytesが一致した。
golden SHA-256は `f328780edd93b12a1dc7db93c0b4bbe7f201a96209b2be0e59b569fec987ab98`。
`native-proof.zip` は抽出span、adapter、cases、compiler応答file・ログ、stdout／stderr、PID・birth time・
保持handleのsignaled状態・exit・両EOF・timeout・各SHAを含むreceiptを保持する。binary／object／展開PCRE2は含めない。
`verify.py` は固定fileset・全SHA／size／ZIP CRC、抽出byte範囲、token header、二回の全stdoutとreceipt、
CR／LF／CRLFの行body・Unicode／NONEのEOF検索traceをregex engineなしで独立照合する。

公開CLIは834ケース（成功key792・拒否42）。実Appの左transformとnative serializer由来keyを返す右transformを比較し、
JSONのdifferent=falseで間接照合する。typed value／keyの直接観測やnative例外class同等性ではない。
空fileとBOMだけは実行行がなくboolean keyを観測できないためnative専用とする。NONE matrixのASCII bytesは
製品がUTF-8として読むためnative専用、非ASCII CP1252の公開probeでreader分類を分ける。
Text／RAW Table、変換済みkeyと原文snapshotの区別、左右ログ順、pane mask、workspace複製／Text・Table HTML、
定数不正pattern時の既存HTML保持、動的timeout／共有処理・生成予算の拒否も製品契約として確認する。
全文生成、取消注入、GUI操作、同じcompiled planの複数寿命、Native AOT、macOS、全体E2Eの成功は限定結果から推定しない。

再現は外部の新規Temp出力を指定する。

```powershell
dotnet $e2eDll --app $appDll --python $python --output $newExternalOutput --line-transform-recontains-only
& $python -B tests/Fixtures/LineTransformREContains/verify.py
& $python -B tests/Fixtures/LineTransformREContains/verify.py --actual $publicObservations
& $python -B tests/Fixtures/LineTransformREContains/capture.py --output $newExternalTemp --compiler $cl --vcvars $vcvars64
```

`$publicObservations` はrun内 `fixtures/<run-id>/line-transform-recontains/public-observations.json`。
再採取はgoldenとの全byte一致を要求する。`--initial-observation` は初回採取専用。
`fixed-manifest.json` は自身以外の全fileのSHA／size、近い `.gitattributes` は `* -text` を固定する。

Poco既定のnewlineはCRです。string内のLFはdotで照合でき、LF直前のdollar／backslash-Zを末尾扱いしません。multiline caretは末尾CR直後のEOFに追加一致しません。dot／^／dollar／backslash-Z／extended commentとCR・LF・CRLFの44追加ケースを固定しています。Contentの行bodyはEOLを除き、この境界とは分けます。
