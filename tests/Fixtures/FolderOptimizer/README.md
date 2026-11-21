# Folder expression Optimize E2E 候補

この fixture は実アプリを別プロセスで呼ぶ公開 consumer の準備です。期待は managed 候補を実行する前に固定しています。`public-plan.json` は 5,304 件（原典実測の最初の値または公開失敗境界に結ぶ 5,208 件、source-derived 96 件）です。原典 6,756 件を公開 parser/AST と同等に測定した主張ではありません。

`native-proof.zip` は原典 revision-r3 の全 source/cases/二つの新規 compile・native 出力/r1・最終 r2 qualification を保持し、大きな中間 obj だけを除いています。原典 commit は `6780924ba35f7f55af0f6f14dd302561175830a6`。出力は二つとも SHA-256 `c2b28802f077519f92fde2d9665ef0a1ab5c986f20be59c21bba1fbbef8c6232` です。WinMerge GPL、Poco、PCRE2 の原典ライセンスは ZIP の source に保存されています。

`projection.py` は固定 cases/golden のみから計画を再構築します。内部 Probe、直接 ArrayLiteral/RegexLiteral、制御時計など 1,548 件は理由付きで分類しています。公開 Array 呼出し等による別 witness の可能性は残し、parser の旧型制約を除外理由にはしていません。consumer 包装では literal provenance や trace が同じとは限りません。

`semantic-expectations.json` は旧期待の変更根拠、原典行、native case ID と source-derived の区別、全 numeric/grammar 影響条件を記録します。`source-support.zip` の二つの元 C++ source は native 実行 closure とは別の source-derived 根拠です。`old-new-plan.json` は同一 predicate を旧/新 immutable App で対照する実行前契約です。

`runner-source-chain.json` と ZIP は元 Program 全 byte と候補 Program、whole-file diff、全 local function/record の token 同一確認、元 Mac launcher/gate の SHA を保持します。selector/dispatch の追加により Program 全 SHA は変わります。過去 R3 の originalProgram SHA を新 whole Program の実行資格として流用せず、新 build の実 App/runner SHA と OS producer 証拠を別途結びます。

限定実行は既存 `build/Dev.ps1` 経由で E2E に `--folder-optimizer-only --app <candidate02 immutable App DLL> --python <Python 3>` を渡します。`--output` は新しい D: run を指定し、TEMP/TMP/build/fixtures/results も同 run に集約します。既存 folder-property 実行にも helper を追加してあり、ProviderNull/SideFunctions の既存二つの呼出しは保持しています。

`verify.py` は ZIP 全 entry/CRC/SHA と source blob、r2 finite schema、実 OS process 終了、全 stdout/stderr、入力/filter/list/sentinel、dirs2/3 の JSON 全側を読む独立 reader です。`fixed-manifest.json` で fixture 全 file を固定します。`required-host-checks.json` の compile phase、型/AST、時計、list 二回評価/read 回数、GUI cancel/stale/dependency 保護は CLI では直接測っておらず未合格です。PCRE/LIKE/FileContent/provider-present/full parser の別資格をこの fixture で上書きしません。

準備時は C# syntax/Python AST/XML の静的解析だけを行い、App/E2E/new reader は未実行です。ビルド・実実行・旧新対照・host 専用 E2E の親 GO 後に結果を追記せず、出力 run へ別証拠として保存します。

Reader revision 02 は Mac 正常完了の process record 検査だけを追加します。元 `MacCommandLauncher.cs:54-69,124-129` に従い、OS birth の外側値との100ns一致、ready/gate の strict true、birth<=ready<=gate<=exit、32桁 lowercase hex nonce と既存V2 readiness nonce/PID行を照合します。旧 reader の5 controls/77変異と追加source-normal control1/29変異は旧成果物のまま保持し、旧controlのplaceholderとsource-normal controlを分けます。新reader校正は別成果物であり、Mac実OS/App/E2E実測や全schema保証にはしません。C#・Program・native ZIP・public plan・runner source proofは無変更です。

Reader revision 03 は独立レビューに基づき、正常 Mac producer の25個の `CommandLaunchEvidence` フィールドの必須集合・型を元 Program 宣言へ固定し、launch<=birth<=ready<=gate<=exit の100ns順序、AppPath の絶対性、DLL/native 分岐と row.arguments に基づく native/launch argv の関係、固定 gate suffix/SHA、grace5、identity scope を追加します。元 Program93 が app を FullPath にし、MacCommandLauncher10-23/28/45-69/124-129 が各値を構築します。caller Python の期待値と外部 App SHA は inner record だけでは認証できず、frozen runtime CLI と outer OS observer が担います。整合する別 App/Python tuple は source schema として受理することも校正し、実行対象を inner text で認証したとは主張しません。旧 V1/V2 合成 control の placeholder/tuple 不足は旧票・erratumを保持し、新 V3 control は別ファイルです。旧106変異に新tuple変異を加えて実関数を校正し、App/E2E/実Macは起動しません。C#/plan/nativeZIP/runnerproof は不変です。

Consumer revision 04: runtime03 の38不一致は公開包装の設計不備でした。旧 `AllEqual(expr,expected)` は expr が配列なら内部一致だけを調べ、expected を読まない原典契約です。native golden は変更せず、全 native-bound 5208 consumer を `AllEqual(Array(expr,expected))` にします。外側2要素から valueEquals の型・配列長・全slot再帰比較へ接続し、expr は一度だけ評価します。scalar も同じ包装で誤った配列の受理を防ぎます。derived の whole-array 比較も同じ契約に変更し、selected/rejected・件数・native分類は固定です。旧5304実行の成功集計は typed array 資格へ昇格しません。追加ノードは共有予算を使い、AST/trace/compile phase の同一性は未資格です。CasingValues の dynamic Name 4命令も source 固定の各側名配列と厳密比較します。Program69 local function/record は全 byte/token 不変、main185 は CasingValues1変更/残184token不変を別chainに記録します。reader03 の completion/finite Mac 検査本体は byte不変です。旧失敗票・旧校正・旧候補は保持し、新候補は build/App/E2E/reader CLI 未実行です。

Lexer preparation revision: doublequoted public strings use doubled doublequote and raw backslash, matching the fixed native lexer rules. Singlequoted strings and C#-style escapes remain DiffBeacon extensions. projection.quote explicitly marks CR/LF payloads Unprojected because .flt rules are physical lines; lexer-plan.json observes singlequote CR/LF escapes separately. Raw tab is permitted. The existing 5304 public-plan bytes and all native golden/proof/runner producer files remain unchanged; only LIST interpolation uses the revised doublequote spelling.

FolderLexerScenarios adds 21 folder-lexer-* process commands after the original public reader, with its own lexical-observations.json count. Fixed inputs include empty/doubled/raw backslash, quote-adjacent backslash, quoted/unquoted ##, singlequote extensions, date lexical/value distinction and unterminated rejection. Input/filter/sentinel hashes and rejection stdout-empty are required. Fixed native evidence is 125 source-expected lexer cases plus one NUL observation, not full native parser/value/extension qualification. No App, E2E or compiler was executed during this preparation.
Fixed SHA files are all protected by * -text in this preparation, including Python readers/projection and this README. Git attribute/blob measurement remains pending integration; no Git command was run here.
