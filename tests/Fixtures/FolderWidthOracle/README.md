# Windows 幅・文字種変換の固定 native oracle

WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の6関数を採取する。
`ToHalfWidth`、`ToFullWidth`、`ToHiragana`、`ToKatakana`、
`ToSimplifiedChinese`、`ToTraditionalChinese` が対象。
固定登録名・public spelling・ID・Windows旗・引数数は `functions.json` に記録する。
原典登録表に追加aliasは存在しない。public spellingの大文字小文字と登録名の対応は
記録するが、parserの名前解決をこのnative採取で実行したとは扱わない。

309ケース（285成功・24評価/arity例外）。公開 `.flt` に投影可能な候補174件、
native-only 135件。全角/半角英数・記号・空白、カナ/濁点・半濁点、ひらがな、
中国語、混在、無関係な互換文字、補助面、NUL、malformed UTF8、None、
typed stringification、浅い/入れ子配列、評価例外とarityを含む。

`FilterExpressionNodes.cpp:2808-2821,2832-2860` のToXFuncと6 wrapper、
登録3845-3853の該当行、`unicoder.cpp:1398-1475` のWindows経路を無改変抽出する。
getAsString・ToStringValue・浅い配列lifting・arity・Poco UTF8 decoder・ucr bridgeも
原典を抽出する。`original/` の9原典のsource path、SHA-256、Git blob、サイズ、
licenseは `provenance.json` に記録。すべて固定commitのGit blobと全byte比較済み。

## 採取境界

- 実Windows `LCMapStringEx` が利用可能であることを必須とし、原典のnull localeと
  関数ごとの旗を使う。normaliz.dllやNFKCで期待値を代用しない。
  幅変換では `ﬃ①ⅣÅ㎏㍿神` のような無関係な互換文字を一括分解しない。
- 原典は値を一度評価し、配列の直下だけを変換する。入れ子は文字列化される。
  NoneはNoneのまま。値評価失敗と引数数拒否は結果とtraceに記録する。
- native専用 `NativeInvalidFlags` wrapperは元ToXFunc/LCMapStringAutoへ
  `0xffffffff` を渡す。実APIの初回長さ取得が0、last errorが1004となり、
  元入力を保持することを3ケースで確認する。これは公開登録関数ではない。
- NULは長さ付き入力の全byteを保持する。malformed UTF8は原典PocoがU+FFFFへ
  変換し得るため、一般的なU+FFFD置換を期待値として流用しない。
- native-onlyはmalformed bytes、NUL、regex/file binding、非有限double、
  例外注入、arity probe、API不正flag probe。public-flt-projectableは
  後続の公開検証へ投影可能な候補区分であり、公開 `.flt` 合格を意味しない。
- parser/Optimize、public name lookup、Timestamp、XP fallback、APIの2回目の
  呼出しだけの失敗、macOS経路は未検証。利用者のlocale/AppDataは変更しない。

## 再採取・独立照合

Windows、Python 3、既存MSVCを使う。未使用のD絶対パスを指定する。
既存出力・repository/CODEX_HOME内・reparse祖先・空き不足は採取前に拒否する。
子processのTEMP/TMPとbuild/run/logはその出力先へ集約する。

```powershell
$python = 'C:\Users\IMT\AppData\Roaming\uv\python\cpython-3.14-windows-x86_64-none\python.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
& $python tests/Fixtures/FolderWidthOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<new-run>\capture-a --vcvars $vcvars
& $python tests/Fixtures/FolderWidthOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<new-run>\capture-b --optimization O2 --vcvars $vcvars
& $python tests/Fixtures/FolderWidthOracle/verify.py
& $python tests/Fixtures/FolderWidthOracle/verify.py --windows-api --actual <作業フォルダー>\DiffBeacon\<new-run>\capture-b\reference.stdout
```

固定採取は `<作業フォルダー>\DiffBeacon\folder-width-oracle-20261020-r1` 配下の
`capture-a` (/Od) と `capture-b` (/O2)。別processで再抽出・再build・再実行し、
全stdoutがbyte一致した。SHA-256:
`8a397b6857cc5113b80f7d212f38846a97d33ca35828876fae0e9b92863ec6a0`。
Windows 11 build 26300、user/system locale ja-JP、thread locale 1033、ACP/OEM 932。
`capture-index.json` にOS/locale/ACP、kernel32 SHA、実API失敗probe、
build/run processのPID・生成時刻・実終了・EOF・終了コード・全抽出SHAを記録する。

`verify.py` は製品/adapterをimportせず、manifest、全byte・型・string化・
trace・例外・登録ID対応・配列形状を照合する。値byte・型・trace・status・ID・
欠落・重複・順序の8改変を拒否する。`--actual` はstdout完全一致も要求する。
`--windows-api` はPython ctypesから別に実LCMapStringExを呼び、
有効UTF8・型変換・配列を261値で独立照合する。malformed UTF8は固定native証拠で
照合し、Python decoderによる再生成をしない。`generate-cases.py` は入力と登録情報
だけを生成し、goldenを生成しない。近い `.gitattributes` の `* -text` を維持する。
