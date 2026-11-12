# 採取前の失敗条件

- 原典commit/source SHA/blob・抽出範囲が不一致。登録名/関数/Windows旗の取り違え。
- D以外・既存出力先・repository/CODEX_HOME内・reparse祖先・D空き不足。
- Windows実LCMapStringExが取得不能、null locale以外の置換経路、build/run失敗・timeout・部分stdout。
- ToXFuncの一度の値評価、None、型変換、浅い配列、入れ子stringification、arity/例外が変わる。
- 全角/半角英数記号空白、カナ/濁点、ひらがな/カタカナ、中国語、互換文字/補助面、UTF8/NUL境界の取り違え。
- LCMapStringExの不正flagによる実API失敗で原文保持されない。NFKC正規化を期待値に流用する。
- 独立build/run stdoutが1byteでも異なる、ID欠落・重複・trace/型/byte改変をreaderが受理する。
- native-only入力・例外注入・API失敗probeを公開.flt実行済みと取り違える。OS/locale/ACP記録が欠落。
