# Binary clipboard 原本と検証

固定 [WinMerge/frhed f952092530cc16e2f8832fc15dc6ba4b13cbc032](https://github.com/WinMerge/frhed/tree/f952092530cc16e2f8832fc15dc6ba4b13cbc032) の GPLv2 `BinTrans` 無改変関数から採取した634件（encoder263 / decoder371）。`manifest.json` は原本process、入力・goldenのSHA/長さ、MSVC原出力を保持する。原関数とrawの行/SHAは `provenance.json`。製品はMSVCや原版exeを使用しない。

`verify.py FIXTURE APP-OUTPUT` はPython標準ライブラリでraw/抽出関数の固定SHAと全634入力/golden/製品出力を全byte照合する。製品出力はE2Eの各別プロセス `--binary-bytecode-self-test` で取得し、検証器はgoldenを書き換えない。

採取範囲は整数b/w/l・d/h、literal、escape、全256byte、CR/LF、大小endian、幅切詰めと数値prefix。原関数のfloat/doubleは未初期化returnを含むため実行していない。製品は有限の `<fl:値>` / `<do:値>` を安全なIEEE表現で実装し、独立Python `struct` 値を別照合する。型/形式不一致・scanf失敗・整数overflow・50文字以上は変更前に拒否する意図的差異。

選択・repeat/skip・直接編集・clipboard公開の製品契約はUI E2EのPNG/JSON・独立保存byte照合で確認する。原codec634件だけでOSclipboardやこれらの機能を検証済みとしない。OS操作はGitHub clean runnerの別writer/readerで行い、利用者clipboardを置換しない。

`oem_reference.py` はWindowsの実ACP/OEMCPと有界1byte `CharToOemBuffA` を全256入力で独立採取する。literalだけを変換し、token生成byteとescapeを保持する製品経路を `oem_verify.py` とGUI保存byteで照合する。DBCSの不成立byteもAPI返値を記録し、失敗なら原入力と既存出力を保持して拒否する。macOSは明示1252/437互換値をPython標準codecで照合し、Windows OS APIの実測とは扱わない。原版の未定義動作は実行しない。

token認識は最大54byte先読みで有界にし、未閉鎖 `<bh:` の反復をsuffix全走査しない。50文字以上の数値prefixは閉じ括弧の有無にかかわらず拒否する安全上の意図的差異。1MiBの反復と長い数値は実GUI／CLI経路で出力全byte／原入力と既存出力保持を照合し、処理時間と処理量境界をrun内JSONへ残す。最大自己clipboardはraw16MiB＋header、text112MiB（Unicode224MiB）を両列挙順で実transferに注入し、Windows合成OEMあり／なし・終端・allocator余白の共通総予算も別に注入検証する。利用者OSclipboardは実行しない。
