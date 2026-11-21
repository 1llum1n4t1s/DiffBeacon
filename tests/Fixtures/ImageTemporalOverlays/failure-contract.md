# 時刻依存 overlay 原本採取の失敗条件

採取前に次の反証対象を固定する。通常 C#／Native AOT 製品へはまだ接続しない。

- ANIM を一度だけの時刻で評価し、二者2回／三者4回の個別 blend read と後続 blink read の順序を失う。
- ANIM の時変 alpha に保存された静的 overlayAlpha を乗算する、整数境界を小数化する、中央二段 blend の中間 byte 切捨てを省く。
- blink OFF 半周期にも MarkDiff を行う、show=false でも blink の now を読む、overlay→highlight→wipe の順序を変える。
- Refresh を前回出力から累積する、原画 BGRA／分類を変更する、透明 alpha0 の hidden RGB を捨てる。
- mock queue の不足・余剰・負 epoch／不正period／pane／byteを受理する。
- ::std の型を追加・原本文字列を変更・実 clock sleep／OS pointer／clipboard 操作で採取する。

原本 class は adapter namespace の内側に無改変 excerpt として include する。
同 namespace 内 std::chrono facade の system_clock::now だけが queue を読み、戻り値は本物の ::std::chrono::system_clock::time_point とする。
標準ライブラリ namespace 自体は変更しない。無改変 excerpt SHA、canonical source SHA、2回 payload／固定 gzip 完全一致、独立 Python の全 BGRA を照合する。

分類 mask は原本の観測値を使用する。分類算法の新しい独立証明、GUI timer の周期・時間実測、OS デスクトップ操作、製品 Native AOT の対応は検証範囲外。
