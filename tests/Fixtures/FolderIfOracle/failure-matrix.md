# 採取前の失敗matrix

case-plan.json を capture 実装より先に固定した。

| 対象 | 失敗として検出する挙動 | 期待 |
| --- | --- | --- |
| 条件全scalar型 | int/double/string/Timestamp/none/opaque参照をtruthy扱い | boolean trueのみtrue枝 |
| 条件配列 | 入れ子を再帰走査・空配列をtrue扱い | 直下のboolean true有無だけ |
| branch返却 | 型変更、配列平坦化・コピー | 全ValueType型・入れ子構造とshared array storage保持 |
| 評価順 | condition重複、両枝評価 | [0,1]または[0,2] |
| 未選択throw | 例外が漏れる | 非評価で正常返却 |
| condition/選択枝throw | 握り潰し・他枝へfall back | 元の例外と評価順を保持 |
| arity 0/1/2/4 | 引数評価後の拒否・受理 | 引数未評価で拒否 |
| arity 3 | 拒否 | 検証ブロック通過、引数未評価 |

opaque regex/FileContentRef はadapterでnull/non-null参照を提供する。実装内容やparserでの生成を証明しない。
