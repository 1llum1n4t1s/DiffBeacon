/* GNU 原本は編集せず同一翻訳単位で読み込む。GPL-2.0-or-later。 */
#define GDIFF_MAIN
#include "../../Src/diffutils/src/analyze.c"
#include "../../Src/diffutils/src/io.c"

/* 原本の計算済み閾値を観測するだけで、分岐・入力・結果を変更しない。 */
int legacy_probe_too_expensive(void) { return too_expensive; }
