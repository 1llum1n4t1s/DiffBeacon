# ToDateTime / ToDateStr 固定原典

`native-functions-capture-closure.v9.zip` は原本の完全一致コピーです。SHA-256 は `10cccbccdb0fc0e47b2c19b2d18307b3c20793d255c7ef47640de4505a31c522`、サイズは1,008,350 bytesです。`fixed-manifest.json` のSHA-256は `0d0c57cb90449124bf0ac8f42765930cc8b3e6f079255d34de52168bc04e53f5` です。固定バイナリ・JSONのGit改行変換は、近い `.gitattributes` の `* -text !eol` で無効にします。

原典commitは `6780924ba35f7f55af0f6f14dd302561175830a6`、`FilterExpressionNodes.cpp` のSHA-256は `a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135` です。ZIPは52項目を持ち、内部の `reference-source.zip` に原典394項目と `licenses/` の原文を保持します。ToDateTime（2642–2660行）、ToDateStr（2145–2155行）、toLocalDateTime（1684–1691行）、applyToScalarOrArray（1693–1704行）の原文span、Poco解析・カレンダー・Windowsホスト経路、採取adapterも変更しません。利用・再配布条件は内包する各ライセンスを参照してください。

Nativeは元の2関数本体だけを、自然なLCID1041／ja-JP／Tokyo／Tzd32400で呼んだ記録です。2回とも121行、111本体を実行し、10件は事前に除外しました。ZIPは固定golden111件、raw UTF-8・型・Timestamp保存値・例外bytes、入力のcase plan、実PID／birth／job割当／OS exit0／両EOFのreceiptを保持します。過去の絶対パスは当時の証跡であり、readerの探索先には使いません。

除外10件は動的NUL2件、null配列2件、危険なToDateStr Timestamp2件、unsafe localeのToDateStr3件、ToDateStr引数なし1件です。goldenを作りません。測定済みのToDateTime引数なしは本体ではnoneを返しますが、公開arity1の検証から除きます。原典公開parser／AST／arity／Optimizeは未採取です。全59件のToDateTime本体は正常returnで、non-Syntax例外伝播の実測証拠はありません。leap-second UTC候補の実結果はToDateTime none／ToDateStr InvalidArgumentでした。

Timestamp serializer armがcatchして返すエラー文字列と、ToDateStr本体が伝播する例外を区別します。unsafe localeのTimestampは保存値のみが測定され、表示goldenがありません。空欄・除外・例外を空文字の日付へ置き換えません。

読み取り専用の独立検証は次で実行します。

```powershell
python -B tests/Fixtures/LineTransformTimestampFunctions/verify.py
```

readerはZIPを展開せず、外部Tempへの参照・Native compile/link/run・製品起動・一時ファイル作成を行いません。外側とmemberのSHA／サイズ、内包原典394項目のCRCと固定Nodes SHA／原文span、全receipt、raw121×2の固定欄一致、golden111とrawの入力／結果／例外／行番号join、除外10件を確認します。

`LineTransformTimestampFunctionScenarios.RunAsync` は既存の実アプリ別プロセスE2Eです。自然ホストが原本と一致する場合だけ、公開到達可能な110本体をText CLIへ投影します。他profileでは理由付きでNative期待値をskipし、期待値を再計算しません。Tableは両関数・型付きTimestamp・配列・不正入力の代表を確認します。本文の原文と期待キー、stdout／stderr／exit／receipt／入力SHA／既存HTML保持、選択理由をrun内へ残します。

公開投影は外部候補検証の `input-v2.json` と同じ境界を使います。Timestamp入力は `d"1970-01-01T00:00:00Z" + ToInt("符号付き64bit値")`、ToDateTimeが返したTimestampはepochとの差で保存値を照合します。通常整数literalには原典std::stoiの32bit制限があるので、大数literalで代用しません。配列は各要素のTimestampをepoch差へ投影し、none・順序・全要素を保持します。ToDateStrの文字列はNativeのUTF-8を直接期待値にします。有限アンカーでnormal／@noopt／動的Lineを、2件で実serializerのreturn bytesを照合します。

公開arity0／2、literal NUL拒否、unsupported配列、動的配列4097行の共有上限は、原典コード由来のengineeringです。共有上限は128要素の入力配列と変換後配列が累積1,048,576 slotsを越える実CLI拒否を確認します。invalidToDateStrはCLIのstdout空とstderr拒否理由を、report-projectでは既存HTML・入力・project JSONの不変も確認します。OS argvにNULを渡せないため、literal NUL拒否だけproject JSON経由です。

CLIは取消token、任意の小さなbudget、Windowsホストの欠落／失敗の注入を公開していません。これらとFoldのhost-event観測は外部公開Planのengineering24件、またはApp担当のheadless検証で扱い、Native互換認定に混ぜません。WindowsとmacOS以外では、日時環境未対応の拒否をCLI engineeringとして確認します。製品GUI・Native保存dialog・他profileの日時値をこのfixtureだけで検証済みにしません。

新しいNative採取や固定ZIPの再生成はこのreaderとE2Eの仕事に含めません。変更が必要なら原典固定・安全除外・自然profile・実OS終了と両EOFを伴う別の採取判断が必要です。日時literalのfixture／限定検証と、日時関数のfixture／限定検証は独立です。
