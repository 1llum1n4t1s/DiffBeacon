# 実行経路の検証
## フォルダー関数と補助平面の固定検証

`--folder-replace-lists-only` は既存リスト検証と [補助平面case-fold](../Fixtures/FolderSupplementaryCaseFold/README.md) の310命令へ到達します。`--folder-properties-only` と全体E2Eはさらに [provider-null](../Fixtures/FolderProviderNull/README.md) の864命令、[側別6関数](../Fixtures/FolderSideFunctions/README.md) の2,094命令を含みます。最適化の固定検証はoptimizer 5,304件（native 5,208＋source 96）とLexer 22件、fixture Contentは67件です。CF public 310件はdirect 144＋list 144＋derived 22をstrict outer Arrayで照合し、実測出力から期待値を生成しません。

`--folder-optimizer-only` の固定期待値のうち、`StartOfWeek`／`StartOfMonth`／`StartOfYear` とゾーン指定のない `ToDateTime` 文字列は、ローカル日時をUTC+9（Tokyo、夏時間なし）で解釈して採取しています。他のタイムゾーンでは期待値と一致しないため、CIはWindows runnerを `Tokyo Standard Time` に切り替えて実行します。ja-JP／LCID1041まで要求する暦関数スイートは、CIでもlocale不一致として理由付きでskipします。固定readerは履歴上の原典コミットのblobを照合するため、CIは全履歴をcheckoutします。

`--folder-side-functions-only` は `--folder-properties-only` の部分集合で、側別6関数の固定reader・2,094命令と下記の子Optimize fold checkだけを実行します。provider-nullの固定readerは、E2E runner（`Program.cs`）の全体SHAと行番号を照合せず、待機・stream・時刻記録のspanが改行をLFへ正規化した本文にちょうど1回あることを確認します。runnerの他の変更で固定記録を付け直す必要はありません。

側別6関数の固定原本に残した子Optimize fold条件（`required-unimplemented.json`）は、固定原本を書き換えずに別checkで二者／三者の各4命令を実行します。固定式の`@opt`と接頭辞なしは原典`FunctionNode::Optimize`の子先行foldと`ParseColumnRanges`に従い、終了2・空stdout・`invalid column specification`で拒否されることを確認します。`@noopt`と有効な列指定へ畳まれる`Substr('1',0)`の対照は受理され、全行が除外表示になることを確認します。いずれも入力とフィルターのSHAを保持します。固定readerの`completeFeatureVerification:false`は変更しません。

外部候補の限定実測はHostProperties 1,104 checks成功、Optimizer／Lexer全5,326命令・20,951成功／0失敗／1 scope skip、ReplaceLists 542命令・2,585成功／0失敗／0 skipです。独立readerの資格、Host専用scope skip、Lexerに別独立readerがないこと、Dateのfixed-offset／年境界と未認定範囲は [MIGRATION](../../Docs/MIGRATION.md) を参照してください。正本適用後のbuild・全体E2E・全体UI・Native AOT・macOSは未認定、既存full Nativeの600秒timeoutも未解決です。全parser／AST・provider-present・40 field全体の移植完了や、過去R3の成功票の流用は意味しません。限定実行を全体E2Eの代替にはしません。

## フォルダーコピーの公開と外部リスト依存

`.flt`関数本体は`--folder-replace-lists-only`、一般行変換のリストは`--line-transform-replace-lists-only`で限定実行します。

`--folder-copy-only`は、別タブがコピー先を置換リスト依存として公開直前に採用する場合、依存採用を公開区間からqueueする場合、取消、公開失敗、新規親ディレクトリ、空ディレクトリの6ケースも実行します。最終検査と変更を同じ同期UI区間へ渡し、前者は元bytesを保持して拒否、queued採用はatomic公開後に完了することを実ボタンで確認します。`folder-copy-publication-observations.json`へ順序・SHA・日時・staging清掃を残し、独立readerが実bytesと空ディレクトリのstatを照合します。PNGは固定layout readerの予約名と分けた`folder-publication-*.png`です。

2026-10-23のWindows x64 Release限定E2Eは156成功／0失敗／1 Mac専用skip、78命令。管理者STAのGUI子processは1,127成功／0失敗、既存依存5件・出力保護12件・公開6件のreaderと固定FolderSync readerも合格しました。証拠は`D:\Claude\DiffBeacon\folder-publication-20261023-r1/e2e-final-r2`。全体E2E・全体UI・Native AOT・macOS実行は別工程です。旧r1のPNG接頭辞衝突による固定reader失敗は保全し、名前修正後のr2成功へ読み替えません。

## フォルダー `.flt` の `RegexReplace`（2026-10-20、Windows x64 Release限定E2E済み）

`RegexReplaceValues` は[固定Regex原典](../Fixtures/LineTransformRegex/README.md)の107 IDを分類し、公開可能な74本文（50 String／17 None／6 Array／1本文例外、NUL3件を含む）を二者／三者へ148投影します。除外33件はoptimizer14／typed Regex4／raw FF・FE byte binding3／null args1／arity5／Throw6です。例外型や診断本文の一致は主張せず、本文例外はmanagedの失敗段階へ照合します。期待値はgolden UTF8Hexから独立readerが再生成します。

`RegexReplace(source, pattern, replacement)` は厳密3引数で、source→pattern→replacementを左から一度ずつ評価します。patternとreplacementはStringのみを受け付け、sourceはscalarまたは一階層のArrayへ適用し、nested値はnative文字列化します。全置換のmutable-subject loopで`$0`～`$9`／`$10`／`$$`、未参加capture、zero-width／Unicode／case指定を扱います。入力source／patternの不正UTF-8はNoneです。timeout、共有予算超過、未対応grammar、未参加captureのエラーはNoneへ隠しません。

公開Substrで作れるC3 replacementは実match後にだけ可逆なbyte表現へ変換します。raw-invalid replacementはpatternが空・通常literal・escaped regexp punctuationだけと確認できる場合に限定し、group／class／quantifier／assertion／word-boundary等を持つpatternは最初のmatch時に明示unsupportedとなります。matchなし／空source／None sourceではreplacementを解釈しません。一般のPCRE max-lookbehindは推定しません。

`RegexReplace('aba','a',Substr('é',0,1))` のC3 62 C3期待値は、[FolderReplaceOracle](../Fixtures/FolderReplaceOracle/README.md)のnative FF 62 FF結果と共通`regexSubst`／Poco再探索位置から導いたsource-derived投影です。C3の新native採取とは扱いません。3 byteごとのSubstrと終端emptyで検査し、lead-byte幅を使うStrlenをbyte長に読み替えません。raw replacementの末尾／複数match／no-match／None／Array／escaped literal／欠けたgroup tokenも確認します。

Windows x64 Releaseの`--folder-properties-only`は6,651 CLI command、21,963 assertion成功／0失敗／2環境skipで完了しました。RegexReplace独立readerは278 command・148 native projection・期待エラー48件・入力保持295件を照合しました。`--line-transform-regex-only`は359 command、1,056 assertion成功／0失敗／0 skipです。resource検査は65,536共有予算、literal pattern準備の直前prefix、source／Array／反復探索／出力／複数呼出し、100ms timeoutを含みます。

2 skipはmacOS実FIFO検査と、Windows標準filesystemで大小文字だけ異なる別名を作る検査です。native optimizer／typed Regex／FF・FE raw byte binding、一般raw pattern／lookbehind互換、公開取消注入、GUI、Native AOT、macOS、全体E2Eはこの限定実測の範囲外です。`--folder`に永続出力引数はないためsentinel保護は主張しません。出力と独立readerの結果は外部D runの`folder-properties/regexreplace`へ保持します。

## フォルダー `.flt` の `RegexFind`／`RegexExtract`（Windows x64 Release限定E2E済み）

`RegexPositionValues` は[固定Regex原典](../Fixtures/LineTransformRegex/README.md)の122ケース／関数を全IDで分類し、公開可能な94本文／関数（Findは52 Int64、Extractは52 String、共通33 None／4 Array／5本文例外）を二者／三者へ376投影します。各28件の除外理由はoptimizer 11、typed Regex 4、raw invalid UTF-8 2、null args 1、arity 3、Throw 5、NUL 2としてmanifestに残します。partition・観測・goldenから再生成した期待manifest・reader結果は`folder-properties/regexfind`／`regexextract`に保存します。

`RegexFind(source,pattern[,group[,occurrence]])` と `RegexExtract` は2～4引数を受け、source→pattern→group→occurrenceの順に一度ずつ評価します。group／occurrenceはInt64のみで、group型不一致ではoccurrenceを評価せず、両guardの後にString patternを確認します。Findはcapture開始位置のUTF-8 byte offset、ExtractはcaptureのStringを返し、配列は直下の要素だけを評価します。None、nested値、Unicode／zero-width、末尾／継続byte例外、Date／Timestampの既存serializer形、row・side・Prop metadata、lazy／Each分岐を検査します。

Windows x64 Releaseの`--folder-properties-only`は6,373 command、20,663 assertion成功／0失敗／2 platform skipsで完了しました。各関数の独立readerは332 command、188投影、期待エラー60件（Find）／62件（Extract）、保持確認349件を照合します。二者／三者のrow path・実在側・filtered値、row内のsource→pattern→group→occurrence順、stdout／stderr／終了コード、呼出し直後と最後の入力／filter SHAをC#とPython readerで確認し、複数row間の順序は固定しません。2 skipsはmacOS FIFO検査とcase-sensitive別名検査です。`--line-transform-regex-only`は別実行で359 command、1,056 assertion成功／0失敗／0 skipでした。Release buildは警告0／エラー0です。

共有Regex本体の100ms timeoutと65,536 work／生成量予算を使います。長いToLower式の評価失敗、共有予算、病的pattern timeoutを検査します。RegexCountで5,000文字分を消費した後のpattern準備は、b×102（残予算519）を通し、b×104（523）を65,536予算で拒否し、単独のb×104は通ることを実測しました。取消の公開注入はありません。

全PCRE構文、typed Regex／NUL／任意native raw binding、optimizer分岐、新しいnative Date／Timestamp採取、取消注入、GUI、AOT、macOS／全体E2Eは今回の限定実行では未検証です。`--folder`に永続出力引数はないため、入力とfilterの保持を確認し、永続出力先のsentinel保護は主張しません。

## フォルダー `.flt` の `RegexCount`（Windows x64 Release限定E2E済み）

`--folder-properties-only` の `RegexCountValues` は [固定Regex原典](../Fixtures/LineTransformRegex/README.md) の81ケースを全IDで分類し、function-body/noopt・厳密2引数・公開primitive／Array・NULなしの59本文を二者／三者へ118投影します。型内訳は44 Int64／9 None／4 Array／2評価例外です。22件の除外理由はoptimizer 6、typed Regex 4、raw UTF-8 2、null args 1、arity 5、Throw 2、NUL 2としてmanifestに残します。native raw errorの `bad offset into UTF string` とmanagedの継続byte診断は、同じ評価失敗段階の資格として区別し、例外本文のbytes一致とはしません。

Windowsの `--folder-properties-only` でRegexCount検査は220 commandを実行し、独立readerが118投影・期待エラー46件・保護対象237件を照合しました。source→patternの一度ずつの評価、空Arrayでの遅延compile、型付き戻り値、Unicode／zero-width、@cs／@ci、Date／Timestamp serializer形、row／全側metadata、AndEach／OrEach、lazy branch、厳密arity、parser拒否を確認しています。他OSではWindows固有の2検査を除く218 commandとなりますが、未実行です。

共通Regex本体の100ms timeout、Unicode scalar境界、UTF-8 byteでのzero-width進行、予算課金と取消を再利用します。Folder側ではsource変換・pattern準備・Array要素・各探索のworkと生成量を同じ65,536予算へ戻します。近接scalar 5,000／5,100文字、pattern準備、Array、反復探索、2呼出しの共有予算と病的patternの時間制限を検査します。取消の公開注入はありません。LineTransform側は既存Contextの予算／tokenへの薄い委譲だけとし、`--line-transform-regex-only` も回帰確認します。

期待row pathと非null側集合を起動前に固定し、C#とPython stdlib readerで完全一致を確認します。複数rowの順序は固定せず、重複を保つevent配列とrow内source→pattern順を照合します。readerはケース／goldenから引数と型付き期待predicateを独立再生成し、stdout／stderr／終了コードと入力／filterのSHAを検査します。`--folder` はstdout JSONを返す経路であり、失敗時の空stdout・部分JSONなしを検査します。永続出力先の保護はこの検査の対象にありません。`e2e*/fixtures/<run>/folder-properties/regexcount` にpartition・観測・期待row manifest・生成readerと結果を保存します。

Windows x64 Releaseの `--folder-properties-only` は全folder-properties suiteで17,565 assertions成功・0失敗・2 platform skipsでした。skipはmacOS FIFO検査と、case-sensitiveな別名を区別できないWindows file systemでの検査です。`--line-transform-regex-only` も別実行し、359 command・1,056 assertion成功、0失敗・0 skipでした。Release再buildは警告0・エラー0です。PCRE全構文、typed Regex、NUL／raw native入力そのもの、optimizer、Date／Timestampの新native採取、公開取消注入、GUI、Native AOT、macOS／全体E2Eは今回の限定検証の資格外です。

## フォルダー `.flt` のログ関数

`--folder-properties-only` の `LoggingValues` は実アプリの `--folder --filter` を二者／三者で110回呼びます。`LogError`／`LogWarn`／`LogInfo` の最小1引数、左から一度ずつの評価と即時文字列化、単一空白の連結、severity、最後の型付き値、式の `@name` とファイルの `name:` の独立、未選択If／and／or、ルールの短絡、入れ子の順序、途中失敗時の完成済みイベント保持を確認します。CLIログはstderrへ出し、stdoutの比較JSONと終了コードは式の結果に従います。GUIのTrace接続と注入sink例外の伝播はsource reviewの資格です。

固定 [LineTransformLogging原典](../Fixtures/LineTransformLogging/README.md) の独立readerは35ケース・別プロセスnative採取2回を検査します。公開投影は25成功本文×二者／三者の50件と、3途中例外本文のイベント順序をmanaged共有予算拒否で検査する6件です。直接本文のzero-args 2件は公開登録表のmin1と異なるため成功投影から除き、不正arityとして拒否します。残るdirective 5件もnative-onlyです。native外側の例外→false回復や例外本文の一致は採用範囲に含めません。

同一プロセスの `alpha.bin`／`beta.bin` は異なるログと反対の採否を持ち、各rowで一回の評価を要求します。DIFFITEM間の走査順は固定せず、row固有event行をソートした配列で件数・重複ごと照合します。本文内の引数／入れ子順序は厳密に照合します。期待row path集合と非null側集合（通常は二者／三者の全側、欠損ケースはleftだけ）を起動前に固定し、C#と独立Python reader双方で完全一致を検査します。

`e2e*/fixtures/<run>/folder-properties/logging` にnative reader結果、`observations.json`、各stdout／stderr、期待row manifest、E2Eから生成したPython stdlib reader `verify-folder-logging.py` と結果を残します。readerは固定goldenのmessageHex／severityからUTF-8 bytesを独立復元し、順序・終了コード・全側filtered・入力／filter／既存sentinelのSHAを検査します。型付き結果はstrict `AllEqual` を通る公開predicate投影です。Int64の投影には単位接尾辞を付けず、既存サイズ変換の丸めを混入させません。raw不正UTF-8のsink bytes、GUI Trace採取、他CLIの出力先保護はこの検証の資格外です。

## フォルダー `.flt` の `Now` / `Today`

`--folder-properties-only` の `ClockValues` は実アプリ別プロセスの `--folder --filter` で二者／三者を検証します。大小文字を無視する厳密0引数の `Now()`／`Today()`、同じParser内の反復snapshot、Timestampの型比較・array・非predicate、If／and／or、Date metadata、fe／de／eとディレクトリの枝刈り、全実在側の `filtered`、arity／不正構文、非時計式と入力保持を確認します。

Nowの固定値は要求しません。2,048個の入力mtimeを100ms刻みの境界（204.7秒幅）とし、同じParserが評価した全JSON entryからNowの区間を推定します。実測したmtime設定などの準備時間と実runnerのcommand timeoutを観測幅が包含することを確認し、実採用点と実除外点の両方を必須にします。前後のUTC／単調時計／local profileを記録し、推定区間全体が起動前／終了後のUTC区間内にある場合だけbound合格とします。明確な区間外は失敗、観測幅／解像度／時計profileの資格不足は理由付き資格外とし、曖昧なoverlapを合格にしません。

Todayの独立midnight期待は、固定原典の自然Windows ja-JP／LCID1041／Tokyo固定offset・DSTなし・TZ overrideなしを先に確認した場合だけ生成します。他profile／macOS／DSTではBCL期待を作る前に理由付きskipとし、反復snapshot・`ToDateStr(Now()) == ToDateStr(Today())` などの公開関係式は維持します。条件一致後も前日／当日／翌日0時のinvalid／ambiguousを `ConvertTimeToUtc` より先に検査します。有資格の3入力で、前後が同じlocal日付なら当日のみ、日付を跨いだ場合は両端日付内のmidnightだけを認めます。原典にTZ環境変数の記録はないため、現在のoverride拒否をNative TZの一致資格とはしません。

`ClockValue.UsesAllSideMetadata` は補助関数のない `Now() >= Date` でも守ります。二者／三者の同名fileをside別の過去／未来mtimeに設定し、どれかのsideが過去なら全側採用、全側未来なら全側filteredとなることを確認します。

固定 [時計oracle](../Fixtures/LineTransformTimestampClockFunctions/README.md) は `python -E -B verify.py --archive <絶対ZIP> --fixed <絶対manifest>` で独立検査します。原典のUpdate4／Now12／Today12と同snapshot・dynamic range・local midnightの関係を現在の固定値へ読み替えません。reader結果 `independent-folder-clock-oracle.json` とruntime前後区間 `folder-clock-projections.json` はrunの `e2e*/fixtures/<run>/folder-properties` に残ります。capture call count・lexing前の順序・各Parserの分離／同eのroot共有・Coreの環境なしAPI・clock／calendar失敗伝播はsource reviewの根拠であり、CLIからの注入資格とは区別します。Core直接APIの別consoleやunit testは追加しません。

新runのassetsは `build/Dev.ps1` 経由でプロジェクト別 `restore --locked-mode --no-dependencies` により用意します。Core／Providersは既存lockに合わせ `-r win-x64 -p:PublishAot=true`、Appは既定4RID、E2E／FakeProviderはRIDと追加PublishAotを指定しません。lock不一致では通常restoreへ切り替えず停止します。その後default propertyの `build DiffBeacon.slnx -c Release --no-restore`、`run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --folder-properties-only --output <run>/e2e --app <run>/build/bin/DiffBeacon.App/release/DiffBeacon.dll --python <実Pythonの絶対パス>` を同じwrapperで実行します。`--no-build` は `--no-restore` を含意します。Appとrunnerは通常managed DLLで、publish／Native AOTの実測には数えません。

2026-10-20のWindows x64 managed検証は、Release buildとE2E明示Rebuildが警告0／エラー0、r3限定全体が **16,404成功／0失敗／2環境skip、5,379命令・16,406 assertion、実終了0**。時計62命令・198チェック（正常exit0=14／差分exit1=20／期待拒否exit2=28）がすべて成功しました。bare Date二者／三者とtree4命令を実commandsで確認し、二者／三者のNow strict bound・観測幅と準備＋30秒上限の包含・Today midnight資格も成立しました。skipは既存macOS FIFOとWindows case-sensitive distinct fileで、時計の資格skipはありません。5 lockfileのSHA／サイズ／属性／更新時刻は不変です。証跡は `D:\Claude\DiffBeacon\nowtoday-20261020-impl01` のbuild log・`e2e-r3/assertions.json`・runner summary・clock projection／reader JSONへ保持します。全体E2E／UI／Native AOT／他OS／実midnight跨ぎはこの限定結果に含めません。

初回は静的Timestampノードが既存Date型／bool検査へ衝突して27命令・76成功／15失敗で停止しました。runtime Timestampを保ち、他の日時関数と同じDynamicノードへ訂正しました。r2は5,373命令・16,380成功／0失敗でしたが、build中の追記によりtree4命令がDLLへ未反映でした。r3は全編集を終えてsource／doc hashを固定した後にE2Eを明示Rebuildし、実コマンド名とビルド・E2E中のhash不変を照合しました。旧 `e2e`／`e2e-r2` の失敗・部分資格証跡も保持します。

## フォルダー `.flt` の `StartOfWeek` / `StartOfMonth` / `StartOfYear`

`--folder-properties-only` は固定 [暦関数原典](../Fixtures/LineTransformTimestampCalendarFunctions/README.md) のZIPを独立readerで検証し、自然なWindows ja-JP／LCID1041／Tokyo／Tzd32400 profile一致時に108本体（70正常／38例外）を二者／三者の実 `--folder --filter` CLIへ計216投影します。成功は `AllEqual(Array(actual,expected))` でTimestampのepoch・None・array要素を照合し、Timestampと同じ数値のInt64を区別します。例外は終了2・空stdout・非空diagnosticを確認し、18静的除外にgoldenを作りません。epoch近傍のWeek例外とMonth／Yearの正常な `-32400000000us` を保持します。

各関数の大小文字・arity・一度評価の予算対照・If／and／or短絡、直下array／入れ子／中途失敗、Date／Modified／Prop配列・欠落側・全側共有の採否、共有65,536予算、入力／filter／sentinelのSHAを検査します。異なるmtimeの専用入力では、関数を含む式が全側のDateを使用し、全側で同じ採否を返すことも確認します。非負Timestampのserializerは固定bytesを照合し、成功した負epoch Timestampのnative serializer System文字列14観測は本体例外と分けて記録します。既存 `.flt` Literal／System serializerは変更しません。

`startof-projections.json` と `independent-startof-oracle.json` はrunの `e2e/fixtures/<run>/folder-properties` に生成します。profile不一致は期待値を再計算せず理由付きskipとし、別profile／DST／macOS／Native全ASTの資格には広げません。CLIへ注入できないcallback副作用・取消・host失敗は公開E2Eの注入資格に含めません。実Pythonの絶対パスを指定し、`dotnet run --no-build`（`--no-restore`を含意）で実行してください。

2026-10-20のWindows x64 Release buildは警告0／エラー0、run `D:\Claude\DiffBeacon\startof-20261020-impl01` の限定全体は **16,182成功／0失敗／2環境skip、5,311命令・16,184 assertion**。StartOf追加516命令・1,787チェックがすべて成功しました。原典216投影は140正常返値／76期待例外で、別readerも実CLIのJSON・全実在側の採否・streams・exit・入力13ファイルのSHAを照合しました。skipはmacOS実FIFOとWindowsのcase-sensitive distinct fileで、StartOfの固定profile skipはありません。全体E2E・UI・Native AOT・他OSはこの限定結果に含めません。

## フォルダー `.flt` の `ToDateStr`

`--folder-properties-only` は固定 `LineTransformTimestampFunctions` ZIPを独立readerで検査し、自然なWindows ja-JP／LCID1041／Tokyo／Tzd32400 profile一致時にToDateStr52本体（正常33／例外19）を二者／三者の実 `--folder --filter` CLIへ投影します。8件の静的除外はnative-onlyとして記録し、goldenを作りません。String／None／array結果はtyped `AllEqual(Array(actual, expected))`、body例外は終了2・stdout空で確認します。epoch -1us成功／-1秒失敗、DateとTimestampのmicrosecond往復、空／不正文字列、入れ子、metadata／Prop、arity・一度評価・短絡、共有65,536予算と入力／sentinel SHAを確認します。profile不一致時は期待値を再計算せずskipします。projectionと独立reader結果はrun内の `todatestr-projections.json`／`independent-todatestr-oracle.json` です。

