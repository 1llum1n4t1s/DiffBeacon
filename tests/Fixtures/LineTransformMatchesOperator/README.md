# `matches` / `not matches` の固定原典

`golden.ndjson` は固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` のC++評価器、Poco、PCRE2をMSVCで別々に二回compileし、別childで実行した106件のstdout原本です。SHA-256は `1e656f041600072588ac168fc4ad546eb2abbea78ca7e762c36b6e17ef5598ff`。期待結果をPython／.NETのregexで計算したり、goldenを手編集したりしません。100件成功、6件例外です。

原本は既存の [LineTransformRegex/reference-source.zip](../LineTransformRegex/reference-source.zip)（SHA-256 `882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf`）と [LineTransformArrays/original/FilterParser.h](../LineTransformArrays/original/FilterParser.h) を再利用します。GPL-2.0はZIP内 `original/LICENSE.md`、Poco BSL-1.0は `original/Externals/poco/LICENSE`、PCRE2のライセンスはZIP内の原本に含まれます。原本を変更・複製しません。

`capture.py` は `BinaryOpNode::Optimize`／`Evaluate`、`compute`、`NotNode::Evaluate`、bool判定、serializer、regex literal constructor、Pocoのcompile／match本体をbyte範囲ごとに無改変で抽出します。固定token headerとadapterがASTを接続し、実PCRE2を実行します。`TryFoldConstants`はmatchesを扱わないため、adapterはmatchesだけでnullptrを返す接続です。日時・glob・内容参照・文字列の大小比較は未使用stubで、呼ばれたら失敗します。旧parserとwhole optimizer、これら未使用経路の同等性を実測したものではありません。

`native-proof.zip` は二回分の抽出span、adapter、cases、compiler応答file、compileログ、stdout／stderr、receiptを保持し、binary／object／展開したPCRE2原本を含みません。receiptはPID、birth time、保持handleのsignaled状態、終了コード、両EOF、timeout、compiler／実行file／stdoutのSHAを記録します。絶対パスは採取時のものです。`verify.py` は全entryのCRC、原本byte範囲、compile入力adapter、固定token header、二回のstdout全bytesと固定filesetを照合します。初回のadapter compileは不足していた標準headerを追加して修正し、その失敗はgoldenへ含めていません。

固定契約は開始位置固定＋`RE_NOTEMPTY`で最初の一致を得た後、その長さが全文と等しいか調べることです。`"ab" matches "a|ab"` はfalse、`"ab" matches "ab|a"` はtrue、空subject／空patternはfalse、`"a" matches "|a"` はtrueです。終端を正規表現へ追加して代替をbacktrackさせる実装と区別します。prefix／suffix／内部部分一致、greedy／lazy、zero-width、改行、capture／backref、UTF scalar、`@ci`／`@cs`、scalarとarrayの両方向、array同士の対応とnoneの余り、入れ子、none／非stringを含みます。`not matches` はarray各要素の反転ではなく既存のbool集約後の否定です。

不正な定数patternは最適化時に左の型や空arrayによらず例外になり、`@noopt`または動的patternではstring対stringのみfalseを返します。nativeの左右評価trace、左throw時の右評価抑制、非string左でも右を評価することを固定しています。旧例外class・error phase・prepared ASTはnative専用の証拠です。

公開E2Eは106件を実App CLIで実行します。成功100件はnative serializer由来の期待keyを別側のtransformへ渡し、比較JSONの`different=false`を確認する間接観測です。actual keyやtyped valueの直接取得ではありません。残り6件はexit=2、stdout空、stderrありを確認し、nativeの例外classと同一とは扱いません。動的値は`If(LineNumber>0,value,none)`、native整数は`ToInt`、throwはprovider依存のゼロ除算へ接続します。nativeのtraceやoptimizer内部観測の同等性は主張しません。公開ログによる左右順・定数不正patternの事前拒否、表、project-copy／report-project、既存HTMLの保持も別の製品契約probeです。

移植は既存のbounded regexで対応する構文に限ります。任意PCRE2構文、全Unicode casefold、補助面Han、raw不正UTF-8、PCRE2の実行制限例外と.NET timeoutの同等性は対象外です。製品は共有処理量・pattern長・100ms regex timeout・取消を維持し、上限超過をfalseに隠しません。GUI/headless、同じcompiled planの複数寿命、取消注入、Native AOT、macOS、全体E2Eの成功をfixture単独の成功から推定しません。

限定実行は新しい外部Tempへ出力します。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-matches-operator-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformMatchesOperator/verify.py
& $python -B tests/Fixtures/LineTransformMatchesOperator/verify.py --actual $publicObservations
& $python -B tests/Fixtures/LineTransformMatchesOperator/capture.py `
  --output $newExternalTemp --compiler $cl --vcvars $vcvars64
```

`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-matches-operator/public-observations.json`です。`--initial-observation`は初回採取専用です。通常再採取ではgoldenとの全byte一致を必須とします。C++／MSVC／PCRE2は検証だけに使い、通常buildや製品依存へ追加しません。`* -text`と固定manifestを維持します。
