# 採取前の失敗条件

- 原典commit/source SHA/blobが不一致、抽出範囲が変更されている。
- D以外・既存出力先・repository/CODEX_HOME内・reparse祖先・D空き不足。
- Windows normaliz.dllの実NormalizeStringを取得できない、build/run失敗・timeout・部分stdout。
- form先行評価、無効string/int formのvalue未評価、None/誤型formのNFC既定が崩れる。
- 4形式/alias、scalar変換、浅いarray lifting、nested stringification、UTF8/NUL境界が原典と異なる。
- 独立build/runのstdoutが1byteでも異なる、case欠落・重複・trace/型/byte改変をreaderが受理する。
- native-only入力・例外注入を公開.flt実行済みと取り違える。OS/localeと採取境界が記録されていない。