同時に、専用 `probe.bin` の二者／三者で異なる秒単位mtimeを設定・読戻しし、`ToDateTime('1970-01-01T00:00:01Z') == Date` が全側metadataへ依存することを各側の `filtered=false` とlistingのmtimeで検証します。既存fixtureのmtimeを上書きしません。CLI注入できない取消・非Syntax環境例外と実OSダイアログは、この限定結果へ含めません。


2026-10-20 Windows x64 Releaseの最終限定結果は **14,395成功／0失敗／2環境skip、4,795命令・14,397 assertion**。ToDateStr追加156命令・412チェックとToDateTime依存回帰2命令・13チェックが全成功しました。原典52件×二者／三者＝104投影（正常66／例外38）を固定profile一致で確認しました。初回の4失敗は、新規fixtureのmtimeを2023年へ変えてShell propertyのNoneを避け、512token上限へ先に達した予算式を15,176文字の短い2halfへ変更して解消しました。単half正常controlと2halfの65,536診断を検証しています。初回／最終assertionsは `D:\Claude\DiffBeacon\todatestr-20261020-impl01\targeted-r1` の `e2e`／`e2e-r2` へ保持します。全体E2E・UI・Native AOT・他OSはこの限定結果へ含めません。

## フォルダー `.flt` の `ToDateTime`

`--folder-properties-only` は `LineTransformTimestampFunctions` の固定ZIPを独立readerで検証し、自然なWindows ja-JP／LCID1041／Tokyo／Tzd32400が原本profileと一致するときだけ、原典59本体のうち52件を二者／三者の実 `--folder --filter` CLIへ投影します。34文字列（Timestamp23／None11）、Timestamp入力11、None／bool／int／doubleの4件、配列3件を `AllEqual(Array(actual, expected))` で型とepoch microsecondsまで照合します。0引数1件、date literalで表せないInt64両極Timestamp入力2件、年0000／localeの期待Timestamp4件は理由付きで除外し、別profileでは期待値を作り直さずskipします。

共有65,536予算、一度評価の予算対照、If／and／orの短絡、arity／構文拒否、空文字列・不正値・入れ子配列、metadata／Name／Prop配列と欠落側、入力SHAと既存sentinelの保持を確認します。選択根拠はrun内の `todatetime-projections.json`、独立reader結果は `independent-todatetime-oracle.json` へ残します。CLIから注入できない取消と非Syntax host例外はsource-contract evidenceに留めます。実Pythonの絶対パスを明示してください。


2026-10-20 Windows x64 Releaseの最終限定結果は **13,970成功／0失敗／2環境skip、4,637命令・13,972 assertion**、追加分146命令・424チェックが全成功しました。52候補×二者／三者の104投影を原本profile一致で実行しました。初回の共有予算用1,900文字×2は閾値未満だったため、実Consumeに基づいて2,100文字×2へ修正し、二者／三者の終了コード・stdoutに関する4失敗を解消しました。初回と最終のassertionsは `D:\Claude\DiffBeacon\todatetime-20261020-impl01\targeted-r1` の `e2e` と `e2e-r2` に保持します。全体E2E・UI・Native AOT・他OSの検証をこの限定結果へ含めません。

## フォルダー `.flt` の幅・文字種6関数

`ToHalfWidth`／`ToFullWidth`／`ToHiragana`／`ToKatakana`／`ToSimplifiedChinese`／`ToTraditionalChinese`を大小無視の1引数関数として確認します。固定[FolderWidthOracle](../Fixtures/FolderWidthOracle/README.md)は309件＝174 public-projectable＋135 native-onlyです。登録表に追加aliasはありません。

`--folder-properties-only`は固定manifest・typed result/itemsから174行すべてを`AllEqual(Array(actual, expected))`で二者／三者CLIへ投影します。metadata・欠落側・Prop・Folder／FullPath・直接double・名前の大文字小文字・arity／誤alias拒否・一度評価の公開予算対照・共有65,536予算拒否と入力SHA保持も確認し、135行のnative-only理由を`width-projections.json`へ記録します。Windowsでは独立readerの直接`LCMapStringEx`照合も実行します。実Pythonの絶対パスを指定してください。

変換は既存`ILineTransformStringEnvironment.MapCharacters`／enum／App環境を使い、NFKCで代用しません。Coreに環境が無い場合の明示拒否、None／空Arrayでのcallback省略、注入callback例外の伝播は既存LineTransform契約と実装を照合し、常に環境を注入する公開CLIで実測したとは扱いません。native側のAPI不正flag失敗は公開関数ではなく、6関数のAPI第2呼出だけの失敗・XP fallback・全Unicode／locale・macOS同値性を保証しません。失敗行列は製品編集前に`D:\Claude\DiffBeacon\width-20261020-impl01\failure-matrix.md`へ記録しました。

2026-10-20 Windows x64 Release実測は **13,546成功／0失敗／2環境skip、4,491命令・13,548 assertion**。Width追加564命令・1,625チェック（専用569、命令共通1,056）がすべて成功しました。174行×二者／三者の348原典投影と135 native-onlyの記録を含みます。両raw captureの独立readerは309件・8改変拒否・直接Windows API 261値・`actualVerified=true`で成功しました。

成果物は`D:\Claude\DiffBeacon\width-20261020-impl01`の`e2e/assertions.json`、`capture-a-reader.json`／`capture-b-reader.json`、`width-projections.json`と独立reader receipt・入力・stdout/stderrへ保持します。修正後E2E runnerは同rootの`final-runner`へ確定buildし、rootのApp DLLを使用しました。skipはmacOS実FIFOとWindows case-sensitive distinct file環境です。全体E2E・UI・AOTはこの限定結果に含めません。

## フォルダー `.flt` `NormalizeUnicode` の実装前失敗行列

固定[FolderNormalizeOracle](../Fixtures/FolderNormalizeOracle/README.md)の178件を正本とし、次の失敗条件と期待を製品編集前に確定します。

| 起こり得る失敗 | 期待する契約・確認 |
| --- | --- |
| arity・関数名の登録不一致 | 大小無視の関数名、1〜2引数だけ受理。不足・余剰・空引数・閉じ括弧不足は終了2、stdout空。 |
| valueをformより先に読む／二度読む | formを先に一度評価し、有効なform解決後にvalueを一度評価。不正string／Int64 formはNoneでvalueを読まない。公開の共有予算超過valueを有効／無効formへ渡す対照で短絡を検証し、native throw traceとは分ける。 |
| formの過剰な型変換 | ASCII大小無視のNFC/C、NFD/D、NFKC/KC、NFKD/KDとInt64の1/2/5/6だけをaliasとして受理。空白をtrimせず、他のstring／Int64値はNone。未指定・double／bool／None／array等のwrong-typeはNFC既定。double 2.0/5.0を整数aliasとして扱わない。 |
| 値の型・array shapeを失う | Noneを保持し、その他は既存native文字列化。直下arrayだけをmap、nestedは文字列化。95候補全件をgoldenのtyped result/itemsから`AllEqual(Array(NormalizeUnicode(args), expected))`へ投影し、multi-arg AllEqualの要素flattenを使わない。 |
| Unicode処理を近似する | 固定Windows NormalizeString環境を再利用し、結合文字・互換文字・補助面・各形式を全byte照合。Poco UTF8→UTF16境界は既存casing契約を保つ。83 native-onlyのraw bytes／NUL／内部型／非有限double／throw／arityは理由を記録し、公開投影済みとは扱わない。 |
| metadata/contextが脱落する | valueとform双方のproperty・side・全側metadata・folder context・NativeValueExpression依存を伝播。二者／三者、欠落側、Prop、FullPathを公開CLIで確認。 |
| 失敗をNone/falseへ隠す／共有予算を迂回する | 環境例外・予算拒否を伝播し、array slot・文字列化・UTF16変換・変換結果を共有予算へ課金。入力SHAと拒否時stdout空を確認。 |

実Pythonの絶対パスを指定して独立readerと`--actual`を照合し、Release build、既存`--folder-properties-only`全件、必要な既存UI自己検証を別scopeとして実行します。新しいunit test、fixture原本変更、他OS／全localeの合格への一般化は行いません。

2026-10-20 Windows x64 Releaseでは限定E2E **11,921成功／0失敗／2環境skip、3,927命令・11,923 assertion**。NormalizeUnicode追加246命令・724チェックがすべて成功し、95固定行を二者／三者へ投影しました。形式先行と一度評価は、共有予算を超えるvalueの有効／無効form対照、二度読むと上限を超えるform／valueの公開式で確認します。native throw traceを公開CLIへ注入した証拠にはしません。固定2 captureの独立`--actual`は178件・8改変拒否確認を含めて成功、既存GUI回帰scopeは754成功／0失敗です。GUIの新しい正規化操作専用検証や全体UI・全体E2E・AOT・他OSの合格とは区別します。

成果物は`D:\Claude\DiffBeacon\normalize-20261020-impl01`の`e2e/assertions.json`、`ui/ui-report.json`、`capture-d-reader.json`／`capture-e-reader.json`とJSON・PNG・入力・stdout/stderrに保持します。skipはmacOS実FIFOとWindows case-sensitive distinct file環境です。

## フォルダー `.flt` の `ToLower` / `ToUpper`

実行時は`--app <Release App DLLの絶対パス>`と`--python <実際に起動できるPython 3の絶対パス>`を明示します。WindowsAppsの`python`エイリアスだけが解決できる環境では独立readerの検証は成立しません。

`--folder-properties-only`で[FolderCasingOracle](../Fixtures/FolderCasingOracle/README.md)の固定70件を独立readerへ渡し、公開式で表現できるscalar・None・浅い配列／入れ子の文字列化・NUL・不完全UTF-8のSubstr断片を二者／三者の実CLIへ投影します。結果の型と全bytesは厳密な`AllEqual`で照合します。case-insensitive関数名・1引数のarity、metadata／Prop／FullPathの伝播、共有作業量上限と入力SHA保持も確認し、投影できないnative内部型・任意raw bytes・throw／arity traceの理由は`casing-projections.json`へ記録します。

Windowsアプリは既存の`ILineTransformStringEnvironment`経由で`LCMapStringEx(nullptr, LCMAP_LOWERCASE/UPPERCASE)`を使います。Core単独では現在カルチャの`TextInfo`を使い、環境を明示注入することもできます。macOSのmanaged casingを固定Windows goldenとの一致とは扱いません。不完全UTF-8は固定Pocoの単位幅で読み、無効な単位をU+FFFFへ変換してからcasingします。raw Stringの保存と他のobserverは変更しません。

既存UI自己検証の`--self-test <output> --folder-properties-only`ではCore単独・注入環境の1回評価と例外伝播・Noneの省略に加え、GUIの`.flt`比較ボタンとworkspace保存／復元後の再比較を両関数・二者／三者で確認します。合成したlegacy glob maskへ関数規則は適用しません。全Unicode・全locale・XP fallback・nativeのPoco Timestampの新規採取、実OSダイアログを検証した扱いにはしません。

2026-10-20 Windows x64 Release実測は、限定CLI **11,197成功／0失敗／2環境skip、3,681命令・11,199 assertion**、限定UI **754成功／0失敗**。casing追加は144命令・415チェックとUI15チェック、公開原典46行の二者／三者投影、native-only24行の理由記録です。成果物は`D:\Claude\DiffBeacon\casing-20261020-impl01`の`e2e-python01/e2e`と`ui-recheck01/ui`です。CLI限定からUI自己検証は起動せず、それぞれ明示したDLLで別実行しています。初回のPython alias失敗と空needle containsの古いUI期待値による失敗も同rootへ保持しています。scopeを全体E2E・全体UI・AOT・他OSの合格へ広げません。

## フォルダー `.flt` `Substr` の実装前失敗行列

想定失敗は、case-insensitive名・2〜3引数登録とparser arityの食い違い、Int64以外の`start`受理、評価順の崩れ、`start`不正時に`length`の評価を省略すること、`start`不正後に`source`まで評価すること、3番目の引数が非Int64のとき長さ指定なしとして扱わないことです。原典では`start`が非Int64なら`None`、`length`だけが非Int64なら省略同様に末尾まで、`source`の`None`は`None`になります。`getAsString`で他の型は`ToStringValue`へ進みます。

sliceはUTF-8 code pointではなく`std::string`のbyte位置です。負の`start`はbyte長から数え、`start`が負のままか範囲外なら空文字、負の`length`は末尾基準で算出して0へclampします。Int64極値、空文字、NUL、不正／途中で切れたUTF-8、浅いsource配列のmapと入れ子配列の文字列化を照合し、境界配列のbroadcastや未捕捉の符号付きoverflowを導入しないことも確認します。

Stringは`ValueKind.String`のまま正確なUTF-8 byte列を保持し、分断されたmulti-byte文字を拒否・置換復号しません。後続演算はobserver別の固定原典に照合します。`==`／`AllEqual`はraw byte equality、case-sensitive順序と検索はbyte単位、`Len`／`Strlen`はlead-byte幅で数えます。case-insensitive比較／rangeはPocoのsigned-byte順序、`contains`はC localeのunsigned-byte fold、`like`は不正列をiterator token `-1`として比較し、異なる不正byte列でも一致し得ます。`Trim`／数値化／regexはそれぞれbyte trimming／CRT parse／UTF-8 validationを保持します。正規・不正UTF-8のsource、入れ子配列の文字列化、共有予算・取消・拒否時の入力と既存出力保持をE2Eで確認します。

`FolderSubstrOracle` と `FolderStringByteObserversOracle` の独立readerで原典のtyped bytes／traceを固定し、公開 `.flt` は有効・不正UTF-8を含むSubstr結果を生成して二者／三者CLIへ投影します。`Substr("é",1,1)`と`Substr("雪",1,1)`は異なる不正byteであり、置換復号による同値化を検出する比較ケースに使います。`like`では別途固定されたPoco iterator token化を照合します。引用符内のraw NULはBOM付きUTF-8 filterで公開表現し、NULと空文字の区別・長さ・検索・Trimも実行します。公開literal／sliceで生成できない任意の不正byte bindingなどはnative-onlyとして理由を記録し、managed observerやCLIで実行した扱いにしません。

実装前の公開E2E設計は、固定Substr全268行を分類して公開可能な行を型付き期待値へ投影し、原典の有効UTF-8入力から生成できる不正byte列を後続observerの引数へ再利用するものです。observer・consumer・Globの各固定caseについて公開構文で表せる引数／結果だけを実行し、`Plus`、直接`ToString`、動的／不正構文regex pattern、注入throw、任意のnative byte bindingは未投影理由をJSONへ残します。初期行列のNUL非公開という前提は、BOM付きUTF-8 filterのraw NULを実CLIへ渡して訂正しました。`def: exclude`でpredicateがtrueなら`filtered=false`となることを確認し、NUL行も公開投影へ含めます。加えて実関数合成によるstart→length→source評価順（共有予算の失敗をprobeにする）、2〜3引数の不正arity、shallow map、metadata context、二者／三者、予算拒否のstdout空・入力SHA・既存sentinel保持を確認します。取消はscannerの既存検査と関数単体の非公開native traceを区別し、新しい公開取消hookは追加しません。

Windows x64 Releaseの最終`--folder-properties-only`は**10,782成功／0失敗／2環境skip、3,537命令・10,784 assertion**です。Substr追加は1,106命令・3,305チェック（専用1,115、命令共通2,190）がすべて成功しました。4つの固定readerはSubstr268行、byte observer1,226行、consumer1,042行、Glob756行の合計3,292行を照合し、公開CLIでは2,088行を二者／三者へ投影しています（Substr260、observer989、consumer489、Glob350）。残る1,204行の理由は`substr-byte-projections.json`に記録します。Substr専用の未投影8行はnative arity3行と注入throw5行で、公開の不正arity終了2・budget probeとは異なる証拠です。

最終成果物は`D:\Claude\DiffBeacon\substr-20261020-impl01\e2e-nul-final`です。NULを含むfilterはUTF-8 BOMと引用符内のraw `00`を使い、空文字との区別、AllEqual／通常等値、Len／Strlen、検索、Trim、数値化、range、Glob／regexの公開投影を確認します。`nul-public-probe`には最初のfilter全bytesのhex・SHAとCLI結果を保持します。`def: exclude`で`filtered=false`はpredicateがtrueで採用された意味です。directory CLIは出力fileを指定しないため、sentinel保持は非変更の証拠に限ります。原典のtyped raw bytes／traceと公開CLIのboolean合成は区別し、任意の不正byte binding・`Plus`／直接`ToString`・動的／不正構文regex patternを実行済みとは扱いません。全体E2E・全体UI・Native AOT・他OSはこの限定結果へ含めません。

同じApp/Core DLLの`--line-transform-only`も**33,853成功／0失敗／0 skip、6,617命令**で成功し、既存`like`とscope=`line-transform-only`の限定GUI 2,833 assertionを含みます。5つのroot lockfileは`lock-before.json`／`lock-after.json`／`lock-comparison.json`でSHA・size・全属性・creation/writeTicksが一致しました（accessTicksのみ全5件変化、書戻しなし）。`nul-final-source-binaries.json`と検証後の照合で、対象ソース3件・App/Core DLL・最終E2E runnerのSHA／size／writeTicksも一致しています。

## IsWithin の実装前失敗行列

想定失敗は、3引数以外の受理、lower→upper→valueの評価順の崩れ／型検査前の評価省略、上端を除外するInRangeとの混同、逆順境界の並べ替え、辺ごとのInt64精度の脱落、DoubleのNaN／infinity誤判定、Timestamp異型受理、Unicode case foldingやunsigned／signed UTF-8順の混同、値配列の再帰map／境界配列の展開、全引数のproperty／side metadata／比較contextとDouble literal contextの脱落、配列／UTF-8共有予算の未検査です。

`--folder-properties-only`に[FolderIsWithinOracle](../Fixtures/FolderIsWithinOracle/README.md)の独立readerと公開CLI投影を追加します。二者／三者で上端受理をInRangeとの対照で確認し、整数端点・混在精度・Double特殊値、日時・文字列、空／混合／入れ子配列、metadata／Prop／FullPath、厳密arity、予算拒否と入力／sentinel保持を確認します。固定nativeのtyped binding／throw trace／constructor arityと、公開CLIの値・filter結果は別の証拠です。投影できないNUL等は理由を記録し、native spyを公開CLIへ注入した扱いにはしません。

Windows x64 Release最終実測は**7,477成功／0失敗／2環境skip、2,431命令・7,479 assertion**です。固定159行のうち134行を公開投影し、25行はnative専用として`iswithin-projections.json`へ理由を記録しました。IsWithin追加は346命令・1,022チェック（専用352、命令共通670）がすべて成功し、既存を含む15件の独立reader確認も全成功です。golden SHA-256は`015f153b16c156444226763d7525f7187eed922772b995b593819556abd894f6`で、suite内の固定readerと親工程で照合した二回のMSVC raw evidenceは区別しています。

`IsWithin(1.5,1,1.5)`は他関数を含まず、上端包含とnative literal contextを確認します。共有rangeノードのflagだけfalseにした変異版は終了0・両行除外、通常版は終了1・両行採用となり、回帰検知を確認しました。元コードをbytes／SHA一致で復元後、最終solution build（警告0／エラー0）とE2Eを逐次実行しています。専用のASCII basename 128 bytes入力により予算超過を保証し、二者／三者の実測はL=263、作業量下限158,928>65,536で終了2・stdout空でした。三者の不正構文／予算検証も`--middle`を明示し、入力SHAと既存sentinelを保持しました。directory CLIに出力file指定はないため、sentinelは指定先への書込み保護ではなく非変更の証拠です。

成果物は`D:/Claude/DiffBeacon/iswithin-20261020-impl01`の`build-final.log`／`e2e-final`／`mutation-probe/evidence.json`に保持しています。実bin/objは`build`、TEMP/TMPは`temp`、fixtureは`e2e-final/fixtures`配下で、既存D設定propsは読み取るだけです。5つのroot lockfileは`lockfiles-before.json`／`lockfiles-after-final.json`／`lockfiles-comparison-final.json`でSHA・size・全属性・creation/writeTicksが一致し、accessTicksだけ変化しました。時刻は書き戻していません。skipはmacOS実FIFOとWindows case-sensitive filesystemの環境限定です。全体E2E・UI・AOT・他OSは今回未実施です。

## InRange の実装前失敗行列

想定失敗は、3引数以外の受理、lower→upper→value順の崩れ／型検査前の評価省略、半開区間の上端包含、境界の一括Double化による2^53超の精度損失、NaN／infinityの誤判定、Timestampと数値の混同、Date／metadataのTimestamp正規化漏れ、配列の再帰mapや境界配列の展開、`@cs` unsigned UTF-8／`@ci` ASCII fold後signed-byte順の混同、Unicode case foldingの誤用、全引数のmetadata／比較context脱落、配列・UTF-8作業予算の未検査です。

`--folder-properties-only`は[FolderInRangeOracle](../Fixtures/FolderInRangeOracle/README.md)の145固定ケースを独立readerへ渡し、公開式で表現できるtyped入力を二者／三者の実CLIへ投影します。直接double述語、整数端点・混在精度・特殊Double、Date/Timestamp、ASCII／高bitの値と境界、配列／不正型、metadata／Prop／FullPath、厳密arity、入力SHAと既存sentinel保持を確認します。nativeの評価trace・throw注入・FunctionNode constructor証拠と、公開parser／CLIの実行証拠は区別します。原典のembedded NUL／Regex／FileContent等で公開表現できないものは理由付きで投影対象外に記録します。

Windows x64 Releaseの最終限定実測は**6,455成功／0失敗／2環境skip、2,085命令・6,457 assertion**です。InRangeは121固定行を二者／三者へ投影し、24行はnative専用として理由を記録しました。追加306命令・902チェック（専用312、命令共通590）が成功し、既存を含む14件の独立reader確認も全成功です。UTF-8／配列の共有65,536予算超過では終了2・stdout空を確認しました。`--directory`には出力file指定がないため、既存sentinel保持は指定先への書込み保護ではなく非変更の証拠です。

`InRange(1.5,1,2)`を通常版とInRange自身の`UsesAllSideMetadata`だけfalseにした変異版へ渡し、通常版は終了1・両行採用、変異版は終了0・両行除外となることを確認しました。両processの実終了・両stream EOFを記録し、元コードをbytes／SHA一致で復元後、最終solution build（警告0／エラー0）とE2Eを逐次実行しました。全証拠は`D:/Claude/DiffBeacon/inrange-20261020-impl01`の`build-budget-final.log`／`e2e-final`／`mutation-probe/evidence.json`へ保持しています。実build/bin/objは同runの`build`、TEMP/TMPは`temp`、最終fixtureは`e2e-final/fixtures`配下です。既存`Directory.Build.local.props`は正本D設定を読むだけで編集していません。

