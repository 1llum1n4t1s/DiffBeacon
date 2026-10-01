# WordDiff 実アプリ E2E の契約と出典

## 先に固定する失敗条件

- inclusive の末端、空区間、短い側の入替え、中央一致の選択、隣接変更の結合を誤る場合は、終了コードと全 UTF-16 区間の完全一致で検出する。
- 大文字小文字、数字、空白、改行、区切り文字の無視が原文の位置を失う場合は、通常設定の文字／単語比較と明示フラグの組合せで検出する。
- 結合文字、半角濁点、絵文字、ZWJ、CRLF の位置や内容を失う場合も同じ golden と照合する。入力ファイルは BOM なし UTF-8 で生成し、各呼出後に全バイトが変わっていないことを確認する。
- 予算ゼロ、トークン過多、巨大な単語で黙って通常結果を返す場合は、明示された fallback 理由と全原文区間で検出する。
- 未知のフラグ、引数不足、負または 8,000,000 超の予算、不明な EOL／空白モードは exit 2、診断あり、成功 JSON なし、既存入力保持を期待する。
- 正常ケースで fallback する、UTF-8 の文字コード情報を落とす、差分ありでも exit 0 にする場合は失敗とする。

正本は `artifacts/verification/table-worddiff/contract.md`。実行ログ、終了コード、標準出力／エラーは既存 E2E の記録系に渡し、代表ケース一覧は出力先の `word-diff-selection.json` に保存する。単体テストは追加しない。

## Golden の由来

`legacy-worddiff-golden.json` は **旧バイナリ実測でなく元ソース実行** の結果。ルートの GPL ライセンスに従う旧 WinMerge ソースを原本のまま MSVC で実行した。GUI や旧配布アプリの動作保証ではない。

- 元ソース revision: `76ad8b97af7c9923d614b798af8a253543b1e459`
- Windows: `Microsoft Windows NT 10.0.26300.0`
- MSVC: `19.51.36260.0`、toolset `14.51.36231`、`/std:c++17 /EHsc /Y- /utf-8 /DUNICODE /D_UNICODE /W3`
- ICU: Windows SYSTEM32 の `icu.dll`、`72.1.0.4`。CRT locale: `C`
- 生成手順／完全なメタデータ: `artifacts/verification/table-worddiff/Run-LegacyProbe.ps1`、`legacy-probe.cpp`、`legacy-probe-metadata.json`
- JSON SHA-256: `458337D4F51C4F89D4E131F5DFBB7F0BA08956B0E31146D87B986BC541816121`

| 原本 translation unit | SHA-256 |
| --- | --- |
| `Src/stringdiffs.cpp` | `5D9593CF5A96C0964DBE9ED998CB47476A75A0392845334807AEDB542F7BF74D` |
| `Src/Common/UnicodeString.cpp` | `3B5943EBAFCF41FF83200A0BF54B95911B0F5EE4139B65F8CC8E5AE852FCBE20` |
| `Externals/crystaledit/editlib/utils/icu.cpp` | `91CC859B666C5EDEBE25472D7ABCC2AC9A8A8F93C56FEA42AF285CC3A12377B1` |
| `Externals/crystaledit/editlib/utils/string_util.cpp` | `288EB6CEDB8DC83E38A131EFB7A5EAEEB203F1994B6F21262E7ECBC9E4A781B6` |

全 28 入力 × 216 設定 = 6,048 ケースをそのまま保持する。`unpaired` の 216 ケースは TextDocument の UTF-8 decoder が replacement するため、原文区間の実アプリ照合は未実測として除外する。残る 27 入力の通常設定 × 文字／単語、入力名＋区間結果の distinct 代表 55 件、case＋numbers＋区切り／空白／EOL の 27 組合せを元配列 index で重複排除して実行する。golden の `[begin,end,begin,end]` は `[begin,end-begin+1,begin,end-begin+1]` に変換し、空区間の長さ 0 も保持する。

選択は golden 135 件＋fallback 3 件＋引数拒否 8 件。通常文字比較の 27 件はオプションを全省略し、CLI の既定値も確認する。さらに実際の `--compare`・`--report` を呼び、離れた二つの変更の強調と全行で共有する inline 予算を確認する。fallback は予算ゼロの `work-limit:separators`、過多トークンの `token-limit`、巨大単語の `work-limit:` を確認する。巨大単語の内部処理 phase は固定しない。

`classificationRanges` は原本実行で採取した全 65,536 UTF-16 code units の Windows CTYPE1 と CRT C 分類、`caseFoldPairs` は ASCII 26 組の折畳みである。.NET 分類との差は550文字あり、タイトルケース・上付き数字で実アプリの不一致を観測した。Core の固定プロファイルはこの採取値から生成し、実行時の Windows DLL 依存なしで各 OS に同じ分類を適用する。ほかの OS・CRT locale・旧配布バイナリ全般との一致は保証しない。

固定プロファイルは `uv run --no-project python build/Generate-WordCharacterProfile.py` で同梱 fixture から再生成できる。全65,536 code unitsの被覆・分類値・26折畳み・区間圧縮を検査する。改行横断の追加probeは `legacy-worddiff-golden-edges.json` に保持し、CLI区間と本文投影も照合する。NUL入力は原本では扱えるが現行TextDocumentはバイナリとして拒否するため、区間互換の実測範囲に含めず拒否と入力保持を確認する。

キャンセル、巨大 64 Mi 文字の text-limit、trace-limit、GUI／HTML の表示はこの別プロセスランナーの実測範囲に含めない。巨大単語は約 1 Mi 文字の原文に既定の work 予算を使い、明示的な work fallback を検証する。元ソースと fixture の照合は保持した SHA により確認する。実アプリのビルド／実行は親の統合検証で行うため、追加時点では未実測。
