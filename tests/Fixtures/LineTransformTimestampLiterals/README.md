# 日時リテラルの固定原本

`primary31/` と `guarded48/` は採取済みファイルのバイトコピーです。新しい Epoch・calendar・guard・golden を再計算しません。各 fixed-manifest の全入力、公式固定 commit 6780924ba35f7f55af0f6f14dd302561175830a6 の375 source、ZIP全394 entryのCRC/SHA、Git blob、raw span、二回のNative出力とprocess receiptを各 reader で確認します。

```powershell
python3.14 -B tests/Fixtures/LineTransformTimestampLiterals/verify.py
```

この既定検証は読み取り専用で、Native compile/link/run や `primary31/reproduce.py` を実行しません。safe30再採取は未実行です。絶対パスは forensic の当時の証跡で、reader の入力探索先にはしません。

primary31 の embedded-NUL は未定義動作を含む forensic 観測のみで、通常goldenから除外します。guarded48 の rawCtor12 は公開構文案なし、unsafeLocale3 はserializer未実行、NUL1 は公開拒否です。これらの空欄を空文字のgoldenに変換しません。

E2Eは安定する演算を source-derived engineering として検証します。Windowsの自然なユーザーLCID・locale・timezone state/name/bias・現在Tzdが原本と一致した場合だけ、guarded48 safe32 (24 serialized / 8 constructor error) を d/D、通常/@noopt で直接raw joinします。他profileはscope-qualified skip理由をJSONへ残し、期待値を再生成しません。公開lexer/C# serialized観測はNative parser/AST/typed観測を意味しません。host profileを保存DTOへ追加しません。

製品経路はCLI compare/table、単体HTML、project/workspace複製、包装・展開・再読込み、入力/既存出力保護を実アプリ別processで検証します。CLIとruntime拒否の種類は原本Native exception enumとの同一性を主張しません。