予算fixtureは短いrun pathでも拒否を保証するよう、ASCII 128 bytesのbasenameを持つ専用入力へ修正しました。200 slotの作業量は`602*L+602`で、FullPathのUTF-8長`L>=128`から最小77,658となり、65,536を必ず超えます。二者／三者の実測はともにL=261、下限157,724、終了2・stdout空でした。三者は`--middle`も明示し、新入力をSHA保持へ含めています。`inrange-budget-{2,3}.json`に長さ・下限・入力pathを保存し、修正前の成功runは`e2e`に保持しました。

5つのroot lockfileは`lockfiles-before.json`／`lockfiles-after-final.json`／`lockfiles-comparison-final.json`でSHA・length・全属性・creation/writeTicksが一致し、accessTicksだけ変化しました。最終アクセス時刻は書き戻していません。初回のafter／comparisonも元の名前で保持しています。skipはmacOS実FIFOとWindows case-sensitive filesystemの環境限定です。全体E2E・UI・AOT・他OSは今回未実施です。

## Abs の失敗行列と限定検証

実装前の想定失敗は、Int64最小値のoverflow／Doubleへの型拡大、範囲外decimalの受理、double符号・特殊値の破壊、暗黙変換、配列の再帰map、引数の重複評価／例外隠蔽、metadata／比較contextの脱落、不正arity・区切り・括弧の受理です。

`Abs(-1.5) == 1.5`は他の配列／変換関数を含めず、Abs自身の値型contextを確認します。Absの`UsesAllSideMetadata`だけをfalseへ変更した変異版が同predicateを不採用にすることを別の公開CLI起動で照合し、最終build前に復元します。

公開CLIの`--folder-properties-only`は、二者／三者で整数端点・2^53超・finite double・±zeroの型と値、ToDouble経由のinfinity／NaN、None／bool／string／日時の拒否、空／混合／入れ子配列の浅いmap、metadata／Windows Prop／FullPath、不正構文の終了2・stdout空、入力SHA保持を確認します。signed-zeroの符号bitとNaN payload、直接型binding、throw trace、FunctionNode constructorのnull／未対応arityは[FolderAbsOracle](../Fixtures/FolderAbsOracle/README.md)41件の固定native証拠の範囲で、公開parser／optimizerの同等性を証明した扱いにしません。

Release solution build後、既存の実App／Python絶対path指定と外部runを使って`--folder-properties-only`を実行します。独立reader結果は`folder-properties/independent-abs-oracle.json`へ保存します。raw evidenceは`python -B tests/Fixtures/FolderAbsOracle/verify.py --evidence D:/Claude/DiffBeacon/abs-oracle-20261020-r1`で別途照合します。全体E2E・UI・AOTは今回の小追加の検証範囲外です。

Windows Release最終実測は5,553成功／0失敗／2環境skip（macOS FIFO、Windows case-sensitive filesystem）、1,779 CLI命令・5,555 assertionです。Abs追加は56命令、専用59＋命令共通96の155チェックがすべて成功しました。13件の独立reader確認も全成功で、Absは固定41件と二プロセスraw evidenceを照合済みです。単独predicateの変異検証は通常版が終了1・両行`filtered=false`、Absのflagだけfalseにした変異版が終了0・両行`filtered=true`となり、回帰を検知しました。両process終了とstdout／stderr EOFを確認し、`D:/Claude/DiffBeacon/abs-20261020-impl01/mutation-probe/evidence.json`とraw streamを保持します。

最終Release buildは0警告／0エラー。5つのroot lockfileは同runの`lockfiles-before.json`／`lockfiles-after.json`／`lockfiles-comparison.json`でSHA・length・全属性・creation/writeTicksが一致し、accessTicksだけ差がありました。最終アクセス時刻は書き戻していないため「全時刻不変」とは扱いません。最終E2Eは`e2e-final`、先行E2E・変異build・probeも同runへ保持しています。


```powershell
. ./build/DevelopmentOutput.ps1
$workRoot = Get-DiffBeaconWorkRoot
if (-not $workRoot) { $workRoot = Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon' }
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
$app = Join-Path $runRoot 'build/bin/DiffBeacon.App/release/DiffBeacon.dll'
function Invoke-DevDotnet { & ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments $args }
function Invoke-DevPowerShell { & ./build/Dev.ps1 -WorkDirectory $runRoot -Tool pwsh -ToolArguments $args }
```

以下の例はこの設定後に実行する。生成先をCへ切り替えない。裸圧縮writerの独立readerは出力先がsystem Tempまたは`RUNNER_TEMP`配下にあることを検査するため、D上のrunでは上記の`RUNNER_TEMP`も子processへ継承する。

