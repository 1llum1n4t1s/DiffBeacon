# NormalizeUnicode の固定 native oracle

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の
`FilterExpressionNodes.cpp:2753-2806` と `unicoder.cpp:1301-1396` を無改変抽出する。
既定NFC、文字列・整数alias、form先行評価、無効formの短絡、型変換、浅い配列、
入れ子の文字列化、評価例外、結合文字・互換文字・補助面、UTF8/NUL境界の178件。
結果は170成功・8例外。公開 `.flt` へ投影可能な候補95件、native-only 83件。

`original/` は13個の固定原典。`provenance.json` に各sourceのupstream path、
Git blob ID、サイズ、SHA-256とlicenseを記録した。すべて固定commitのGit blobと
実byte比較済み。原典のPoco UTF8 decoder、ToStringValue、getAsString、
配列lifting、形式名のPoco ASCII大文字化、enum、登録arityも抽出する。
adapterの型・式node・例外注入は採取用であり、製品実装ではない。

Windows実 `normaliz.dll!NormalizeString` を原典loaderで取得し、取得失敗は採取失敗にする。
`UNICODE` / `_UNICODE` 経路を実行する。Poco Timestampは未使用のdead binding。
parser・Optimize、登録名の大文字小文字、実 `.flt` 実行、macOSでの正規化は未検証。
`public-flt-projectable` は後続の公開検証へ投影可能な入力・期待値という区分であり、
公開経路の合格を意味しない。malformed UTF8、NUL、regex/file参照binding、
非有限double、例外注入、arity probeは `native-only` に固定する。

観測した境界:

- 2引数では形式を先に評価する。無効なstring/int形式はNoneで値を未評価。
- double・None・bool・array等の形式はNFC既定を使う。整数2とdouble2.0は異なる。
- 配列の直下だけをliftingする。入れ子は `[item, ...]` へ文字列化後、正規化する。
- malformed UTF8はPocoでU+FFFFへ変換され得る。NormalizeStringが失敗すると
  変換後の文字列全体を保持する。U+FFFDへの一般的置換と同一視しない。
- NULを含む入力は長さ付き変換を通る。stdoutは文字列の全UTF8 byteをhexで保持する。

## 再採取

Windows、Python 3、既存MSVCを使う。新しいD作業領域の親を事前確認する。
出力先は未使用の絶対Dパスを指定し、repository/CODEX_HOME内とreparse祖先を拒否する。
作業領域の `TEMP` / `TMP` を子プロセスにも渡す。原典SHA/blob不一致も採取前に拒否する。

```powershell
$python = 'C:\Users\IMT\AppData\Roaming\uv\python\cpython-3.14-windows-x86_64-none\python.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
& $python tests/Fixtures/FolderNormalizeOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<new-run>\capture-a --vcvars $vcvars
& $python tests/Fixtures/FolderNormalizeOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<new-run>\capture-b --optimization O2 --vcvars $vcvars
& $python tests/Fixtures/FolderNormalizeOracle/verify.py --actual <作業フォルダー>\DiffBeacon\<new-run>\capture-b\reference.stdout
```

固定採取は `<作業フォルダー>\DiffBeacon\folder-normalize-oracle-20261020-r1` の
`capture-d` (/Od) と `capture-e` (/O2)。別processで再抽出・再build・再実行し、
全stdout byteが一致した。SHA-256:
`b85dbedef5b48be7b5b2fccf0a58a1dd3de73eb60dcf245064b69cf789d956c8`。
Windows 11 build 26300、user/system locale ja-JP、thread locale 1033、ACP/OEM 932。
OS情報、normaliz.dll SHA、コンパイラーと実行processのPID・生成時刻・実終了・
stdout/stderr EOF・終了コード・全抽出SHAは `capture-index.json` に保存する。
最初のcompile失敗と旧分類による採取は同indexで区別し、成功証拠へ混ぜない。

## 独立 reader

`verify.py` は製品/adapterをimportせず、manifest、全ケースの型・byte・string化、
form短絡・評価順・例外を照合する。通常のstring入力はPython Unicode databaseでも
独立正規化照合する。malformed byteのgoldenをPython decoderで再生成しない。
値byte・型・trace・status・ID・欠落・重複・順序の8改変を拒否する。
`--actual` はnative全stdoutのbyte完全一致も要求する。
`generate-cases.py` は入力だけを生成し、goldenを生成しない。
固定ファイルは近い `.gitattributes` の `* -text` で変換を防ぐ。