起動・出力先は [開発出力の起動経路](../../Docs/DEVELOPMENT.md#開発出力の起動経路) に従う。このPCは `D:\Claude\DiffBeacon` が正本。`build/Dev.ps1` でbuildとTEMP/TMPを同じrunへ揃え、`--output <run>/e2e` と `--app <run>/build/bin/DiffBeacon.App/release/DiffBeacon.dll` を明示する。

置換リストは`--line-transform-replace-lists-only`、描画はAppの`--self-test <外部出力先> --line-transform-replace-lists-only`で限定実行できます。`--app <外部Release buildのDiffBeacon.dll>`と`--python <実Pythonの絶対path>`を指定してください。[固定原本70件](../Fixtures/LineTransformReplaceLists/README.md)の独立reader、公開plan28件（18 serialized-key比較をopt/noopt両方、4実行拒否、6解析拒否）、file-caseのnative serialized-keyとの派生比較、元bytes保持、eager順序、4 MiB上限、HTML／workspace／出力保護、v10 logical path mapの包装・展開・project-copyを実Appの別processで確認します。file観測はUTF-8 adapterの結果との比較で、旧UniMemFile／codepage detection／OS I/Oの同等性ではありません。通常の`--line-transform-only`と全体E2E／UIにも含み、限定実行を全体E2E・Native AOTの代替にしません。

同限定実行では、literal元リストの4 MiB超／不在後も包装assetだけで再読込み・HTML比較・project-copy・CLI上書きを確認します。数値を含む連結pathは通常評価を保ち、依存を安全に列挙できないため包装を拒否して既存出力を保持します。限定GUIはoversized literalの追加／適用エラーの表示位置と訂正後の継続、asset公開直前の衝突時のJSON／既存asset／temp保持境界と同target再試行、包装Aから既存workspace Bへ保存した再利用assetへのalias Text SaveAs拒否を確認し、PNGとobservations JSONを保存します。

文字列連結内のIf／Choose／At(Array)と入れ子選択に非string枝がある場合も、opt／nooptの通常評価と包装拒否・入力／既存出力保持を確認します。限定GUIでは、CWDの同名hash asset／包装relative assetを4 MiB超にして、workspace保存・包装の相対DTO直列化が絶対snapshotだけを使うことと、復元したassetへの出力拒否を確認します。

`--line-transform-content-only`は[Contentの固定契約](../Fixtures/LineTransformContent/README.md)をopt／nooptで実App CLIへ渡します。原文snapshotの物理行数・正確なEOL・負の範囲・整数極値・一段配列・引数評価、`Content contains`のcase directive・空needle・CR/LF/CRLFをまたぐ不一致・共有作業予算と、Sublinesを含む式のopaque複数行キー・後続変換・原文全行／行番号／入力保持、CSV引用内改行、三者とproject／HTML／包装を確認します。実ファイルを持つleft／right paneと三者比較のleft／base／right paneごとに`<FileContent:absolute-path>`の互換文字列化も確認し、pathlessなCore APIの`<Content>`はheadlessで確認します。headlessはancestor／independent FourPane materializationが複数行キーを受け入れることと、pair比較で元行ごとの複数行キー境界を保つことも確認します。`--line-transform-only`と全体E2E、全体／一般行変換UI自己検証にも含みます。`line-transform-content/observations.json`は固定fixtureの独立readerで照合できます。Prop、pathを引数に受けるLineCount、任意path読込み、一般`.flt`全体の移植を検証した扱いにはしません。

`ToHalfWidth`／`ToFullWidth`／`ToHiragana`／`ToKatakana`／`ToSimplifiedChinese`／`ToTraditionalChinese`は`--line-transform-character-mapping-only`で単独確認し、`--line-transform-only`と全体E2Eにも含めます。実装前に整理した失敗条件は、arity拒否、無関係な互換文字の過剰変換、noneの変換、配列への再帰適用、原文や原行の破壊、HTMLの分類未反映です。手定義の期待値で通常／`@noopt`・遅延分岐・文字列化・直下／入れ子配列・表列・単体／包装HTML・入力bytes保持を確認し、OSと判定を`observations.json`へ残します。headlessでは全6関数の環境呼出し・非定数化・全pane scope・case directiveの独立性・共有作業／出力予算・呼出し前後の取消と、実システムの埋込みNULを確認します。WindowsはLCMapStringEx、macOSはCoreFoundation／ICUの文字種変換を使います。Native WinMerge golden・両OSの全Unicode一致・macOS現物や他RIDのAOTを検証済みと扱いません。

現行の検証出力は、最初に次のPowerShell式で一意な外部作業先を決めてください。`artifacts`という完全一致のフォルダー名、repo内、Codexユーザーディレクトリ内、リンク祖先のある出力先は使用しません。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
```

## 一般行変換の限定検証

`ToLower`／`ToUpper`は`--line-transform-casing-only`で単独確認し、`--line-transform-only`と全体E2Eにも含めます。手定義したASCIIの公開契約で、大小変換、空文字・none・bool・数値・直下／入れ子配列、表列、通常／`@noopt`、引数数拒否、入力bytes保持を確認します。nativeの実行やlocale依存文字のgoldenではありません。UI自己検証では注入環境の呼出し、各pane scope、実dialog、workspace復元、原文保持とPNGを確認します。

`NormalizeUnicode`は`--line-transform-unicode-normalization-only`で単独確認し、`--line-transform-only`と全体E2Eにも含めます。手定義の期待値でNFC／NFD／NFKC／NFKD、ASCII大小混在の別名・数値形式、非string／非整数formの既定NFC、不正form時の値評価省略、none・scalarの文字列化・直下／入れ子配列、表列、通常／`@noopt`、arity拒否と入力bytes保持を確認します。単体／包装HTMLレポートでは比較キーの正規化と原文表示を確認します。Windowsの`NormalizeString`で採取したgoldenではなく、他OSでは.NETのUnicodeデータを用いた別の動作確認です。UI自己検証では注入環境によるform→valueの評価順と呼出し数・定数化しない境界、共有出力予算・取消、case directiveとpane scope、実dialog・workspace復元・原文保持をJSON／PNGへ記録します。

時計関数は`--line-transform-clock-functions-only`で限定実行し、`--line-transform-only`・全体E2Eへ各一回、UI自己検証へCalendar checksの直後に登録します。[時計fixture](../Fixtures/LineTransformTimestampClockFunctions/README.md)の5ファイルと独立readerを使い、原典ZIPの全entry SHA／size／CRCとreceipt joinsを照合します。製品のText／Table／report／workspace／project-copy、normal／`@noopt`・arity・型・入力と既存出力保持はsource-derived工程として区別します。UI checksで一式一snapshot・全行共有・別式／再compile・取消を確認します。自然Windows時計6観測、fake工程、製品E2Eを混同せず、原典native profileのlocale記録範囲を超えたTZ一致・full API／他OS／原典全AST互換は未資格です。限定成功は全体E2E・UI・Native AOTの代替にしません。

日時変換関数は`--line-transform-date-functions-only`で限定実行し、`--line-transform-only`・全体E2EとUI自己検証にも含めます。`LineTransformTimestampFunctions`の固定原典121行×2、111body／10未実行を保持し、公開arityが異なるToDateTimeの0引数を除く110bodyの投影へ照合します。ToDateTimeのnoneとToDateStrの例外、Timestampのepochと表示、配列の順序・一要素失敗、通常／`@noopt`・動的入力、原文・既存出力の保持を確認します。Native未実行NUL／nullarray／unsafe値とsource由来のarity・最適化・共有予算・取消を分け、限定成功を全体E2E・AOT・原典全ASTの代替にしません。手順と独立readerは[日時変換fixture](../Fixtures/LineTransformTimestampFunctions/README.md)を参照してください。

暦関数は`--line-transform-calendar-functions-only`で限定実行し、`--line-transform-only`・全体E2EとUI自己検証にも含めます。固定原典126行×2の108body／18除外を[暦関数fixture](../Fixtures/LineTransformTimestampCalendarFunctions/README.md)の独立readerで照合します。関数の返却TimestampとserializerのSystem exception文字列14件を分け、通常／`@noopt`・動的入力、配列の途中失敗、原文・既存出力・設定の保持を実アプリで確認します。UIでは実ボタンとダイアログ、Text／Table、取消・失敗時の前回表示、設定共有と到達boundsをPNG・JSON・NDJSONへ記録します。原典全AST／parser／Optimize・Mac／他profile／DST／unsafe値は未資格です。限定実行は全体E2E・AOTの代替にしません。

日時literalは `--line-transform-timestamp-only`、一般行変換の限定実行、全体E2E／UI自己検証に含む。[固定原本reader](../Fixtures/LineTransformTimestampLiterals/README.md)はPython 3.14で `-B tests/Fixtures/LineTransformTimestampLiterals/verify.py` を実行し、primary31／guarded48の全source・span・ZIP・Native二回とreceiptを照合する。Native再採取は行わない。自然なWindows hostが原本profileと一致する場合だけsafe32をd／D・通常／`@noopt`でraw joinし、異なるprofileと未定義serializerは選択JSONへ未実行理由を残す。CLI compare／table、project／workspace複製、単体HTML、包装・展開・再読込み、入力と既存出力の保持を実アプリ別processで照合する。GUIは日時dialogの追加・編集・適用・取消、原文・履歴・再比較・世代・保存DTO／HTMLとPNG／boundsを確認する。constructor／serialized bridgeはNative全parser・typed AST・別OSの資格を意味しない。

Version literalも`--line-transform-only`・全体E2E／UI自己検証に含める。`LineTransformVersionLiterals/verify.py`はself-containedな原典60件・整数文字列化／quote128観測・C++17境界36観測と原典source／process proofを照合する。同じreaderの`--public <diagnostics.ndjson> --probes <public-probes.ndjson> --engineering <engineering.ndjson>`は77安全原典のv／V別154 serialized bridge・NUL別2拒否と、別の公開compile／engineering観測を検査する。Native全lexer／parser・typed context、公開private型／constructor originは未観測としてfalse／nullを保持する。実GUIの原文・履歴・保存・再比較・取消・世代・通常／最小850×550／37タブのPNG／bounds、実CLIの原文保持・project複製／HTML／包装／再読込みと出力保護を照合する。限定成功は全体・AOT検証の代替ではない。

期間literalも`--line-transform-only`・全体E2E／UI自己検証に含む。`LineTransformDurationLiterals/verify.py`は固定106 Native原典（17 blobs・62 spans・実Native二回）をshared `LineTransformReplace/reference-source.zip`とlocal `native-evidence`へ照合する。`verify-public.py --actual <line-transform-duration-literals/diagnostics.ndjson> --probes <public-probes.ndjson> --engineering <engineering.ndjson> --fixture tests/Fixtures/LineTransformDurationLiterals --native-proof tests/Fixtures/LineTransformDurationLiterals/native-evidence/native-proof.zip`で92公開serialized候補と14歴史的未実行、別21解析probe、24engineering観測と1共有予算観測を照合する。constructor／platform丸め未実行を互換成功へ数えず、容量unitの優先・最長期間prefix・後続AND／OR・解析失敗時0・倍率後範囲外拒否を区別する。実UIのRAWセル・履歴・保存・project／HTML／包装・取消・readonly・最小850×550／37タブを同経路で確認する。[原本と再生成](../Fixtures/LineTransformDurationLiterals/README.md)を参照し、Native全parser／typed AST／cacheと実OS pointer／dialogは検証済みにしない。

容量literalも`--line-transform-only`・全体E2E／UI自己検証に含む。`LineTransformSizeLiterals/verify.py`は固定Native95原典（17 blobs・62 spans・実native二回）をshared `LineTransformReplace/reference-source.zip`へ独立照合する。別の`verify-public.py --actual <line-transform-size-literals/diagnostics.ndjson> --probes <public-probes.ndjson> --fixture tests/Fixtures/LineTransformSizeLiterals`は77公開serialized-key一致、18歴史的未実行と別18probeを厳密照合する。constructor14・丸め4の未実行を互換成功へ数えず、追加public丸め4と隣接／空白・拒否は`engineering.ndjson`へ別保存する。実UIのCSV原セル・編集履歴・保存・workspace／HTML／包装・取消・読取り専用・最小850×550／37タブと共有256規則予算・257拒否を同経路で確認する。[原本と再生成](../Fixtures/LineTransformSizeLiterals/README.md)を使い、Native parser／typed値の同等性、実OS dialog／pointerを検証済みにしない。

`Average`・`Sum`・`Maximum`・`Minimum`も`--line-transform-only`と全体E2E／UI自己検証に含めます。固定Native141件の原典readerと、Appの`line-transform-aggregates/diagnostics.ndjson`・`arity-probes.ndjson`を照合する公開readerを別に実行し、104件のserialized key／bytesの派生一致、37件の未表現・未実行、別の引数数probe8件を区別します。公開結果はstring[]なので、原本kind／IEEE bitsの参照を実測したtyped valueとして扱いません。`engineering.ndjson`と実GUIでは集計の初回provider凍結・ルールと変換ごとのcache寿命、false条件の値評価抑制、共有上限と取消、表の原セル・履歴・保存、workspace／HTML／包装と出力保護を確認します。Nativeのshared AST・特殊contextや失敗前のSetLineは公開互換に含めません。[原本と再生成手順](../Fixtures/LineTransformAggregates/README.md)に従い、限定成功を全体検証・AOT・他OSへ読み替えません。

範囲判定は同じAppの`line-transform-ranges/diagnostics.ndjson`全72 IDと別`probes.ndjson`を採取します。`tests/Fixtures/LineTransformRanges/verify.py --actual <diagnostics.ndjson> --public-plan tests/Fixtures/LineTransformRanges/public-case-plan.json --probes <probes.ndjson>`で59 serialized-key・3解析拒否・10表現不能の未実行と別四引数probeを独立照合します。固定EOF空行はstring[1]／index 0を保ち、物理CLIの59件はCRLFを持つ派生文脈として別台帳へ記録します。12件のToInt bridgeはvalue／keyだけの同等性で、typed ASTや全parserを検証済みと扱いません。256件共有予算・257件拒否、表・workspace・HTML・包装・原文と既存出力保持も同限定／全体経路に含めます。

包含範囲の`IsWithin`も同じ限定／全体経路で実Appを呼びます。`line-transform-within/diagnostics.ndjson`全72 IDと別`probes.ndjson`を、`tests/Fixtures/LineTransformWithin/verify.py --actual <diagnostics.ndjson> --public-plan tests/Fixtures/LineTransformWithin/public-case-plan.json --probes <probes.ndjson>`へ渡します。readerは原典200 checksのJSONと公開診断59一致／3解析拒否／10未実行・12 value-only bridge／別probe1のJSONを別々に出力します。両summaryと実readerの終了・出力回収を確認し、上限一致／等しい両端の実Table、共有予算・取消・原文保存・包装再読込みも照合します。物理CLIの派生文脈と固定EOFの診断を区別し、typed AST／Timestamp／全parser互換を成功件数へ含めません。

`--line-transform-only`は実AppのText／Table、三者pane mask、動的列範囲とgroup順、設定保存／復元と`--report-project`上書き、原文保持、明示拒否と既存出力保護を検証します。共通dialogの実ボタン・取消・古い完了・設定復元を描画自己検証で確認し、固定C++原典86ケース（正常73・拒否13）の製品NDJSONを別Pythonプロセスの独立readerで照合します。配列の固定90ケースも同じAppから診断し、独立readerの`--public-plan`で89件の原典一致と物理CR/LFを生成する1件の評価拒否・全原文保持を照合します。CLIでは有効なPlainText文脈の原典25例、RAW表・三者mask、順次処理、opt／noopt、workspace／HTML／包装、累積配列slotsの8192行成功・8193行拒否を検査します。原典parser全体と旧C++ Optimize実行はこの採取の範囲外です。

検索関数の固定68ケースも実Appの診断から別readerへ渡します。`--public-plan`は62件を関数本体oracleへ照合し、直接ASTではnoneになる引数数不正の6件は、公開constructorのcompile拒否・原文保持を別に照合します。CLIは固定文脈を保てる57例、引数数拒否、RAW Column2の引用符を含むUTF-8位置5、三者mask・順次処理・workspace／HTML／包装、長いprefixを検査します。256規則と60,000 byteのneedleでは、none／空配列／全noneの6例で不要patternを作らず成功することと、検索元のある2例の共有上限拒否・入力／workspace／既存HTML保持を確認します。共通stderrだけでworkと生成量のどちらが先に上限へ達したかは断定しません。

文字列置換の固定100 IDは同じAppからmetadataと元の行文脈を含むNDJSONを採取します。[独立reader](../Fixtures/LineTransformReplace/README.md)へ`--actual <diagnostics.ndjson> --public-plan tests/Fixtures/LineTransformReplace/public-case-plan.json`を渡し、C++の実serialized UTF-8 bytesへの78一致・10公開拒否・12表現不能の未実行を区別します。EOF空行一行の原典文脈はCLIの0byte file（0行）と異なるため、CLIは忠実な文脈で再現できるsubsetと、文脈差を明記する派生probeを別に記録します。通常／全体にも含め、限定実行を全体UI／E2E・AOTの代わりにはしません。

外部Tempの一意な`$runRoot`を上の式で決め、既存の出力先を使い回さず、repo／CODEX_HOME／禁止フォルダー名を避けてください。
`$python`には起動できるPython 3実行ファイルの絶対パスを指定します。

```powershell
$appDll = (Resolve-Path "$runRoot/build/bin/DiffBeacon.App/release/DiffBeacon.dll").Path
$e2eDll = (Resolve-Path "$runRoot/build/bin/DiffBeacon.E2E/release/DiffBeacon.E2E.dll").Path
$python = '<利用するPython 3実行ファイルの絶対パス>'
Invoke-DevDotnet $e2eDll --app $appDll --output "$runRoot/e2e" --python $python --line-transform-only
Invoke-DevDotnet $appDll --self-test "$runRoot/ui" --line-transform-only
& $python -X utf8 -B tests/Fixtures/LineTransforms/verify.py
& $python -X utf8 -B tests/Fixtures/LineTransformArrays/verify.py
& $python -X utf8 -B tests/Fixtures/LineTransformSearch/verify.py
& $python -X utf8 -B tests/Fixtures/LineTransformReplace/verify.py --verify
```

E2Eは`assertions.json`と命令の終了コード・stdout／stderr・readerの実終了／出力回収を保存します。UIの`ui-report.json`にはfixture rootが記録され、その配下`line-transforms/diagnostics.ndjson`が製品結果です。独立再照合は `python -X utf8 -B tests/Fixtures/LineTransforms/verify.py --actual <diagnostics.ndjson>` で行います。配列の診断は同じfixture rootの`line-transform-arrays/diagnostics.ndjson`で、`& $python -X utf8 -B tests/Fixtures/LineTransformArrays/verify.py --actual <diagnostics.ndjson> --public-plan`で全90 IDを再照合します。`public-boundary.json`は唯一の拒否境界の原文保持を記録します。PNGとJSONは実際に確認し、限定成功を全体UI／全体E2E／Native AOT／別OS・RIDの資格へ読み替えません。保持が必要な最小証拠以外の一時物は作業契約のごみ箱手順で清掃します。

検索診断は同じfixture rootの`line-transform-search/diagnostics.ndjson`です。`& $python -X utf8 -B tests/Fixtures/LineTransformSearch/verify.py --actual <diagnostics.ndjson> --public-plan`で全68 ID・370 fieldsとconstructor境界を独立照合します。`input-sha.json`は操作前の6入力です。実セル編集のUndo／Redo後、左だけを明示保存する検証では左本文を`edited,raw`へ更新し、他の5入力は保持します。保存後のworkspace・HTMLと失敗／取消／古い候補時の表示も検証します。

## バイナリ検索と画像比較の全体初期設定

既存の外部ビルドを使う場合は、`$e2eDll` にE2EのDLL、`$appDll` に同じソースからビルドしたアプリのDLL、`$python` にPython実行ファイルの絶対パスを指定します。リポジトリルートで実行してください。出力先は冒頭の `$runRoot` を使います。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --python $python --output $runRoot/e2e/binary-search --binary-search-only
Invoke-DevDotnet $e2eDll --app $appDll --python $python --output $runRoot/e2e/image-defaults --image-defaults-only
```

バイナリ検索は実アプリの検索ボタン・Ctrl+F・F3・Shift+F3とダイアログを操作し、選択／前回語の優先、無語と不一致、ANSI／OEM・UTF16・バイトコード、方向と大文字小文字、取消・古い完了・読取り専用、原本・共有履歴・保存点の保持を確認します。[独立reader](../Fixtures/BinarySearch/verify.py)を別プロセスで呼び、固定文字列と保存bytesを照合します。

画像初期設定は差分表示・ズーム・ブロックサイズ・差分色の不透明度・閾値・行列挿入削除の6項目を確認します。新規比較への継承、旧projectとCLIの固定初期値、保存失敗後の再試行、取消・採用拒否・古い完了後の原画と実bitmap、最小850×550で各設定欄への到達を[独立reader](../Fixtures/ImageDefaults/verify.py)で照合します。run内の設定fileを注入し、利用者の設定を変更しません。

## 表の列見出しと内容に合わせた列幅

既定でオフの表見出しチェックボックスと「列幅を内容に合わせる」を操作し、二者・三者それぞれの列名、空欄時のA/B形式の代替名、先頭行を比較対象に残すこと、先頭行編集後の見出し更新とUndo/Redo、全ペイン・全行からの幅決定、現在の32列ページ外の列幅、ページ間とペイン間の幅共有、空の表を確認します。設定は二つの表タブ間で双方向に同期し、新しい設定ストア／ウィンドウで再読込みした後も復元すること、設定出力先と一致する比較入力への書込みを拒否し入力bytesを保つことも検証します。ソースと比較結果が維持されることを確認します。出力は外部の `$runRoot` に保存してください。

```powershell
Invoke-DevDotnet $app --self-test $runRoot/verification/table-headers --table-headers-only
```

両方とも全体E2Eと全体UIに登録されています。assertions、実プロセスの終了・stdout／stderr、reader receipt、入力・保存bytes・JSON・PNGを保持してください。限定成功を全体E2E・Native AOT・macOS・実OS pointer／保存ダイアログの検証済み扱いにしません。

## CSV原文をまたぐ表検索・置換

「CSV原文を検索」を有効にし、区切り・囲み引用符・escaped quote・複数行の正規表現をまたぐ一致を検索します。前後移動・折返し・全ペイン・raw offsetで固定する範囲を操作し、一致箇所からセルへの移動を確認します。原文上の現在一致／一括置換、行列構造の変更とUndo/Redo、読取り専用・不正CSV・引用内改行禁止・正規表現timeout・計画中の取消・古い検索条件での拒否に加え、原文書込み後の再比較取消で元の原文・比較モデル・入力ファイルを復元しUndo履歴を作らないことも検証します。限定selectorは、E2E runnerから実アプリの headless UI 操作を起動し、assertionsとPNGを出力先へ残します。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --output $runRoot/e2e/table-raw-search --table-raw-search-only
```

このselectorの成功は限定UI検証です。全体E2E・全体UI自己検証・Native AOT・他RIDの実行結果と区別してください。

## 表の行対応から列を除外

`--table-column-ignore-only` は、既定の対応、全ペインと左右／祖先の個別scope、反復指定の累積、引用符内区切り・escaped quote・引用内改行、列の欠けた短い行を実CLIと `--report-project` で確認します。報告HTMLにも元セル値とセル差分分類が残ること、入力とプロジェクトのbytes保持、不正列番号・不明scope・表以外での指定拒否を照合します。headless UIでは追加・重複抑止・対象scope別置換・全解除、再比較、workspace／古いプロジェクトの往復と列別設定復元を操作します。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --output $runRoot/e2e/table-column-ignore --table-column-ignore-only
```

限定成功を全体E2E・全体UI・Native AOT・他RIDの資格へ読み替えません。入力・出力・終了コード・stdout／stderr・`assertions.json`・`table-column-ignore-observations.json` とUIのJSON／PNGを外部の `$runRoot` へ保持してください。

## 表の行対応に使う列範囲

`--table-column-range-only` は、包含・除外の優先、複数指定の追加、左右／祖先ごとのscope、短い行での実セル境界、引用符内区切り・escaped quote・引用内改行をCLIで確認します。元行が各側で順序を保って一度ずつ対応し、入力bytesを保持すること、`--report-project` の範囲適用とHTMLの元セル値・通常差分表示、旧プロジェクトでの既定動作、不正構文・scope・コマンドでの拒否と入力／既存出力の保持を照合します。headless UIでは規則の置換・追加・全解除、選択scope、無効入力時の設定／比較保持、workspace往復と再読込みを操作します。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --output $runRoot/e2e/table-column-range --table-column-range-only
```

限定成功を全体E2E・全体UI・Native AOT・他RIDの資格へ読み替えません。入力・出力・終了コード・stdout／stderr・`assertions.json`・`table-column-range`のJSON／CSV／HTML・UIのJSON／PNGを外部の `$runRoot` へ保持してください。

## Text行ブックマーク

左右のTextペインで通常ブックマークと番号付き10枠を操作し、側別一覧、次／前への周回移動、行挿入／削除時の位置追従・除去、原文保持を確認します。アプリ全体UI自己検証にも登録されています。出力は外部の `$runRoot` に保存してください。

```powershell
Invoke-DevDotnet $app --self-test $runRoot/verification/text-bookmarks --text-bookmarks-only
```

## 独立三側Textの入力選択GUI

正本workroot配下の新しいrunを使う。次の限定E2Eは実アプリを別processで起動し、対応する独立Python readerを集計前に実行する。全体E2E／全体UIにも登録されるが、限定成功を全体実行や4RID Native AOTの資格へ読み替えない。

| 担当範囲 | E2E限定flag | アプリ自己検証flag | 独立fixture |
| --- | --- | --- | --- |
| Basic32＋Critical16 | `--independent-text-inputs-only` | `--self-test-independent-text-inputs` | `IndependentTextInputSelection`／`IndependentTextInputSelectionCritical` |
| Archive15 | `--independent-text-input-archives-only` | `--self-test-independent-text-input-archives` | `IndependentTextInputSelectionArchives` |
| Cipher1 | `--independent-text-input-cipher-only` | `--self-test-independent-text-input-cipher` | `IndependentTextInputSelectionCipher` |
| Lifetime9 | `--independent-text-input-lifetime-only` | `--self-test-independent-text-input-lifetime` | `IndependentTextInputLifetime` |
| Route8 | `--independent-text-input-routes-only` | `--self-test-independent-text-input-routes` | [Route](../Fixtures/IndependentTextInputSelectionRoutes/README.md) |
| Saved Archives2 | `--independent-text-input-saved-archives-only` | `--self-test-independent-text-input-saved-archives` | [Saved Archives](../Fixtures/IndependentTextInputSavedArchives/README.md) |

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output $runRoot/e2e/new-saved-archives --independent-text-input-saved-archives-only
Invoke-DevDotnet $app --self-test-independent-text-input-saved-archives $runRoot/verification/new-saved-archives
python -B -X utf8 tests/Fixtures/IndependentTextInputSavedArchives/verify.py --gui-report $runRoot/verification/new-saved-archives/ui-report.json --output $runRoot/verification/new-saved-archives-reader.json
```

他の限定実行は表のflagへ置き換える。Basic readerは `--fixture tests/Fixtures/IndependentTextInputSelection --repo . --gui-report <run>/ui-report.json --output <new-receipt>.json`、Archive／Cipher／Lifetimeは各fixture READMEの追加引数に従う。Criticalは同reportのfixtures配下の `independent-text-input-selection-critical` を `verify.py --run <path> --output <new-receipt>.json` で照合する。Route／Saved Archivesは `verify.py --gui-report <run>/ui-report.json --output <new-receipt>.json` を使う。Basic／Route／Saved Archivesに全体UIのreportを渡す場合は `--full-ui` を追加する。reportのscopeを自動的に限定扱いへ置き換えない。

入力・出力・実ボタンの元Task・PID／生成時刻／実終了・全stdout／stderr・assertions・reader receipt・PNGを保持する。固定literalの全bytes／BOM／CRLF・LF・CR、全container／leaf／assetのSHAとZIP全entry／CRC、PNG全scanlineとboundsを独立照合する。Saved Archivesはflatと二重内包の両方で、三側作業保存、project再読込み、包装を通常Archiveとして開いて全展開し、展開projectを再読込みするところまで必要とする。ネイティブpicker／実OS pointer／暗号化されたSaved Archives／macOS／AOTはこの限定実測に含めない。再生成と原本保護は各fixture READMEに従う。


独立三者Textは`--independent-text-only`で限定実行します。実アプリの三つの文字コード／改行・全3pair、v6 Physical／pristine Untitledのproject往復、HTML三側原文と包装・展開再読込み、旧version／不正descriptor・未対応固定祖先Untitledの拒否、独立CLI JSONの32 MiB上限時のstdout空と入力保持を確認します。`--self-test-independent-text OUTPUT`は実画面の六方向コピー、中央編集・保存・Undo／Redo、readonly・取消・後発編集・役割／pair切替、通常／最小850×550の多数タブと操作到達を検査します。手動同期点も実ボタンで追加・消去し、左右の順序が交差する指定と末尾改行後の空行を拒否すること、消去後に自動対応へ戻ること、本文編集後に古い位置を消すことを確認し、`sync-points.json`へ結果を残します。[独立reader](../Fixtures/IndependentText/README.md)を別Pythonプロセスで集計前に呼び、固定literalの全bytes／BOM／改行、ZIP全entry CRC／SHA、HTML原文、PNG全画素とboundsを照合します。入力・出力・commands／process receipt・生stdout/stderr・assertionsを保持します。限定成功は全体E2E・全体UI・4RID Native AOTの代替ではありません。

実在Archive葉を含む独立三者Textは `--independent-archive-text-only`、限定UIは `--self-test-independent-archive-text OUTPUT` で実行します。全体E2Eにも同じscenarioを含みます。v6 original／saved projectの `--project-copy`・`--report-project`・パッチなしの `--package-project`、展開再読込み、Archive／Physical／Untitled混在を[固定synthetic入力と独立reader](../Fixtures/IndependentArchiveText/README.md)へ照合します。GUIの全6方向コピー・3pairと役割metadata、全3側のworking保存、文字コード／BOM／混在改行、指定側だけの外部保存とPhysical化、元rootとZIP全entry／CRC／SHA、相対asset・三側HTML原文を確認します。readonly true／null／省略の拒否とfalse明示だけの編集、未保存本文でのworkspace／包装拒否、取消・後発編集・兄弟revision競合、通常／最小850×550／多数タブのPNGとboundsを保持します。追加境界caseの実測範囲はrunのassertionsとreader receiptで確認し、限定成功を全体E2E・全体UI・4RID Native AOTの完了として扱いません。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output <新しいrun> --independent-archive-text-only
Invoke-DevDotnet $app --self-test-independent-archive-text <新しいUIrun>
```

E2E harnessは `verify-products.py` を別Python processで呼び、PID・OS生成時刻・実終了・stdout／stderrの全回収とreader SHAをreceiptへ保存します。固定入力のreaderは製品出力から期待値を作りません。未実装の独立Absent／不在Archive葉・provider変換・merge結果・patch包装、ネイティブ保存dialog／実OS pointerは検証済みに含めません。CLIの内包独立入力は保存projectから使い、専用の `--compare-source` 独立入力flagsがあるとは扱いません。

三者フォルダーは`--folder-threeway-only`で限定実行します。実アプリの`--directory LEFT RIGHT --middle MIDDLE`、全6方向コピー、三者全体statusを使う差分gate、不在／除外、非コピー先rootへの出力保護、読込予算と相対中央path／readonlyのproject往復を[固定原本と独立reader](../Fixtures/FolderThreeWay/README.md)へ照合します。GUIは別プロセスの`--self-test OUTPUT --folder-threeway-only`で16ケースと通常／最小PNGを保存し、既存二者の`--folder-copy-only`と分けて各120秒の枠を維持します。全体E2E・全体UIは両方を含みます。限定成功を全体／Native AOT／4RIDの完了とは扱いません。

Windowsの長いlocalhost UNC別名コピーは、属性cacheを事前に読んだ後も両方向の実bytes・通常属性資格・所有tempの清掃を照合します。通常UNC directoryのsnapshotは`READ_ATTRIBUTES | READ_EA`のhandle照会を使うため、検査対象祖先を含め`READ_EA`が必要です。権限拒否時に古い属性cacheへfallbackせず、高度metadataの保持資格も追加しません。localhost管理共有の実測を、別SMB serverや制限付きACLの検証済み結果へ広げないでください。

Windowsの`.flt`プロパティ／`FullPath`は`--folder-properties-only`で限定実行します。同じ限定実行は`LeftRelPath`／`MiddleRelPath`／`RightRelPath`も二者・三者で確認します。side-only path maskと各属性の完全一致でindex選択・不在時の`None`を検証し、nested path、directory prune、未対応の`LeftPath`拒否を照合します。case-sensitive filesystemが有効化できない環境では異なる綴りの追加ケースだけをskipします。今回のRelease限定E2Eは2,501成功・0失敗・2件の環境skip（macOS FIFO、Windows case-sensitive filesystem）でした。固定Shell／hash property原本に加え、実アプリCLIが動的な二者／三者rootを使い、bare／side指定、`MiddleFullPath`のindex 1、欠落側の`None`、同一規則内の全側評価、別規則の側別評価、directory除外後の再帰抑止を`--show-filtered`の結果へ照合します。FullPathはsnapshotのnative絶対パスを使い、テストrootはrun内で作成します。限定成功は全体E2E・全体UI・Native AOT・4RIDの完了とは扱いません。

同じ限定実行では、旧`AllEqualFunc`を無変更抽出した[FolderAllEqualOracle](../Fixtures/FolderAllEqualOracle/README.md)の90件と、旧`AllOfFunc`を無変更抽出した[FolderAllOfOracle](../Fixtures/FolderAllOfOracle/README.md)の53件を独立readerで再検証します。AllEqualは二者／三者の`Size`・`Prop(...)`・`Name`、欠落側、scalarの複数値・厳密型・文字列caseと不正構文を確認します。AllOfは配列の空／全true／false／非boolean／入れ子、scalarのbooleanとnone、引数数を原典へ照合し、実アプリCLIでは二者／三者の`AllOf(Size > 0)`・`AllOf(Exists)`、scalar、非boolean配列と不正構文を確認します。GUI自己検証は実比較ボタンによる全側条件と、注入したproperty配列の空・入れ子・欠落をPNG／JSONへ保存します。2026-10-17のWindows x64 Release限定E2Eは1,978成功・0失敗・2環境skipで、AllOf関連41 assertionと53件の独立native oracleが成功しました。skipはmacOS実FIFOとWindowsのcase-sensitive filesystemです。成果物はrun単位で保持し、限定成功を全体E2E・全体UI・Native AOT・4RIDの完了へ広げません。

同じ`--folder-properties-only`は[FolderAnyOfOracle](../Fixtures/FolderAnyOfOracle/README.md)の固定62件を`--python`の独立readerで検証し、`folder-properties/independent-anyof-oracle.json`へ保存します。手動では`& $python -B tests/Fixtures/FolderAnyOfOracle/verify.py`を実行します。golden SHA-256は`1d38099dd6fc0a546ec55351878fb67c29361308c9b7f71c10afe4f23f458766`です。再採取する場合だけ同READMEの割当済み外部run rootと既存MSVCを使い、`capture.py --output <new-run> --compiler <cl.exe> --vcvars <vcvars64.bat>`を二つの別runへ実行し、`verify.py --observed <new-run/golden.ndjson>`で照合します。通常検証でfixtureやgoldenを再生成しません。

AnyOfの実アプリCLIは二者／三者の`AnyOf(Exists)`・`AnyOf(Size > 0)`、不在側、boolean／非boolean scalar、非boolean配列、`not`／AllOf／AllEqual／Len合成、arity 0／2／3と不完全構文を確認する構成です。Windowsは実Shellの`AnyOf(Prop('System.Size') > 0)`と非boolean配列も通します。nativeの直下true・nested-only・empty・各scalar・評価一回／例外伝播・arity全62行は`HeadlessFolderPropertyChecks`で固定case-planとgoldenを読み、既存property contextへ注入して製品parser／evaluatorと照合し、`folder-properties/any-of-native-oracle.ndjson`へ対応するid・式・評価回数を保存します。GUI実ボタンの全側metadata／Shell比較、候補採用後に不正arityを拒否した場合のモデル／表示行保持はPNG／JSONにも保存します。任意の入れ子／空配列をCLIや実Shellへ注入する検証ではありません。追加コードは親工程でbuild／限定E2E／全体E2E／自己検証を実行するまでは未検証です。

同じ`--folder-properties-only`は[FolderNotEachOracle](../Fixtures/FolderNotEachOracle/README.md)の固定67件を`--python`の独立readerで照合し、`folder-properties/independent-noteach-oracle.json`へ保存します。手動は`& $python -B tests/Fixtures/FolderNotEachOracle/verify.py`です。golden SHA-256は`10ec86c8ed458a46ae1fe5369b5f45f9d2d98b793a3f9ed2da80a3177086c613`で、通常検証でfixtureを再生成しません。原典のdirect function 63行と別のarity block 4行を区別し、旧parser／GUIを通した証拠として扱いません。

NotEachのCLIは二者／三者の`NotEach(Exists)`・`AnyOf(NotEach(Exists))`・`NotEach(Size > 0)`・`AllOf(NotEach(Size > 0))`、不在側、scalar／非boolean配列、`not`の内外合成・二重NotEach・Len、arity 0／2／3と不完全構文を扱います。Windowsでは実Shellの`NotEach(Prop('System.Size') > 0)`と非boolean配列も通します。任意の空／入れ子／混在配列は`HeadlessFolderPropertyChecks`が固定plan／golden全67行を既存property-contextへ注入し、配列等値で全slotの長さ・順序・型・None、spyで一回評価と例外伝播を照合します。`folder-properties/not-each-native-oracle.ndjson`はid・式・expected・読取り回数・arity／direct-functionの対応範囲を記録します。GUIの実Compareボタンは全側metadata／実Shell比較と、不正arity後の採用済みモデル・行・ボタン復帰を確認し、`not-each-{2,3}-*.json`と`folder-not-each-retained-{2,3}.png`を保存します。AnyOfの対応成果物は`any-of-{2,3}-*.json`／`any-of-{2,3}.flt`です。任意配列注入は実Shellで取得できる型の保証とは区別し、新しいコードのbuild／限定・全体E2E／UI自己検証は親工程の実行待ちです。

同じ`--folder-properties-only`は、旧`LenFunc`とUTF-8 helperの[FolderLenOracle](../Fixtures/FolderLenOracle/README.md)38件を、`--python`で指定したPythonの独立readerで照合し、結果を`folder-properties/independent-len-oracle.json`へ保存します。配列の空・直下slot数・入れ子非展開、scalarのNone／boolean／整数／double／timestamp、有効Unicodeのcode point数と引数数を固定原典へ照合します。実アプリCLIでは二者／三者の`Len(Name/Size/Exists)`、side名の文字数・不在側0、WindowsのProp配列／scalar、AllOf／AllEqualとの合成、補助平面・結合文字・ZWJ・国旗・異体字selectorと不正構文を確認する構成です。公開CLIで表せない空／入れ子property配列はnative oracleとGUI注入検証の範囲です。不正Unicodeのhelper互換は対象外で、過去のAllOf実測をLenの検証成功には含めません。

同じ`--folder-properties-only`は、フォルダー`.flt`の`Strlen`について[FolderStrlenOracle](../Fixtures/FolderStrlenOracle/README.md)45件を独立readerで照合し、`folder-properties/independent-strlen-oracle.json`へ保存します。固定goldenのSHA-256は`4d31dbab06e96df545b2346481b36721d7cc394d9365a74578ce127aa01ab0d6`です。実アプリCLIでは二者／三者の名前・Unicode・欠落側、`Name/Size/Exists`とWindowsのProp配列／scalar、AllOf／AllEqual／Lenとの合成を確認します。scalarのNone・boolean・整数境界・小数6桁・timestamp、case-insensitive関数名、補助平面・結合文字・ZWJ・国旗・異体字selector、arity 0／2／3・trailing comma・unclosed／bare call・非boolean結果の拒否も含みます。各命令の終了コード・stdout／stderrと入力SHA保持を記録し、不正構文では終了2・stdout空を確認します。`--directory`には出力file指定がないため、既存出力fileの上書き保護はこのシナリオの検証対象外です。埋込みNUL・空／入れ子／混在配列の非再帰mapは固定native oracleの範囲であり、公開CLIによる全45件の直接実行とは区別します。既存Lenシナリオも継続し、Strlen追加後の配列slot数・None／boolean／double／UnicodeのLen出力を別途確認します。限定実行は全体E2Eの代替にしません。

同じ`--folder-properties-only`は、`.flt Array(...)`の固定native原典[FolderArrayOracle](../Fixtures/FolderArrayOracle/README.md)50件を独立readerで照合し、結果を`folder-properties/independent-array-oracle.json`へ保存します。型label付きfixtureは空／null／異種／入れ子／順序、128引数、integerと同値doubleの区別、左から一度ずつの評価と全例外位置を固定します。公開filterでは二者／三者の`Len(Array(...))`・AllEqualによる型と順序の比較、rootの再帰的なboolean探索とAnyOf／AllOfの浅い判定、side metadata、不正構文と128引数を確認します。型付きProp配列・評価trace・例外後の停止は実アプリUI自己検証で別途確認します。native fixtureのcoverage境界はREADMEに記録し、限定実行を全体E2EやNative AOTの代替にしません。

同じ限定実行は、`.flt At(array,index)`の固定原典[FolderAtOracle](../Fixtures/FolderAtOracle/README.md)を独立readerで照合し、`folder-properties/independent-at-oracle.json`へ結果を保存します。公開CLIはcase-insensitive名、0／末尾／負index、Int64極値、空／範囲外／非配列のNone、integerとdouble／boolean／string／None／array indexの区別、Noneと入れ子の型・順序保持、rootの再帰boolean探索とLogical／AnyOfの浅い判定、混在数値型、二者／三者のmetadataとWindows Shell Prop・不在側、両引数の比較context、arity・カンマ・括弧エラーを確認します。入力SHAと不正構文の終了2・stdout空も照合します。配列とindexの一度評価、非配列時のindex省略、Arrayの左からの即時評価と例外伝播はnative fixtureのspy証拠であり、公開CLIへspyを注入した検証とは区別します。限定実行は全体E2E・UI自己検証・Native AOTの代替にしません。 Windows Releaseの最終限定実測は2,772成功・0失敗・2件の環境skip（macOS FIFO／case-sensitive filesystem）でした。At追加分は公開CLI 66命令・専用69チェックと命令共通122チェックの計191アサーションで、独立readerの固定95件・native 2回の証拠を含みます。

`AndEach`／`OrEach`のfolder `.flt` 移植では、固定原典[LineTransformArrays](../Fixtures/LineTransformArrays/README.md)の二引数関数を基準に、case-insensitive名・arity・scalarのboolean化、左右配列の最大長までのslotごとの論理演算、scalar broadcast、短い配列の欠落slotと非boolean値の`None`、空配列、入れ子slotの浅いboolean化を失敗行列にします。両引数を左から一度ずつ評価する順序と例外伝播は固定native source／oracleの証拠で確認し、公開CLIのspy注入済みとは扱いません。実アプリCLIでは二者／三者の`Exists`・`FullPath`配列と両引数のfolder context、不正arity・区切り・括弧、入力保持を確認します。2026-10-19 Windows Releaseの`--folder-properties-only`は2,904成功・0失敗・2環境skip（macOS FIFO／Windows case-sensitive filesystem）で、AndEach／OrEachは50 CLI命令・132 assertionが成功しました。独立した`LineTransformArrays/verify.py`は固定90件（正常88件・原典での評価エラー2件）を検証します。native fixtureと実アプリCLIの確認範囲は別々に記録し、限定E2Eを全体E2E・UI自己検証・Native AOTの代替にしません。

`If`／`IfEach`のfolder `.flt` 移植では、同じ固定native原典のcase-insensitive名とarity 3を基準に、`If`の条件を浅くboolean化してtrue枝だけを選ぶこと（false／Noneはfalse枝）、選択値の型保持、未選択枝の遅延評価、`IfEach`の条件配列slotごとの結果形状、scalar条件のindex 0、scalar枝のbroadcast、配列枝のindex選択と不足slotの`None`、nested条件の浅いboolean化を失敗行列にします。`IfEach`は条件・true枝・false枝を左から一度ずつ先に評価し、空条件配列でも両枝を評価することを区別します。両関数のcondition・true枝・false枝にあるside metadata／property／folder context依存、不正arity・区切り・括弧、入力保持を実アプリCLIで確認します。false枝だけが参照する`RightName`・`Prop('System.Size')`・`FullPath`も検証します。遅延／先行評価の例外・順序はnative fixtureの証拠とし、CLI spyを注入済みとは扱いません。2026-10-19 Windows Releaseの`--folder-properties-only`は3,094成功・0失敗・2件の環境skip（macOS FIFO／Windows case-sensitive filesystem）で、`If`／`IfEach`は70 CLI命令・190アサーションが成功しました。限定E2Eを全体E2E・UI自己検証・Native AOTの代替にしません。

`Choose`／`ChooseEach` の folder `.flt` 移植の失敗行列は、case-insensitive 名・2以上の可変 arity、int64 以外の index 拒否（整数値 double も拒否）、負数／整数極値の clamp、選択値の型と入れ子保持、`Choose` の配列 index 拒否、`ChooseEach` の scalar 直接選択・混在配列の直下 slot だけの選択・不正 slot の `None`・空配列保持・配列枝の非平坦化です。index は一度、選択枝は必要な slot ごとに評価し、未選択枝は評価しません。例外伝播・評価回数・遅延性の native 証拠は固定 `LineTransformArrays` 原典・cases・golden・独立 reader とし、実アプリ CLI に spy を注入した証拠とは区別します。公開 CLI では二者／三者、全 choice と index の side metadata／property／FullPath 依存、不正 arity・区切り・括弧、全入力 SHA 保持を検証します。Shell property は Windows 以外で明示 skip します。限定 `--folder-properties-only` は全体 E2E・UI 自己検証・Native AOT の代替にしません。2026-10-19 Windows Release の限定実測は3,464成功・0失敗・2件の環境skip（macOS FIFO／Windows case-sensitive filesystem）で、Choose系128 CLI命令・370アサーションが成功しました。入力SHA保持は二者／三者で確認し、root lockfile全5件のSHA・サイズ・属性・UTC ticksは指定snapshotと一致しました。

ChooseEach の増幅に対する失敗行列: 短い式でも入れ子の枝再評価で出力が指数増加するため、最上位の独立predicate評価ごとに65,536単位の共有予算を新規設定します。ChooseEachのmapped配列slot数を確保前に、Choose／ChooseEachの選択枝評価を実行前に消費し、入れ子全体で共有します。上限時は評価作業量エラーとして中止し、stdout／比較結果を公開しません。nativeのslot選択・枝の再評価をmemoizeせず保持します。公開CLIでは深さ14（65,532単位）の成功・入力ごとの予算resetに加えて、ちょうど65,536単位の成功と、外側のChooseで65,537単位となる式の拒否を二者／三者で確認します。深さ31の有限拒否・診断・stdout空・入力SHA保持も確認します。ガードのない式は実行しません。2026-10-19 Windows Release の再実測は3,488成功・0失敗・2環境skip、Choose系136 CLI命令・394アサーション成功です。65,537単位の拒否は二者123 ms／三者124 ms、深さ31の拒否は二者120 ms／三者127 msでexit 2・stdout空となり、二者／三者の入力SHAとroot lockfile全5件のSHA・サイズ・属性・UTC ticksを保持しました。

`Trim`／`TrimLeft`／`TrimRight` の folder `.flt` 移植は、case-insensitive名・厳密arity 1、ASCII space／tab／CR／LFだけの左右除去、内部空白とNBSP／全角空白／VT／FFの保持、None保持、scalarの文字列型への変換、配列直下だけのmapとnested配列の全体文字列化を失敗行列にします。[固定native oracle](../Fixtures/FolderTrimOracle/README.md)の独立readerを実行し、公開式で表現できるevaluate行は型付き期待値と実アプリCLIを照合します。二者／三者のside metadata／property／FullPath、空配列・混在配列・型・入力SHA、不足／余剰引数と構文エラー時の終了2・stdout空も確認します。dynamic戻り値の非booleanをpredicateに置いた場合は一致なしとしてCLIが成功します。引数の一度評価とspyによる例外伝播・評価順、typed Regex／contentは固定native fixtureの範囲で、公開CLIにspyを注入した検証ではありません。timestampは既存NativeString変換と公開CLIのローカル時刻表現を確認しますが、Poco native timestampは固定oracleで未実測です。限定 `--folder-properties-only` は全体E2E・UI自己検証・Native AOT・他OSの代替にしません。2026-10-19 Windows Releaseの限定実測は4,343成功・0失敗・2環境skip（macOS FIFO／Windows case-sensitive filesystem）でした。Trim系は固定oracle 204件、公開CLIへ接続したevaluate成功168件を含む291 CLI命令・855アサーションが成功しました。

`StrCount`／`StrFind` の folder `.flt` 移植は、case-insensitive関数名・厳密arity、引数一度ずつの左から右の評価、空／非string needle拒否、`StrFind` のInt64限定occurrence、負数・範囲外、Countの非重複件数、Findの非重複occurrenceとUTF-8 byte offsetを失敗行列にします。None・空配列・直下配列・nested配列、scalar文字列化、ASCII大小文字と非ASCIIの区別、`cs`／`ci`、2側／3側metadata・property・FullPath、不在側、不正構文と入力／既存コピー先のSHA保持も対象です。[固定native oracle](../Fixtures/FolderStrSearchOracle/README.md)の既定readerで全93行の型・値・評価traceを独立検証し、公開式へ投影可能な78行を型付き期待値へ照合します。null引数ポインター・不正arity・throw nodeを使う15行は理由付きでnative本体専用の証拠として記録し、公開parserのarityと構文拒否は別途CLIで確認します。例外・評価traceは公開`.flt`へのspy注入として扱わず、既定readerの成功をnative再コンパイル・実行receiptの検証として扱いません。

2026-10-19 Windows Releaseの `--folder-properties-only` は4,809成功・0失敗・2環境skip（macOS FIFO／Windows case-sensitive filesystem）でした。StrSearchは139 CLI命令・466アサーションが成功し、二者／三者metadataの公開CLI再確認も2件成功しました。長い共通prefixを持つsource 8,001 bytes／needle 6,001 bytesは両関数で成功しました。KMP実装の計算量はneedle前処理と全haystack走査の合計で線形ですが、この有限入力の実測を漸近計算量の証明として扱いません。初回のmetadata期待値誤り2件を含むrunと修正後runの証拠を別々に保持します。限定実行は全体E2E・UI自己検証・Native AOT・他OSの代替にしません。

実アプリの子プロセスは共通の `CommandTimeoutPolicy` に従い、通常CLIを30秒、登録済みの描画・操作自己検証を120秒、命令 `image-defaults-reader` はWindows120秒／macOS300秒の上限で実行する。例外は入力選択Basic／Criticalの `--self-test-independent-text-inputs` だけ240秒、命令 `folder-copy-large-stream` は180秒、`folder-copy-gui` は300秒とする。二者GUIはごみ箱操作の自己検証を加えた後のWindows実測に合わせた有限上限である。通常起動とmacOS gate起動で同じ規則を使う。自己検証は複数の実画面操作と原本照合を含むため、通常CLIとは予算を分ける。時間超過では子プロセス群を終了し、終了コード`-2`と選択した制限時間をstdout／stderr・`assertions.json`へ残す。上限を延ばしただけで検証成功とは扱わず、実行完了と全条件の合格を確認する。

TAR.Zは`--tar-z-only`で限定実行し、`--archives-only`と全体E2Eにも含む。全39固定原本（block9–16、nonblock10–16、独立literal nonblock9）の全entry bytes・型・サイズ、通常CLI全操作と標準archive／tar metadata provider、壊れたheader/code/TAR、既存出力保護、包装と相対入力再読込みを照合する。[固定原本・出典](../Fixtures/Archives/TarZ/README.md)を参照する。writer出力は`build/Build-ZReference.ps1 -OutputDirectory "$runRoot/z-reference"`で別buildした公式decoderを`--z-reference <decoder>`へ明示し、Python標準tarfileで全内容・時刻・writer metadataを独立検証する。Windowsは`ncompress.exe`、macOSは`ncompress`。`--z-sevenzip <full7z>`はローカル第二decoderの追加照合。reference toolとC/compilerは製品・通常.NET buildの依存ではない。入力・出力・独立復号TAR・proof・exit・stdout/stderr・assertionsを保持する。限定は全体の代替ではない。

画像重ね合わせの限定実行は`--image-overlay-only`（核と通常CLI）、`--image-overlay-reports-only`（通常CLI）を使う。`--image-overlay-script`へ期待値を除いた入力を送り、[静的原本](../Fixtures/ImageOverlays/README.md)49ケースと[時間原本](../Fixtures/ImageTemporalOverlays/README.md)77ケース83状態の全BGRA・個別clock読取り・blend alpha・分類／原画保持を照合する。通常CLIは適合する原本を単体HTML30ケース、領域診断25ケース、画像比較6ケースへ接続し、独立PNG復号・Alpha優先順位・無指定payload・不正引数・32MiB超過時のstdout空と入力／既存HTML保持を確認する。通常UTCのANIM出力のdigest検証と、固定原本clockの全画素照合を区別する。限定実行は全体E2Eの代替にしない。

GUIの`HeadlessImageOverlayChecks`は全体`--self-test`へ含む。run内のoptionsとclockを注入し、全タブ設定共有、Alphaの明示／継承とJSON／record状態、timer停止／再開、最大一つの候補・取消／世代／revision、frameと表示の同時採用、元表示保持を実Bitmap・PNG・JSONへ残す。通常／最小と64／80px操作欄で全6部品の到達と周期4桁の実文字配置を確認する。選択組HTMLのclock0・確定capture、全ページのtuple／個別blend時計／選択とワイプ縮小の継承、描画予算の時計前拒否、包装の原本SHA照合・同size／mtime差替え拒否を独立PNGで検証する。矩形・guide・浮動貼付・原画PNGの境界を保ち、通常OSのpointer／保存ダイアログをheadless成功から推定しない。

画像ドラッグ操作は実アプリの`--self-test`内の`HeadlessImageDragChecks`と`HeadlessImageWipeChecks`で検証する。利用者AppDataを読まず、run内の設定fileを注入して共有／reload／不正設定・保存失敗を確認する。2/3paneの実pointer/key、画像外余白の押下、native scrollbarのThumb／line／page／端メニューと通常ScrollViewerとの共有メニュー、OFFSET負previewとpress差の切捨て・Escape/release・既存矩形優先・readonly TIFF・MOVE/wheel/keyboard同期・座標文脈取消を、`image-drag-modes/observations.ndjson`と`ui-report.json`、PNGへ保持する。矩形pointerの既存チェックはRECTANGLE_SELECTを明示する。通常／最小と64／80px操作欄のviewport・PNG保存到達を維持する。原本offsetの固定12ケース43状態は`--image-offsets-only`と全体E2Eで確認する。重ね合わせ各modeは通常の左右／三者viewでドラッグと同期scrollを使う。画素差viewはdrag gateで拒否する。

ワイプの限定実行は`--image-wipe-only`。実アプリの`--image-wipe-script`へ期待値を除いた入力を渡し、[原本fixture](../Fixtures/ImageWipes/README.md)40ケース400状態の全BGRA、原画・分類保持を照合する。通常`--image`・`--image-regions`・`--report-project`、単体／包装／展開後HTMLの独立PNG復号、非整列／整列全ページの共有256M予算による描画前拒否、入力・既存出力保持も含む。診断の不正mode・寸法・空操作・32MiB出力上限は終了2・stdout空で確認する。UI自己検証はreadonly二／三者の実pointer/capture、倍率・Escape・press中mode変更、TIFFページ境界縮小、候補採用前の取消・失敗・古い完了、capturelost・release・Disposeを操作する。押下中に内部保存APIを呼び、確定表示・選択強調の単体／包装HTML一致と原画PNG保持を全画素で検証し、PNG・JSONを残す。通常GUIのrelease後保存入口とネイティブ保存ダイアログの到達性を、この内部API検証で実証したとは扱わない。None-overlay原本6件と、独立閉式で計算した強調後ワイプを照合する。None以外の静的原本とANIM／blinkは後述のoverlay検証で照合する。限定実行は全体E2Eの代替にしない。通常OSのpointer/capture・保存ダイアログはheadless操作と区別する。

矩形の画素操作は`--image-rectangles-only`と全体E2Eで検証する。[矩形原本](../Fixtures/ImageRectangles/README.md)32ケースのうち、現行の入力・安全契約に適合する20ケースを実`--image-copy`へ送り、全BGRA・履歴・Undo/Redo・原画PNGを照合する。残り12ケースの除外理由を`image-rectangles/proof.json`へ記録し、未対応の空入力、原本の読取り専用貼り付けや不正paneを成功済み扱いにしない。回転・反転16組合せ、同pane貼り付け、三者履歴分岐、整数極値、不正schema、履歴と作業量の上限も実アプリで確認する。期待値の独立計算と原本関数の採取範囲を区別し、GUI・OSクリップボード・原本FreeImageのcrop・浮動貼り付けはこの限定E2Eの検証範囲外とする。

`--image-insertions-only`には通常CLI・プロジェクト保存二回往復・単体／包装／展開再読込みHTMLも含む。代表8件で整列全画素と原画保持を確認し、[強調原本](../Fixtures/ImageInsertionHighlight/README.md)12件146状態は実`--image-regions`の全canvas画素SHAへ照合する。透明実画素・ghost・通常／選択色・alpha0/.3/.7/1・offsetを含む。GUI自己検証は編集原本58件304状態と、強調原本12件146状態のalpha0/.3/.7/1を実Bitmapへ照合する。4値の実Slider変更も確認する。通常／最小ウィンドウでは実テーマの高さに加え64／80pxのSliderで全操作部品へのスクロール到達を確認し、三者画像のviewportを24px以上、PNG保存ボタンを全体表示できる状態に保つ。PNGと`ui-report.json`の実測座標・寸法を保持する。Windowsでは自己検証プロセスに`DIFFBEACON_IMAGE_LAYOUT_STRESS=1`を渡すと、高さ80pxのSliderで画像パネルの可用高さを約32px減らす追加の寸法実験を実行する。これは実Macの再現や通常の対応最小サイズを変更するものではなく、通常の6条件に追加する実験である。全未選択状態は通常CLI・単体HTML・CLI上書き・包装・展開再読込みの強調PNG全BGRAへ接続する。多ページ予算の自作TIFFは整列前240M／整列後360Mの描画量を再現し、全ページ単体／包装HTMLの拒否・選択1ページ成功・空stdout・既存HTML／ZIPと入力保持を確認する。準備データの往復は正常10ページの二／三者・縦／横・90度方向変換・短入力の最終ページ反復を使い、単体／包装HTMLの埋込みPNGを独立復号して原画・整列画素の全BGRAを照合する。実一時ファイルの正常終了・上限拒否・OS強制終了・私有consoleのCTRL_BREAKからのCancellationToken取消は `artifacts/local/image-report-preflight/temp-*-observations.json` と再現用harnessに区別して保持する。GUIの既存取消検証を多ページ準備の取消実測へ転用しない。

画像の挿入・削除コピーは `--image-insertions-only` と全体E2Eで検証する。[固定原本](../Fixtures/ImageInsertions/README.md)の58ケース304状態を実`--image-copy`へ送り、縦・横の整列、構造コピー、Undo／Redo、モード・位置・回転の変更、読取り専用、保存点を照合する。全canvas BGRA・座標対応・差分と競合数を確認し、原画PNGの出力を独立復号する。`includeAlignment`は診断出力の指定で、共通canvas4096画素までに限定する。GUI・通常比較CLI・HTMLの挿入削除モード接続は、このコピー経路の検証とは別に行う。

画像整列・逆座標の限定実行は `--image-alignment-only`。`--image-align`の小画像診断へ原本PNGを送り、[挿入削除fixture](../Fixtures/ImageInsertions/README.md)の最初のmode選択56状態で全ghost BGRA・共通canvas・差分と競合数・全座標と外周1画素・原画保持を照合する。回転後・offset後・構造コピーの残りの状態、GUIの操作をこの初期比較と混同しない。整列診断入口は単一フレーム・入力と共通canvas4096画素までで、製品整列核の処理上限とは別にJSON出力量を限定する。

画像行Myers核の限定実行は `--image-lines-only`。[固定原本](../Fixtures/ImageLines/README.md)の14,797ケースを一つの実アプリ別プロセスへ送り、期待値を入力から除いて全script・uint32行hashを照合する。全体E2Eにも含む。寸法・負の閾値の拒否、結果の部分公開がないことと入力保持を確認し、元入力・stdout/stderr・終了コード・`assertions.json`・`image-lines-proof.json`を保持する。行比較核だけの検証であり、画像表示と構造コピーの挿入削除対応は別途接続・検証する。

位置ずらしの比較・コピー核は `--image-offsets-only` と全体E2Eに含む。[原本fixture](../Fixtures/ImageOffsets/README.md)の12ケース43状態を実`--image-copy`の全BGRA・寸法・変換・位置・dirty/savepoint・Undo/Redoへ照合する。raw PNGは独立BCL復号で確認する。不正pane、整数極値、巨大canvasの拒否では先行exportを含め既存出力・入力・scriptを保護する。代表6状態で通常CLI・source-generated JSONの二回往復・単体／包装／展開再読込みHTMLの原画と位置canvas全BGRAを照合する。不正座標・型・整数overflow・巨大canvas・中央なし・重複引数で入力と既存出力を保護する。UI自己検証は全12ケース43状態の全canvas BGRA・位置・方向・dirty・履歴、readonly矢印実ボタン、取消・古い完了・多ページと設定保存復元をPNG／NDJSONに記録する。

回転・反転は `--image-transforms-only` と全体E2Eで検証する。[原本fixture](../Fixtures/ImageTransforms/README.md)の288ケース・2,816状態を実`--image-copy`別プロセスの全BGRA・寸法・変換・領域・共有Undo／Redo・保存点へ照合する。PNG出力は表示変換を焼き込まない原画へ独立復号して比較する。通常`--image`、プロジェクト保存・復元、単体／包装／再読込みHTML、不正設定と出力保護も同じ限定実行に含む。GUI自己検証は代表14操作列、読取り専用の実回転ボタン、取消・古い完了、TIFFページ切替・再読込み・レポートを検査し、4枚の画面PNGとJSONを保持する。限定実行とheadless操作は通常デスクトップの操作確認とは区別する。

画像設定の保存・復元は `--image-project-only` と全体E2Eで検証する。実CLIの`--project-copy`・`--report-project`・`--package-project`へ保存設定を渡し、source-generated JSONの全値・既定値・境界・CLI上書き、単体／包装HTMLの原画PNG全BGRA、包装の相対入力・展開再読込みを照合する。不正型・null・範囲外・中央なし指定・存在しないページでは、入力と既存出力のbytes・属性を保持し終了2を確認する。GUI自己検証の`HeadlessImageProjectChecks`は再比較・workspace再読込み、取消・古い完了・表示保持を操作し、PNG・設定JSON・選択HTMLを保持する。

TIFFの限定実行は `--tiff-only`。ビルド完了後、`dotnet tests/DiffBeacon.E2E/bin/Release/net11.0/DiffBeacon.E2E.dll --tiff-only --output $runRoot/e2e-tiff` でも実行できる。[自作TIFF入力](../Fixtures/Images/Tiff/README.md)のページ寸法・全BGRA／SHA、byte order、圧縮・色の代表構成、全／選択・三者・短い側の反復、領域・単体／包装HTMLの独立PNG復号を照合する。不正IFD・切詰め・巨大宣言・旧JPEG参照・scan欠損の拒否、既存出力と入力保持を確認する。GUI自己検証は全ページの実bitmap、取消・古い完了、snapshot HTMLをBGRA・PNG・JSONへ記録する。限定実行は全体E2Eの代替にしない。

APNGの限定実行は `--apng-only`。小さな[自作CC0入力](../Fixtures/Images/Apng/README.md)で既定画像の含有／除外、位置・合成・3種のdisposal、透明RGB、全／選択フレーム・三者・短い側の反復を実アプリへ渡す。CLIの画素SHA、単体／包装／再展開HTMLの原画PNGを独立復号して手書き全BGRAへ照合する。不正sequence・CRC・矩形・枚数・欠損・宣言上限の拒否、既存出力・入力の保持も確認する。共有256M作業量の拒否には約36KBの正常APNGを実行時生成し、巨大な復号画素は確保しない。全体実行にも含める。GUI自己検証は自動画像判定・全ページの実bitmap、同期ボタン・取消・古い完了・snapshot HTMLをPNG／JSONに記録する。

静止画像GUI編集はアプリの `--self-test` で実比較タブ・ボタン・領域移動・PNG保存を操作する。コピー原本fixtureの代表12操作列・66状態を全原画BGRA、領域、共有履歴、dirtyへ照合し、PNG独立復号・再読込み、編集済みHTML、未保存包装拒否、読取り専用・全タブの入力保護、取消・古い完了・保存中の再比較拒否を検証する。最小ウィンドウのviewportとスクロール後の保存ボタンも確認する。`image-copy-gui-observations.json`、元golden、入力・PNG・HTML・画面・`ui-report.json`を保持する。原本143ケース・935状態のCLI全件照合と通常デスクトップの操作検証は別の検証範囲である。

画像コピーの限定実行は `--image-copy-only`、全体E2Eにも含む。原本143ケース・935状態をBCL PNGへ戻し、実CLI `--image-copy` の原画全BGRA・寸法・region grid／分類・全pane共有履歴／dirty／savepointへ照合する。PNG／TIFF別名保存を独立復号・通常画像CLI再読込みで照合し、無効入力・読取り専用・出力保護とscript／作業／履歴／JSON上限を検証する。入力・script・stdout／stderr・終了コード・observations・assertionsを保持する。[コピーfixtureの境界](../Fixtures/ImageCopy/README.md)を参照する。GUI編集・処理中のOSシグナル取消・元形式／多ページ保存はこの限定検証に含めない。

単ページTIFF別名保存の事前失敗契約：入力は編集済みの静止RGBA画像で、保存先は`.tif`または`.tiff`の新規／既存別名だけ。classic TIFF little-endian、非圧縮RGB、8-bit chunky RGBA、unassociated alpha、top-left、単一stripを独立readerで検査し、BGRA/RGBAの取り違え、alpha=0の色成分消失、奇数幅の行ずれ、IFD・StripOffset・StripByteCountsの破損、32-bit offset overflow、header分を含む64 MiB超過は失敗として出力を公開しない。入力・script・別pane・readonly pane・link・filter・workspace・protected pathへの上書き、readonly出力、存在しない親directory、不正拡張子も拒否し、既存出力・入力・属性・dirty・履歴・savepointを保持する。取消・encoder/decoder失敗・保存先再検査の競合では一時出力だけを破棄し、元入力を保持する。正常保存は独立TIFF readerの全BGRA・寸法・透明RGBと通常画像CLIの再読込みを一致させ、GUI自己検証でもSaveToAsync経路で原画・保存点を確認する。native保存dialog、multi-page TIFF、元圧縮・metadata・paletteの保持、元入力上書きは対象外。

画像領域の限定実行は `--image-regions-only`、全体E2Eにも含む。無改変WinIMerge v1.0.54 C++関数から採取した164件のraw BGRAをBCLのPNGに包装し、実アプリの復号・開発用CLIを通して全pair grid・領域ID・分類・矩形・個数を完全一致照合する。通常buildにC++やFreeImageを追加しない。固定goldenのSHA、入力保持、不正引数、選択組、入力／共通canvas／合計復号量／診断grid上限も確認し、PNG・成功／拒否出力・終了コード・`image-region-observations.json`・`assertions.json`を保存する。[出典と再生成](../Fixtures/ImageRegions/README.md)。通常GUI・HTMLへの統合やOSシグナル取消の実測はこの限定E2Eに含めない。

画像HTMLの限定実行は `--image-reports-only`、`--reports-only` と全体E2Eにも含む。既存画像fixtureの全画素期待値を、単体・ZIP包装から取り出した埋込みPNGへ独立BCL PNG復号で照合する。全／選択、後続差分・欠落・透明合成・閾値・寸法、説明のescape、原本保持、包装展開と相対プロジェクト再読込み、入力・本文サイズ・出力保護・取消を確認する。画像UIは全／選択の設定と表示原本スナップショットの保持も自己検証する。

画像強調の限定実行は `--image-highlight-only`、全体E2Eにも含む。無改変C++原本の72件でalpha・選択色・透明度・三者paneの分類除外を開発用CLIへ照合する。既定強調の入力は通常CLI・単体HTML・包装へ接続し、埋込みPNGを独立BCL復号で全BGRA照合する。UI自己検証はalpha0.7の24件を実Bitmapへ照合し、領域選択・強調解除・レポート・原本保持も検証する。[強調fixture](../Fixtures/ImageHighlight/README.md)を参照する。限定検証は全体や通常デスクトップの実測の代替にしない。

画像フレームの限定実行は `--image-only`。自作GIF/PNGと独立Pillow生成lossless WebPの固定全BGRA/SHAを実CLIへ照合する。先頭一致・後続変更、全ページと選択、ページ数差・欠落、透明部分更新・disposal、寸法・閾値、入力/キャンバス/フレーム数/復号作業量の上限、不正引数・入力保持を検証する。上限の診断も照合し、別の上限で拒否されただけの結果を合格にしない。[fixtureの契約](../Fixtures/Images/README.md)を参照する。全体E2Eにも含み、通常デスクトップの実測と区別する。

GNU算法の限定実行は `--gnu-line-only`。原本の同値クラスと変更scriptを開発用CLIの別プロセスで照合し、予算上限・境界・入力拒否と原行保持を確認する。[採取範囲](../Fixtures/GnuLines/README.md)に原本入力変換と算法の境界を記載する。

通常テキスト比較への接続は `--gnu-text-only` で限定実行する。原本のdefault 273ケースの元bytesをBOM・EOLごと実ファイルへ戻し、通常 `--compare --eol strict --max-work 8000000` の元行番号から変更scriptを復元して照合する。逆順4095/4097だけ予算退避を許容し、他ケースの不一致・退避は失敗とする。フィルター、共通端保持、予算0/1、巨大一行、EOF、EOL、三者マージとパッチ再構成も確認する。`gnu-text-observations.json` と全入力・終了コード・ログ・assertionsを保持する。両GNU検証は全体E2Eにも含み、限定実行は全体の代替にしない。

表の初期GNU行対応は `--gnu-table-only` で限定実行する。原本defaultのA/B・LF全225ケースから、各変更が片側だけの143ケースを選び、元bytes・SHAを維持してCLIの全元行対応とHTMLの全セル・ghost・色を原本scriptと照合する。両側変更のraw採点はこのscriptから推定しない。引用表記だけの差と三者の無変更側一致、予算0/1/120の全元行保持・GNU退避理由、EOL・EOFも検証し、`gnu-table-observations.json` と入力・出力を保持する。全体E2Eにも含み、旧callerの全raw CSV入力変換との互換は宣言しない。

表行対応の三者回帰では引用符・大小文字・空白・置換設定・部分挿入・左右反転の8ケースを別プロセスで検証する。既知の復号一致組とHTMLのEqual表示、予算0の全元行保持を照合し、入力と対応JSONを保存する。

表行対応の限定実行は `--line-alignment-only`。88件の元関数golden、三者の元行保持、CLI/HTMLの共通対応、4096境界・共有予算・不正引数・入力保持を実アプリ別プロセスで検証する。`--word-level` と `--no-separators`／`--separators` が表行のraw採点と `--compare` の行内範囲へ届くことも確認する。[fixture契約](../Fixtures/LineAlignment/README.md)に全ブロックoracleと初期アンカー分割の検証範囲を記載する。全体実行にも含める。

原文 WordDiff の限定実行は `--word-diff-only`。6,048件の元ソース採取 fixture から設定と区間結果の代表135件を選び、実アプリ別プロセスの終了コード・全UTF-16区間・入力保持を照合する。さらにfallback・不正引数・NUL入力拒否・離れた変更のHTML span・改行横断ブロックの行投影・共有inline予算を確認する。句読点分割の既定・独自文字・無効化に加え、`prefixold`／`prefixnew` の文字単位span `6:3` と単語単位span `0:9`、HTML設定メタデータ、旧workspaceの省略項目と文字／単語設定のコピー／レポート往復も確認する。GUIの設定操作・workspace保存再読込み・GUIレポートはmanaged UI自己検証で照合する。出典と採取環境は [fixture契約](../Fixtures/WordDiffs/README.md) を参照する。全体実行にも含める。

アーカイブの独立検証にはPython 3が必要で、CIは3.13を設定する。標準ライブラリだけを使い、アプリの配布物には含めない。既定はPATHの`python`、必要なら`--python <実行ファイル>`を指定する。

実装前に想定した失敗を列挙する。

- UTF-8 / UTF-16 BOM、CRLF / LF、最終改行なしの入力を誤って読込み、比較や保存でバイト列が変わる。
- 大文字小文字・空白・空行を無視する指定が CLI から比較処理へ渡らない。
- ASCII 数字の無視で文字の差まで消える。CStyle / Python / Xml のコメント除去が複数行状態や引用符内のマーカーを誤って扱う。
- C++ の R / u8R / uR / UR / LR raw string 内の引用符・コメントマーカーを誤って解釈し、文字列本文の差分を消す。
- 正規表現置換・大文字小文字指定・複数の置換ルールが比較へ渡らない。不正な置換正規表現が正常終了する。
- 空白モード trim / changes / all / none がそれぞれの比較契約を満たさない。
- 競合の LEFT / BASE / RIGHT 選択が非競合変更を失う、CRLF・UTF-16 / UTF-8 BOM・最終改行なしを保存時に変更する。
- 三者マージで左右一致や片側だけの変更を処理するとき、採用側の LF を祖先の CRLF へ戻してしまう。
- 独立した三者変更を失う、競合を正常終了として扱う、競合マーカーが保存されない。
- 生成したパッチや外部 unified diff が適用できない、最終改行なしの内容を破損する。
- 再帰フォルダー比較で深い階層・片側だけのファイルを見逃す、シンボリックリンク経由で外部や循環を追う。
- バイナリの途中の違い・長さの違いを見逃す。
- 存在しないパスや不正な正規表現で異常終了する、エラーに終了コード 2 を返さない。
- ネイティブ発行物に対する CLI 呼出しが動かない、処理が停止して検証が終了しない。
- WinMerge XML の相対パスや比較オプションが JSON 保存・再読込みで失われる。DTD や不正 XML を受け入れる。
- 詳細比較設定の JSON 保存で数字・コメント構文・空白モード・置換ルールの順番や無効状態が失われる。旧 `.WinMerge` の white-spaces 1 / 2 を同じ状態へ変換する。
- 旧プロジェクトの ignore-comments を CStyle に固定して Python / C# / XML の構文を誤る。右・祖先側だけが既知の拡張子でも推定できない、未知の形式へコメント除去を適用する。
- HTML レポートで入力中のタグが実行可能な HTML になる。
- フォルダーコピーが内容を失う、親パス・絶対パス・リンク経由で比較ルート外を書き換える。コピー先がコピー元の祖先になる重複パスで入力を書き換える。
- CSV の引用符内の改行・区切り文字を誤読する、不正な引用符を正常入力として扱う。
- JSON のキー順や同値な数値表現を差分とする、配列の順序変更を見逃す。
- 固定 seed の空文書・混在改行・最終改行なしのパッチ往復が、対象バイト列と一致しない。
- XML の属性順を差分とする、DTD を受け入れる。HTML 可視テキストに script / style の内容を混ぜる。
- XML の `xsi:type` が参照する名前空間 URI の変更を見逃す、同じ URI の prefix 変更を差分とする。
- HTML 本文の `<` / `>` をタグと誤認して削除する、`</scripture>` を script 終端と誤認して JavaScript を本文へ漏らす。
- PPTX の `presentation.xml` と relationships に定義された表示順の変更を見逃す。
- DOCX の本文変更を見逃す。XLSX の sharedStrings 参照・セル順・数式を誤って比較する。
- TAR の内容・項目名の変更を見逃す、比較だけでアーカイブをディスクへ展開する。
- 7z / RAR の solid ブロックを先頭だけで検証し、後続エントリの内容やハッシュを誤る。
- 暗号化 7z / RAR4 / RAR5 / WinZip AES で正しいパスワードが使えない、未指定・不一致を正常終了する、パスワードを argv / 記録へ残す。
- 7z 再パック・新規作成で名前やバイト列を失う。再パックの出力が入力自身・読取り専用の場合に原本を変更する。入力フォルダー内へ作るアーカイブに出力自身や一時ファイルを含める。
- unsafe / duplicate エントリや壊れた ZIP を受け入れる、読込みでルート外へ展開する。ZIP の宣言 CRC=0 と実データ CRC の不一致を見逃し、失敗した再パック・エクスポートで既存出力を破壊する。
- SharpCompress の出典・ライセンス・fixture SHA256 が記録されない、発行物にライセンスが同梱されない。
- Unix で再パック・エクスポート時に既存出力の 0600 権限を失う。macOS の大小文字非区別ファイルシステムで入力の別名を出力にして原本を上書きする。別名が同一ファイルを指さないファイルシステムは理由付きで省略する。
- ローカル HTTP の Web ソース／可視テキスト比較が不正な差分や失敗を返す。
- 外部プロバイダーの実行ファイル・stdin/stdout 経路が動かない、不正なプロトコル版や 4 MiB を超える応答を受け入れる。
- 旧 `.flt` の既定 include / exclude、ファイル・ディレクトリ規則を逆に扱う。
- 読取り専用の既存出力をマージで変更する、Unix のパッチ保存で実行権限を失う。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
Invoke-DevPowerShell -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory "$runRoot/z-reference"
$zReferenceName = if ($IsWindows) { 'ncompress.exe' } else { 'ncompress' }
$zReference = (Resolve-Path -LiteralPath (Join-Path "$runRoot/z-reference" $zReferenceName)).Path
Invoke-DevDotnet build DiffBeacon.slnx -c Release
Invoke-DevDotnet run --project tests/DiffBeacon.E2E -c Release --no-build -- --output "$runRoot/e2e" --z-reference $zReference
```

プロバイダーのレビュー境界 5 ケースだけを再現する場合は `--provider-boundaries-only` を追加する。CLI とファイル形式を通す E2E の経路は全体実行と共通である。

外部プロバイダーの4ケースだけを再現する場合は `--external-provider-only` を追加する。`--app` が `--artifacts-path` のビルド出力なら、同じ `bin` 配下の `DiffBeacon.FakeProvider` を実行し、見つからない場合は通常のRelease出力を使う。

詳細テキスト比較・競合選択と設定保存の 42 ケースだけを実行する場合は `--text-advanced-only` を追加する。競合選択は LEFT / BASE / RIGHT 各経路を UTF-16 BOM と UTF-8 BOM の両方で保存し、CRLF と最終改行なし、左右の独立変更を含めて出力をバイト単位で確認する。通常マージは CRLF 祖先に対する左右同一 LF と片側変更 LF の保存を確認する。設定保存は実プロセスの `--project-copy` で全フィールド・置換ルール配列順と旧空白モードの区別を確認する。

複数比較プロジェクトだけを検証する場合は`--projects-only`を追加する。実アプリの`--project-copy`で全entryの順序・選択位置・設定、旧単一JSONの省略値、旧XMLの相対パス／URL、置換ルール既定値、過大／不正入力の拒否、readonly・リンク出力の原本保持を確認する。GUI自己検証は実際の全タブ復元・保存・確認キャンセル・表設定・readonly編集／形式別保存の操作をPNGとJSONに残す。

旧プロジェクトの正式な `ignore-comment-diff` と既存互換の `ignore-comments` を受け入れ、両方ある場合は正式タグを優先する。コメント構文は、左・祖先・右の順に最初のサポート済み拡張子を使う。Python は `.py` / `.pyw`、C# は `.cs`、XML 系は `.xml` / `.xaml` / `.axaml` / `.svg` / `.html` / `.htm`、CStyle は C / C++ / Objective-C のソース・ヘッダー拡張子に限定する。未知の形式だけの場合は本文の差分を誤って消さないよう None とする。ファイル本文・shebang による推定や三者それぞれの異なる構文設定は未対応。`--legacy-comments-only` はこのインポート経路の 10 ケースを実行し、`--text-advanced-only` と全体実行にも含める。

`--app <実行ファイル>` で Native AOT 発行物を検証する。省略時は Release のアプリ DLL を別プロセスで実行する。`--output` の下に入力、出力、標準出力・標準エラー、コマンド、終了コード、アサーション JSON を保存する。シンボリックリンクが作成できない環境は理由付きでその検証を省略する。UI の操作・画像はアプリの `--self-test` と発行スクリプトで別途検証する。

macOS のアプリ起動では、固定 SHA の `process_startup_gate.py` が nonce と実 PID を通知して待機し、harness が実 OS の開始時刻を取得してから同じ PID の `exec` でアプリを実行する。既存の検証用 Python を使い、製品の依存には追加しない。通知と native の stdout を分離し、UTF-8 stdin と EOF を引き継ぐ。通常 CLI は30秒、既知の自己検証 selector は120秒、`image-defaults-reader` は300秒、フォルダー大容量コピー／GUI は180秒で制限する。`--self-test-independent-text-inputs` だけは、CI osx-x64 の同一製品 SHA で入力選択145.6秒＋critical17.4秒を正常完走した実測に合わせ、両段階と起動を240秒の有限枠で扱う。この policy は通常起動と macOS gate 起動で共通とし、native 全体自己検証の Windows／macOS両方に1,200秒の有限枠を使う。これらの実行制限に加え、異常時の終了待機と出力回収は共通の5秒枠で扱う。実終了コード・終端の観測・部分出力を `LaunchEvidence` に保存し、launcher の初期化失敗や回収不完全をアプリの期待終了コードと同一視しない。秘密の stdin は記録しない。固定 script の `-text` 属性を維持する。Windows の起動経路は従来どおりで、実 Mac での成功は対象 SHA の GitHub runner 結果を確認して判定する。

パッチ往復は 60 個の固定 seed を記録し、それぞれの入力・生成パッチ・適用結果を保持する。再実行で既存入力を消さないよう実行ごとに固有の fixture ディレクトリを作る。検証で作成した循環リンクだけは、保存した LinkTarget と一致することを確認して終了時に解除し、リンクのパス・ターゲット・解除結果を JSON に残す。

`tests/DiffBeacon.FakeProvider` は外部プロバイダーの E2E 専用実行ファイル。ソリューションの Release ビルドで同時に生成し、正常応答・不正プロトコル版・応答上限を実プロセスで検証する。Web の検証は `TcpListener` を使って 127.0.0.1 の空きポートにだけ待受け、検証終了時に停止する。外部ネットワークへ接続しない。

`--archives-only` は実ファイルの 7z / RAR / ZIP 読込み・暗号化・再パックと、TAR.Z の読書き・独立復号も CLI 経由で検証する。入力 fixture は `tests/Fixtures/Archives/manifest.json` に固定した公式 SharpCompress 0.50.4 のファイルで、出典とライセンスを同じディレクトリに保持する。公開テスト用パスワードは stdin だけで渡し、コマンド引数・JSON レポートへ記録しない。全体実行にも同じ検証を含める。

ZIP/TAR系作成と全件抽出は、実装前に次の失敗条件を検証契約として固定する。未知の出力拡張子、破損CRC、unsafe/重複/大小文字衝突/ファイルを親に持つ格納名、リンク、既存の展開先はエラーにし、原本・既存出力を保持する。抽出失敗で新しい展開先や途中ディレクトリを残さない。成功時は空ディレクトリ・Unicode名・全バイト・SHA-256を保持する。作成形式は7z/zip/jar/ear/war/xpi/tar/tar.gz/tgz/tar.bz2/tbz2/tbz・tar.Z/tazを通し、ZIP/TAR/GZipはBCLからも内容を読む。BZip2はPython標準ライブラリで独立に全名・サイズ・SHA-256を照合する。アプリ依存やユニットテストを追加しない。

`archive_verifier.py`は独立したTAR作成器でもあり、標準的な`.`/`./`ルート表記の入力を生成する。独立読込み・作成のコマンド、終了コード、Python版、OS、全内容のハッシュを`independent-archive.log`へ残す。Windows ARM64の標準tarでUnicode名が読めず作成時に異常終了した実測があるため、OS標準tarに依存しない。GZipの連結メンバーは内容を保持し、末尾欠損は一覧・展開・再梱包の各経路で拒否する。深い暗黙親による保持名の増幅も実ZIP入力から拒否する。UI自己検証は実行ごとに固有のfixtureディレクトリを作り、同じ出力先でも再実行できる。抽出の一時ディレクトリができてからキャンセルし、途中出力の除去を確認する。

## 比較プロジェクトの包装の失敗契約

`--packaging-only` は実アプリの `--package-project` を呼び、包装した ZIP を BCL の `ZipArchive` で独立に読み、格納名と原本の全バイトを照合する。同じケースは全体実行にも含める。実 CLI の `--archive-extract` → `--project-copy` → 比較・パッチ適用まで通し、入力、ZIP、展開物、保存した JSON、適用結果、標準出力・標準エラー、終了コードを `--output` の下へ保持する。結果は `assertions.json` に記録する。

実装前の失敗条件と期待結果は次のとおり。

- 二者の `original/altered`、三者の `1/2/3`、側ごとの共通親からの相対名、Unicode 名、選択順・active・全比較設定を失わない。同じ原本の再選択は重複格納せず、別原本の大小文字・NFC 衝突は拒否する。ファイルシステム上で衝突する二名を別ファイルとして作れない場合は理由付きで省略する。
- 同梱 `project.json` の文書参照は展開後のプロジェクト所在へ解決し、包装前の原本へ戻らない。フィルターはプロジェクトを含む場合に `filters/<選択順>-<名前>` として同梱する。文書を含めない場合は元の絶対文書参照を維持する。
- HTML に入力のタグを実行可能な形で混ぜない。テキストと表のパッチは相対ヘッダーを持ち、展開後の適用で UTF-8 / UTF-16 BOM、CRLF、最終改行の有無を保持する。
- HTTP / HTTPS は参照として維持し、暗黙に取得しない。保存されたプロバイダー ID・unpacker・prediffer を包装処理で実行しない。URL のレポート・パッチは拒否する。パッチは Text / Table の比較を対象にし、対象の比較がない場合は旧方式と同じく空の `patch.diff` を同梱する。
- 空・重複・範囲外・不正な選択、すべての出力オプションを無効化、不正プロジェクト、空または不存在の左右文書、フォルダー入力、256 MiB を超える文書は拒否する。フォルダー比較の包装と Binary / Image / Archive / Provider の詳細レポートは後続工程に残る。
- 未対応の出力拡張子、原本自身、読取り専用出力、リンクおよびリンクを経由する入力・出力は拒否し、原本と既存出力を保持する。拒否と成功のどちらも所有する一時出力・snapshot ディレクトリを残さない。

生成HTMLの32 MiB超過は、HTMLエスケープで膨らむ実文書の包装から拒否を確認する。AutoのTARはテキストパッチにせず、7z/TAR.GZ/TAR.BZ2でも包装→展開→同梱プロジェクト読込み→文書バイト列を確認する。

GUI の未保存内容・非同期の選択変更・キャンセル・OS のファイルクリップボードは CLI E2E とは別の UI 検証対象である。入力と生成物の合計 1 GiB、生成パッチの32 MiB、異なるボリューム、出力フォルダーの OS 権限は、この限定実行では境界を実測していない。

## 形式別 HTML レポートの実行経路

`--reports-only` は実アプリの `--report-project INPUT_PROJECT OUTPUT_HTML [--entry N]` と包装の `--package-project --report` を通す。同じケースは全体実行にも含める。単体 HTML と BCL `ZipArchive` から読み出した `report.files/1.html` を保存し、入力、コマンド、終了コード、標準出力・標準エラー、`assertions.json` とともに再現可能な成果物にする。差分がある場合もレポート生成の成功は終了コード0とし、既定activeと1始まりの明示選択を確認する。従来の `--report LEFT RIGHT OUTPUT_HTML` の Text 経路も維持する。

- Text の二者・三者に左右・祖先の本文と description、行番号、行内変更 span を含める。本文・description のタグはエスケープする。左右が同じでも祖先だけ異なれば三者全体を差分ありとする。
- Table は独自の delimiter / quote / 引用符内改行設定でセルを比較し、行・列座標と各側のセルを表示する。empty と missing は異なり、大文字小文字・空白・数字・無視する行パターン・置換ルールなどの比較オプションをセルへ適用する。不正 quote、許可されない引用符内改行、祖先だけの不正パースは既存出力保持で拒否する。
- Json はプロパティ順を正規化して同値とし、値変更を差分とする。不正な祖先 JSON は拒否する。単体 HTML は外部リソースや相対リンクを必要としない。
- `body` の `data-mode` / `data-different`、各側の `data-side`、Text の `data-line` と `inline-diff` span、Table の `data-row` / `data-aligned-row` / `data-column` / `data-missing` を検証する。Table の `data-row` は各側の元論理行番号、`data-aligned-row` は表示行番号であり、ghost の元論理行番号は空とする。
- 無効な entry、未知・重複オプション、未対応の Binary / Image / Archive / Provider / Folder、URL入力、容量超過、readonly・リンク・原本・祖先・非選択文書・フィルター・元プロジェクトへの出力を終了コード2で拒否し、既存出力と原本を保持する。所有一時出力と包装 stage を残さない。リンク作成不能は理由付きで省略する。

CLIへのOS signal注入はこの限定実行に追加していない。キャンセルはUI自己検証のtoken取消経路で別途扱い、通常デスクトップでのCtrl+Cや外部リソースのブラウザー通信はこのHTML構造確認から実測済みと推定しない。

表の行整列は二者の中間挿入・削除、三者の独立挿入・同じ挿入・祖先だけの変更を通す。単体と包装内の HTML の各比較行を解析し、各側の全元行が一度だけ元順序で現れること、後続の一致アンカーが同じ表示行へ対応すること、各セルの元値が失われないことを照合する。quoted CRLF を一つの論理行として扱い、独自引用符・二重引用符によるエスケープ・末尾空セルとmissingの区別も確認する。正規化で差分を無視したセルも元の値を表示する。解析した対応表を `*-mapped-table.json` に保存し、単体 HTML と BCL で読み出した包装 HTML の対応表を完全一致で確認する。旧 `--table LEFT RIGHT` の二者 CLI は従来の `rows`（元文書の最大行数）を維持し、追加の `alignedRows` / `mapping` が HTML の表示行数・元行対応と一致し、小さなケースでは `alignmentFallback=false` となることを確認する。取消・GUIのセル編集と保存は親の別検証へ引き継ぐ。

表の解析上限は論理行 262,144、セル 1,048,576、本文 64 Mi 文字。行合わせの変更区間で左右行数の積が262,144を超えると、決定的なfallbackを使う。600行ずつの完全一致アンカーがない反復値入力で `alignmentFallback=true`、表示600行、左右の元行1～600が一度ずつ順番どおりに対応することを実CLIで確認する。262,145単セル行と1行1,048,577セルは、同じパスを左右へ指定しても終了コード2で拒否し、成功JSONを出力せず原本を保持する。入力は約0.5 MiB・2 MiBで、stderrと入力の長さ・論理行数・セル数・SHA-256を `table-capacity-inputs.json` と既存コマンド成果物へ保持する。本文64 Mi文字と上限ちょうどの高コスト境界はこの追加E2Eでは実行しない。

TAR.ZのGUI自己検証はAuto/picker/preview/export/extractに加え、標準tar/tar-metadata選択の全canonical本文・metadataと変換後テキスト保存拒否を確認します。展開32 MiBの反復entryを含む小さい圧縮入力で、比較開始前にpostした実「中止」ボタン操作が前回本文を保持したまま実行され、取消表示と入力SHAが保持されることを検査します。archive-z-gui-providers.json、archive-z-gui-cancel.jsonとPNGを保持します。

標準TAR providerの結果完成後・採用前の中止も、実Builtin tar-metadata結果と実「中止」ボタンで再現します。default-nullの内部GUI採用境界callbackで中止し、完成した全canonical本文とmetadata、最終両Editorの前回本文、取消表示、入力SHAをarchive-z-gui-late-cancel.jsonと編集4ペインPNGへ保存します。早期取消も最終本文保持までassertします。

### 裸BZip2／Zの限定読込みとv9

`--bare-compression-only`は[固定原本](../Fixtures/Archives/BareCompression/README.md)を使う実アプリ別processの限定E2Eです。実行前にraw／空／TAR風本文のAuto／File／Tar、cross-kind設定、各層設定とidentity、v9／旧version拒否、入力・既存出力・readonly／link・共有予算・取消・後続破損の失敗条件を確認します。workspaceの複製／包装・v9保持はCLI限定契約で、旧候補build3での実測です。裸BZip2／Zのwriter、内側repack、複合writerを追加した検証ではありません。限定GUIは初回再試行・左右picker・拡張子なしinner Open・保存版・workspace保存／復元・世代／取消・失敗時の確定表示保持と通常／最小window一覧を対象にします。

.NET 11 SDKと既存lockを維持し、同じsourceからビルドした絶対パスの`$e2eDll`／`$appDll`、固定公式decoderの`$zReference`、Pythonの`$python`を指定します。出力は冒頭の一意な外部`$runRoot`へ置き、repo内・ドットfolder・Codexユーザーディレクトリ・名前が`artifacts`のfolderへ作業物を出しません。通常build・decoder buildの出力も外部へ明示指定します。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --output "$runRoot/e2e/bare-compression" --z-reference $zReference --bare-compression-only
Invoke-DevDotnet $appDll --self-test "$runRoot/ui/bare-compression" --bare-compression-only
$cliRuns = @(Get-ChildItem -LiteralPath "$runRoot/e2e/bare-compression/fixtures" -Directory | ForEach-Object { Join-Path $_.FullName 'bare-compression' } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'fixture-input-proof.json') })
if ($cliRuns.Count -ne 1) { throw 'CLI reader対象のrunを一意に決定できません。' }
& $python -B -X utf8 tests/Fixtures/Archives/BareCompression/verify.py --root tests/Fixtures/Archives/BareCompression --z-reference $zReference --run $cliRuns[0]
$uiRuns = @(Get-ChildItem -LiteralPath "$runRoot/ui/bare-compression/fixtures" -Directory | ForEach-Object { Join-Path $_.FullName 'bare-compression' } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'proof.json') })
if ($uiRuns.Count -ne 1) { throw 'UI reader対象のrunを一意に決定できません。' }
& $python -B -X utf8 tests/Fixtures/Archives/BareCompression/verify-ui.py --run $uiRuns[0]
```

CLI readerの`--run`はE2E出力下の`fixtures/<時刻とrun ID>/bare-compression`で、`fixture-input-proof.json`がある実runを指定します。UI readerは`proof.json`と`expected.json`がある実fixture runを指定し、親の`--output`directoryを渡しません。[UI-CONTRACT.md](../Fixtures/Archives/BareCompression/UI-CONTRACT.md)のbounds／PNG／原本／全byte照合を確認します。CLIの実PID・出生・終了・stdout／stderr・`assertions.json`、独立readerの終了とEOF、GUIのPNG・bounds・proofとreceiptを保持します。限定GUI候補build10は83成功／0失敗、reader67ケース・23原本・15復号ケースです。通常一覧257 DIP／最小850×550一覧101 DIPを確認しました。160命令・395成功のCLI証拠は旧候補build3のものです。source292は実repoへ反映済みですが、通常solution build・全体E2E／全体UI・Native・他RID・通常desktop・実OS clipboardはまだ未検証です。候補限定結果をこれらの資格へ読み替えません。現時点の資格は[移行記録](../../Docs/MIGRATION.md#裸bzip2zの読込みとv9ワークスペースsource292候補2026-10-09)を参照してください。全体E2Eは`--app`／`--output`／`--z-reference`を明示して実行します。

### 裸の gzip と階層別の格納名文字コード

`--bare-gzip-only` は [固定CC0原本](../Fixtures/Archives/BareGZip/README.md) のseed32件＋追加11件と固定TAR原本を使い、既存240命令と本文形式の追加411命令、合計651命令を実アプリで確認します。既存のlist／entry／compare／extract／repack、typed Source、v7格納名設定・Text／Binary保存版・包装・HTMLを維持し、Auto／File／Tar、TAR風本文と空gzip、入れ子の各階層、完全性・共有予算・既存出力保持、v8の選択identity・保存版・複製／包装を追加します。名前は最初memberだけ、本文は全memberを検証します。FHCRC／CRC／ISIZE／EOF、末尾zero padding、危険path／名前復号失敗、CLIの不正値／重複／右指定の用途違い、descriptorの配列長／未知／重複field、旧versionへの設定混入を拒否します。root932＋inner65001、1252／1251、DBCS末尾`0x5C`も対象です。

writer追加前のsource284の追加411命令はseed192、本文・拡張子matrix80、repack／extract4、root予算15、破損9、TAR FNAME3、長名3、UTF-8名2、不正CLI8、比較3、非gzip4、入れ子56、不正descriptor17、workspace15です。成功時だけ実行するentry／export枝も固定名・期待exitのmultisetへ含めます。全651命令・1,302本のstdout／stderr、実PID／出生／終了、全期待exit、最終assertionsとfixture原本・source／payload不変を監査し、成功枝が欠けた部分実行を合格にしません。

読込みCLIは`--gzip-payload-kind auto|file|tar`、比較右側の上書きは`--right-gzip-payload-kind auto|file|tar`です。右指定の省略は左設定を継承し、Source descriptorでは`containerGZipPayloadKinds`にroot＋各containerのAuto／File／Tarを指定します。

同じソースから生成した外部ビルドの `$e2eDll`／`$appDll` を指定して、リポジトリルートで実行します。出力先は冒頭の一意な外部 `$runRoot` を使います。この限定はZ原本を使わないため `--z-reference` は不要です。harnessから限定headless自己検証も別processで呼びます。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --output "$runRoot/e2e/bare-gzip" --bare-gzip-only
Invoke-DevDotnet $appDll --self-test "$runRoot/ui/bare-gzip" --bare-gzip-only
```

Native AOTでは同じ限定E2Eの `--app $appDll` を `--app $appExe` へ置き換え、自己検証は `& $appExe --self-test "$runRoot/ui/bare-gzip-native" --bare-gzip-only` とします。各DLL／exeは絶対パスを明示してください。構文検証・fixture原本の独立復号・限定成功を、製品の全体実行・Native AOT・別RIDの成功へ読み替えません。

通常全体と `--archives-only` にも登録しています。全体E2Eは既存の公式Z decoderを明示します。例は `dotnet $e2eDll --app $appDll --output "$runRoot/e2e/all" --z-reference $zReference` です。全体UIは `dotnet $appDll --self-test "$runRoot/ui/all"` とし、既存の時間制限を変更せず最終集計まで確認します。

E2Eの入力・出力・実終了・stdout／stderr・`assertions.json`、`bare-gzip-independent-proof.json`、追加411命令と原本SHAを持つ`gzip-mode-proof.json`を保持します。限定headlessでは`ui-report.json`・`bare-gzip/ui-proof.json`・`bare-gzip-confirmed.png`と本文形式のraw／PNGを保持し、writer追加前の78 unique checksを照合します。writerを含む現行限定headlessは旧78＋writer11＝89 unique checksを固定します。候補460Cでは別processの専用独立readerが固定入力・保存版全byte・ZIP／PNG・操作後のrawを検証して終了0でした。readerの実終了・EOFとproducerとの結合を保管し、producerのboolだけを独立照合の代用にしません。GUIは名前復号再試行、本文形式picker、確定／取消／再比較・stale／失敗後の表示保持、親と保存版の選択identity、root保護を確認します。候補managedの限定結果と取り込み後repoの通常build／全体E2E／Native AOT／他RIDは別資格です。実OS pointer・ネイティブdialog・視覚goldenとの比較は未検証です。実測のDLLとreceiptは[移行記録](../../Docs/MIGRATION.md#gzip本文の形式選択とv8ワークスペース)を参照してください。

限定writerは`--archive-create SOURCE_FILE_OR_DIRECTORY OUTPUT`と`--archive-repack INPUT OUTPUT`の裸`.gz`／`.gzip`を対象にします。`--output-gzip-name-code-page`は出力専用で、読込みの`--gzip-name-code-page`から継承しません。単一論理file（空を含む）、safe basename FNAME・Latin-1既定と明示UTF-8／932／1252、CRC／ISIZE・単一DEFLATE EOF・MTIME範囲・TAR優先、directory／複数entry拒否、既存出力・原本・readonly／link・取消保護を確認します。別readerは出力gzipの全bytes・FNAME・単一DEFLATE EOF・CRC／ISIZE・空memberを独立照合し、MTIMEや操作状態まで独立検証したものとは扱いません。候補App8A024／E2E4C67の限定実測は698命令・1,829成功／0失敗／0skip、13gzip独立照合です。候補UI89では4create＋1rootRepackの5gzip全bytesと3actualmodal PNGを独立readerで照合しました。`bare-gzip/writing/ui-proof.json`、parent before／after PNG、modal PNG、writer raw／終端／EOFとreader receiptを保持します。modal bounds／owner寿命／親状態snapshotはproofに無く、producer boolだけで独立資格を与えません。source288の通常repo build／全体runtime、Native／他RID／通常OS pickerは未資格で、source284全体成功は前版履歴です。[移行記録](../../Docs/MIGRATION.md)を参照してください。


### 明示 archive wrapper 鎖の限定実行

`--archive-wrappers-only` は ZIP派生/7z/RAR＋gz/bz2/Zの固定鎖、全entry export、compare/repack/extract、暗号化stdin、破損後member/終端/深度と出力保護を実App別プロセスで検証します。通常全体と `--archives-only` にも接続します。既存 Build-ZReference のdecoderを明示します。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --archive-wrappers-only --z-reference <absolute-existing-ncompress> --output $runRoot/e2e/wrappers
```

`archive_wrapper_verifier.py` はPython標準のstrict各member復号と明示公式Zを使い、全層・終端bytes/SHA、ZIP全entry bytes/timeを独立照合して `wrapper-oracle/proof.json` を保存します。小予算・取消・GUI候補採用/Refresh/実Stop/Retry/Cancel/×は App のheadless自己検証にJSON/PNGを残します。限定実行は全体E2Eの代替ではありません。新wrapper出力形式や内側entry navigationは検証済みとして扱いません。

E2E harnessは未知option、値欠損、同じoptionの重複、複数selectorをoutput作成/App起動前に終了コード2で拒否します。wrapper限定はこの前処理もharness別プロセスで検証し、11組のstdout/stderrを保持します。

### 明示した内包アーカイブ

`--archive-tar-wrappers-only` は [TAR多層wrapper原本](../Fixtures/Archives/TarWrappers/README.md) の正常22件・拒否22件とSource用ZIP4件を実アプリで照合します。通常list・entry・展開・repack、標準プロバイダー、typed root／Sourceを確認し、独立readerは正常ケースのID集合・一意性と各typed-root成果物を必須にします。共有作業量は入力SHAの上限拒否と、SHA確認後のアーカイブ処理中の上限拒否を別々に検証します。通常全体と `--archives-only` にも含み、限定実行は全体E2Eの代替にしません。

`--archive-sources-only` は実アプリのSource専用CLIへdescriptorを渡します。通常全体と `--archives-only` にも含みます。[固定入力](../Fixtures/Archives/Sources/README.md)を変更せず、ZIP内ZIP、raw TAR、未知名gzip／bzip2 TAR、内側wrapper、深度8／9、二層の異なるpasswordとplaintext空行を検証します。最終全entryのsize／SHA、empty file、directory／missing拒否、後続sibling／内側CRC破損、root同サイズ・同mtime差替え、共有復号量の正確なbyte境界、件数・名前・深度・作業量、descriptor／root／readonly／link出力保護を確認します。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
$env:RUNNER_TEMP = $runRoot
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --archive-sources-only --output $runRoot/e2e/sources
```

280MiBのTARは各140MiBの二entryを含む固定圧縮入力でlistし、巨大raw fileを保存しません。Python標準libraryは全入力SHA・サイズ、全container CRC／内容、raw TAR SHA、取得した全entry bytesを独立照合し、run内の `archive-sources/independent-proof.json` と標準出力・エラー・終了コードを保持します。Windowsのlink作成特権がない場合はその操作をskipとして区別し、管理者／GitHub runnerで別途実行します。今後の検証用linkとtargetは小さい `<出力先>-source-links-<GUID>` siblingに分け、run内とsiblingの `retained-links.json` に絶対pathを保持します。過去runのlinkは移しません。Source CLIの実CancellationToken取消、GUI子タブ・workspace・包装・HTMLは、この限定実行の検証済み範囲へ含めません。限定実行は全体E2Eの代替にしません。

`--archive-project-only` はversion 2のtyped Source入力で二者／三者・混在・empty、内包ZIP／raw TAR、gzip rootのText 7比較と物理中央入力を持つ三者Binaryを作り、実アプリのproject-copy・report-project・包装・製品展開・再読込み・leafの外部patch適用を確認します。通常全体と `--archives-only` にも含みます。[独立検証](../Fixtures/ArchiveProjects/README.md)はPython標準libraryで全container／包装entryのCRC・bytes、root SHA・chain・leaf、本文・caption、3patch適用結果を照合します。schema・既定version・未知／重複DTOfield・readonly解除・root変更・missing／directory・入力／descriptor／既存出力保護も確認し、products／independent-proof／入力・標準出力・エラー・終了コード・assertionsを保持します。暗号化Sourceのworkspace包装／CLI reportへpasswordを渡す経路は未対応です。

`--archive-missing-only` はversion 3の不在入力について、新規・削除・空ファイル・仮想の内包階層・両側不在・祖先不在の8比較を実アプリで保存、HTML化、包装、展開、再読込みします。通常全体と `--archives-only` にも含みます。[独立reader](../Fixtures/ArchiveMissing/README.md)は元containerの全CRCとSHA、不在の起点、HTMLの全文と改行、包装の全ファイルを照合します。さらに6件の包装patchを実アプリで外部テキストへ適用し、原本から独立に得た右本文と全byteを照合します。不正schema、実在するfile／directory、後続CRC破損、原本SHA不一致の拒否では既存出力の保持も確認します。継承readonlyの真偽値の往復と不正型の拒否、実GUIの同じ側での編集・差分コピー・外部SaveAs・取消・古い完了・保存中の編集・実在0byte・復元・包装を実アプリ別processで検証し、[未命名reader](../Fixtures/ArchiveDraft/README.md)で原本・HTML全本文・包装全byteを照合します。その包装patchも独立生成した入力へ実アプリで適用します。ネイティブ保存ダイアログとheadlessの注入経路は区別します。限定実行は全体E2Eの代替にしません。

内包画像の故障条件と期待結果は次の通りです。既知の画像entryをAutoと明示Imageで開くと、全フレームを既存デコーダーで比較し、親アーカイブは不変、画像側は読取り専用になります。破損画像・誤パスワードでは子タブを追加せず、再試行または取消後も親一覧と原本SHAを保ちます。画像読込み中のStop、親の再読込みでの世代変更、親タブ終了では候補を破棄して古い子を採用しません。workspaceの往復ではroot SHA・entry chain・leafを保ち、復元後の明示的な再比較まで画像を読み込みません。証拠は画像の各フレーム全画素ハッシュ、workspace JSON、親root SHA、PNGと標準出力・エラー・終了コードへ残します。

GUIは全体 `--self-test <出力先>` の `HeadlessArchiveSourceChecks` で実controlと子タブ追加を確認します。開発時には `--self-test <出力先> --archive-sources-only` で同じSource画面操作だけを限定実行できます。結果JSONのscopeを区別し、全体UI検証の代わりにはしません。内側一覧・Text／Binary/Image・readonlyと原本保存拒否・empty・二階層password・DTO複製・workspace明示復元・三者／混在・HTML・別名export、早期／完成後Stop・parent-close・refresh世代・tab-switch、同size／mtimeのSHA差替えを確認し、PNGと `archive-sources/source-gui-proof.json` を保持します。内包ImageはAuto／明示選択、GIF全フレームのBGRA hash、固定readonlyとworkspace再開を検証し、`image-child-proof.json` をPython標準libraryで画像期待値・元root ZIP・内側ZIPに照合します。1-frame静止PNGでreadonly解除を試しても保存が「読取り専用」で拒否され、入力rootが保持されることも確認します。採用済み内包画像の表示設定変更は注入したoptions fileへ保存し、同じleafに別paneからBinary作業版が保存されてもImage workspaceはその作業版を含まずrootから再読込みされることを確認します。4096bytesを超えるleafはprefixだけ保持して全CRCを検査します。通常／最小windowの一覧・previewの実viewportとscroll後の比較button、再比較のSource読込み、exportのread前／公開前Stopと既存出力SHA、取消後のpreview操作、全タブのroot・通常入力・filter・workspace出力保護も確認します。通常デスクトップとネイティブダイアログはこのheadless検証から推定しません。

### Binary Copy All

`--binary-copy-all-only` は実アプリ別プロセスの `--self-test <output> --binary-copy-all-only` を起動し、二者両方向と三者全6方向の実ボタン・実確認dialogのキャンセル／続行を検証します。短いsourceの末尾保持・空／同一bytesのno-op・読取り専用・未適用Hex拒否・確認中のowner／StateStamp／再比較／readonly変更・共有履歴上限を確認します。通常保存、内包Binaryの作業保存、project-copy・包装展開／再読込みも実行します。[独立reader](../Fixtures/BinaryCopyAll/README.md)が固定期待bytesと元入力・ZIP entry／CRC・workspace／包装assetを全byte照合します。PNG・facts.json・assertions.json・各プロセスstdout／stderrを保持します。全体E2Eと全体UI自己検証にも含まれます。ネイティブ保存dialog・実OSpointerはこのheadless検証に含みません。

### バイナリ範囲編集の限定検証

#### バイナリ置換

`--binary-replace-only` は実アプリを `--self-test <GUI出力先> --binary-replace-only` で別プロセス起動し、実ボタン・Ctrl/Command+H・ダイアログの次／前検索・選択置換・前方／後方全置換を検証する。固定Frhed51原本の全bytes／実置換数／選択／caret、二者／三者全側、ANSI／OEM・文字列／バイトコード、空置換、共有Undo／Redo・保存点、readonly・未適用Hex・取消・古い完了・タブABA、16 MiB／作業量／履歴上限の拒否を確認する。失敗時に本文・選択・revision・既存Redo・保存点が保持されることを検査する。

`BinaryReplaceScenarios` は `verify.py <reference-product.ndjson>` と `verify_gui.py --gui <GUI出力先> --receipt <新規JSON>` を別プロセスで呼び、通常／内包の保存版からCLIのproject-copy・package-projectを実行した後、`verify_cli.py --work <E2E出力>/fixtures/<run-id>/binary-replace --receipt <同work直下の新規JSON>` で全bytes・ZIP全entry／CRCと実終了metadataを照合する。入力／出力・PNG・proof.json・NDJSON・assertions.json・stdout／stderr・各readerのprocess metadataとreceiptを保持する。GUI単独ではCLI readerの前提を満たさない。具体的な再実行手順は[置換fixture](../Fixtures/BinaryReplace/README.md)を参照する。全体E2E／UIにも含むが、限定成功は全体／AOT／他RIDと実OSクリップボード・ネイティブ保存ダイアログの検証を代替しない。


`--binary-clipboard-only` は固定Frhed codec634件を実アプリの別プロセスで出力し、[独立reader](../Fixtures/BinaryClipboard/README.md)でraw/原関数SHA、全input/golden/output bytesと4件のIEEE独立値を照合する。実AvaloniaのCopy/Cut/Paste/FastPaste/選択dialog、Hex nibble/文字/キー/pointer、4096ページ跨ぎ、repeat/skip、公開／flush失敗、古い完了、readonly、未適用Hex、共有履歴とRedo、通常／内包全側保存・workspace・包装・展開再読込みも検証する。GUI限定は `--self-test <output> --binary-clipboard-only`。PNG、設定/入力、stdout/stderr、assertions、保存bytesと独立reader出力を保持する。全体E2E/全体UIへ含め、限定成功を全機能完了へ読み替えない。

OEMはWindowsの実ACP/OEMCPと全256入力の有界1byte `CharToOemBuffA` 結果を独立readerで採取し、別processの製品decoderと実FastPaste形式dialogの保存byteへ照合する。macOSは1252/437互換値の検証と区別する。Ctrl/Command+Shift+ZのRedoも実key注入で確認する。

実OSのBinary clipboardは `--clipboard-self-test <output> --binary-write` と別processの `--binary-read`。GitHub clean runner以外で拒否する。Windows CF_TEXT/CF_UNICODETEXT/custom raw、macOS標準text/custom rawと全256bytesを記録し、既存画像clipboard検証を維持する。ローカル利用者clipboardは操作せず、ネイティブpointer・保存dialogの操作証拠とheadless注入を区別する。

`--binary-range-edits-only` は実アプリ別プロセスの実ダイアログ、全二者／三者側、訂正・取消・古い結果拒否、境界・上限・共有Undo／Redo・保存点・保存中の後発編集を検証する。内包作業保存・workspace／包装・CLI複製／展開再読込みを `tests/Fixtures/BinaryRangeEdits/verify.py` の固定期待bytesとZIP CRCで独立照合する。GUI限定は `--self-test <output> --binary-range-edits-only`。全体E2Eと全体UIにも含み、限定実行は全体検証の代替にしない。
`--line-transform-logging-only` は固定C++原典 `LineTransformLogging` を独立readerで検証し、実App CLIの `LogInfo`／`LogWarn`／`LogError` と `@name` のseverity・UTF-8 message・評価順・短絡・例外境界を照合します。`--table`・`--report-project`・レポート付き `--package-project` のstderr sink経路も確認します。`--line-transform-only` と全体E2Eにも含め、GUI側の注入sink・最適化時の副作用なし・workspace JSON除外はHeadless UI自己検証で確認します。固定原典の限定範囲は [fixture README](../Fixtures/LineTransformLogging/README.md) を参照してください。

`--line-transform-regex-only` は固定PCRE2/Poco原典432件のうち公開式で表現できる358件を実App CLIで確認します。343件をFind／Count／ExtractのUTF-8 byte位置・group・occurrence・省略capture・空一致・配列と、RegexReplaceの一桁capture展開・置換反復・zero-width・配列・型検査・定数化のgolden値／実行結果へ照合し、15件の誤った引数数は公開parserが拒否することを確認します。RegexReplaceの中間未参加capture参照など15件は実行時または定数最適化時エラーとしてstdout空を確認します。旧adapter専用のtyped Regex、未検証raw UTF-8、ThrowNode、null ASTは製品入力と混同しません。実装範囲は.NET Regexの構文・Unicode表と固定原典で確認した差分補正を用いる有限時間backendで、任意PCRE互換ではありません。限定実行は全体E2Eの代替にしません。固定原典と公開面の境界は [fixture README](../Fixtures/LineTransformRegex/README.md) を参照してください。


## MatchContext・MatchBlock系の限定検証

`--line-transform-match-only` は `MatchContext`・`MatchBlockCount`・`MatchBlockNumber`・`MatchBlockSize`・`MatchBlockOffset` を実App CLIで検証します。固定Native132件／61原典spanを独立readerで照合し、91件の全行serialized-key比較、11件のruntime拒否、30件のnative専用probeを区別します。公開結果は比較JSONの`different=false`による間接観測で、actual keyの直接採取やnative typed value・AST・cache・例外classの同等性とは扱いません。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-match-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformMatches/verify.py
& $python -B tests/Fixtures/LineTransformMatches/verify-public.py --actual $publicObservations
```

`--output` は新しい外部Temp配下を指定します。`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-matches/public-observations.json` です。`native-reader.json`／`public-reader.json` と各CLIの入力・stdout・stderr・終了コードを保存します。追加の公開契約probeでは引数数、全行のpredicate凍結、pane分離、別AST、ルールmask境界、workspace JSON往復、HTMLのignored分類、共有生成予算と既存出力保持を検証します。CLIの取消注入と同一compiled planの複数Transformはこの限定scenarioでは未実行です。UI/headlessのcache・取消検証、および全体E2Eは別に実行します。限定成功を全体E2E・UI・Native AOT・他OSの成功へ読み替えません。[固定原本と再生成手順](../Fixtures/LineTransformMatches/README.md)を参照してください。


## MatchInsideの限定検証

`--line-transform-matchinside-only` は `MatchInside(beginPredicate,endPredicate)` を実App CLIで検証します。固定native原本を独立readerで照合し、`public-case-plan.json` の `serialized-key-comparison` 全件をnative由来の全行期待keyへ比較します。製品側の `@noopt` あり／なしを両方照合し、`runtime-rejection` と `native-only` は分離します。件数はplanから数え、比較JSONの `different=false` を間接観測として記録します。actual keyの直接取得やnative型・AST・cache・例外classの同等性とは扱いません。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-matchinside-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformMatchInside/verify.py
& $python -B tests/Fixtures/LineTransformMatchInside/verify-public.py --actual $publicObservations
```

`--output` は新しい外部Temp配下です。`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-match-inside/public-observations.json`。`native-reader.json`／`public-reader.json` とCLI入力・stdout・stderr・終了コードを保存します。引数数、pane／rule分離、別pairと単一predicate cacheの分離、表経路、ログによるbegin→end順と同一ASTの再走査抑制、別CLI contextでの再走査、throw後のstdout未公開、workspace JSON往復・HTMLのignored分類、共有生成予算と既存HTML保持も確認します。同じcompiled planの複数Transform寿命・取消注入・失敗後の同一state再試行は公開CLIから直接観測できず、このscenarioでは未検証です。UI変更はありません。限定実行は全体E2E・AOT・他OS検証の代替にしません。[固定原本と再生成手順](../Fixtures/LineTransformMatchInside/README.md)を参照してください。

## `matches`／`not matches` の限定検証

`--line-transform-matches-operator-only` は固定原典106件（成功100件・例外6件）を実App CLIで検証します。部分一致・空一致・alternationの先頭非空一致、case directive、scalar／arrayとnone、定数／動的patternの不正入力、評価順を含みます。Text CLIとRAW tableでの比較、project copy後のHTML反映、失敗時の既存HTML保持も確認します。成功ケースはnative由来の期待keyと公開CLI比較を照合し、例外ケースは終了コード・stdout・stderrを確認します。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-matches-operator-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformMatchesOperator/verify.py
& $python -B tests/Fixtures/LineTransformMatchesOperator/verify.py --actual $publicObservations
```

`--output` は新しい外部Temp配下です。`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-matches-operator/public-observations.json` です。限定selectorの成功だけを全体E2E、UI、Native AOT、他OSの検証へ読み替えません。[固定原典と独立reader](../Fixtures/LineTransformMatchesOperator/README.md)を参照してください。

## MatchCount・MatchNumber・MatchDistance系の限定検証

`--line-transform-match-metrics-only` は `MatchCount`・`MatchNumber`・`MatchDistance`・`MatchDistanceBefore`・`MatchDistanceAfter` を実App CLIで検証します。固定native160件／63原典spanを独立readerで照合し、95件の全行serialized-key比較に加えて、native由来の期待keyに対する最適化あり比較も95件実行します。15件のruntime拒否と50件のnative専用probeを分離します。公開観測は比較JSONの `different=false` による間接比較で、actual keyの直接取得やnative型・AST・cache・例外classの同等性とは扱いません。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-match-metrics-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformMatchMetrics/verify.py --verify --git
& $python -B tests/Fixtures/LineTransformMatchMetrics/verify-public.py --actual $publicObservations
```

`--output` は新しい外部Temp配下です。`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-match-metrics/public-observations.json`。scenarioは引数数、pane分離、全行predicateの凍結、表経路、別AST、5関数を組み合わせた式、project JSON往復とHTMLのignored分類、共有生成予算と既存HTML保持も確認します。取消注入、同一compiled planの複数Transform寿命、実GUI/headless、他OS/AOTは別検証です。fixture準備時点では製品build・E2Eを未実行で、限定実行は全体E2Eの代替にしません。[固定原本と再生成手順](../Fixtures/LineTransformMatchMetrics/README.md)を参照してください。

## `like`／`not like` の限定検証

`--line-transform-like-only` は固定Glob原典99件の成功・条件付き構文例外を、実App CLIへ接続します。`@cs`／`@ci`、optimized／`@noopt`、`LIKE`／`NOT LIKE`、実ファイルの`Line`値を比較し、Unicode scalar・集合・range・escape・全体一致・不正setの遅延評価を含みます。Content型のnone、明示的な文字列化、array lifting、左右の評価順、共有予算超過時の入力と既存HTMLの保持も確認します。`public-observations.json`は公開キー比較を記録し、native例外型の同等性や取消注入は主張しません。

```powershell
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- `
  --line-transform-like-only --output $newExternalOutput --python $python
& $python -B tests/Fixtures/LineTransformLike/verify.py
& $python -B tests/Fixtures/LineTransformLike/verify.py --actual $publicObservations
```

`--output` は新しい外部Temp配下です。`$publicObservations` はrun内の `fixtures/<run-id>/line-transform-like/public-observations.json` です。限定selectorは全体E2E、Native AOT、UI、他OSの検証を代替しません。[固定原典と独立reader](../Fixtures/LineTransformLike/README.md)を参照してください。

## recontains の文字列・原文Content検索

`--line-transform-recontains-only` は固定原典の独立readerを起動し、部分一致・空一致、物理行bodyとEOF、UTF-8／UTF-16 LE・BE／CP1252、配列・none・not、定数と動的不正pattern、左右評価順を実App CLIで確認します。TextとRAW Table、変換済みキーから独立したContent snapshot、workspace複製とHTML、既存出力保持、timeoutと共有処理／生成予算の拒否も含みます。

```powershell
Invoke-DevDotnet $e2eDll --app $appDll --python $python --output $runRoot/e2e/recontains --line-transform-recontains-only
```

[固定原典とreader](../Fixtures/LineTransformREContains/README.md)は1140件のnative採取と834件の公開CLI照合を区別します。空file／BOMだけは行変換キーがなくnative専用です。原文のencoding分類は比較snapshotへ渡し、NONE readerの最終非EOL行とUnicode readerのEOF候補の違いを保持します。原本bytes、assertions、CLI stdout／stderr、public-observations.jsonとreader receiptを外部runへ残します。任意codepage・旧自動判定・UTF-32の旧reader互換性、取消注入、GUI・AOT・他RID・全体E2Eの成功は限定結果から推定しません。

## フォルダー `.flt` の `ToInt`／`ToDouble`／`ToNumber` の契約と失敗行列

固定native oracleは `ToInt`／`ToDouble`／`ToNumber` の直接bodyを対象にし、公開parserの厳密arityとnative direct-callの引数挙動を区別します。各関数は引数を一度だけ評価し、文字列だけを受け取り、ASCII commaを全除去した後に全byteを消費できる場合だけ変換します。`ToInt` はbase-10 Int64、`ToDouble` は MSVC `std::stod`、`ToNumber` は cleaned string に `.`, `e`, `E` のいずれかがあればDouble、それ以外はInt64を選ぶため、型と値を別々に照合します。固定oracleで評価順・例外伝播・MSVC特殊値を実測し、公開CLIの注入証拠と混同しません。

失敗行列: int64の両端・境界超過、末尾junk・NUL・先頭／末尾空白・全角空白・内部空白、位置や連続数を問わないcomma削除、`1,,2`、`1.0`／`1e2`の型分岐、hexfloat・NaN／payload・inf・signed zero・subnormal・overflow／underflowを3関数で比較します。string以外（int・double・bool・None・timestamp）はNone、空／混在の直下arrayは一段だけ変換し、nested array要素はstring化せずNoneとなることも確認します。MSVC原典で長いmantissaと指数の相殺を実測し、explicit decimal exponent ±5200は相殺後1.0でも成功、±5201は相殺後1.0でも`out_of_range`となることをToDouble／ToNumberの公開CLIへ固定します。UCRTのdecimalとhexfloatはそれぞれ768桁のmantissaを使い切り、769桁目だけのtailを丸めに使いません。両型の769桁目境界候補はnativeで1.0となること、ゼロmantissaはexponent ±5201でも0.0を保つことを公開E2Eへ固定します。負NaNの公開合成ではToDouble→Trim／Strlenが`-nan`／4、正NaNが`nan`／3になることに加え、`nan(ind)`が`-nan(ind)`／9、`nan(snan)`と`-nan(snan)`が符号を保ち長さ9／10になることを確認します。公開`.flt`では不足／余剰arityのexit 2・stdout空、二者／三者のName／FullPath／Prop string、数値metadataのNone化、input／既存outputのSHA保持と処理予算拒否を確認します。Parse失敗によるNoneと処理失敗を分け、限定実行を全体E2E・UI・Native AOTの代替にしません。

Release buildと公開CLI限定E2Eは、明示したrun出力を使って次のように実行します。

```powershell
$runRoot = 'D:\Claude\DiffBeacon\verify-numeric-<unique>'
$python = '<利用するPython 3実行ファイルの絶対パス>'
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('build', 'DiffBeacon.slnx', '-c', 'Release')
$app = Join-Path $runRoot 'build/bin/DiffBeacon.App/release/DiffBeacon.dll'
$e2eOutput = Join-Path $runRoot 'e2e/folder-properties'
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('run', '--project', 'tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj', '-c', 'Release', '--no-build', '--', '--folder-properties-only', '--output', $e2eOutput, '--app', $app, '--python', $python)
```

2026-10-20のWindows実測はRelease build警告0／エラー0、`--folder-properties-only`が5,398成功／0失敗／2 skip・1,723 CLI命令・5,400 assertionです。skipは実FIFOのmacOS検証と、Windows標準filesystemで大文字小文字だけ異なるファイルを同時に作れない検証です。固定readerは246原典ケース（3評価例外）とgolden SHA `52d07df1d5b0da74350a3d946c344ea8e6fc5aada7513fdedebccb852c2e125a`を照合し、198公開string候補を型付き期待値へ投影、48件はtyped binding／NUL／body専用のためCLI未実行として別記録しました。指数補償の追加probeでは長い16進入力の`p±5200`がdouble 1.0、`p5201`と`p-5201`がnative `out_of_range`となり、実際のCLIでも二者／三者双方で4条件を確認しました。NaN文字列化の追加MSVC probeは8入力表記と9個の直接bit patternをそれぞれ別process 2回で照合し、stdout SHA-256は`3B87441DE28F35744723AB914B2A2C73A596538BF0B095D71E09430A28D3751D`と`E0FEAC43DEFE550227A2BC12C3E5A522BA4C363A8A73D8D707E39BAD2086BC01`です。公開E2Eは`nan(ind)`と符号付き`nan(snan)`の`ToDouble`→`Trim`／`Strlen`を二者／三者で照合します。native bodyの評価traceは固定原典readerの証拠であり、公開managed parserのspy評価証拠ではありません。成果物は`D:\Claude\DiffBeacon\nan-fix-final-20261020-r1\e2e-final`、native probeは`D:\Claude\DiffBeacon\nan-string-20261020-r1`へ保持します。全体E2E・UI自己検証・Native AOT・他OSはこの限定結果に含めません。

## 機能別の検証契約

- バイナリ編集・保存の変更は Core の `BinaryEditSession`・`BinaryFileStore`、App の `BinaryPanel`・`BinaryRangeDialog`・`MainWindow.BinarySaving.cs`・`ArchiveWorkingStore`・`WorkspaceStore`・`ProjectInputReader`・`ComparisonPackage` と通常／内包入力の呼び出し元を照合する。限定 E2E `--archive-binary-only`・`--binary-threeway-only`・`--binary-copy-all-only`・`--binary-range-edits-only` と全体 E2E、全体UI自己検証の `HeadlessBinaryWorkingChecks`・`HeadlessBinaryThreeWayChecks`・`HeadlessBinaryCopyAllChecks`・`HeadlessBinaryRangeEditChecks` を実行し、[Binary作業版の独立reader](../../tests/Fixtures/ArchiveBinaryWorking/README.md)で全原本entry／CRC・保存bytes／SHA・包装展開／再読込み・PNG・dirty／保存点／共有履歴を照合する。v4 Text互換とv5 Binaryのkind・形式別容量、共有store／履歴の上限拒否、全タブ入力・元root・workspace・asset・リンク・readonlyの出力保護、取消・古い完了・形式切替・採用失敗・保存中の後発編集・兄弟revision競合を確認する。三者は [独立reader](../../tests/Fixtures/BinaryThreeWay/verify.py) で全6方向コピー、中央の通常／内包保存、全側の保存点・共有履歴、v5の中央asset・包装展開・再読込みを全byte照合し、混在入力は同自己検証の原本byteへ照合する。全体コピーは [独立reader](../../tests/Fixtures/BinaryCopyAll/README.md) で二者両方向・三者全6方向の保存bytes・原本・ZIP全entry／CRC・workspace／包装assetを照合する。実ボタンと確認dialogで取消／続行、短いコピー元の末尾保持、空／同一bytesで履歴を作らないこと、未適用Hexと読取り専用のコピー先への拒否、確認中の比較・編集状態変更と共有履歴上限での本文保持を確認する。限定実行の手順と成果物は [Binary Copy All](../../tests/DiffBeacon.E2E/README.md#binary-copy-all) を参照する。範囲編集は [限定検証の手順](../../tests/DiffBeacon.E2E/README.md#バイナリ範囲編集の限定検証) と [独立reader](../../tests/Fixtures/BinaryRangeEdits/README.md) に従い、二者／三者の全側の挿入・上書き・削除、実ダイアログの訂正・取消・古い結果拒否、整数極値・末尾・不正Hex・16 MiB境界、64 MiB／256操作の履歴上限拒否で本文・revision・Redo・保存点が保持されることを確認する。保存中の後発編集、通常／内包保存・workspace／包装・CLI複製／展開再読込みを全byte・SHA・ZIP CRCで独立照合する。比較候補の採用境界を変える場合は `MainWindow.cs`・`MainWindow.ArchiveSources.cs`・`ComparisonPane.Table.cs` の呼び出し元も照合し、Archive／Image／Folder／Tableへの切替中のHex編集（通知前の未適用値を含む）、取消・古い完了・採用拒否後の表示と操作復帰を同じ自己検証で確認する。通常／最小ウィンドウと多数タブでHex本文・保存ボタンへの到達も確認する。設計境界は [DESIGN.md](../../DESIGN.md#データフロー) に従い、PNG・JSON・ログ・独立照合結果を保持する。BinaryのHTML／patch・祖先に対する競合分類・元アーカイブへの書戻しや、ネイティブ保存ダイアログを検証済み扱いにしない。

- バイナリ選択・直接入力・クリップボードの変更は Core の `BinaryBytecode`・`BinaryEditSession.Clipboard.cs`、App の `BinaryPanel.Selection.cs`・`BinaryClipboardDialog`・`BinaryClipboard`・`WindowsBinaryClipboard`・`MainWindow.BinarySaving.cs` と通常／内包保存・workspace・包装の呼び出し元を照合する。[限定検証の手順](../../tests/DiffBeacon.E2E/README.md#バイナリ範囲編集の限定検証)の `--binary-clipboard-only` と全体E2E、全体UI自己検証の `HeadlessBinaryClipboardChecks` を実行する。[固定Frhed原本と独立reader](../../tests/Fixtures/BinaryClipboard/README.md)でcodec・OEM・保存bytes・原本ZIP／CRC・workspace／包装展開を照合し、原本SHAとライセンスを保持する。選択の4096byteページ跨ぎ、直接ニブル／文字／キー入力、repeat／skipと容量境界、公開／flush失敗・古い完了・readonly・未適用Hexでの本文／共有履歴／保存点の保持を確認し、PNG・JSON・ログ・独立reader結果を残す。実OSクリップボードはクリーンなGitHub Windows/macOS runnerで `--clipboard-self-test <output> --binary-write` と別プロセスの `--binary-read` を実行し、headlessの注入検証と区別する。ローカルでrunner判定を偽装せず、利用者のクリップボードを検証用に置換しない。設計境界は [DESIGN.md](../../DESIGN.md#データフロー) を参照する。

- フォルダー比較・コピーの変更は Core の `DirectoryComparisonResult`・`DirectoryCopyPlanner`、App の `FolderComparisons`・`FolderCopyPlan`・`FolderCopyPreparation`・`FolderCopyExecution`・`FolderCopyStreams`・`FolderCopyWindowsStreams`・`FolderCopyWindowsMetadata`（Native／Stagingを含む）・`FolderCopyWindowsCurrency`・`FolderCopyWindowsExecution`・`FolderCopyMacFileKind`、GUI の `MainWindow.Folders`、CLI の `--folder`・`--folder-plan`・`--folder-sync`・`--folder-copy` を照合する。[コピー契約](../../Docs/FOLDER-COPY.md) と [固定fixture](../../tests/Fixtures/FolderSync/README.md) に従い、限定 `--folder-model-only`・`--folder-copy-only`・全体E2E・`HeadlessFolderCopyChecks`（Metadataを含む）を含むUI・同OS AOTを実行する。macOS の種別判定は同ホストの SDK と `build/Build-FolderMacReference.ps1` で照合し、実行手順と成果物は [開発手順](../../Docs/DEVELOPMENT.md) に従う。全streamの名前・長さ・SHA、共有6S+3D予算・descriptor／UTF16名上限、入力／既存出力の保持、全タブとfilterの保護、取消と部分公開の事実を維持する。Windowsの実ADS全bytes・metadataを独立readerで照合し、directory ADS非転送／既存保持、空・Unicode・末尾空白・long pathを確認する。local NTFS経路では [metadataの限定観測](../../Docs/DEVELOPMENT.md#windowsフォルダーmetadataの限定観測) に従い、`FolderWindowsMetadataScenarios` の主アプリ実行と `verify-metadata-copy.py`、GUIの `verify-metadata.py` による別process照合を確認する。EA転写・既存creation／DACL保持・新規security継承・Close後の属性、確認後のmetadata変更と共有予算超過の拒否、一次診断と清掃／close診断の保持を検査し、入力SHA・実process終了・JSON・PNG・独立reader結果を残す。公開済みとmetadata検証完了を区別し、保持と公開の設計境界は [DESIGN.md](../../DESIGN.md#不変条件と採用済み判断) を参照する。非対応volume、SMB、native異常注入、EFS／SACL／任意owner/groupと通常desktop操作は実測した範囲だけ報告する。
