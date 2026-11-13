# .NET 11 / Avalonia 移行の到達点

## 本家WinMerge風の「ファイルまたはフォルダーの選択」欄（2026-11-13、段階4・限定検証）

比較タブ上部のパス欄を、本家の選択画面（`Merge.rc` の `IDD_OPEN`／`OpenView.cpp`）と同じ構成にした。文言は `Japanese.po` の訳を使う。
- 欄: 1 番目（左）／2 番目（右）／3 番目（オプション、三者比較の祖先・中央）のファイルまたはフォルダー。各欄に「参照(B)...」（ファイル／フォルダーを選ぶメニュー）と、本家と同じ組合せの交換ボタン（1 番目と 2 番目、2 番目と 3 番目、1 番目と 3 番目）を置いた。
- 「フォルダー: フィルター」とサブフォルダーを含める(I)、「プロジェクトを保存(V)...」と「比較(M)」を下段に置いた。
- まだ何も比較していないタブでは、本家と同じく選択欄を比較ボタンまで表示する。比較後は従来どおり本文が主役で、選択欄を含む上部は本文の3割までに抑えてスクロールで参照する。「ファイル > 開く」（Ctrl+O）とツールバーの開くは、選択欄を見える位置へ戻して 1 番目の欄へ移動する。
- 対応していない項目: 本家の各欄の「読み取り専用」、展開／前処理プラグインの選択、選択画面を閉じる「キャンセル」と「ステータス:」行は、対応する機能が無いため置いていない。
- ペイン上部の操作ボタン（再比較・保存・パッチ出力など）のメニューへの集約は次の段階で行う。
- 限定UI自己検証 `--open-view-only`（19件、失敗0）で、既存インスタンスの載せ替え、欄の並び、見出し、交換、参照メニュー、「開く」の復帰と移動、通常／最小ウィンドウでの比較ボタンの表示、比較後の高さ制限の復帰を確認した。

## 本家WinMerge風のフォルダー比較一覧（2026-11-13、段階3・限定検証）

フォルダー比較の一覧を、本家のフォルダー比較画面（`DirView`）と同じ列表示にした。
- 列: 既定の並び（`DirViewColItems.cpp`）に合わせ、名前／フォルダー／比較結果／左更新日時／右更新日時／拡張子にした。三者比較では中更新日時を加える。名前と比較結果の列は一覧の幅に合わせて伸縮し、横スクロールは使わない（重ね表示のスクロールバーが最下行を覆わないようにするため）。
  - フォルダーはルート直下を「.\」で表す。
  - 拡張子はフォルダーでは空にし、側ごとに異なれば「|」で区切る。
  - 更新日時は最も新しい側だけに「* 」を付ける。
- ルートパス帯: 一覧の上に、比較した左／（中央／）右のフォルダーを灰色の帯で示す。
- アイコン: 本家の .ico（`Src/res`、`Merge2.rc` の `IDI_*` と同じファイル）を埋め込み、16pxの画像を取り出して表示する。状態ごとの選び方は `GetColImage` の判定順（エラー→除外→片側のみ→三者の欠落→同一→相違）に従う。
- 行の色: `GetColors` の優先順に従う。
  - 除外は (250,245,215)。
  - いずれかの側に無い行は (221,221,221)。
  - 相違は (240,222,125)。
  - 同一は白。
  - 選択行は状態色の上に淡い青で示す。
- 比較結果: `ColStatusGet` の判定順と `Japanese.po` の訳を使う。
  - 例: 「同一」「ファイルは異なっています」「左側のみ: <フォルダー>」「スキップされたファイル」「<ルート> 内のみ存在しません」。
  - 三者比較では「 (中央と右側は同一です)」などを付ける。

本家との違い:
- テキスト／バイナリ／画像の種別は比較モデルに無いため、文言とアイコンは汎用のもの（同一／ファイルは異なっています）を使う。
- 本家に無い状態は DiffBeacon 独自の文言にした（種類の衝突は「種類が異なります（ファイルとフォルダー）」、未比較は「未比較」）。
- 列幅は固定で、列の並べ替え・表示切替・幅変更とソートは未対応。一覧上部の操作ボタンも従来のまま残している。

検証:
- `--folder-view-only`: 全アイコンの16px復号、二者・三者の列、各行のアイコン・色・文言・フォルダー・拡張子、新しい側の「* 」、選択色、最小幅での見出しと行の列境界の一致と横スクロールが出ないこと、通常／最小ウィンドウでの見出しと一覧の配置を確認し、44成功／0失敗。

## 本家WinMerge風のファイル比較画面（2026-11-13、段階2・限定検証）

「差分」タブを、本家のファイル比較画面（`MergeEditFrm.cpp`）と同じ構成にした。
- ロケーションペイン: 左端に置き、全行に対する差分ブロックを左右の縦バーに本家の色で描く。選択中のブロックは選択色にし、表示中の範囲を枠で示す。クリック・ドラッグで本文をその位置へ移動する。描画定数は `LocationView.cpp` の値に合わせた。
- ファイル名の帯: 各ペインの上に灰色の帯を置き、既存の見出し（左／右と状態）の横に比較したパスを出す。
- 本文: 左右の境界線を引き、選択中の差分ブロックを本家の選択色（239,119,116、行の無い側は 240,192,192）で塗る。一覧の青い選択色は淡い灰色に抑えた。
- 差分の移動: ツールバー・メニュー・キーのどれで移動しても、ブロックを画面中央へ表示する。
- Diff ペイン: 下段に置き、選択行の左右を行内差分の色付きで表示する。高さは境界線で変えられる。
- 画面の詰め: 共通の設定・操作欄は本文の3割まで（スクロールで参照）に抑え、比較タブと表示切替タブの見出しを小さくした。

本文は従来の整列一覧（`DiffList`）を引き続き使うため、編集は「編集」タブで行う。本家のように左右の本文を直接編集する形は未対応。
- `--frame-only`: ロケーションペイン、パスの帯、Diff ペイン、選択色、ロケーションペインのクリックによる移動を加え、27成功／0失敗。

## 本家WinMerge風の外枠と配色（2026-11-13、段階1・限定検証）

本家のUIへ寄せる作業の第1段階として、独自のヘッダー（「+ 新しい比較」「明 / 暗」）を本家のメインフレーム構成に置き換えた。
- メニューバー: `Merge.rc` の `IDR_MERGEDOCTYPE` の順。文言は `Translations/WinMerge/Japanese.po` の訳。
- ツールバー: `ToolbarButtons.cpp` の `g_toolbarFileCmp3` の並びと区切り。画像は `Src/res/ToolbarEnabled32.png` を埋め込み、16px で表示する。
- 本家アクセラレータ: Alt+矢印・Alt+Home／End／Enter、Ctrl+Alt+矢印・Ctrl+Alt+M、F5、Ctrl+O／J／W／Q。
- 既定の配色: 明るい配色を既定にし、差分色は `OptionsDiffColors.cpp`／`OptionsDirColors.cpp` の既定値（差分 239,203,5・削除側 192,192,192・行内差分 241,226,173）を使う。表ビューも同じ色に揃えた。
- ステータスバー: 比較ペインの状態表示を、灰色の帯に変えた。

ペイン内の既存ボタンは、自己検証が表示名で操作しているため残している。左右エディター・ロケーションペイン・Diffペイン、フォルダー比較の列とアイコン、「開く」ダイアログは次の段階で寄せる。
- 未移植のため無効表示にしたツールバーのコマンド: 行内差異の選択、前のコンフリクト、すべてを右側／左側にコピー。
- 新しい限定selector `--frame-only`: Windows x64 Release（警告0）で20成功／0失敗。
- 既存の最小ウィンドウ系: `--image-copy-only` は347成功／0失敗。`--folder-copy-only` は、通常／最小ウィンドウの配置を含む949件が成功した。失敗は、D: でごみ箱を使えないことによるごみ箱移動4件と、非管理者でシンボリックリンクを作れないことによる中断だけで、どちらもこのPCの環境に起因する。
- 全体UI自己検証・全体E2E・Native AOTは未確認で、CIの結果で確認する。

## フォルダー側別関数の子Optimize fold（2026-11-12、限定検証）

固定原本の `required-unimplemented.json` に残した `@opt LineAt(ColumnRange(Substr('0',0)))` は、既存ASTの子先行foldで原典 `FunctionNode::Optimize` と同じく、provider guardより前に `invalid column specification` で拒否される。固定原本は書き換えず、E2Eの別checkで二者／三者の固定式・接頭辞なし（拒否）、`@noopt`・`Substr('1',0)` 対照（受理・全行除外表示）と入力SHAの保持を確認した。新しい限定selector `--folder-side-functions-only` はWindows x64 Release（警告0）で2,102命令・10,831成功／0失敗／1 skip（266秒）、子fold checkは24件すべて成功した。固定readerの `completeFeatureVerification:false` と、provider-presentの本文取得・全parser／ASTが未移植であることは変えない。全体E2E・全体UI・Native AOTはCIの結果で確認する。

同時に、WIPで説明を編集したFolder*Oracle 6件のREADMEと、provider-nullのrunner固定を付け直した。provider-nullのreaderは、E2E runnerの全体SHAと行番号を照合せず、改行を正規化した本文に固定spanがちょうど1回あることを照合する方式へ変えた。

## フォルダー式の最適化・全側評価（2026-10-23、限定検証）

既存ASTの6種のOptimize override、部分fold、共有65,536作業予算とリストcacheに対応した。全条件式は全側DIFFITEMで一度評価し、正規表現f:／d:は側別評価を保つ。存在属性のBool、FileExists／FileMissing／Missing、側prefix付きIsFolderとDateStr、括弧付き文字列literal一つだけのProp引数を扱う。式の二重引用符は引用符の二重化とraw backslashを使い、単一引用符のescape拡張とf:／d:の旧quote規則を維持する。全parser／AST・provider-presentの本文取得・40 field全体は未移植である。

外部候補のManagedSolution buildは実終了0・警告0／エラー0で、source／fixture 152件、変更source 15件、5 DLL・Content 67件とApp optimizer fixture Content 19件のbinary chainを確認した。runtime05 HostPropertiesは実終了0・1,104 checks全成功、独立Host readerはHost 255件・existence 150件・入力15件のbytesを確認した。独立Date readerはDate 94件・UI 94件・models 80件・snapshots 808件・原本file 20件＋directory 20件・stat／bytes・年境界6件を確認した。DateStrは既存formatterと通常fixed-offset source contractを使い、年[-4713,9999]・year 0許容・年10000拒否を扱うが、実mtime pre-1970／unsigned FileTimestampとnative year 0実oracleは未認定である。SourceAuthのnormal CLI04は実終了0、5 negativeはsame-algorithmの有限校正として成功した。

runtime05のoptimizer 5,304件とLexer 22件は実CLI全5,326命令・20,951成功／0失敗／1 scope skip、独立optimizer reader 2件が成功した。実終了0・handle解放・EOF・Disposeと保護対象不変を確認し、assertions SHA-256は`F1DA24BE56BC110D934115CA125793FA6CFA660194686A411C69E223B40070A8`。skipはHost専用typed／AST／compile-phase／list-read範囲で、Lexerには別独立readerの資格を付けない。runtime06 ReplaceListsは542命令・2,585成功／0失敗／0 skip、必須native／public reader 4件が成功した。CF public 310件（direct 144＋list 144＋derived 22）をstrict outer Arrayで照合し351件を保護、Regex folder-readerは想定拒否54件を含む220命令と303件の保護を確認した。事前承認した4分岐のうち、二者・三者のリンク作成が双方成功する分岐を採用し、固定540＋任意2命令・固定名2,561 unique＋重複名7＋任意10の2,585件を実行した。期待値は実測出力から生成していない。実終了0・強制終了なし・両stream EOF・handle解放・Disposeとrun内のcohort入力不変を確認した。preparerの最終不変条件も確認済みで、runtime06資格記録のSHA-256は`5703B81A4922A488A1EF96302064D5C0C304FCADBE73385B68544A8AFC427227`。統合plan04はsource 15件・Content 67件・physical 475件・protected 1,444件とXML追加のみの差分について独立レビューを通過した。

以上は外部候補の限定検証であり、正本適用後のbuild・全体E2E・全体UI・Native AOT・macOSは未認定。既存full Nativeの600秒timeoutも未解決である。過去App04のproperties 1,010成功／0失敗（withhost 255）、foldercopy 1,127成功／0失敗とUI 6 PNG／bounds確認は別cohortとして保持し、今回の全体実行へ流用しない。

## フォルダー関数・補助平面case-foldの統合範囲（2026-10-23、R3の限定検証記録）

Core2・E2E4・self-test工程計測1の計7コードとfixture47件を、外部candidate03のWindows x64 Release限定検証を経て取り込む。待機poll描画の変更は速度改善を示さなかったため採用しない。FileFiltersの対応summaryだけはコンパイル済みcandidate03へ文書修正を加え、実行コード不変とraw SHAの差を統合planに保持する。

R3 `--folder-replace-lists-only` は2,585成功／0失敗／0省略・542命令、独立reader14件すべて合格で、補助平面case-fold310命令を含む。`--folder-properties-only` は42,101成功／0失敗／3省略・10,581命令、独立reader49件すべて合格となり、provider-null864命令とside-functions2,094命令を含む。同じ製品PIDのWin32実終了0・終了FILETIME・handle解放・log EOFを確認し、元source417件・旧新DLL各5本・lock5件・実行fixture47件は不変だった。初回observerの1,800秒timeoutとwrapper失敗は保持し、同一PID／birthへの追加観測で製品の自然終了を確認した。製品を再起動した結果でも、最初の監視失敗を合格へ書き換えたものでもない。

省略3件は、provider-nullのlexer／depth／取消／子Optimizeの公開注入、side-functionsのinternal／cache／provider-presentおよび未実装条件1件、macOS実FIFOである。SideのcompleteFeatureVerification=false／nativeFullAstEquivalent=false、providerのnativeDefaultOptimizationQualified=falseを保持する。旧新版の実CLI対照16起動では旧CI4件だけ原典期待と不一致、旧CS4件と新版8件は一致した。補助sideFilteredの旧計画誤記は元の失敗を残し、原典ではなく製品のフィルター伝播ソースに基づく別改訂で既存観測を照合した。同一cohortの全体E2E・全体UI・Native AOT・macOS実機は未認定である。

R3時点では、provider不在body-onlyの原典投影は明示`@noopt`に限り、default／`@opt`のfull Native Optimize、子fold-before-provider-guard、provider-presentの本文取得は未対応だった。既存ASTの子foldは上記新候補で追加するが、このR3成功票をその検証には流用しない。側別6関数の正式名・prefixと現在の対応範囲はCore READMEを参照する。CFの文字class／escape／backreference等の未採取経路と任意PCRE互換も認定しない。固定golden・native proof・public planを製品出力から再生成しない。R3 fixture原本との差分はREADME、SideFunctionsの固定SHA保護用`* -text`属性、その対応固定manifest entryだけで、実測原本と文書整備版のSHA chainを別に残す。

## フォルダー置換リストと出力公開の依存保護（2026-10-23）

フォルダー`.flt`へ`RegexReplaceWithList(source,listOrPath)`を接続した。inline pairとホストから読むfile listを順次適用し、Coreの直接I/Oを除いた。採用済みの依存pathは比較結果に保持し、取消・古い候補では旧依存を維持する。CLIコピーはstaging後にも依存内容を再検査し、GUIは全タブの入力・外部リストを最終検査してから、同じ同期UI区間でファイル公開・新規ディレクトリ作成・空ディレクトリの日時変更を行う。HTML・workspace・包装・派生assetにも依存保護を引き継ぎ、実行時依存情報自体は永続化しない。

Windows x64 Releaseの最終buildは0警告／0エラー。管理者STAの`--folder-copy-only`は**156成功／0失敗／1 skip、78命令**、内包headlessは**1,127成功／0失敗**。独立readerは既存依存5件・出力保護12件・新規公開6件を照合し、固定FolderSync readerも合格した。新規6件は公開直前の依存採用、queued採用の順序、取消、失敗、新規親、空ディレクトリを対象に、bytes・SHA・mtime・staging清掃を確認する。独立ソースレビューは追加指摘なし。skipはMac FIFO／socketの実OS検証であり、portable経路はsource確認のみ。この結果を全体E2E・全体UI・AOT・他RIDの合格へ広げない。

証拠は`<作業フォルダー>\DiffBeacon\folder-publication-20261023-r1`の`e2e-final-r2/assertions.json`、`e2e-final-r2-exit.json`、`source-hashes-final.json`、`dll-hashes-final.json`へ保持する。関数本体の先行限定CLI（432成功／0失敗）は`replacelist-20261020-impl01/verify-reportpaths-20261020-01/focused-r4`であり、今回の最終DLLの全体資格とは区別する。

同じDLLの全体E2Eは`full-e2e-admin-final01/assertions.json`で**221,647成功／3失敗／6 skip、21,663命令**となった。失敗は長いlocalhost UNC別名でのフォルダーコピー両方向と、その結果を照合する独立readerの3件。`unc-repro-final01/results.json`でも通常の長いデバイスパスは両方向成功、UNC別名は両方向で公開前のコピー先状態検査に失敗し、入力bytesを保持した。この全体実行は不合格であり、全体UI・AOTの後続資格はまだ与えない。以前のBareCompression独立readerの出力root制約による2失敗は今回の失敗一覧には含まれない。

UNCの親ディレクトリで属性cacheが自身のtemp作成後も古い日時を返すことを実測し、UNC directoryの通常snapshotだけを`READ_ATTRIBUTES | READ_EA`の単一handle照会へ変更した。no-follow・属性・mtimeの検査とchecked closeを維持し、高度metadataの保持資格は広げない。検査対象のUNC directory・祖先にも`READ_EA`が必要で、拒否時は古いcacheへfallbackしない。別SMB server・制限付きACL・native API失敗注入は未検証。修正後の最小再現4件、外部mtime変更の公開前拒否、`--folder-threeway-only` **92成功／0失敗／1 skip、47命令**、`--folder-copy-only` **156成功／0失敗／1 skip、78命令**と独立readerが合格した。関連GUIは三者105項目、コピー1,127項目に合格し、独立レビューは追加指摘なし。証拠は`<作業フォルダー>\DiffBeacon\unc-copy-20261023-fix01`へ保持する。

修正後DLL（App SHA-256 `6987869EBE3469DD99D608B1BA9D2120362EC6B0D78390AEBED8EBD1CF7C8C98`）の管理者STA全体E2Eは、同runの`full-e2e-admin-final01/assertions.json`で**221,654成功／0失敗／6 skip、21,663命令**、実終了0となった。以前失敗したUNCコピー両方向と独立readerの3項目も合格し、実行前後の5 DLLと公式Z decoderのSHAが不変である。skipはWindowsでのUnix mode、macOSのcase alias／FIFO／socket、実デスクトップ操作と任意入力互換の範囲外、case衝突を作れないfilesystemの条件であり、成功へ数えない。同じDLLの管理者STA全体UIは`verification/managed-final01/ui-report.json`で**18,059成功／0失敗**、実終了0となり、DLL・decoderのSHAは不変だった。独立readerは依存5件・出力保護12件・公開6件と、FolderSyncの44観測・6 PNG・toolbar到達15操作を照合した。通常の最小／多数タブPNGも確認し、ネイティブ保存dialogや実OS pointerの検証とは区別する。Windows x64 Native AOTは`aot-final01/publish/win-x64/DiffBeacon.exe`の生成後、全体自己検証が600秒の制限に達して実終了1となった。最終`ui-report.json`と発行manifestはなく、途中の15,204成功／0失敗を全体合格へ数えない。同一SHA-256のnative実行ファイルを別出力先へ発行した1回の再検証も600秒でtimeoutし、publisher終了1／native終了-1、最終report／manifestなしだった（`aot-final01/quiet01-final-evidence-summary.json`）。開始時の競合検査は合格したが、実行中に別Prunlet dry-runのCPU負荷を観測しており、静穏継続や遅延原因は断定しない。native CPU時間は407.266秒、最後の工程記録は約485.848秒でFolderCopy開始・15,204成功／0失敗だった。元source 417件・旧DLL 5本・元lock 5本・前回失敗証拠は不変で、両実行の失敗ログと途中成果物を保持する。追加の同条件再試行はせず、工程別計測の修正と待機中の描画費用の検証へ進む。AOT・他RIDは未認定のままである。

## フォルダー `.flt` の `RegexReplace`（2026-10-20、Windows x64 Release限定E2E済み）

`RegexReplace(source, pattern, replacement)`をfolder-property evaluatorへ移植した。ちょうど3引数をsource→pattern→replacementの順に一度ずつ評価し、pattern／replacementの型を検査する。sourceはscalarまたは一階層のArrayへ適用し、nested値はnativeと同じ文字列化を行う。既存の`LineTransformRegex` backendを共有し、全置換、replacement token、zero-width、Unicode、case指定を処理する。folder側は65,536共有予算と100ms timeoutを適用し、不正source／patternはNone、未参加capture・未対応grammar・上限超過は明示失敗とする。

固定[Regex原典](../tests/Fixtures/LineTransformRegex/README.md)107 IDを分類し、公開可能74本文（50 String／17 None／6 Array／1本文例外、NUL3件）を二者／三者の148投影へ照合した。除外33件はoptimizer14／typed Regex4／raw FF・FE byte binding3／null args1／arity5／Throw6であり、native optimizerやtyped Regexまで移植したとは扱わない。C3 replacementは最初の実match後だけbyte-preserving経路へ通し、raw-invalid replacementの適用はpatternが空・通常literal・escaped regexp punctuationと確認できる場合に限る。一般のraw patternやlookbehind互換は主張しない。`RegexReplace('aba','a',Substr('é',0,1))`のC3 62 C3はnative FF 62 FF結果と共通`regexSubst`／Poco再探索位置から導いたsource-derived期待値で、新しいC3 native採取ではない。

Windows x64 Releaseの`--folder-properties-only`は6,651 command・21,963成功／0失敗／2環境skip。RegexReplace独立readerは278 command、148投影、期待エラー48件、入力保持295件を照合した。`--line-transform-regex-only`は359 command・1,056成功／0失敗／0 skip。skipはmacOS実FIFO検査とWindows filesystemのcase-sensitive別名検査。全体E2E・UI自己検証・Native AOT・macOSはこの時点のRegexReplace専用記録では未検証で、限定実行を代替扱いしない。

## フォルダー `.flt` の `RegexFind`／`RegexExtract`（2026-10-20、Windows x64 Release限定E2E済み）

`RegexFind(source,pattern[,group[,occurrence]])` と `RegexExtract` をWinMerge `.flt` folder-property evaluatorへ移植した。固定原典は[FolderReplaceOracle](../tests/Fixtures/FolderReplaceOracle/README.md)の`FilterExpressionNodes.cpp:3033–3104,3148–3218,3826–3827`。2～4引数をsource→pattern→group→occurrenceの順に一度ずつ評価し、Int64 guard、guard後のString pattern判定、浅いArrayとNoneを保つ。Findはcapture開始位置のUTF-8 byte offset、ExtractはcaptureのStringを返し、共通の有限時間Regex本体・65,536 work／生成量予算・診断を再利用する。

固定[Regex原典](../tests/Fixtures/LineTransformRegex/README.md)の各122ケースから各94公開本文を選び、各52成功・33 None・4 Array・5評価例外と28除外を保持した。二者／三者376投影、source等の評価順、zero-widthのbyte進行、timeout／予算、rowと全側metadataを検査する。readerはgoldenから型付き期待式を独立再生成し、stdout／stderr／終了コードと入力／filter SHAを照合する。複数row間の走査順、native例外本文との完全一致、`--folder`に指定のない永続出力先の保護は資格化していない。

Windows x64 Releaseの`--folder-properties-only`は20,663 assertions成功／0失敗／2 platform skips、6,373 command。各関数の独立readerは332 command・188投影・期待エラー60（Find）／62（Extract）・保持確認349件を照合した。skipはmacOS FIFOとcase-sensitiveな別名ファイル検査。`--line-transform-regex-only`は359 command・1,056 assertion成功／0失敗／0 skip、Release buildは警告0／エラー0。共有予算の残量を使ったpattern準備の102／104文字境界と単独104文字の通過も実測した。

全PCRE構文、typed Regex／NUL／任意native raw入力、optimizer、新しいnative Date／Timestamp採取、公開取消注入、GUI、Native AOT、macOS／全体E2Eは今回の限定検証の対象外。

## フォルダー `.flt` の `RegexCount`（2026-10-20、Windows x64 Release限定E2E済み）

厳密2引数の `RegexCount(source, pattern)` を実装した。source→patternを左から一度ずつ評価し、String patternだけを受け付ける。pattern型不一致は呼出し全体のNoneとし、Scalarまたは一階層のArrayへ適用する。Noneは変換／compileをせず、nested Arrayは再帰mapせずnative形式へ文字列化する。source変換後のString byteを保持してpatternを遅延準備する。原典 `FolderReplaceOracle/original/FilterExpressionNodes.cpp:3105–3145` のgetAsString→createRegex→match順に従い、不正source＋不正patternはNoneを返す。有効patternでのsource不正UTF-8・zero-width継続byte・timeout・共有予算超過は評価失敗として伝播する。

LineTransformRegexの準備／翻訳／探索本体を共有するため、同ファイル内に内部work contextと予算relayを設けた。既存LineTransform Contextの呼出し元・RegexPatternの型identityを保ち、元のwork／生成量・CancellationTokenをそのまま委譲する。既存100msとUnicode境界・診断を維持し、Folderだけは同predicateの65,536予算へworkと生成量を課金する。Native filter外側の例外→false回復は採用しない。

固定 [Regex原典](../tests/Fixtures/LineTransformRegex/README.md) のreaderを `--verify --git` で再実行し、432観測（RegexCount 81）・57原本／40抽出／97ZIP entryを照合した。golden SHA-256は `3e4dbfa2e88eace81f62bc41ae2d8eb58bff6f2a7c98dfd224ec708241a708f7`。公開本文候補59件（44 Int64／9 None／4 Array／2例外）とnative-only 22件を明示する。原典例外の `bad offset into UTF string` はmanaged継続byte診断との評価段階を照合し、本文一致とはしない。

Windows x64 Releaseの `--folder-properties-only` は17,565 assertions成功／0失敗／2 platform skips。RegexCount専用readerはWindows 220 command・二者／三者118公開投影・期待エラー46件・入力等237件の保持を照合した。row内の評価順と呼出し回数は資格化したが、複数row間の走査順・native外側例外からの回復・取消注入は資格化していない。2 skipsはmacOS FIFO検査とcase-sensitiveな別名を区別できないWindows file systemでの検査。別実行の `--line-transform-regex-only` は359 command・1,056 assertions成功／0失敗／0 skip。Release再buildは警告0／エラー0。readerはケース／goldenから型付き期待式を再生成し、rowと実在側集合・streams／終了コード・入力／filterのSHAを検査する。`--folder` の失敗時には空stdout／部分JSONなしを要求し、指定のない永続出力先の保護は主張しない。

PCRE全構文、typed Regex、NUL／raw native入力自体、optimizer、Date／Timestamp専用native採取、取消注入、GUI、Native AOT、macOS／全体E2Eは今回の限定検証の資格外。固定fixture・package・unit testは追加していない。

## フォルダー `.flt` のログ関数（2026-10-20）

`LogError`／`LogWarn`／`LogInfo` をフォルダー式へ接続した。最小1引数を要求し、各引数を左から一度ずつ評価して直ちにnative形式へ文字列化する。全引数が完了してから単一空白で連結した本文をerror／warn／infoへ一度渡し、最後の型付き値を返す。式の有効な `@name` だけを `[name] ` として付け、ファイルの表示用 `name:` は混入させない。既存のroot共有評価を用い、二者／三者の同じDIFFITEMでsideごとに再実行しない。If／and／orと先行ルールの短絡、途中失敗前に完了した内側ログの保持を維持する。

Coreのログ環境は既存APIの末尾optional引数に追加した。CLIの直接filter読込みと保護対象filterのbytes読込みは既存StandardError logger、GUIのfilter読込み／旧式読込みは既存Trace loggerへ接続する。日時値のログ文字列化には既存Timestamp serializerと注入環境を使い、既存の他関数からの文字列化経路は維持する。共有評価予算の拒否やsink例外は伝播し、未完成の外側イベントを公開しない。

固定 [LineTransformLogging原典](../tests/Fixtures/LineTransformLogging/README.md) の独立readerは35ケース・別nativeプロセス2回を照合した。関数登録表のmin1に合わない直接本文zero-args 2件と、既存managed directive文法との差を含む5件をnative-onlyとして区別する。実フォルダーE2Eは二者／三者の110命令で、25成功本文×2＝50投影、3途中例外本文のイベントをmanaged予算拒否で検査する6投影、arity／lazy／metadata／欠損側／ルール境界を確認した。型付き結果はstrict AllEqualを通る公開predicateで照合する。複数rowでは名前別のイベントと反対の採否を要求し、期待row path／非null側集合をC#と独立Pythonで完全照合する。row間の走査順は固定せず、件数と重複を保つevent行配列で比較し、本文内の引数／入れ子順は厳密に守る。

Windows managed実測runは `<作業フォルダー>\DiffBeacon\folder-logging-20261020-impl01`。Release明示Rebuildは `--no-restore` で警告0・エラー0、限定 `--folder-properties-only` は5,489命令・16,772合格・0失敗・環境依存skip 2件（16,774 assertions）で終了0。`e2e-r2/assertions.json`、runner summary、logging reader結果、129入力／filter／既存sentinelのSHA、5 lockfilesのSHA／長さ／属性／更新時刻の不変、RebuildからE2Eまでの担当ファイルhash不変を記録した。初回の5失敗はInt64投影に付けたサイズ単位接尾辞とrow間順序の検査前提を修正して解消し、原典goldenと製品処理は合わせ込んでいない。

原典外側の例外→false回復は採用せず、managedの例外伝播を保つ。今回の本文goldenの値範囲はnone／bool／Int64／double／String／Arrayであり、日時ログ専用の新native採取は追加していない。不正UTF-8のsink bytes、GUI Traceの実採取、注入sink例外の実プロセス検査、他CLIの出力先保護、macOS／AOT／全体E2Eは今回の限定検証の資格外。GUIとsink例外の接続・伝播はsource reviewの根拠とする。

## フォルダー `.flt` の `Now` / `Today`（2026-10-20）

大小無視・厳密0引数の `Now()`／`Today()` を追加した。各Parser生成の最初、最初の `Advance()` より前に、明示された `ILineTransformTimestampClockEnvironment` からnowとtodayを一組だけ確定する。既存 `LineTransformTimestampParser.Today` とadapterを再利用し、全ノード・file・side評価は同じ値を使う。fe／de／eごとにParserを作り、同じe由来のfile／directory ruleは同rootを共有する。環境なしの非時計Core式は従来通りで、環境なし／clock拡張なしのNow／Todayは解析拒否とする。clock／local calendarの例外をNoneや現在時刻へのfallbackで隠さない。解析ノードは既存日時関数と同じDynamic、runtime値はTimestampで、Date比較と既存serializer契約を保つ。

固定clock closureのSHAは `23339417df345ce7332b4dfaf4146cad32d51d1fde433beb1d7a42d8a969a1d0`。独立readerが437 source rows・11 raw spans・35 inherited-object receipt joins・4実command・2 Native runs／4 snapshots・Update4／Now12／Today12を照合し、同snapshot・capture前後区間・local midnightの動的関係を確認した。過去の保存epochを現在の時計goldenへ流用せず、Nativeの再採取やactor絶対pathの読取りは行っていない。

Windows x64 Release buildとE2E明示Rebuildは警告0／エラー0。r3の `--folder-properties-only` は **16,404成功／0失敗／2環境skip、5,379命令・16,406 assertion、実終了0**。時計62命令・198チェックがすべて成功し、大小文字・arity・構文・型／array・短絡・反復snapshot・fe／de／e・directory枝刈り・入力保持を確認した。補助関数なしの `Now() >= Date` はsideごとの過去／未来mtimeを持つ同名入力で、全側共有の採否と実mtimeを照合した。

Nowは2,048境界×100msの204.7秒幅で実採用／実除外点から区間を推定し、二者／三者とも推定区間全体がbefore／after UTC内に収まった。実測準備時間＋実runnerの30秒上限を包含することも確認した。範囲・解像度・clock資格が不足する場合は理由付き資格外とし、曖昧overlapを合格にしない。Todayの独立期待は自然Windows ja-JP／LCID1041／Tokyo固定offset・DSTなし・TZ overrideなしの条件をBCL変換前に検査し、invalid／ambiguousのlocal midnightを先に拒否する。前日／当日／翌日0時の入力から当日だけを採用した。他profile／macOS／DSTではこの直接期待を作らず、公開のsnapshot／日付関係検査と区別する。原典はTZ環境変数を記録していないため、現在のoverride拒否をNative TZ同一性の資格とはしない。

初回は静的TimestampノードによるDate型／boolの解析拒否で27命令・76成功／15失敗となり、runtime型を変えずDynamicノードへ修正した。r2は5,373命令・16,380成功／0失敗だったが、build中の追記がコンパイラー入力へ未反映でtree4命令の実行資格がなかった。r3は全編集後にE2Eを明示Rebuildし、tree4命令／bare Date2命令の存在とsource／doc hashのビルド前後・実行中の不変を照合した。5 lockfileはプロジェクト別locked restoreと以後の `--no-restore` build／`--no-build` runを通じ、SHA／サイズ／属性／更新時刻が不変だった。

証跡は `<作業フォルダー>\DiffBeacon\nowtoday-20261020-impl01` のfailure matrix・build／Rebuild log・`e2e-r3/assertions.json`・runner summary・`folder-clock-projections.json`・`independent-folder-clock-oracle.json`・lock／担当file hash台帳へ保持する。2skipは既存macOS FIFOとWindows case-sensitive distinct file。clock capture call count・lexing前順序・Parser分離／同e root共有・環境なしCore API・clock／calendar故障伝播はsource reviewの根拠であり、CLI注入で検証済みとはしない。Core直接console／unit testは追加していない。全体E2E／UI／Native AOT／他OS・DST・実midnight跨ぎ・clock backward・原典全AST／parser／Optimizeはこの限定検証の資格に含めない。

## フォルダー `.flt` の週・月・年の初め（2026-10-20）

大小無視の1引数関数 `StartOfWeek(value)`／`StartOfMonth(value)`／`StartOfYear(value)` を追加した。引数を一度評価し、文字列とTimestamp（既存Date literalを含む）を明示した日時環境のローカル暦開始へ変換する。他型と入れ子arrayはNone、直下arrayだけを順番にmapし、空／不正文字列・暦構築・OS変換の失敗は候補全体を例外で拒否する。既存 `LineTransformTimestampParser.Construct`／`StartOf` とadapterを再利用し、週だけのUTC時間軸による曜日分の減算と再構築を保つ。固定Tokyoのepoch近傍ではWeekが例外、Month／Yearが `-32400000000us` を正常に返す差を隠さない。旧 `.flt` のLiteral／System serializer互換は変更しない。

固定closureの全66entry、参照原典394entry、SHA・サイズ・CRC・receipt・raw126行×2・typed goldenを独立readerで照合した。自然なWindows ja-JP／LCID1041／Tokyo／Tzd32400に一致し、108本体（70成功／38例外）を二者／三者へ計216投影した。型・epoch・配列、正常140／期待例外76のCLI終了・stdout／stderr・全側採否を確認し、18静的除外はnative-onlyのまま保つ。成功した負epoch Timestampのnative serializer System文字列14観測は本体例外と区別して保持し、非負Timestampだけ固定表示bytesも照合した。

Windows x64 Release buildは警告0／エラー0、`--folder-properties-only` は **16,182成功／0失敗／2環境skip、5,311命令・16,184 assertion**。追加516命令・1,787チェックがすべて成功し、大小文字・arity・一度評価の予算対照・短絡・共有65,536予算・Date／Modified／Propと欠落側・異なるmtimeの全側metadata依存・入力／filter／sentinel SHAも確認した。別のCLI readerは全216投影をrawにjoinし、JSON全entry／実在側の採否・streams・exit・入力13ファイルのSHAを独立照合した。証跡は `<作業フォルダー>\DiffBeacon\startof-20261020-impl01` のbuild log・`e2e/assertions.json`・`startof-projections.json`・独立reader JSONに保持する。skipはmacOS実FIFOとWindowsのcase-sensitive distinct fileで、今回の固定profile skipはない。

このsliceで製品E2Eの初回失敗はなかった。限定実行は `dotnet run --no-build`（`--no-restore`を含意）を使用した。原典Nativeの再実行、他profile／DST／macOS、原典全AST／parser／Optimize、公開CLIへ注入できないcallback副作用・取消・host失敗、全体E2E／UI／Native AOTはこの限定検証の資格に含めない。

## フォルダー `.flt` の `ToDateStr`（2026-10-20）

大小無視の1引数関数 `ToDateStr(value)` を追加した。引数を一度評価し、StringとTimestamp（既存Date literalを含む）を明示した日時環境のローカル日付文字列へ変換する。他の型と入れ子arrayはNone、直下arrayだけを順番にmapする。空／不正文字列、日時構築・formatter・OS変換の失敗は例外で候補全体を拒否し、Noneへ隠さない。既存 `LineTransformTimestampParser.Construct`／`FormatLocalDate` とadapterを再利用し、現在localeから日付を書式推測しない。符号付きepoch microsecondsを保ち、Windows原典のepoch -1us成功／-1秒失敗をそのまま照合する。

実装前にE2E source commentへ失敗行列を記録した。固定 `LineTransformTimestampFunctions` のToDateStr52本体（正常33／例外19）を二者／三者CLIへすべて投影し、8件のnative-only静的除外はgoldenを作らず保持する。自然なja-JP／LCID1041／Tokyo／Tzd32400 profile一致時だけ期待値を適用する。正常結果は `AllEqual(Array(actual, expected))` でString／None／arrayの型と値を確認し、例外は終了2・stdout空を確認する。Date／Timestampのmicrosecond往復、metadata／Prop、arity・一度評価・短絡、共有65,536予算と入力／既存sentinelのSHA保持も実CLIで検証する。

ToDateTimeの全側metadata依存回帰も追加した。専用の同一bytes `probe.bin` に左epoch+1秒、中央+3秒、右+2秒を設定・読戻しし、直接predicate `ToDateTime('1970-01-01T00:00:01Z') == Date` の二者／三者JSONで各側のmtimeと全 `filtered=false` を照合する。既存fixtureの時刻は変更しない。

Windows x64 Release buildは警告0／エラー0、最終 `--folder-properties-only` は **14,395成功／0失敗／2環境skip、4,795命令・14,397 assertion**。ToDateStr追加156命令・412チェック、ToDateTime依存回帰2命令・13チェックがすべて成功した。自然な固定profile一致で原典52件×二者／三者の104投影（正常66／例外38）を実行し、epoch -1us成功／-1秒失敗とDate／Timestampのmicrosecond境界も確認した。5つのpackages.lock.jsonのSHA-256は前後で一致した。skipはmacOS実FIFOとWindows標準filesystemでのcase-sensitive distinct fileの環境限定である。

初回は14,385成功／4失敗／2skipだった。新規fixtureの1970年mtimeをこのホストのWindows ShellがSystem.DateModified=Noneとして返した2件と、650引数×2の共有予算式が512token上限へ先に達した2件が原因だった。既存fixtureを保持して新規ToDateStr fixtureだけを2023年へ変更し、Lower7500文字→Substr0→IfでLeftDateを返す短い式へ差し替えた。各halfの実Consumeは約37,550で正常、合計15,176文字・512token未満の2halfだけが65,536診断で拒否されるcontrolを確認した。製品本体を変えずに全限定を再実行し、4失敗すべての解消を確認した。

証拠は `<作業フォルダー>\DiffBeacon\todatestr-20261020-impl01` の `build-r1.log`／`build-r2.log`、`targeted-r1/e2e/assertions.json`（初回）／`targeted-r1/e2e-r2/assertions.json`（最終）、projection／独立reader JSON・入力・stdout/stderr、property-time／half-budget／shared-budget probeとowned前後SHA／lock監査へ保持する。全体E2E・UI・Native AOT・他OSと独立レビューは親の別検証範囲で、この限定結果には含めない。


## フォルダー `.flt` の `ToDateTime`（2026-10-20）

大小無視の1引数関数 `ToDateTime(value)` を追加した。引数を一度だけ評価し、非空文字列を既存 `LineTransformTimestampParser.Construct` でTimestampへ変換する。空／不正文字列と非文字列（既存Timestampを含む）はNone、配列は順序と要素数を保って直下だけ変換し、入れ子はNoneとする。日時literalの文法は変更しない。共通パーサーのformat順・6桁精度・Windows locale fallback・timezone挙動を再利用し、非Syntax host例外をNoneへ隠さない。

Coreへ `ILineTransformTimestampEnvironment` を注入し、CLI filter読込み、保護付きCLI selection、GUI／projectの3経路すべてで遅延host環境を生成する。文字列変換と同じフォルダー式の共有65,536予算を使う。取消は既存呼出側の境界を維持し、CLIの取消注入を検証済みにしない。

固定 `LineTransformTimestampFunctions` 原典59本体から公開文法へ投影可能な52件（文字列34＝Timestamp23／None11、Timestamp入力11、その他scalar4、配列3）を選び、二者／三者の実CLIで型とepoch microsecondsを厳密照合するE2Eを追加した。公開arityと異なる0引数1件、date literalで表せないInt64両極Timestamp入力2件、年0000とlocaleの期待Timestamp4件は除外理由を保持する。Native期待値は自然なja-JP／Tokyo／Tzd32400 profile一致時だけ実行する。原典bodyと公開parser／AST／Optimizeの証拠を区別し、非Syntax host例外の原典実測を追加したとは扱わない。

Windows x64 Release buildは警告0／エラー0、修正後の限定 `--folder-properties-only` は **13,970成功／0失敗／2環境skip、4,637命令・13,972 assertion**。追加146命令・424チェックはすべて成功し、固定profile一致で52候補×二者／三者の104原典投影を実行した。独立readerはclosure52項目・source394項目・raw121行×2を照合した。補足の実CLIで1～6桁のfraction・+09:00明示offset・Modified配列・文字列Prop／Name配列も確認した。

初回は共有予算用の1,900文字×2が実際には65,536未満となり、二者／三者それぞれの終了コード・stdout確認の計4チェックが失敗した。実Consumeを合算し2,100文字×2へ訂正した後、同じ全限定を再実行して4失敗すべての解消を確認した。skipはmacOS実FIFOとWindows標準filesystemでのcase-sensitive distinct fileの環境限定である。5つのpackages.lock.jsonのSHA-256は実行前後で一致した。

証拠は `<作業フォルダー>\DiffBeacon\todatetime-20261020-impl01` の `build-r1.log`／`build-r2.log`、`targeted-r1/e2e/assertions.json`（初回）、`targeted-r1/e2e-r2/assertions.json`（最終）、projection／reader JSON・入力・stdout/stderr・supplemental出力・owned前後hashへ保持する。全体E2E・UI自己検証・Native AOT・macOS／他RIDは親の別検証範囲で、この限定結果には含めない。


## フォルダー `.flt` の幅・文字種6関数（2026-10-20）

`ToHalfWidth`／`ToFullWidth`／`ToHiragana`／`ToKatakana`／`ToSimplifiedChinese`／`ToTraditionalChinese`を大小無視の1引数関数へ追加した。登録名から既存`LineTransformCharacterMapping`を厳密に選び、`ILineTransformStringEnvironment.MapCharacters`へ渡す。Appの既存Windows `LCMapStringEx`とmacOS環境を再利用し、NFKC等の別アルゴリズムをCoreへ追加しない。Windows旗・null locale・既存API失敗時の原文保持契約は変更しない。

引数は一度評価し、Noneを保持する。その他は既存NativeString→NativeUtf16、直下Arrayだけのmap、nested arrayの文字列化を使う。array slot・文字列化・復号・変換結果は共有65,536予算で検査し、property・side／全側metadata・folder contextとnative数値literalの依存を伝播する。実変換時にCore環境が無ければ既存LineTransform式と同じ明示環境要求で拒否する。None／空Arrayはcallbackを呼ばず、環境例外をNoneへ隠さない。これらの注入境界はsource契約として照合し、App環境を必ず注入する公開CLIの実測と区別する。

固定[FolderWidthOracle](../tests/Fixtures/FolderWidthOracle/README.md)の309件＝174公開候補＋135 native-onlyを正本にする。公開候補のtyped result/itemsを二者／三者へ全投影し、native-onlyのraw bytes／NUL／内部型／非有限値／throw／arity／API不正flagは別証拠として理由を保持する。追加aliasは存在せず、parserの大文字小文字と誤alias拒否は公開E2Eで確認する。全Unicode／locale・XP fallback・API第2呼出だけの失敗・macOS同値性や専用GUIの実測を意味しない。

Windows x64 Release buildは警告0／エラー0、限定`--folder-properties-only`は**13,546成功／0失敗／2環境skip、4,491命令・13,548 assertion**。Width追加564命令・1,625チェック（専用569、命令共通1,056）がすべて成功した。174固定行×二者／三者の348投影、metadata・欠落側・Prop・Folder／FullPath、直接double、arity・誤alias拒否、一度評価と共有予算の対照、入力SHA保持を確認した。両native raw captureの独立readerは309件・8改変拒否・直接Windows API 261値・`actualVerified=true`で合格した。

成果物は`<作業フォルダー>\DiffBeacon\width-20261020-impl01`の`build-final.log`、`final-runner/build.log`、`e2e/assertions.json`、`capture-a-reader.json`／`capture-b-reader.json`とprojection／reader JSON・入力・stdout/stderrへ保持する。CoreとAppはrootのRelease DLL、E2E runnerはFolder検証を長さ照合へ修正後に同rootの`final-runner`へ確定buildしたDLLを使った。開始時の指定4対象SHA／原本を採り、既存未コミット変更と今回分を区別する。skipはmacOS実FIFOとWindows case-sensitive distinct file環境。全体E2E・UI・Native AOTは今回未実施である。

## フォルダー `.flt` の `NormalizeUnicode`（2026-10-20）

`NormalizeUnicode(value[, form])`を1〜2引数の公開`.flt`関数へ追加した。formを先に一度評価し、無効なstring／Int64形式はNoneでvalueを読まない。形式名はASCII大小無視のNFC/C・NFD/D・NFKC/KC・NFKD/KD、Int64は1/2/5/6だけを受理する。未指定やdouble・bool・None・array等のwrong-typeはNFC既定で、double 2.0／5.0を整数aliasへ変換しない。形式名の空白をtrimしない。

形式解決後にvalueを一度評価する。Noneを保ち、直下arrayだけを変換し、nested arrayと他の型は既存native形式で文字列化する。valueとform双方のproperty・side／全側metadata・folder contextとnative数値literalの依存を伝播する。既存casingの固定Poco UTF8→UTF16処理をprivate helperへ共通化し、共有予算への課金を維持した。array slot・文字列化・復号・正規化結果を同じ65,536作業予算で検査し、環境例外や予算超過をNoneへ置き換えない。

Appの既存`.flt`環境注入を使い、Windowsでは既存`NormalizeString`実装を呼ぶ。OS API失敗時の変換前UTF16文字列の保持も既存実装に従う。Coreの環境省略時はmanaged `string.Normalize`を使い、その例外を伝播する。固定[FolderNormalizeOracle](../tests/Fixtures/FolderNormalizeOracle/README.md)178件のうち95件をgoldenのtyped result/itemsから厳密な`AllEqual(Array(NormalizeUnicode(args), expected))`へ投影し、83件は固定fixtureのnative-only区分として理由を保持する。公開CLIの共有予算対照とnative throw traceを別証拠として扱う。全Unicode／他OSの同一結果、Poco Timestamp・XP fallback・任意raw bindingの公開検証は意味しない。

Windows x64 Release buildは警告0／エラー0、限定`--folder-properties-only`は**11,921成功／0失敗／2環境skip、3,927 CLI命令・11,923 assertion**。追加246命令・724チェックがすべて成功した。95固定行の二者／三者全投影に加え、形式とvalue双方のmetadata・欠落側・Prop・FullPath、厳密arity、形式先行／一度評価の公開予算対照、予算拒否時stdout空と入力SHA保持を確認した。2つの固定native raw captureは178件・8改変拒否確認・`actualVerified=true`を独立readerで再確認した。

同じbuildの既存フォルダーproperty GUI回帰scopeは754成功／0失敗で、PNG・JSONも確認した。新しい正規化専用GUI操作、全体E2E・全体UI・Native AOT・他OSは今回の限定結果に含めない。skipはmacOS実FIFOとWindows case-sensitive distinct file環境。成果物は`<作業フォルダー>\DiffBeacon\normalize-20261020-impl01`の`build.log`、`e2e/assertions.json`、`ui/ui-report.json`、`capture-d-reader.json`／`capture-e-reader.json`へ保持する。変更対象4ファイルの開始時SHA／原本を採り、既存未コミット変更と今回分を区別した。

## フォルダー `.flt` の `ToLower` / `ToUpper`（2026-10-20）

大小文字変換を1引数の`ToLower(value)`／`ToUpper(value)`として公開`.flt`式へ追加した。引数を一度評価し、Noneを保持し、その他の型は既存native形式で文字列化する。配列は直下だけをmapし、入れ子は文字列化して変換する。property・全側／個別側metadata・FullPathの依存とnative数値literalのcontextを伝播し、文字列化・復号・map結果と配列slotを共有65,536作業予算で検査する。

WindowsのCLI・GUI・workspace復元は既存`ILineTransformStringEnvironment`の`LCMapStringEx`経路を注入する。Coreの`Load`／`ParseBytes`／`Parse`／`ParseExpression`は任意の環境を受け取り、省略時は現在カルチャの`TextInfo`を使う。注入環境の例外はNoneやfalseに置き換えない。既存のWindows API失敗時の原文返却契約は維持する。legacy glob maskの合成規則は変更しない。

[固定native oracle](../tests/Fixtures/FolderCasingOracle/README.md)は70件で、Windows ja-JP環境の独立した2回のMSVC buildと独立readerを照合した。大小文字関数のUTF8境界だけは固定Poco `UnicodeConverter`／`TextIterator`と同じ単位幅を使い、不正な単位をU+FFFFへ変換する。NULは明示長で保持する。Substrが保持するraw bytes、比較・検索・Trim等の他observerは変更しない。全locale／全Unicode／他OSとの同一結果、任意のnative内部型／raw bytes／注入throwの公開表現、XP fallbackやPoco Timestampの追加原典採取を意味しない。

Windows x64 Release solution buildは警告0／エラー0、実Python絶対パスを指定した`--folder-properties-only`は**11,197成功／0失敗／2環境skip、3,681 CLI命令・11,199 assertion**。追加144命令・415チェックはすべて成功し、固定46行を二者／三者へ投影、24行はnative-onlyの理由を`casing-projections.json`へ記録した。20件の独立readerも成功した。skipはmacOS実FIFOとWindowsのcase-sensitive distinct file環境である。5 root lockfileはSHA・size・属性・creation/writeTicksが一致した。

限定UI自己検証は**754成功／0失敗**。追加15チェックでCoreの省略環境、注入環境と引数の1回評価・例外伝播・Noneの省略、両関数の二者／三者GUI `.flt` 比較とworkspace保存／復元を確認した。既存`AttrStr contains ''`の古い期待値は、固定consumer oracleの`search-control-998/999`がfalseであることを確認して除外へ修正した。製品のcontains処理は変更していない。

成果物は`<作業フォルダー>\DiffBeacon\casing-20261020-impl01`の`build-final.log`、`e2e-python01/e2e/assertions.json`、`ui-recheck01/ui/ui-report.json`とPNG・JSONに保持する。CLI限定は初回buildの不変DLL、UIは期待値修正後に別buildしたDLLを使い、CLI限定からheadless自己検証を呼んだ扱いにはしない。Python aliasで独立reader20件が起動できなかった初回`e2e`と、古いGUI期待値で停止した初回`ui`も失敗証拠として保持した。全体E2E・全体UI・Native AOT・他OSはこの限定検証の結果に含めない。

## フォルダー `.flt` の `Substr` とString byte保持（2026-10-20、Windows x64 Release限定E2E済み）

`Substr(source, start, length)`は2〜3引数で、start→任意length→startのInt64型検査→sourceの順に評価する。startが非Int64ならNoneでsourceを読まず、lengthだけが非Int64なら省略と同じ末尾までのsliceになる。UTF-8のbyte位置で切り出し、負startはbyte長から数える。範囲外startは空文字、負lengthは残りbyte長との和を0へclampする。sourceのNoneはNone、source配列は浅いmap、その他の型とnested arrayは既存native形式で文字列化する。Int64極値の加算／切出しと、配列・文字列化・コピー・後続observerは共有作業予算内で処理する。

内部Stringは同じ値種別のraw UTF-8 bytesになり、文字の途中で切れたsliceを拒否・U+FFFDへ置換しない。AllEqualの厳密等値と比較・順序・検索はbytesを保持する。case-sensitive順序はunsigned byte、case-insensitive比較／rangeは固定PocoのASCII foldとsigned byte順、containsは記録したCRT C localeのunsigned-byte foldを使う。Len／Strlenはstrict decoderではなく原典のlead-byte幅で数え、Trimはspace／tab／CR／LFのbytesだけを除く。数値変換の全消費検査とregexのstrict UTF-8境界はそれぞれ維持し、`like`はPoco TextIteratorのinvalid token `-1`と既存の固定Unicode lower表を使う。行変換側の既存lower課金経路は変更していない。

固定WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6`の[Substr境界](../tests/Fixtures/FolderSubstrOracle/boundaries.md)、[byte observer](../tests/Fixtures/FolderStringByteObserversOracle/boundaries.md)、[consumer](../tests/Fixtures/FolderStringByteObserversOracle/consumers/boundaries.md)、[Glob](../tests/Fixtures/FolderStringByteObserversOracle/glob/boundaries.md)を正本にする。固定readerの3,292行と親工程の二回ずつのMSVC raw採取照合を、公開parser／CLIの証拠と区別する。公開E2Eは2,088行を二者／三者へ投影し（Substr260、observer989、consumer489、Glob350）、1,204行はnative-only理由をJSONへ保持する。Substr未投影8行はarity3行・注入throw5行である。任意の不正byte binding、`Plus`／直接`ToString`、動的／不正構文regex pattern、全Unicode／全locale／全PCRE機能の移植は意味しない。

NULの「公開表現なし」という初期前提は実CLIで反証した。BOM付きUTF-8 filterの引用符内raw `00`は受理され、SubstrのNULと空文字、等値・長さ・検索・Trim等を公開投影に含める。`def: exclude`で`filtered=false`はpredicateの成功を意味する。Release solution buildは警告0／エラー0、最終`--folder-properties-only`は**10,782成功／0失敗／2環境skip、3,537命令・10,784 assertion**で、Substr追加1,106命令・3,305チェックは全成功した。入力SHA、非変更sentinel、構文拒否のstdout空、実際の65,536作業量拒否と不正start時のsource省略を確認した。取消のnative注入traceとscannerの既存検査は区別し、新しい公開取消hookは設けていない。

成果物は`<作業フォルダー>\DiffBeacon\substr-20261020-impl01`の`build-verified.log`／`build-nul-runner.log`／`e2e-nul-final`／`nul-public-probe`へ保持する。E2E runnerだけを同runの`nul-runner`で再buildし、App/Coreは同一SHAの最終DLLを使用した。初回の公開regex構文誤投影・token上限probe・比較の追加課金によるChoose exact-limit回帰は初回結果を保持して修正し、最終実行で解消した。skipはmacOS実FIFOとWindows case-sensitive filesystemで、全体E2E・全体UI・Native AOT・他OSは今回未実施である。

共有lower表の回帰は同一App/Core DLLの`--line-transform-only`で**33,853成功／0失敗／0 skip、6,617命令**を確認した。既存`like`と限定GUI scope=`line-transform-only`の2,833 assertionを含み、全体UIの結果とは区別する。5 root lockfileは`lock-before.json`／`lock-after.json`／`lock-comparison.json`でSHA・size・全属性・creation/writeTicksが一致し、accessTicksのみ変化した（時刻の書戻しなし）。対象ソース3件・App/Core DLL・最終E2E runnerも検証前後のSHA／size／writeTicksが一致し、`git diff --check`は成功した。

## フォルダー `.flt` の `IsWithin` 関数（2026-10-20、Windows x64 Release限定E2E済み）

`IsWithin(value, lower, upper)`は3引数固定で両端を含み、境界を並べ替えない。既存InRangeのscalar評価器に上端包含モードを追加し、lower→upper→value評価、辺ごとのInt64／Double昇格、Date／metadataのTimestamp正規化、固定MSVC ABIの文字列順序、浅い値配列mapと共有作業予算を共用する。InRangeの上端除外と行変換のWithinは維持する。

固定原典はWinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6`の`IsWithinFunc`（2157–2234）、shallow helper、登録3..3とconstructor arity検査である。[FolderIsWithinOracle](../tests/Fixtures/FolderIsWithinOracle/README.md)は159件、golden SHA-256は`015f153b16c156444226763d7525f7187eed922772b995b593819556abd894f6`で、独立readerの固定照合と親工程による二回のMSVC raw evidence照合が成功した。公開CLIでは上端・同値境界の受理をInRangeとの対照で確認し、逆順拒否、混在精度、Timestamp／string、配列・metadata／Prop／FullPath、不正構文と予算拒否を確認する。native typed binding／throw traceと公開CLIの値・filter結果は区別し、投影できない行は理由を記録する。

Windows x64 Release最終buildは警告0／エラー0、限定`--folder-properties-only`は**7,477成功／0失敗／2環境skip、2,431命令・7,479 assertion**。134固定行を二者／三者の公開CLIへ投影し、25行をnative専用として理由を記録した。IsWithin追加の346命令・1,022チェック（専用352、命令共通670）と、既存を含む15件の独立reader確認がすべて成功した。既存InRangeを同じ実行で継続検証している。

他関数を含まない`IsWithin(1.5,1,1.5)`は通常版で終了1／両行採用、共有rangeノードのnative-context flagだけfalseにした変異版で終了0／両行除外となり、回帰を検知した。元コードはbytes／SHA一致で復元後、最終buildとE2Eを逐次実行した。予算専用入力はASCII basename 128 bytesで最小超過を保証し、二者／三者ともUTF-8 FullPath長263・下限158,928>65,536で終了2／stdout空となった。不正構文・予算の三者は`--middle`を明示し、入力SHAと既存sentinelを保持した。directory CLIは出力fileを指定しないため、sentinelは非変更の証拠に限る。

成果物は`<作業フォルダー>\DiffBeacon\iswithin-20261020-impl01`の`build-final.log`／`e2e-final`／`mutation-probe/evidence.json`へ保持する。bin/obj・TEMP/TMP・fixtureはすべて同run配下で、既存D設定propsは変更していない。5つのroot lockfileは`lockfiles-before.json`と最終build／E2E後の`lockfiles-after-final.json`／`lockfiles-comparison-final.json`でSHA・size・全属性・creation/writeTicksが一致し、accessTicksのみ変化した。時刻は書き戻していない。skipはmacOS実FIFOとWindows case-sensitive filesystemの環境限定で、全体E2E・UI自己検証・Native AOT・他OSは今回未実施である。

## フォルダー `.flt` の `InRange` 関数（2026-10-20、Windows x64 Release限定E2E済み）

`InRange(value, lower, upper)`は3引数固定で、lower→upper→valueの順に全引数を評価してから型を検査する。区間は下限を含み上限を含まない。値の配列だけを浅く同順序でmapし、配列境界・nested値・不正型はNoneとなる。Int64／Doubleの混在は各辺だけで昇格し、他方の整数比較を維持する。Date literalとmetadata日時は既存のTimestampマイクロ秒表現へ正規化し、数値との異型比較を拒否する。

文字列は通常のComparisonを使わず、`@cs`でunsigned UTF-8 byte順、`@ci`でASCIIだけをfoldしたMSVC x64 signed-char byte順を使う。`InRange('é','','z')`はcs=false／ci=trueであり、Unicode case foldingを行わない。この順序は固定MSVC ABIへの互換方針であり、他OSのC++ char ABIへ一般化したものではない。全引数のproperty／side metadata／比較contextを継承し、配列確保・UTF-8変換・比較は共有EvaluationBudgetを消費する。

[FolderInRangeOracle](../tests/Fixtures/FolderInRangeOracle/README.md)は固定原典145件で、golden SHA-256は`6cf9d8a007ce311c366c89c71f095255c1d642abde90e21763420dac2340be42`。独立readerの固定golden照合と、親工程で採取・独立照合した二回のMSVC raw evidenceを、公開CLIの投影とは区別する。評価trace・throw注入・constructor arity・直接typed bindingはnative証拠であり、公開CLIへ同じspyを注入した扱いにはしない。公開投影から除いたNUL／Regex／FileContentやDateTime範囲外のTimestamp等は`inrange-projections.json`へ理由を記録する。

Windows x64 Releaseの最終buildは警告0／エラー0、限定`--folder-properties-only`は**6,455成功／0失敗／2環境skip、2,085命令・6,457 assertion**。固定121行を二者／三者の公開CLIへ投影し、24行はnative専用へ区分した。InRange追加の306命令・902チェック（専用312、命令共通590）と、既存を含む14件の独立reader確認がすべて成功した。高bitを値／下限／上限へ置いたcs／ciの順序、65,536共有予算の超過時拒否、入力SHAと既存sentinelの保持を確認した。directory CLIには出力file指定がないため、sentinelは非変更の証拠に限る。

他の関数を含まない`InRange(1.5,1,2)`は、通常版の終了1／両行採用に対してInRangeのflagだけfalseにした変異版が終了0／両行除外となり、値型context回帰を検出した。元コードはbytes／SHA一致で復元後、最終buildとE2Eを逐次実行した。成果物は`<作業フォルダー>\DiffBeacon\inrange-20261020-impl01`の`build-budget-final.log`／`e2e-final`／`mutation-probe/evidence.json`へ保持する。build/bin/obj・temp・E2E fixtureはすべて同run配下で、既存D設定propsは変更していない。

共有予算の拒否fixtureは短いrun pathへの依存を除き、ASCII basename 128 bytesの専用入力へ修正した。200反復の作業量`602*L+602`は、FullPathのUTF-8長L>=128から最小77,658>65,536を保証する。最終二者／三者はともにL=261・下限157,724で終了2／stdout空となり、新入力SHAも保持した。三者には`--middle`を明示する。修正前の成功runは`e2e`として保持している。

5つのroot lockfileは`lockfiles-before.json`と、最終build／E2E後の`lockfiles-after-final.json`／`lockfiles-comparison-final.json`でSHA・size・全属性・creation/writeTicksが一致し、accessTicksのみ変化した。最終アクセス時刻は書き戻していない。初回比較資料も保持する。skipはmacOS実FIFOとWindows case-sensitive filesystemの環境限定である。今回の小追加では全体E2E・UI自己検証・Native AOT・他OSは実行していない。

## フォルダー `.flt` の `Abs` 関数（2026-10-20、Windows x64 Release限定E2E済み）

`Abs(value)`はちょうど1引数を評価し、Int64／Doubleの型を保って絶対値を返す。配列は直下だけを写し、nested arrayと非数値はNoneとなる。範囲外decimalも拒否する。Int64最小値は固定MSVC原典で観測された最小値のまま保持し、Doubleは符号bitだけを除去する。子のproperty／side metadata／比較contextを継承し、全側共通の値型評価を使う。

[FolderAbsOracle](../tests/Fixtures/FolderAbsOracle/README.md)は固定commitの原典を無変更抽出した41件で、独立readerと保存済み二プロセスのraw evidenceを照合する。golden SHA-256は`960f7b625490c165db774d4b8c3f27f6c89b7b4ab042411dd99f168f8a2ad7e3`。公開CLIでは整数端点・finite・±zero・ToDouble経由のinfinity／NaN・配列・metadata／Prop／FullPath・構文拒否を確認する。符号ゼロのbit、NaN payload、直接typed binding、throw trace、FunctionNode constructorの未対応arityはnative oracle専用の証拠であり、公開parser／optimizerやmanaged評価traceの同等性とは区別する。Int64最小値の結果もC++の移植性保証ではなく、このMSVC原典の観測へ合わせたものである。

Release buildは警告0／エラー0、最終`--folder-properties-only`は**5,553成功／0失敗／2環境skip、1,779命令・5,555 assertion**。Abs追加の56命令・155チェック（専用59、命令共通96）、既存を含む13件の独立reader確認もすべて成功した。skipはmacOS実FIFOとWindows case-sensitive filesystemの環境限定である。`Abs(-1.5) == 1.5`は他関数の値型flagへ依存しないpredicateで、通常版の終了1／両行採用に対し、Absの`UsesAllSideMetadata`だけfalseにした変異版が終了0／両行除外となることを別processで確認した。元コードはbytes／SHA一致で復元後、最終buildとE2Eを逐次実行した。

検証出力は`<作業フォルダー>\DiffBeacon\abs-20261020-impl01`の`build-final.log`／`e2e-final`、変異検証は`mutation-probe/evidence.json`とraw stdout／stderrへ保持する。5つのroot lockfileは前後snapshotでSHA・length・全属性・creation/writeTicksが一致したが、accessTicksだけ変化した。最終アクセス時刻は書き戻しておらず、全時刻不変とは扱わない。この小追加では全体E2E・UI自己検証・Native AOT・他OSは実行していない。

## フォルダー `.flt` の `StrCount`／`StrFind` 関数（2026-10-19、Windows x64 Release folder-property E2E済み）

Windowsのフォルダー比較で `StrCount(haystack, needle)` と `StrFind(haystack, needle, occurrence)` を評価する。両関数は一致を重ねて数えず、`StrFind` は0始まりの非重複一致番号を受け取り、見つかった位置をUTF-8 byte offsetで返す。第3引数は省略時0、明示時はInt64のみで、負数・範囲外はNoneとなる。空／非string needleもNone。`@cs`／`@ci`に従い、大小文字変換はASCII byteだけに適用する。配列は直下だけを写し、Noneを保持し、それ以外の値とnested arrayは旧nativeの文字列化規則に従う。

固定native oracle 93行のうち、公開`.flt`式へ投影できる78行をReleaseアプリCLIで照合し、null引数・不正arity・throw nodeの15行は理由付きでnative body専用に記録した。Windows x64 Releaseの限定`--folder-properties-only`は1,620 CLI命令・4,809成功／0失敗／2件の想定platform skip（macOS FIFO、Windows case-sensitive filesystem）。StrSearchは139 CLI命令・466 assertionがすべて成功し、8,001 byte source／6,001 byte needleの長い共通prefixも両関数で通過した。この有限入力は線形計算量の証明として扱わない。Release buildは警告0／エラー0、5 source lock fileのSHA・サイズ・属性・UTC時刻は実行前後で一致した。全体E2E、UI自己検証、Native AOT、他OSは未検証で、残りの`.flt`全文法の移植完了を意味しない。

## フォルダー `.flt` の `Array(...)` 関数（2026-10-19、Windows x64 Release全体E2E・UI・Native AOT済み）

Windowsのフォルダー比較で `Array(...)` を評価する。引数は0個以上で、左から一度ずつ評価し、値の型・順序・入れ子を保った配列を返す。配列をfilter predicateとして使う場合は入れ子までBoolean `true` を探索する一方、`AnyOf`／`AllOf`は直下のBoolean要素だけを判定する。

固定native oracleは50ケースを二回の独立実行で一致させた（golden SHA-256 `31e439ea586ba539b462e5912069c01b1e904c311e1f7493ff01bc3d741948a0`）。限定folder-property E2Eは2,581成功／0失敗／2件の想定platform skip（macOS FIFO、Windowsのcase-collision filesystem条件）、80件のArray assertionをすべて確認した。Windows x64 Release全体E2Eは15,760 command・201,848成功／0失敗／6件の想定platform skipで、全commandの実終了を観測した。全体managed UI自己検証は17,977成功／0失敗、Array関連304件もすべて成功した。

Windows x64 Native AOTはSDK 10.0.402／`win-x64`、manifest `nativeAot=true`・`selfTest=passed`、自己検証755.97秒（上限1,200秒）。14 payloadのサイズ・SHA-256、5ライセンスを照合し、5 source lock fileのSHA・サイズ・属性・UTC時刻を維持した。これはWindows x64と上記Array経路の実測であり、残りの `.flt` 全文法や他RIDの移植完了を意味しない。

## フォルダー .flt の側別相対パス属性（2026-10-19、Windows x64 Release全体E2E・UI・Native AOT済み）

LeftRelPath／MiddleRelPath／RightRelPathは各比較側rootからの相対パスをスラッシュ区切りで返す。MiddleRelPathは三者比較のindex 1、二者比較では右側を示し、存在しない側はNoneとなる。ファイルとディレクトリに使え、比較走査が保持する相対パスを参照するため追加のfilesystem／Shell問い合わせは行わない。既存のRelPathは先頭root基準のまま維持する。旧WinMergeはWindowsのバックスラッシュ区切りを返すため、区切りを含む旧filter expressionは新しいスラッシュ表記へ合わせる必要がある。

限定folder-property E2Eは2,501成功／0失敗／2件の想定platform skip（macOS FIFO、Windows case-sensitive filesystem）、837 command。全体Release E2Eは15,729 command・201,768成功／0失敗／6件の想定platform skipで、RelPath assertion 72件もすべて成功した。全体managed UI self-testは17,968 assertion成功／0失敗。Windows x64 Native AOTはSDK 10.0.402／win-x64、manifest nativeAot=true・selfTest=passed、自己検証804.0秒（上限1,200秒）。14 payloadのサイズ・SHA-256一致、5ライセンス同梱、5 source lock filesのSHA・size・属性・mtime不変を確認した。検証範囲はWindows x64であり、他RID／OSの実測や残り .flt 全文法の移植完了を意味しない。

## フォルダー `.flt` の `Strlen` 関数（2026-10-19、Windows x64 Release全体E2E・UI・Native AOT済み）

Windowsのフォルダー比較で `.flt` の `Strlen(value)` を評価する。文字列ではUTF-8 code point数を返し、`None` は `None` のまま扱う。配列には直下の要素へ一度だけ適用して形を保ち、入れ子配列やその他の値は旧実装と同じ文字列形式にしてから数える。整数は十進表記、doubleは小数点以下6桁、boolは小文字、timestampはローカル `YYYY-MM-DD HH:MM:SS` 表記を使う。`Strlen` は数値関数なので、filter predicateとして単独ではなく比較式の中で使う。

固定native oracleの45ケース（golden SHA-256 `4d31dbab06e96df545b2346481b36721d7cc394d9365a74578ce127aa01ab0d6`）は独立readerで一致した。限定folder-property E2Eは2,441成功／0失敗／2件の想定platform skip、限定UI自己検証は730成功／0失敗。Windows x64 Release全体E2Eは15,707 command・201,702成功／0失敗／6件の想定platform skip、全体managed UI自己検証は17,968成功／0失敗。

Windows x64 Native AOTはSDK 10.0.402／runtime .NET 10.0.13、manifest `nativeAot=true`・`selfTest=passed`、自己検証889.91秒（上限1,200秒）。14 payloadすべてのsize・SHA-256がmanifestと一致し、5ライセンスを同梱した。実行前の5 source lock fileは各SHA・size・属性を維持した。検証したRIDはwin-x64のみで、他RID・macOS・CIや旧 `.flt` 全文法の移植完了は意味しない。

## .flt 関数内比較のスタンドアロン互換性（2026-10-18、Windows x64 Release全体E2E・UI・Native AOT済み）

関数を含む規則では、旧 .flt の値意味を入れ子の比較とliteralまで伝える。これにより、standalone FileFilter.Matches で AllEqual(1.0 == None, false) はfalse、AllOf(None < 0) == None と Len(None < 0) == 0 はtrueとなり、旧native評価に一致する。関数を含まない通常のstandalone評価と、フォルダー側別metadataの経路は既存のまま保つ。

限定フォルダーproperty E2Eは2,294成功／0失敗／2件の想定platform skip（766 command、各process exit観測済み）。独立oracleはAllEqual 90件・AllOf 53件・AnyOf 62件・NotEach 67件・Len 38件を全件照合した。限定managed UI自己検証は685 assertion成功／0失敗で、上記3つのstandalone回帰例を含む。

Windows x64 Release全体E2Eは15,656 command・201,555成功／0失敗／6件の想定platform skip、独立／oracle assertionは2,021件成功。全体managed UIは17,923 assertion成功／0失敗。Native AOTはSDK 10.0.402／runtime .NET 10.0.13、manifest nativeAot=true・selfTest=passed、自己検証892.71秒で、14 payloadの全サイズ・SHA-256と5ライセンスの同梱、5つのsource lock fileのSHA・サイズ・属性不変を確認した。検証したRIDはwin-x64のみで、他RIDや旧 .flt 全文法の移植完了は意味しない。

## フォルダー `.flt` の `AllEqual` 条件（2026-10-17、Windows x64 Release全体E2E・UI・Native AOT済み）

Windows のフォルダー比較で `.flt` の `AllEqual(...)` を評価する。scalar引数は左から再帰的な型厳密比較を行い、不一致で後続評価を止める。先頭引数が配列の場合は配列要素を比較し、後続引数を評価しない。Shell `Prop` を含む規則は各フォルダー階層の全側メタデータを一度に評価し、欠落側は `None` とする。scalar-only規則の側別評価と独立したまま、公開 `FileFilter.Matches` のフォルダー外scalar評価も保つ。

限定E2Eは1,937成功／0失敗／2件の想定platform skipで、AllEqual 25 assertion と独立した旧ソース oracle 90ケースを確認した。Windows x64 Release全体E2Eは201,198成功／0失敗／6件の想定platform skipでAllEqual 32 assertionがすべて成功した。全体managed UI自己検証とNative AOT UI自己検証はそれぞれ17,737成功／0失敗で、AllEqual assertionは各28件すべて成功した。

Native AOT manifest は `win-x64`／SDK 10.0.402／`selfTest=passed`、自己検証910.8秒（上限1,200秒）を記録し、manifestの14ファイルすべてで実体のSHA-256とサイズが一致した。JpegLibrary／SharpCompress／TiffLibraryのライセンスと、5個のsource lock fileのSHA不変を確認した。PNGとJSONではサイズ条件が `same.bin` のみを残し、配列先頭の条件が後続SHA引数を無視して `different.bin` と `same.bin` を残す。これはAllEqualの対応範囲であり、残る `.flt` 関数・算術・PCRE固有構文・全文法の移植完了や他RIDの検証を意味しない。

## `.flt` の側別プロパティと絶対パス条件（2026-10-17、Windows x64 Release全体E2E・UI・Native AOT済み）

Windowsのフォルダー比較で `.flt` の `FullPath`／`LeftFullPath`／`MiddleFullPath`／`RightFullPath` を評価する。値はShell property serviceを介さず走査snapshotの絶対パス文字列を使い、不在側は `None`。bare `FullPath` は側順配列、side指定は各位置を参照し、`MiddleFullPath` はindex 1（二者では右）。`FullPath` またはShell `Prop` を含む一つのmetadata規則は全側配列で一度評価し、どちらも含まない既存のscalar metadata規則と別々の規則は側ごとに評価する。directory frontierは各側の直接の子を集めてから評価し、除外・不在・走査失敗の親を越えた子の取得・再帰をしない。`FullPath` はフォルダーroot contextを必要とする。

限定E2Eは1,912成功／0失敗／1件の想定macOS FIFOスキップで、二者・三者の14個のFullPath assertionをすべて確認した。Windows x64 Release全体E2Eは15,496 command・201,122成功／0失敗／6件の想定platform skip、全体managed UI自己検証は17,709成功／0失敗。Windows x64 Native AOT発行のmanifestは `nativeAot=true`・`selfTest=passed` を記録し、同じ17,709 UI assertionがすべて成功した。残る `.flt` 関数・算術・PCRE固有構文・全文法の移植完了を意味せず、他RIDの検証結果にも広げない。

## 一般行変換の置換リスト（2026-10-16、Windows x64 Release限定検証）

`ReplaceWithList`／`RegexReplaceWithList`をinline配列とfile pathの両方で接続した。前者はcompiled regex pairだけ、後者はcompiled／string pattern pairを順に適用する。CoreはI/Oを持たず、Appから有界・取消可能なloaderを注入する。literal pathは最適化時、dynamic pathと`@noopt`は評価時に読み、既存原文・編集本文を保持する。blank／先頭`#`／TABなしの行を飛ばし、最初のTABで分割して後続列を無視する。通常loaderのescape、不正regex ruleの診断／skip、UTF-8 BOMを確認した。

[固定原典70件](../tests/Fixtures/LineTransformReplaceLists/README.md)の独立reader（原本SHA・source span・二回native採取・Git属性）と公開readerが成功した。公開planは18 serialized-key比較（opt／noopt両方）、4実行拒否、6解析拒否。さらにfileの32原典をopt／nooptの64観測でnative serialized keyへ照合した。Windows x64 Release限定E2Eは425 assertion成功／0失敗／0 skip、限定GUI自己検証は12成功／0失敗。実ボタン・原文保持・compile／各行の読込み寿命・予算／取消・失敗時の確定表示保持をPNG／JSONで確認した。buildは0警告／0エラー、5正本lockのSHAと属性は不変だった。

project/paneの依存としてstatic候補とloaded dynamic pathを保護し、HTML・workspace複製・Table・包装／展開を確認した。包装は式を変えずv10 logical path→相対asset mapへ保存し、元リストの変更後も同梱snapshotから再読込みする。別workspaceへ複製したassetも確認した。literal・文字列連結・If／Choose／At(Array(...))から列挙できる候補は同梱し、行本文から作る未列挙pathは通常比較で使えるが包装は明示拒否する。

fixtureのfile層はUTF-8 adapterで、旧UniMemFileのOS I/O・環境変数・任意codepage推定／lossy処理を実行していない。Appは既存のBOM／strict UTF-8／windows-1252 fallbackを使い、その同等性は主張しない。公開typed regex identity・全PCRE構文・旧parser全体も対象外。この変更の全体E2E・全体UI・Native AOT・macOSはまだ未実行で、以前の機能の成功記録を本変更へ転用しない。

2026-10-17の追加修正では、dialogの追加／適用でlistのI/O・検証エラーを表示し、スクロール領域内のエラー位置へ移動する。JSONの構造検証はlist内容を読み込まず、相対asset解決後の検証とclone／GUI設定適用／HTML／CLI上書きはprojectのmapを渡す。文字列以外を含む連結pathは依存を動的として包装拒否する。workspaceのlist assetはtempへの書込み・flush・保護再確認後のatomic moveで公開し、作成したtempだけを失敗時に除去する。既存の一致assetも保存元windowの保護登録へ渡す。

追加2件を含む再検証のWindows x64 Release限定E2Eは147 command・468成功／0失敗／0 skip、限定GUIは22成功／0失敗。oversized／不在の元リストから独立した包装復元、CLI上書き、数値連結の既存包装出力保持、エラー文が見える追加／適用と訂正、公開衝突時のJSON／既存asset保持・temp除去・同target再試行、包装Aから既存Bへ保存した再利用assetへのalias Text SaveAs拒否を確認した。buildは0警告／0エラー。独立readerの原典70件・公開18件のopt／noopt比較と拒否ケースは成功し、上記の同等性範囲を広げていない。文字列連結のIf／Choose／At(Array)と入れ子選択でも非string枝を動的依存として保持し、通常評価を変えず包装を拒否した。相対DTO直列化のcloneも構造検証へ分離し、実体は相対化前の絶対mapで検証する。CWDの同名oversized assetを置いたworkspace保存・包装・asset出力拒否をGUIで確認し、Windows junctionの同名pathでも保存・包装が成功する4 CLI実行とPythonによる全list bytes／ZIP CRC照合を追加確認した。この追加修正の全体E2E・独立レビュー・全体UI・Native AOT・他OSは未検証である。

## 一般行変換の `like`／`not like`（2026-10-16、Windows x64 Release全体E2E・Native AOT済み）

`like`／`not like`をtyped-ASTの二項演算へ接続した。Poco Globの全文一致を使い、`*`・Unicode scalar単位の`?`・文字集合／範囲・否定集合・backslash escapeを扱う。case-insensitive時は固定した原典由来のUnicode小文字表を使い、正規表現やfilesystem globのslash／dot規則は適用しない。配列は既存の演算子lift、文字列以外とopaque `Content`は`none`、`not like`は既存の真偽集約を保つ。共有作業予算と取消確認を適用する。

[固定Poco原典](../tests/Fixtures/LineTransformLike/README.md)は99ケース／60 source file／独立native実行2回で、golden SHA-256は`2264f26555ca884c93071fca6d88a559d8c869d8e138d3f94e493bb7cc168ef6`。Release buildは警告0・エラー0、Windows x64限定E2E `--line-transform-like-only`は235 command・1,158 assertion成功／0失敗／0 skip。固定fixtureの独立readerも99ケースと原本・二回採取を検証した。E2Eはoptimized／`@noopt`、`@cs`／`@ci`、invalid setの入力依存拒否、配列・Content、評価順、予算超過時の入力／既存出力保持を確認する。

同じRelease buildの全体E2Eは14,719 command・198,720成功／0失敗／5 skipで完了し、全体managed UI自己検証も17,216成功／0失敗だった。Windows x64 Native AOTは.NET SDK 10.0.402／runtime .NET 10.0.13で発行し、manifestは`nativeAot=true`・`selfTest=passed`・14 payloadを記録した。AOT全体UI自己検証は17,216成功／0失敗、837.479秒／1,200秒枠。

## 一般行変換のMatch関数（2026-10-16、Windows x64 Release全体E2E・Native AOT済み）

`MatchContext`・`MatchBlockCount`・`MatchBlockNumber`・`MatchBlockSize`・`MatchBlockOffset`をtyped-AST行変換へ接続した。`MatchContext(predicate, before, after)`はpredicateを第1引数に受け取り、境界式をbefore→afterの順に評価してからpredicateを走査する。直前の一致block距離を`after`、次の一致block距離を`before`と比較する。predicateはscalarのbool、または外側arrayに直接含まれるbool trueだけを真とする。範囲cacheはAST node identityとshared context内に限定し、走査成功後だけ公開する。

[固定原典](../tests/Fixtures/LineTransformMatches/README.md)は無変更C++原典132件／61 source spanで、golden SHA-256は`9b4bb8a2c856658165618c5f3df0a537027956d5439686546b6b5e1a2c9fabf6`。Windows x64 Release限定E2E `--line-transform-match-only`は555 assertion・123 command成功、0失敗・0スキップ。独立readerは91件の公開serialized-key比較、11件のruntime拒否、30件のnative専用probeを分類し、native／public readerとも成功した。公開結果は全行の比較JSON`different=false`による間接観測で、typed value・AST・cache・例外classの直接同等性を主張しない。

同じRelease buildの全体E2Eは191,830成功／0失敗／5スキップ、13,201 commandで完了し、Match関連57 assertionも全件成功した。全体managed UI自己検証は17,216成功／0失敗、`HeadlessLineTransformChecks`は13,654成功／0失敗だった。Windows x64 Native AOTは.NET SDK 10.0.402／runtime .NET 10.0.13で発行し、manifestは`nativeAot=true`・`selfTest=passed`・14 payloadを記録した。AOT全体UIは17,216成功／0失敗、821.849秒／1,800秒枠。AOT実行ファイルのMatch限定E2Eも555成功／0失敗／0スキップで、両独立readerが成功した。

## 一般行変換の正規表現関数（2026-10-16、Windows x64 Release全体E2E・Native AOT済み）

`RegexFind`・`RegexCount`・`RegexExtract`・`RegexReplace`を共通行変換へ接続した。固定原典はWinMerge由来のPCRE2／Poco実装で、実行時は追加native依存なしの.NET regexへ、照合した互換構文を変換する。Findはcapture開始位置をUTF-8 byte offsetで返し、Extractはcapture番号と0始まりの一致番号を受け取る。Replaceは原典の単一桁capture参照・`$$`展開・置換後subject上のglobal検索と、UTF-8 byte単位のzero-width進行を再現する。case directive、型付きpattern、scalar／一段array、定数化と動的引数の最適化分岐を保つ。実行には100 ms timeout・共有作業予算・取消確認を適用する。

[固定原典](../tests/Fixtures/LineTransformRegex/README.md)は432ケース（390件のfunction-body no-opt、42件のoptimizer分岐）で、golden SHA-256は `3e4dbfa2e88eace81f62bc41ae2d8eb58bff6f2a7c98dfd224ec708241a708f7`。Windows x64 Releaseの限定E2E `--line-transform-regex-only` は359 command・1,056 assertion成功、0失敗・0スキップだった。358件の公開観測のうち343件を値／実行エラーgoldenと比較し、15件のwrong-arityは公開parserの拒否、15件の実行エラーは固定goldenとの一致を確認した。全原典432件のうち107件がRegexReplaceで、出力上限の拒否も終了コード2・stdout空で確認した。fixture専用のraw invalid UTF-8とThrowNodeは公開入力面に含めない。

この互換層は任意PCRE2入力との完全互換を主張しない。補助面Han property照合とUnicode scalar途中のcaptureは拒否する。ゼロ幅一致後は原典どおりUTF-8 byte単位で次位置へ進み、複数byte文字上の継続に伴う例外も保持する。固定原典の範囲を超える構文・locale差とmacOS／Linuxでの実測は未資格である。

同じWindows x64 Release buildの最新全体E2Eは191,830 assertion成功・0失敗・5スキップ、13,201 commandで完了した。`line-transform-regex-*` assertionは1,050件すべて成功した。5スキップはUnix 0600属性、macOSのcase alias・FIFO/socket、実デスクトップの全入力互換性、包装入力名のcase collisionである。Native AOTのWindows x64自己検証は17,216 assertion成功・0失敗で、実行時間は821.849秒だった。最新AOT実行ファイルを使った限定Regex E2Eも1,056 assertion成功・0失敗・0スキップで、固定原典432件と出力上限拒否を確認した。

## 一般行変換の `matches`／`not matches` 演算子（2026-10-16、Windows x64 Release全体E2E・Native AOT検証済み）

一般行変換式へ `matches`／`not matches` を追加した。文字列subjectと文字列または型付きregex patternを照合し、先頭位置で得た最初の非空一致がsubject全体を覆う場合に `matches` をtrueとする。終端anchorをpatternへ追加して代替を再試行しないため、`"ab" matches "a|ab"` はfalse、`"ab" matches "ab|a"` はtrueとなる。`not matches` は既存のbool集約結果を論理否定し、配列要素ごとの反転ではない。`@cs`／`@ci`、既定Optimizeと `@noopt` の分岐、共有処理予算、取消、regexの100 ms timeoutを維持する。既定Optimizeは文字列literal patternの不正regexを式の解析時に拒否し、動的または `@noopt` の不正patternは `matches` をfalseにする。対応範囲は既存のregex互換変換器に従い、任意PCRE2構文の完全互換を主張しない。

[固定原典](../tests/Fixtures/LineTransformMatchesOperator/README.md)は106件（成功100件・例外6件）で、golden SHA-256は `1e656f041600072588ac168fc4ad546eb2abbea78ca7e762c36b6e17ef5598ff`。公開readerは106件のCLI観測をnative由来の結果と照合する。Windows x64 Releaseの限定E2Eは334成功／0失敗／0スキップ、全体E2Eは193,382成功／0失敗／5スキップだった。Release buildは0 warning／0 errorで、5つのpackage lockfileは不変。Windows x64 Native AOTは `nativeAot=true`／`selfTest=passed`、全体UI自己検証17,216成功／0失敗（827.357秒）、限定E2E334成功／0失敗／0スキップで、独立readerも成功した。macOS／Linuxの実測は未実施。

## 一般行変換のlogger関数（2026-10-16、Windows x64検証済み）

`LogError`／`LogWarn`／`LogInfo`と`@name`をtyped-AST評価へ接続した。引数を左から一度ずつ評価し、全件が成功した後にseverity・名前・単一空白区切りの本文をloggerへ渡し、最後の引数の型付き値を返す。未選択枝は評価せず、例外前の未完成メッセージを記録しない。最適化後もログ副作用を保持する。

[固定C++原典](../tests/Fixtures/LineTransformLogging/README.md)の35ケースをMSVCで独立に二回実行し、goldenと実行証拠をreaderで照合した。Content containsを含む最新Release buildの全体E2Eは190,221成功／0失敗／5 skip、12,719 commandsで、logger assertions 105件・固定原典reader・Text／Table／report-project／package-projectのsink経路も成功した。GUI自己検証は注入sinkを確認する。

このfixtureはtyped-AST関数・directive adapterの契約であり、レガシー全体parser・optimizerの一致を証明しない。GUIの実Trace listener表示、他OS／RIDは未検証で、全機能移植の完了を意味しない。

## 原文Contentの行数・部分行（2026-10-16、Windows x64検証済み）

`Content`、`LineCount(Content)`、`Sublines(Content,start[,len])`を原文snapshotに限定して実装した。Coreは比較前に渡された全文字列を使い、入力fileの再読込みはしない。実在するText／Table入力では、互換文字列化のためだけに元の絶対pathを一時的に受け取り、`<FileContent:{path}>`を返す。未保存・仮想入力のpathはnullで`<Content>`となる。pathはworkspaceやreportの保存値へ追加しない。Sublinesを含む式の複数行結果は一つのopaque比較キーとし、元の行数・EOL・原文を保つ。既存の非Sublines式の生成CR/LF拒否と固定readerは維持する。

この初版のWindows Release buildは警告0／エラー0。手定義の[固定契約](../tests/Fixtures/LineTransformContent/README.md)と`--line-transform-content-only`は842成功／0失敗／0省略、143 commandsで、独立readerが108成功ケースと26件の期待するarity／evaluation拒否を確認した。`HeadlessLineTransformChecks`は2,788成功／0失敗。これらは後続の`Content contains`追加前の記録である。

Windows x64 Native AOTの初版は.NET SDK 10.0.402／runtime .NET 10.0.13で発行した。`publish-manifest.json`は`nativeAot=true`・`selfTest=passed`を記録し、14 payloadすべてのサイズ／SHA-256が一致した。全体headless UIは17,193成功／0失敗、849.300秒／1,800秒枠。外側の検証wrapperは実行後のTemp内Core／Providers lock seed変更を不変性違反として失敗を記録したが、リポジトリの5 `packages.lock.json` は実行前後で同一SHA-256だった。

## 原文Contentの行単位contains検索（2026-10-16、Windows x64検証済み）

`Content contains "needle"`は比較前に確定したsnapshotの各物理行を検索し、CR／LF／CRLFを含めず、行をまたぐ一致を作らない。空needleはfalseで、`@cs`／`@ci`を尊重する。`@ci`は既存のscalar `contains`と同じ決定的なASCII大小文字foldであり、旧Windows localeのUnicode case-fold goldenとは主張しない。遅延行offsetを使って入力fileを再読込みせず、行・文字比較を共有作業予算に課金し、取消を確認する。

追加後のRelease全体E2Eは190,221成功／0失敗／5 skip、12,719 commands。Content contains assertions 126件、logger assertions 105件、全体の独立reader assertions 2,032件がすべて成功した。独立・oracle reader関連の110 process commandはすべてPID・実終了時刻・終了コードが記録され、期待する比較差分と拒否結果を各E2E assertionで確認した。5 skipはUnix 0600／macOS case alias／macOS FIFO・socket／実デスクトップ・任意入力互換／case-collision filesystemのWindows外または実OS限定範囲である。`--line-transform-content-only`単独実行はこの更新後には再実行せず、同シナリオを含む全体E2Eと全体UIで検証した。

Windows x64 Native AOTは.NET SDK 10.0.402／runtime .NET 10.0.13で発行し、manifestは`nativeAot=true`・`selfTest=passed`、14 payloadを記録した。全体headless UIは17,216成功／0失敗、865.023秒／1,800秒枠で、Content関連51 assertionsもすべて成功した。`Prop`、pathを引数に受けるLineCount、FileContentRefの原本byte／metadata比較、任意path読込み、一般`.flt`全体の移植完了、他OS／RIDの実測を意味しない。

## 一般行変換のUnicode正規化（2026-10-16、Windows x64検証済み）

`NormalizeUnicode(value[,form])`を共通評価へ追加した。既定はNFCで、formはNFC／C／1、NFD／D／2、NFKC／KC／5、NFKD／KD／6を受け付ける。無効な文字列／整数formはvalueを評価する前に拒否し、その他のform型はNFCとして扱う。Windowsは`NormalizeString`、その他のOSはmanaged `string.Normalize`を使い、失敗時は入力文字列を保つ。none・scalar文字列化・一段array・表列・通常／`@noopt`・定数化しない境界、case directive、共有作業／出力予算と取消を検証する。

最新Release buildの限定E2E`--line-transform-unicode-normalization-only`は580成功／0失敗／0省略、85 commands。全体E2EとWindows x64 Native AOT全体UIは前節の同じソースの記録を参照する。期待値は手定義で、Windows `NormalizeString`のOSデータと他OSの.NET Unicodeデータ、Native WinMerge goldenの完全一致を主張しない。

## 一般行変換の文字種変換（2026-10-16、Windows x64検証済み）

`ToHalfWidth`／`ToFullWidth`／`ToHiragana`／`ToKatakana`／`ToSimplifiedChinese`／`ToTraditionalChinese`を追加した。原典`FilterExpressionNodes.cpp:2808-2859`の一引数・none保持・直下配列適用・scalar文字列化を共通経路へ接続し、原文を保持する。Windowsは`unicoder.cpp:1447-1474`と同じ個別LCMAPフラグの`LCMapStringEx`、macOSは固定CoreFoundation frameworkの`CFStringTransform`を使用する。NFKC/NFKDは併用しない。

macOSのAPI根拠は[Apple公式CFString.h](https://raw.githubusercontent.com/apple-oss-distributions/CF/main/CFString.h)の全文範囲・逆変換・ICU ID契約、変換IDは[ICU公式root.txt](https://raw.githubusercontent.com/unicode-org/icu/main/icu4c/source/data/translit/root.txt)の`Fullwidth-Halfwidth`／`Hiragana-Katakana`／`Traditional-Simplified`／`Simplified-Traditional`による。UTF-16明示長、pointer幅のCFIndexと1byte Boolean、finallyでの全CF所有参照解放を使用し、反射や動的DLL探索は追加しない。OSの変換データの完全一致は保証しない。

最新Release buildでの限定E2E`--line-transform-character-mapping-only`は626成功／0失敗／0省略、99 commands。全体headless UIの`HeadlessLineTransformChecks`は2,788成功／0失敗で、前節に記録した同一ソースのmanaged全体E2EとWindows x64 Native AOT全体UIも成功した。Native WinMerge golden、OS間の全Unicode一致、macOS現物と他RIDのAOTは検証済みと扱わない。

## 一般行変換の時計関数（2026-10-13、Windows限定検証済み・全体検証継続）

Now／TodayをCore・App・CLI／GUIへ採用した。compile一式で一snapshotを取得して全行で共有し、別式・再compileではfreshにする。Todayのlocal日付0時UTC構築と23／25時間、Timestamp origin fold／`@noopt`を保持する。E2Eの時計3呼出し元では明示した`--python`を独立readerへ渡す。

WindowsのRelease buildは警告・エラー0、時計限定E2Eは329件、一般行変換限定UIは2430件が合格した。win-x64 Native AOT発行物は実14ファイル＋manifest・ライセンス5件を親が照合し、Native時計E2E329件と限定UI2430件も独立資格化した。Native E2Eの旧postcontrollerは古い出力名の照合で失敗したまま保全し、製品再実行なしの独立reader資格と区別する。Native UIの650出力・297 PNG・時計bounds31行を照合し、通常／最小多数タブのPNGを親が目視した。

全体UIの製品実行は16835件・失敗0／OS0だが、全stream／PNG／workspaceの独立成果物資格は継続中である。全体E2Eは検証用の264〜294文字pathでOSごみ箱への安全な移動を確認できず、2103成功・7失敗・2省略／1000 commandsで途中終了した。同じE:・同じ製品の短いpathでFolder GUI1060件・7ごみ箱caseを独立照合した。長いpathでの永久削除拒否を維持し、短namespaceの全体E2E再実行を準備済みだがC:空き9GiBの開始条件の回復を待っている。全体E2Eの合格、Error本文の視覚到達、通常desktop／ネイティブ保存dialog、macOS／他RIDは未資格。自然Windows時計6観測は原典body／source-derived predicateの限定照合であり、native TZ一致・原典全AST／Optimize・全API一致は未資格。

## 一般行変換の週・月・年の初め（2026-10-13、Windows製品検証済み）

StartOfWeek／StartOfMonth／StartOfYearをCoreとAppの日時環境へ採用した。文字列またはTimestampを対象に、ローカル暦0時の構築、週だけのUTC時間軸による曜日分の減算と再構築、配列の順序・非対応型のnone・途中例外による候補全体拒否を保つ。日時literalだけを定数化し、StringLiteralの関数は実行時に評価する。

固定原典bodyは126記録×2の108body／18除外、Windows自然ja-JP／Tokyoで実終了0・保持出生・両EOFまで採取した。body成功70／例外38と、成功Timestampのserializerが返すSystem exception文字列14件を分ける。外部候補の公開E2Eは原典108bodyの582投影とsource由来62項目の計644項目に合格した。Windows製品cohortはRelease buildがwarning／error 0、全体E2E 187,434 pass／失敗0／既存skip5、全体UI 16,759 pass／失敗0を確認した。win-x64 Native AOT発行と暦関数限定E2E 2,603 pass／失敗0、一般行変換限定UI 2,354 pass／失敗0も実終了0と出力回収まで確認した。Native UIの全258 PNGと暦関数の操作bounds 26行（dialog6行）を独立照合し、親が通常／最小・多数タブと設定dialogを目視した。全Native E2E／UI、通常desktop、実OS dialog／pointer、他RID、CI制限内完了は未実測である。NUL／nullarray／unsafe値・他profile／macOS／DST・原典全AST／parser／Optimizeは未資格とし、原典closureを再実行せず固定SHAと全entry CRCで照合する。

## 一般行変換の日時変換関数（2026-10-13、Windows製品検証済み）

ToDateTimeとToDateStrをCoreへ採用した。前者は文字列をTimestampへ変換し非文字列・空文字列・解釈失敗をnoneとし、後者は文字列またはTimestampからローカル日付を返しconstructor／hostの失敗を例外で拒否する。配列は直下要素だけを順番に変換し、入れ子はnone、途中失敗は候補全体を拒否する。成功した文字列literalのToDateTimeだけを日時literalへ最適化し、ToDateStrは評価時に実行する。

固定原典bodyは121記録×2、111body／10除外を実OS終了0・保持出生・両EOFで採取した。原典ToDateTime59bodyはTimestamp27／none29／array3、ToDateStr52bodyは成功33／例外19。日時serializerが返すSystem exception文字列と関数bodyの例外を分けた。公開planの外部E2Eは原典110bodyの502投影とsource-derived24観測の計526項目に合格した。残る1bodyは公開arityと異なる原典ToDateTimeの0引数であり公開互換には数えない。NUL2／nullarray2／unsafe ToDateStr Timestamp2／unsafe locale ToDateStr3／ToDateStr0引数1は未実行、原典ToDateTime非Syntax例外枝も未観測。初回harnessの大整数入力生成を既存ToInt(string)へ訂正した失敗記録を保持し、同じcompiled候補で再照合した。製品cohort b48はRelease build警告0、全体managed UI 16,633項目、全体E2E 184,832合格／既存5スキップで検証した。Windows x64 Native AOT発行は警告0、Nativeの日時変換限定E2E 1,041項目／191実アプリ呼出し、一般行変換限定UI 2,228項目に合格した。実終了0・保持出生・両EOF・ソース不変を確認し、Native UIの224PNGを独立復号、日時変換の6PNGを目視、12公開観測・6ダイアログboundsを照合した。Native全体UI／E2E・他RID／macOS・実OSダイアログ・原典全AST／parser／Optimize・他profileは未資格である。

## 一般行変換の日時literal（2026-10-12、b47検証）

純Coreへ`d"…"`／`D"…"`と原典の8日時形式を採用した。値はTimestamp型の符号付き64bit epoch microsecondsとして保持し、日時同士の6比較、日時±整数・日時差（microseconds）、`InRange`／`IsWithin`、混在型の`Minimum`／`Maximum`を扱う。日時は`Sum`／`Average`の数値集計に寄与しない。公開NULを拒否し、原典で未定義の符号付き演算になる値の文字列化を拒否する。共有予算・取消・原文を保持し、hostのlocale・現在時差・対象日時のDSTはruntime注入へ分離して保存DTOへ含めない。年の負値表示を符号込み最小4桁へ合わせ、UTC値とlocal変換後の有効性を別々に検査した。

[日時fixture](../tests/Fixtures/LineTransformTimestampLiterals/README.md)に原典constructor31件とguarded48件のsource・raw出力を保持する。primary31のNULはforensicのみ、guarded48のrawCtor12は公開構文なし、unsafe locale3はserializer未実行、NUL1は公開拒否として分ける。原典と同じWindows profileでのみsafe32（24 serialized／8 constructor error）をd/D・通常／@nooptで直接照合し、他profileは理由付きskipとする。安定する演算のsource-derived engineering観測と原典constructor／serializer観測を区別し、Pocoの浮動小数点に関わる最小年境界・他localeを同等性確認済みとはしない。原典全AST／lexer／parser・typed公開観測・全機能移植の完了を意味しない。

凍結source b47のRelease buildは警告0／エラー0、既存5 lockfileを保持した。managed全体UIは16,589成功・失敗0と14独立reader、managed全体E2Eは183,792成功・失敗0・既存skip5・11,563命令を確認した。E2Eは実PID29868の保持handle・OS終了0・両EOF・timeoutなし、3,525.622秒で完了した。最初の全体E2Eは274文字の対象ファイルパスでごみ箱削除のOS guardに拒否され、保護を維持したまま同じ製品を短い外部Tempで再実行して成功した。

Windows x64 Native AOTは実PID26104・142.080秒・警告0・OS終了0・保持handle・両EOFで生成を完了した。PE32+／AMD64、実行file SHA-256 `40afaceb23e05efffd26e6772765fa2fb696a9cc1a42e5ae91b19e598bd2a780`、14製品fileと別manifest1、自然に同梱された5ライセンスを照合した。Publish内蔵自己検証は省略した。生成物のNative行変換UIは2,184成功・失敗0・218 PNGと日時dialogの6操作boundsを照合した。Native日時限定E2Eは3,922成功・失敗0・skip0・1,372命令、実PID31056の保持出生・OS終了0・両EOF・74.804秒を確認した。初回の計測toolは一時fileの列挙と属性取得の間の消失で停止したが、子の消失だけを計測で許容する外部toolへ修正し、root不在・reparse・容量制限の拒否を保持して新しい短いrunで再検証した。旧tool失敗を成功へ置き換えていない。Native全体E2E・他RID・macOS現物・CI600秒枠・通常desktop／実OS pointer・dialogは未資格化。
## 一般行変換のVersion literal（2026-10-12、製品検証中）

純Coreへ`v"…"`／`V"…"`とASCII scanf四部品の部分変換を採用した。majorの符号bit、未変換部品0、非マスクの左結合加算とIntLiteral専用最適化のconstructor originを原典へ照合し、未定義shift／変換／加算は拒否する。原典constructor60件、原典quote・int64文字列化128観測、C++17のsafe sign-bit18件・36観測を保持した。原MSVCの`32768.0.0.0`は`-9223372036854775808`であり、親の独立readerが原典・コンパイラ・実process終了／両EOFを照合した。最終b44はRelease警告0／エラー0、限定E2E14,513成功・失敗0・skip0・1,934命令、実GUI2,140成功・失敗0。公開156観測（154 serialized成功・NUL2解析拒否）、別12 Compile-only probe・34 engineering観測、原セル・行対応・HTML・保存・復元・包装を確認し、親が14独立reader・36操作boundsと通常／最小／37タブのPNGを照合した。managed全体UIは16,545成功・失敗0で、実process終了0・保持handle・両EOF・timeoutなし、追加14独立readerも合格した。managed全体E2Eは179,871成功・失敗0・固定skip5・10,191命令、実終了0・保持handle・両EOF・timeoutなし。skipはUnix mode・macOS case alias・Mac FIFO/socket・実desktop等の範囲外・case collisionの既存5項目として記録した。Windows x64 AOTの生成は165.801秒で実終了0・保持handle・両EOF。13製品file・別manifest1・4ライセンスのSHAを照合した。Publish内蔵自己検証は指定により省略した。生成物へ別プロセスで実行したNative全体UIは二回とも16,545成功・失敗0（936.928秒／810.675秒）、28独立reader・各824描画PNG／50 bounds、限定E2E14,513成功・失敗0・skip0・1,934命令（実GUI2,140成功）、独立テキスト互換95成功・失敗0・40命令を確認した。全子processの実終了・出力回収を照合し、元集計wrapperの整形JSON読込み失敗は保持したまま、親の新しいJSON全文readerと画像再照合を実終了0・両EOFで完了した。ローカル上限1,800秒の検証であり、Native全体E2E・CI600秒枠・他RID・実OS pointer／dialogは未資格化。Native全lexer／parser・typed AST・他OS／RIDと全機能移植完了を意味しない。

## 一般行変換の期間literal（2026-10-12、製品検証中）

純Coreへ週・日・時・分・秒・ミリ秒の22 ASCII unitを数値へ直結するliteralを採用した。原典のdoubleの段階倍率とmicrosecond整数への切捨て・単項負号、数値解析失敗時0を保ち、倍率後の未定義64bit castは拒否する。固定106原典の92公開serialized候補・10constructor専用・4platform丸め未実行、別21解析probeと24engineering観測・共有規則予算を分ける。原典Native二回・17 source blobs・62 spansと実process終了／両EOFは独立readerで照合済み。Durationのみのb39はRelease警告0／エラー0、限定E2E9,732成功・失敗0・skip0・1,369命令、実GUI1,854成功・失敗0。親が12独立reader・36操作boundsと通常／最小／37タブPNGを照合した。Version追加後のb44でも限定14,513成功・失敗0、managed全体UI16,545成功・失敗0と14独立readerの原典・公開照合が合格し、実process終了0・保持handle・両EOF・timeoutなしを確認した。managed全体E2Eも179,871成功・失敗0・固定skip5・10,191命令で、実終了0・保持handle・両EOF・timeoutなし。AOT生成と別プロセスのNative全体UI二回（各16,545成功・失敗0）、28独立reader、限定E2E14,513成功・失敗0・1,934命令と独立テキスト互換95成功・40命令を確認した。元集計失敗を保持し、親のJSON全文readerによる集計と画像再照合は実終了0・両EOF。Native全体E2E・CI600秒枠・他RID・実OS pointer／dialogは未資格化。Native全parser・typed AST・cache互換と全機能移植完了を意味しない。

## 一般行変換の容量literal（2026-10-12）

純Coreへ整数／小数とB／KB／MB／GB／TBの直結literalを採用した。原典のdoubleの段階的1024倍・整数切捨て・単項負号、解析失敗時0を維持し、倍率後の未定義64bit castは拒否する。固定95原典は77公開serialized候補・14constructor専用・4platform丸めを分け、別18probeと隣接token／丸めの追加engineering観測を保持する。原典17 blobs・62 spans・実Native二回と両EOFを独立readerへ照合した。最終b33はRelease警告0／エラー0、限定E2E7,586成功・失敗0・1,074命令、実GUI1,616成功・失敗0。managed全体E2E172,944成功・失敗0・固定skip5・9,331命令、全体UI16,021成功・失敗0で、実process終了0・保持handle・両EOF・timeoutなしを確認した。Windows x64 Native AOTは内部／独立全体UI各16,021成功・失敗0、限定7,586成功、互換95成功。親の独立readerは95 Native原典と77公開serialized一致・18未実行、別18probe、原セル・履歴・保存・包装、通常／最小／37タブのPNGと操作boundsを照合した。AOT発行985.343秒・独立UI818.854秒はローカル1,800秒枠での実測であり、CI600秒・他OS／RID・Native全体E2E・実OS pointer／dialogは未検証。全機能の移植は未完了。

## 一般行変換の集計（2026-10-12）

純Coreへ`Average`・`Sum`・`Maximum`・`Minimum`を採用した。最初に到達したproviderの全行、条件→値、直下array、最初の有効型と全scalar件数、各変換／ルールの成功cacheを固定原典へ照合した。公開変換は失敗時に私有候補全体を拒否し、非有限値と64bit範囲外の整数化は明示拒否する。141 Native原典を104件の公開serialized-key派生観測と37件の表現不能・未実行へ分け、別の引数数probe8件を保持する。原AST・特殊context・IEEE bitsと全parserの同等性は含めない。

最終b30はRelease警告0／エラー0、限定E2E5,748成功・失敗0・820命令、実GUI1,400成功・失敗0。managed全体E2E171,106成功・失敗0・固定skip5・9,077命令、全体UI15,805成功・失敗0で、実process終了0・保持handle・両EOF・timeoutなしを確認した。Windows x64 Native AOTは内部／独立全体UI各15,805成功・失敗0、限定5,748成功、互換95成功。親が849証拠file、13発行payloadと4,395限定E2E entryの全SHA／size／CRC、355ソースと保護1,178件を確認し、別の8readerと通常／最小／37タブのPNG3枚で原セル・編集・保存操作の到達を照合した。AOT発行1,051.018秒、独立UI806.999秒はローカル1,800秒枠の実測であり、CI600秒・他OS／RID・Native全体E2E・実OS pointer／dialogは未検証。復元用backupは全15,383 regular fileと208 named streamを保持する。後続のmodule scope限定検査・36 leaf linkと通常rootの37段階commonTrash清掃で旧Native作業rootと今回のごみ箱実体／管理情報の不在を確認し、既存bin597項目を保持した。全機能の移植は未完了。

## 一般行変換の包含範囲判定（2026-10-12）

純Coreへ`IsWithin(value, lower, upper)`を採用した。下限と上限を両方含み、両端が等しい場合もその端を含む。評価順・型・配列・UTF-8／ASCII比較・共有予算・取消は`InRange`の経路を共有する。[包含範囲fixture](../tests/Fixtures/LineTransformWithin/README.md)は72原典を59公開serialized-key一致・3解析拒否・10表現不能の未実行に分け、別の四引数probeと12件のvalue／keyだけのToInt bridgeを保持する。原典200 checksと採用後の公開診断を独立readerへ照合した。typed AST／Timestamp／全parser互換を意味しない。

最終b26はRelease build警告0／エラー0、限定E2E3,511成功・失敗0・537命令、実GUI1,124成功・失敗0。managed全体E2E168,869成功・失敗0・環境等skip5・8,794命令、全体UI15,529成功・失敗0で、実process終了0・保持handle・両EOF・timeoutなしを確認した。Windows x64 Native AOTの内部／独立全体UIも各15,529成功・失敗0、限定3,511件と互換95件が成功した。親の別readerで従来五系統とIsWithin59一致／3拒否／10未実行・別probe1を照合し、同じ上端をInRangeだけ除外すること、表の原セル保持・編集履歴、通常／最小／37タブの操作到達をPNG・JSONへ照合した。348ソース・保護1,178件と13発行payloadのSHA／sizeを維持し、原本・復元証拠を外部へ保持した。AOT発行997.920秒、内部UI872.277秒、独立UI810.141秒はローカル1,800秒枠での実測であり、CI600秒・他OS／RID・Native全体E2E・実OS pointer／dialogは未検証。全機能の移植は未完了。

## 一般行変換の範囲判定（2026-10-12）

純Coreへ`InRange(value, lower, upper)`を採用した。下限を含み上限を含まない判定、下限・上限・値の評価順、整数辺のexact比較とdouble辺だけの変換、UTF-8順／Poco ASCII fold、scalar／直下arrayを固定C++関数本体へ照合する。型不一致はnone、引数数は解析時に拒否し、既存の共有予算・取消と原文保持を使う。[範囲fixture](../tests/Fixtures/LineTransformRanges/README.md)は72原典を59公開serialized-key一致候補・3公開引数数拒否・10表現不能の未実行に分け、別の四引数probeを保持する。12件のToInt decimal bridgeは最終value／keyの一致だけで、typed ASTや全parserの互換を意味しない。

最終b25はRelease build警告0／エラー0、限定E2E2,825成功・失敗0・450命令、実GUI846成功・失敗0。managed全体E2E168,183成功・失敗0・環境等skip5・8,707命令、全体UI15,251成功・失敗0、実process終了0・保持handle・両EOF・timeoutなしを確認した。1命令の壁時計逆転は同時刻のWindows時刻補正イベントへ照合し、補正後178.039 msとStopwatch178 msが一致した。Windows x64 Native AOTも内部／独立全体UI各15,251成功・失敗0、限定2,825件と互換95件が成功した。親の別readerでscalar500／array465／search370 fields・Replace78一致／10拒否／12未実行・InRange59一致／3拒否／10未実行と別probe1を照合した。344ソース・保護1,178件と13発行payloadのSHA／sizeを維持し、原本・復元証拠を外部へ保持した。CI600秒・他OS／RID・Native全体E2E・実OS pointer／dialog、旧parser／Optimize全体は未検証。全機能の移植は未完了。

## 一般行変換の文字列置換（2026-10-12）

純Coreへ文字列needleの`Replace`を採用した。三引数の先行評価、scalar／直下array、非重複一致、置換結果を再検索しないこと、Pocoの`$0`展開、定数needleのempty-matchとOptimizeの拒否を固定原典へ照合する。`@ci`はこの関数だけ固定PCRE2 10.47／Unicode 16.0.0のcaseless集合で扱い、既存比較／検索のASCII foldを保つ。外部runtime依存は追加せず、生成表のBSD告知を通常／AOT binaryへ同梱する。[Core契約](../Src/DiffBeacon.Core/README.md#一般行変換)、[固定原典と独立reader](../tests/Fixtures/LineTransformReplace/README.md)を参照する。

実C++ sidecarは原典100件を元のvalue／trace／modeを変えずserializedし、93結果・7例外を二回のcompileと実processの終了／EOFへ照合した。公開式は78件のserialized bytes一致・10拒否・12表現不能の未実行を区別する。生成表は全1,114,112 codepoint・2,938 relations／1,484 pairsを検査した。新しい文脈の独立レビューに成立P1／P2はない。採用後のRelease buildは警告0・エラー0、限定E2Eは2,144成功・失敗0／363 commands、実GUIは572成功・失敗0。managed全体E2Eは167,502成功・失敗0・環境等skip5／8,620 commands、全体UIは14,977成功・失敗0。Windows x64 Native AOTの発行と独立全体UIは14,977成功・失敗0（851.714秒）、同限定E2Eと互換95件も成功した。実processの終了0・保持ハンドル・両EOFと、342ソース／保護1,178件のSHA、13発行payloadと告知の同梱を親が独立照合した。CIの600秒制限・他OS／RID・Native全体E2E・実OS pointer／dialogは未検証。typed regexと旧parser／Optimize全体は未完了で、全100件の公開互換完了とは扱わない。

## 一般行変換の検索関数（2026-10-12）

純Coreへ`StrCount`／`StrFind`を追加した。重ならない一致数、0始まりの一致回、UTF-8 byte位置、ASCIIのcase fold、第一引数のscalar／直下arrayへの適用を旧関数本体に照合する。検索patternは関数評価内で遅延生成し、none・空配列・全noneでは作らない。UTF-8 bytesとKMP prefixの容量を確保前に共有予算へ計上し、超過は候補全体を拒否する。Text／RAW表、三者maskとgroup順、共通dialog、保存／復元・HTML・包装は既存経路を使用し、原文セルと保存本文を保持する。[Core契約](../Src/DiffBeacon.Core/README.md#一般行変換)、[固定原典と境界](../tests/Fixtures/LineTransformSearch/README.md)、[限定E2E](../tests/DiffBeacon.E2E/README.md#一般行変換の限定検証)を参照。

最終b14/e11はRelease build警告0・エラー0、実App限定E2E1,367成功・0失敗・0skip（252命令）、UI231成功・0失敗。実PID34240・birth・保持handle・終了0・両EOF・timeoutなし、54.662秒を確認した。別readerは検索68ケースの62原典一致と引数数不正6件の公開compile拒否・原文保持、全370 fieldsを照合し、従来scalar500／array465 fieldsも維持した。原典の6評価エラーと公開constructorの6解析エラーを同じ資格として扱わない。RAW Column2の引用符を含む位置5、dialogの二重適用・取消、三者表の編集・Undo／Redo・明示保存・再読込み、HTMLと古い候補の拒否をPNGとJSONで確認した。256規則・60,000 byte needleの不要pattern6例は成功し、検索元のある2例は共有上限で拒否して入力・workspace・既存HTMLのSHAを保持した。公開stderrはworkと生成量の理由をまとめるため、個別カウンターの先着は断定しない。旧C++ parser／Optimize全体、他OS／RID、実OS dialog／pointer／clipboardは未検証で、残る関数／typed literal等の移植は未完了。

この検索増分を含む全体managed E2Eは166,725成功・0失敗・5skip、8,509主命令（ScopeArchive12件込みstdout file8,521）。実PID15780・birth・保持handle・終了0・両EOF・timeoutなし、2,445.133秒を確認した。5skipはUnixFileMode、APFSのcase alias、Mac FIFO／socket、三者の実desktop／全入力互換、case collisionの環境・範囲制限として理由を保持する。全体managed UIは14,636成功・0失敗、実PID21968・終了0・両EOF・829.868秒。終了した全体UIを親の別プロセスreaderでもscalar500／array465／search370 fieldsへ再照合した。

Windows x64 Native AOTも全体UI14,636成功・0失敗（860.202秒）、対象UI231成功・限定E2E1,367成功・互換性確認95成功。実Publish10520、native UI29856、限定13920、互換32376の保持handle・実終了0と最終集計を確認し、外側Publish／限定runner／独立readerは両EOF・timeoutなし。内部native UIの独立stdout／stderr EOFは未採取で、ローカル1,800秒枠をCI600秒の資格へ流用しない。12 payloadのSHA／size、338 source、5 lock＋bin／obj1,173 file（保護集合1,178）のSHAとpathset不変を親でも照合した。新しい文脈の独立レビューに成立P1／P2はなく、検索原典のreaderも一致した。全体native E2E、他OS／RID・実OS dialog／pointer／clipboard、旧C++ parser／Optimize全体は未検証。

## 一般行変換の配列演算（2026-10-12）

純Coreへ入れ子の配列、Array／At、AnyOf／AllOf／AllEqual、要素ごとの論理・IfEach・Choose／ChooseEach、scalarとの算術・比較・文字列関数適用を追加した。Text／RAW表、三者maskとgroup順、共通dialog、保存／復元・HTML・包装は既存の一般行変換経路を使い、元のセル・本文・外側EOLを保持する。配列の確保前に共有予算を検査し、累積1,048,576 slotsを超える比較候補を拒否する。詳細は [Coreの契約](../Src/DiffBeacon.Core/README.md#一般行変換) を参照。

b11e9の実App限定E2Eは734成功・0失敗・0skip（146命令）、描画・操作UIは203成功・0失敗。別プロセスreaderで既存scalar86件・500 fieldsを維持し、配列90件・465 fieldsは89件の原典一致と、物理CR/LF生成の1件の評価拒否・原文保持を確認した。[配列fixture](../tests/Fixtures/LineTransformArrays/README.md)は原典90件を無改変で保持する。CLIの原典25例、128要素×8192行の成功／8193行の拒否、実dialog・RAW表編集・Undo／Redo・保存再読込み・設定復元・取消と古い候補の拒否、PNGを確認した。Release buildは警告0・エラー0、5 lockfileは変更なし。

前の全体E2Eでは一般変換なしの表JSONへ追加した行分類フィールドにより既存readerが1件失敗した。一般変換を使用する表にだけ分類を出力するよう修正し、b11のIndependentText限定E2Eは95成功・0失敗（40命令）で既存JSON契約を確認した。この修正と配列を含む全体managed E2Eは166,092成功・0失敗・5環境skip、8,403主命令（stdout fileはScopeArchive12件込み8,415）。実PID33204・終了0・保持handle・両EOF・timeoutなし、2,375.984秒を確認した。全体managed UIは14,608成功・0失敗、実PID27988・終了0・両EOF・791.514秒。Windows x64 Native AOTの全体UIも14,608成功・0失敗、対象UI203成功・限定E2E734成功・互換性確認95成功、12 payloadのSHA／sizeと335 source・5 lock／repo bin・objの1,178 entry不変を確認した。Native全体UIは853.866秒のローカル1,800秒枠で、CI600秒の資格にはしない。子Native UIは保持handle・実終了0・最終集計を確認したが、Publish内の明示stdout／stderr EOFは採取していない。外側Publishと限定E2E／readerのEOFは確認済み。他OS／RID・実OS dialog／pointer／clipboard、全体native E2E、旧C++ Optimize実行は未検証。旧parser全体、生成改行の原行写像、残る関数／typed literal等の移植は未完了。

## 一般行変換の基本pipeline（2026-10-11）

純Coreの式解析・provider・共有予算、Text／Tableと三者の実pane mask、同期点・フォルダー三者・4ペイン結果へ一般行変換を接続した。共通の「行変換…」dialog、繰返し指定の`--line-transform all|left|base|right EXPRESSION`、project／workspaceのoptional規則、`--report-project`の保存規則と同flagによる上書き、HTMLの原文・ignored分類と設定表示を追加した。動的`ColumnRange`・基本scalar式・同mask／異maskの順序付きpipeline・プレーンテキスト適用は対応する。静的列範囲／列除外は引き続き行対応補助であり、一般変換を元セルへ再適用しない。

対応構文と拒否範囲は [Core README](../Src/DiffBeacon.Core/README.md#一般行変換)、RAW入力とセル表示は [表の契約](TABLE-EDITOR.md#一般行変換と静的な列指定)、限定実行は [E2E手順](../tests/DiffBeacon.E2E/README.md#一般行変換の限定検証) を参照。生成CR/LFの原行写像・非ASCII区切り文字／引用符・PCRE typed pattern／日時／typed literal／Unicode case等の残る旧言語とparser全体、旧callerの全file I/Oは未完了。旧WinMergeの一般フィルター全機能の互換完了とは扱わない。

最終b10e8ビルドの限定実測は実App E2E 444成功・0失敗・0skip（96命令）、UI 180成功・0失敗、C++固定原典86ケース（73正常・13拒否）の独立reader 500 fields一致。実PID・birth・終了コード0、保持したprocess handleとstdout／stderr EOF、timeoutなしを確認した。原典関数本体と明示AST adapterの境界は [fixture記録](../tests/Fixtures/LineTransforms/README.md) に固定する。二者で未使用のBase規則、静的Right指定、元セル同一で比較キーだけ異なる行のGUI／CLI／HTML分類、変換後の正規表現・空行無視、古い候補の拒否、原文保存とdialog PNGも確認した。処理予算0で一般キーが異なる場合は退避理由を保持し、静的指定だけで意味差を無視しない。この基本pipelineを含む最新の全体managed UI／E2E・Windows x64 Native AOT検証は直前の配列演算節のb11記録を参照する。他OS／RID、実OS dialog／pointer／clipboardの資格へは流用しない。

## 表の行対応に使う静的な列範囲（2026-10-11）

表パネルと `--table-column-range SPEC[:all|left|base|right]` で、行対応キーに使う列範囲を指定できる。包含・除外、末尾までの範囲、指定順と重複抑止、左右・祖先ごとのscopeに対応し、範囲を各行の実セル数へ切り詰める。選択した復号セルを区切り文字で連結して初期行対応に使い、セル値・原文・通常のセル差分表示を保つ。規則は比較プロジェクトとworkspaceに保存し、`--report-project`でも適用する。一般行変換の動的条件式・pipeline・プレーンテキスト適用は本記録の一般行変換節を参照。旧言語の残る構文とparser全体は未完了。

Release buildは警告0・エラー0。限定`--table-column-range-only`は108成功・0失敗・0skipで、列範囲の104 assertionsとheadless UIの11 checksがすべて成功した。

最新managed全体UI自己検証は14,152成功・0失敗。全体managed E2Eは8,252 commands、165,343成功・0失敗・5環境skipで、列範囲の103 assertionsはすべて成功した。E2E本体の`assertions.json`は完了し、ソースの終了分岐からreturn 0となることを確認したが、実行ラッパーのresult JSONは生成されなかった。

Windows x64 Native AOT全体UI自己検証は14,152成功・0失敗（718.107秒）。publish manifestの12ファイルのSHA-256・サイズ照合と、列範囲のAOT限定E2E（108成功・0失敗・0skip）を確認した。repoの`bin/obj`は発行前後で1,173ファイル・2,696,055,885 bytes・aggregate SHAが一致し、5つのpackage lockは発行前の内容へ復元後に各hash一致を再確認した。

実デスクトップ操作は未検証。この実行環境ではWindowsアプリ一覧・起動APIをComputer Useから取得できず、Skyの前回起動も`GetCursorPos failed: アクセスが拒否されました (0x80070005)`で失敗した。macOS／他RID／4 RID CIも未実施。

## 表の行対応から列を除外（2026-10-11）

表パネルで1始まりの列番号を指定し、左右・祖先の全ペインまたは選択ペインだけを行対応キーから除外できる。規則の置換・追加・全解除に対応し、比較プロジェクトとworkspaceに保存する。CLIには`--table-ignore-column N[:all|left|base|right]`を追加した。除外は初期の行対応キーだけに適用し、セル値・セル差分色・原文のraw範囲照合を保つ。一般行変換の動的条件式・pipeline・プレーンテキスト適用は本記録の一般行変換節を参照。旧言語の残る構文とparser全体は未完了。行対応用の静的列範囲は本記録の別節を参照。

Release buildは警告0・エラー0。managed限定E2Eは122成功・0失敗、限定`--table-column-ignore-only` UIは15成功・0失敗、最新の全体managed UIは14,152成功・0失敗。全体managed E2Eは8,233 command、165,240成功・0失敗・5環境スキップで、列除外の111 assertionもすべて成功した。Windows x64 Native AOTの全体UI自己検証は14,152成功・0失敗（704.897秒）、manifestは`nativeAot=true`／`selfTest=passed`。発行物12件のSHA-256・サイズ、発行前後のrepo `bin/obj` 1,173件、5つのsource package lockの不変を照合した。最新AOT発行物の限定E2Eは122成功・0失敗・0skip。実デスクトップ操作はSky起動が`GetCursorPos failed: アクセスが拒否されました (0x80070005)`で失敗し、再取得後もDiffBeaconの対象窓がなかったため未検証。macOS／他RID／4 RID CIも未実施。

## 表の先頭行見出し設定の保存とタブ共有（2026-10-11）

表パネルの「先頭行を列見出しにする」は既定でオフ。設定をLocalAppDataへ保存し、同じアプリの開いている表タブへ同期し、新しい比較と次回起動時に復元する。設定出力を比較入力などの保護対象へ向けた場合は拒否し、設定とチェック状態を元に戻す。

Release buildは警告0・エラー0。限定`--table-headers-only`は26成功・0失敗。最新managed全体headless UIは14,094成功・0失敗で、全体managed E2Eは8,211命令・165,117成功・0失敗・5環境スキップ。Windows x64 Native AOT発行と全体UI自己検証も成功し、UIは14,094成功・0失敗、発行manifestの自己検証は724.539秒、5個のpackage lock hashは発行前と一致した。実デスクトップGUI操作はComputer Useのnative APIが使えず実施していない。最新AOT発行物のE2E、macOS／他RID、4 RID CIは未実施。証拠は `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\tp1011\ui-full`、`C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\tp1011\e2e-full`、`C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\tp1011-aot\publish` に保持している。

## WinMerge テキスト行ブックマーク（2026-10-11）

Text比較の左右ペインに、通常ブックマークと10個の番号付きブックマークを移植した。各側で一覧・次／前への周回移動・消去ができ、行編集では挿入／削除に応じて位置を追従または除去する。ブックマークは比較セッション内だけに保持し、workspaceへは保存しない。

Release buildは警告0・エラー0。限定`--text-bookmarks-only`は18成功・0失敗。全体managed UIとWindows x64 Native AOT自己検証は各14,086成功・0失敗で、AOT manifestは`nativeAot=true`／`selfTest=passed`、自己検証797.697秒を記録した。全体managed E2Eは165,117成功・0失敗・5環境スキップ。最新コードのAOT E2EとmacOS／他RIDは未実施。Windows x64 AOT発行物・自己検証は `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\bm-aot1011\publish` に保持している。E2E入力・出力・PNG・JSON・ログは `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\lb1011\full` に保持している。

## WinMerge単語区切り設定のGUI・workspace対応（2026-10-11）

高度な比較フィルターに「句読点で単語を分割」と区切り文字欄を接続し、`ComparisonOptions`から`TextDiffer`の行内span、`WordLineAlignment`のraw採点へ設定を渡す。比較プロジェクト／workspaceの保存・復元とGUIレポートへの反映を実測し、旧workspaceで項目が欠ける場合はWinMerge既定へ戻る。設定は最大256 UTF-16単位で、カスタム区切りはU+00FF以下に適用し、それより大きい文字は固定分類を使う。直接`WordDiffer`の既存既定と6,048件のgoldenは維持する。WinMergeの全Unicode境界互換は未確認。

Release buildは警告0・エラー0。限定`--word-diff-only`は914成功・0失敗・0skip。最新managed全体headless UIは14,068成功・0失敗で、単語境界設定の既定・独自区切り・無効化・workspace往復・レポート反映と表のヘッダー／列幅チェックを確認した。全体E2Eは8,211 command、165,117成功・0失敗・5環境スキップ。スキップはWindowsのUnixFileMode、macOS/APFSのcase aliasとFIFO/socket、headless対象外の実デスクトップ操作、case-collisionを作れないfilesystemによる。入力・出力・PNG・JSON・ログは `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\sui-retry-1011\full` に保持。34,357 files／2,010,117,417 bytesと28個のテスト生成reparse pointを含むため、圧縮・清掃しない。

Windows x64 Native AOTも最新コードを発行し、全体UI自己検証は14,068成功・0失敗、681.839秒。発行manifestは`nativeAot=true`／`selfTest=passed`を記録した。今回のAOT発行物に対する全体E2Eは未実施で、前候補の165,091件を最新コードへ流用しない。発行物とUI証拠は `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\aot1011-mig4\publish`、managed UI reportは `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\separator-ui-20261011-ed1c1af6\ui-full\ui-report.json` にある。完了時のC:空きは88.66 GiB。

## WinMergeの文字単位／単語単位行内差分（2026-10-11）

比較設定に行内差分レベルを追加した。既定はWinMergeの`OPT_BREAK_ON_WORDS=false`に合わせて文字単位とし、単語単位では変更した単語全体を強調する。GUIの詳細フィルターとCLIの`--word-level`で切り替え、比較プロジェクト／workspaceへ保存・復元し、HTMLレポートの設定情報にも反映する。旧workspaceで設定が欠ける場合も文字単位へ戻る。

Release buildは警告0・エラー0、限定`--word-diff-only`は914成功・0失敗・0スキップ。managed全体headless UIは14,096成功・0失敗で行内差分設定7件を確認した。全体managed E2Eは8,213命令・165,123成功・0失敗・5環境スキップで、word-level関連20件はすべて成功した。Windows x64 Native AOTは発行process終了0、manifestは`nativeAot=true`／`selfTest=passed`、自己検証721.032秒を記録し、全体UIは14,096成功・0失敗、行内差分設定7件も成功した。発行前後で5個のリポジトリpackage lock hashは一致した。5スキップはWindows UnixFileMode、非macOSのAPFS case alias、macOS FIFO/socket、実デスクトップ・全入力互換の範囲外、case-collision非対応filesystemによる。実デスクトップ操作、AOT発行物のE2E、他RIDと4 RID CIは未実施。E2Eは `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\e46c813a7`、AOT manifest・UI reportは `C:\Users\IMT\AppData\Local\Codex\TaskArtifacts\DiffBeacon\word-diff-level-20261011-45de69e3c97240fc84d6473ac4eab257\aot-lock-guard-8f24a2dff82d4863a15c0faf31880e96` に記録した。

## アーカイブ内画像の読取り専用比較（2026-10-10）

アーカイブ一覧から画像を「自動」または「画像」で開けるようにしました。左右の実在するleafを既存の画像比較パネルへ渡し、内包画像は固定読取り専用です。workspaceにはroot SHA・entry chain・leafを保存し、復元後は「比較」を押すまで画像を読み込みません。内包画像のCLIレポート・包装と、元アーカイブへの編集書戻しは引き続き未対応です。

Release buildは警告0・エラー0。`--archive-sources-only` の実GUI自己検証は154成功・0失敗で、自動／明示Image、全GIF frame、Save拒否、workspace復元、破損画像の拒否、停止・再読込・親終了での候補破棄を確認しました。`--archive-present-only` は68成功・0失敗・0skip。Python標準libraryでouter／inner ZIP、GIF原本SHA、左右全フレームのBGRA SHA-256とworkspaceのtyped入力を独立照合し、表示PNGも目視確認しました。Windows Native AOTはMSVC x64 tools componentが未導入のため未実施、macOSのAOT／起動と4 RID CIは未確認です。

## 手動Text同期点（2026-10-10）

Text比較に、カーソル位置から手動同期点を追加・消去する操作を移植しました。左右それぞれの選択行を境界にGNU行対応を区間ごとに実行し、行対応の比較予算は全区間で共有します。通常の二者比較と独立三者Textの選択ペアで使え、同期点は最大256個です。両側で行番号が範囲内かつ厳密に増える場合だけ採用し、対象側の本文を編集するとその側を含む同期点を無効化します。同期点は比較セッション内だけに保持し、workspaceには保存しません。

Release buildは警告0・エラー0。限定CLI E2EのIndependent Textは95成功・0失敗、GNU Textは3,399成功・0失敗。実ボタンを使う限定headless UI検証は70成功・0失敗で、通常二者と独立三者の追加・消去、交差順と末尾空行の拒否、本文保持を確認しました。

全体E2Eは8,190 command、164,975成功・0失敗・5環境別skip。全体managed headless UIは`win-x64`／.NET 10.0.12で13,943成功・0失敗、526.699秒でした。同期点の実操作も成功しています。Windowsの固定SHA付きPython fixtureは`core.autocrlf=true`のcheckout模擬で4 scriptのSHAを維持しました。Binary三者の900×600画面は中央SaveAsまでスクロールしてviewport内にあることを検証し、限定UIは111成功・0失敗です。この修正を含むcommit `e0c23e0` のGitHub 4 RID Native AOT／E2EとCodeQLは成功しました（[.NET CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/38010228288)、[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/38010228381)）。

## 裸BZip2／Zの単一ファイル書込み（2026-10-10）

裸`.bz2`／`.Z`の作成と再梱包に対応しました。通常ファイル1件だけを受け付け、空ファイルも保存できます。ディレクトリと複数entryは拒否し、`.tar.bz2`／`.tbz`／`.tbz2`／`.tar.Z`／`.taz`は従来どおりTAR形式を優先します。複合圧縮鎖の作成と内側アーカイブの再梱包は引き続き未対応です。

Release buildは警告0・エラー0。全体E2Eは8,190 command、164,975成功・0失敗・5 skipでした。Python独立readerはBZip2／Zの30ケース、TAR優先5ケース、期待拒否70件を含めて全検証に成功しました。Headless UI検証は181 assertion成功。Windows x64 Native AOTは.NET SDK 10.0.401で発行され、`nativeAot=true`・self-test passed、13,939 assertion成功・0失敗、自己検証582.01秒でした。これらは同一作業ツリーのローカル実測です。

GitHub Actionsの独立Python readerは、macOSの`tempfile.gettempdir()`が`/var/folders/...`を返して`safe_path`に拒否される場合があったため、`RUNNER_TEMP`設定時は同パスを優先し、未設定時も厳密な一時パス検証を維持するよう修正しました。commit `e0c23e0` の4 RID Native AOT／E2Eと実OSクリップボード検証は成功し、CodeQLも成功しました。

| RID | CI | E2E成功／失敗／skip | 画像clipboard書込／読込 | Binary clipboard書込／読込 |
| --- | --- | --- | --- | --- |
| win-x64 | 成功 | 164,975／0／5 | 8／8 成功 | 6／6 成功 |
| win-arm64 | 成功 | 164,975／0／5 | 8／8 成功 | 6／6 成功 |
| osx-x64 | 成功 | 165,046／0／11 | 6／6 成功 | 5／5 成功 |
| osx-arm64 | 成功 | 165,046／0／11 | 6／6 成功 | 5／5 成功 |

## 裸BZip2／Zの読込みとv9ワークスペース（source292候補、2026-10-09）

BZip2／Zの本文形式Auto／File／Tarをgzip専用設定から分離しました。AutoはTARと明示圧縮鎖を優先し、非TARのraw本文と空本文を単一entryとして読みます。FileはTAR風本文も単一entry、Tarは完全なTARでなければ拒否します。名前は最後の拡張子を除いたbasename、拡張子なしは`noname`です。CLIは`--compression-payload-kind`と比較専用`--right-compression-payload-kind`で、右省略は左を継承します。cross-kindの非Auto設定・gzip設定との非Auto併用・createへの読込み設定は拒否します。GUIの明示inner Openは拡張子なしもmagicから許可しますが、通常の自動候補採用には拡張子なしを追加せず、暗黙再帰は行いません。

source292の41fileを実repoへ取り込み済みです（import receipt SHA `A867FB6F40D08955D38F51E0407C614DECFB8D76F2EFEB3BA682EE9A08112016`）。各層の`containerCompressionPayloadKinds`を選択identity・保存版prefixへ含めます。非Auto保存と明示v9入力はproject-copy／包装でv9を保持し、v1〜v8への新field混入はnullを含め拒否します。gzipだけのv8を維持します。typed入力の原本rootは固定readonly、論理作業版の編集は`InheritedReadOnly=false`を明示した側だけです。包装で原本basenameを維持します。裸BZip2／Z writer・内側repack・複合writerは未対応です。

外部候補build10は警告0／エラー0です。App SHA `2152ACEE096B14A8F050C36752C7F55ECB3C53B37C2072F32434FBAACA852913`の限定GUIは83成功／0失敗で、専用独立readerは67ケース・23原本・15復号ケースを照合して終了0／EOFでした。通常一覧257 DIP・最小850×550一覧101 DIPで、先頭行と操作への到達を確認しました。read end閉鎖と全5 actorの同一出生process不在、source／payload不変を確認しています。証拠は外部Temp `Codex/DiffBeacon/bare-compression292-ui-recheck10-prep-7b6cb4b0f2634be7a86f2da55156c97e/final-closeout.json`、SHA `98099588767DD17B32337C0A9E4C9395FE73F530BCA4916DD6623582B75BC11C`です。[UI契約](../tests/Fixtures/Archives/BareCompression/UI-CONTRACT.md)と[独立reader](../tests/Fixtures/Archives/BareCompression/verify-ui.py)に照合境界を記載します。

限定CLIの160命令・395成功は以前の候補build3での証拠であり、build10のCLI成功へ読み替えません。source288の旧baselineは全体UI13,758成功／0失敗、525.954秒で600秒内、5独立readerとwriterの全byte照合を確認しました。原典は外部Temp `Codex/DiffBeacon/normal-whole-ui-source288-prep-a3715d62177448e0a3763060008adbd1/actual-whole-ui-closeout.json`、SHA `91AD425ADBD5573FC5741A7BAFA4B7277862302978266C9FEE2E509669853E89`です。続く全体E2Eは7,826命令・163,826成功／0失敗／固定skip5、終了0・両stream EOFでした。原典は外部Temp `Codex/DiffBeacon/full-e2e-source288-7826-prep-f0fda05464c844949a1d24c60bfb9a84/actual-full-e2e-closeout.json`、SHA `ADFB4F4152826DFBD8018D9E2AD99191E6EABAAB5BFD5FCCF75C84F102E117B6`です。これらはsource288のmanaged実測で、Optionsの2gap・Treeの実OS終端・prior-scroll視覚golden・native picker／OS pointer／clipboard／Native／他RIDの未資格を維持します。実repo反映済みsource292の通常solution build・全体UI・全体E2E、Native AOT、他RID、通常デスクトップ、実OS pointer／clipboard／native pickerはまだ未検証です。外部候補build10の限定GUI83成功／reader67照合と、CLI旧候補build3の複製／包装契約の実測を、実repoの全体資格へ読み替えません。PNGの復号とbounds照合を、全画素の独立goldenやowner event traceへ拡張しません。以下のsource288以前の履歴は各時点の資格として保持します。


## 単一ファイルgzip writer（source288、2026-10-09）

source284へ既存8置換＋新規4fileのwriter候補を取り込み、source288になりました。裸`.gz`／`.gzip`のcreate／repackは空を含む論理ファイル1件だけを出力し、ディレクトリエントリ・0件・複数件を拒否します。安全なbasenameをFNAMEへ保存し、出力格納名文字コードは既定Latin-1、明示UTF-8／932／1252等、読込み設定とは独立です。単一memberのCRC／ISIZEとMTIMEの0〜uint.MaxValue飽和、atomic公開・取消・入力／既存出力保護を維持します。TAR系は優先判定し、多層writer・内側アーカイブの再梱包・内包entryへの書戻しは未対応です。GUI作成はgzip単一file選択と出力格納名modal、TAR系はfolder選択です。

取り込み前の外部候補compile2はApp `8A024CC2AA8992087D1D0BF2D93233CAC29CDC4A6DB4CF50C1285A12FA6EA301`／E2E `4C67D773C63F3F4AA22C2F4B3B5E2BDC70F10501D5CA7B424AD780FAFAA4F307`、警告0／エラー0でした。先行候補buildの2警告を消した事実は、helperのJSON生成をUtf8JsonWriterへ変更したcompile2のbuild結果として区別します。候補限定CLIは698命令・1,829成功／0失敗／0skipで、13gzip出力を独立readerが全bytes・FNAME・DEFLATE EOF・CRC／ISIZE・空memberまで照合しました。候補headless UIは89 unique checks（旧78＋writer11）が成功し、App PID44036・出生FILETIME134360014645952984・held終了0／EOF・4.601秒でした。独立reader PID45084は終了0／EOFで4create＋1rootRepackの5gzip全bytesと3modal PNGを独立復号しました。証拠は外部Tempの `gzip-writer-candidate288-ui-prep-527a9dbd2c994ef680f9892963a252b3/actual-closeout.json`（SHA `59FA66D71AF61DE30DC454C39C6C9C25DB9504993ECED4C7666DED72A0236499`）です。

現在のrepo source288の通常solution build／runtimeはこの文書更新時点では未資格です。上記は外部候補の限定資格であり、source288の通常全体E2E／全体UI／Native AOT／他RIDへ拡張しません。通常OSのfile／save picker・pointer、modal bounds・owner寿命trace・親before／after状態の独立snapshotは未資格です。modalのowner／closure／parent保持はproducer bool、PNGは表示と復号の証拠として区別します。

writer取り込み前のsource284通常版履歴は、App `666AF2883E762C2056D170F8A9C6770E7F01877814435BD58EEA818C5E549BB0`／E2E `450DD687B5B43159E6CE6DB534F4519563A6C2EC8D778629EB41AC55ECA5147A` です。認識済みv8を未知versionとしていたE2E fixture3件のliteralだけを8→9へ修正後、全体E2Eは7,779命令・163,709成功／0失敗／固定skip5で実終了0／EOF、独立auditも終了0でした。全体UIはApp PID21804・出生FILETIME134360023458869291・522.642秒でWindows600秒内、13,747成功／0失敗、unique13,694／旧重複53の固定multisetに一致しました。Tree／Folder／Options／Binary／Imageの5独立readerは全終了0／held／EOFで原本不変を照合しました。最終原本は外部Tempの `normal-whole-ui-source284-prep-fe084419dd224e22a01079b9bf69d973/actual-whole-ui-closeout.json`（SHA `0896B7B8B2F18591D1C5D350F4D1B3881A54821BDECD14C60776A0BA43DA4CC3`）です。Optionsの2gap、Treeの実OS終端、prior-scroll視覚golden、OS pointer／clipboard／native dialog、Native／他RIDの未資格を維持し、この前版履歴をsource288の成功へ読み替えません。

## gzip本文の形式選択とv8ワークスペース

2026-10-09に本文形式Auto／File／Tarを追加しました。FileはTAR風byteを含むgzip本文全体を単一entryとして読み、Tarはgzip本文を完全なTARとして検証します。Autoは既存の明示圧縮鎖と先頭判定を維持します。File／Tarをgzip以外へ適用すること、形式判定にFNAMEを使うこと、内側containerへの暗黙継承は行いません。CLIは`--gzip-payload-kind auto|file|tar`と比較右側専用の`--right-gzip-payload-kind auto|file|tar`を提供し、右指定省略時は左の指定を継承します。名前復号のcode pageとは独立しています。

typed Sourceと保存版は`containerGZipPayloadKinds`にroot＋各containerの選択を持ち、選択の正規化列をidentityに含めます。同じ原本・leafでもAuto／Fileの保存版を分離し、保存版と親入力のprefix一致、readonly・原本保護・共有容量・全byte／SHAを維持します。新fieldはworkspace v8専用で、旧v1〜v7は省略時Autoとして読みます。旧versionへのfield混入はnull／全Autoでも拒否します。非Autoの保存にはv8を使い、明示v8入力は全Autoの単一entryでも`--project-copy`と包装でv8を保持します。

取り込み前の外部候補managed build4（effective source284、既存25置換＋新規2）は実build PID33352・出生FILETIME134359973576535342・held2204・終了0、54.828秒・警告0／エラー0でした。App SHAは `460C6E12DEC8663E0EC1C6E2311487E1C0BDFF2AEDE5A98ECABE16E2D8AF183C`、E2Eは `22D6E9470C3396F9EA2201DFE744F94D59DDE37EABF0912C66B75787535CABAD` です。限定`--bare-gzip-only`は既存240＋追加411＝651命令、1,712成功／0失敗／0skipでした。実E2E PID33468・出生FILETIME134359976643053353・held終了0・両stream EOF、128.740秒。独立監査PID6772は終了0で全651命令名・重複数・期待exitと1,302 rawのSHA／size、sourceRepo282・旧managed・候補27・新payload202等3,794行の不変を確認しました。保持先は外部TaskArtifactsの `source284-payload-kind-fixed651617-8d20bc6e696a449b971e1ec5bf2ab7ab/candidate-limited651-success-closeout.json`（SHA `6D5A8BA36EA6B6CDFFD13B6A5601E3AE1E6658655DE2173D699E891500C26E94`）です。

同じ候補460Cの限定headless UIは78 unique checksが成功し、App PID42884・出生FILETIME134359980169487485・held終了0／EOFでした。専用独立reader PID14728もverified=true・終了0でraw、固定保存byte、ZIP／PNGを照合しました。before／prelaunch／afterのfreeze SHAは `5D51BCE340092FF09DD0A845A2EBB49FF337B9FF79C48D8DB67380FC1FD0A224` で一致しています。証拠は外部Tempの `source284-payload-kind-ui-compile4-prep-911711ad9d534cf19c38a9f819869c9e/runtime-verified-result.json` と実terminal原本です。実OS pointer・native dialog・視覚golden比較の資格は含みません。

先行候補E131／E2E5636の限定実行は1,672成功／8失敗／646命令・実終了1でした。空matrixのgzip生成が有効な空memberを出しておらず、期待成功の3 entry枝が欠落しました。また明示v8・全Auto単一entryの複製が旧envelopeへ落ちる2件の失敗がありました。有効な空gzip20 bytesを独立PythonでCRC／ISIZE検証し、v8 envelopeと包装を保持する修正後に上記651命令を再確認しました。旧PID33644の失敗・1,292 raw・期待649との差3と8失敗の原本は `source284-payload-kind-candidate617-340941f252934b428d8471022af194fd/candidate-limited-failure-closeout.json`（SHA `E3C830873FCFD1DFAC8918A667044503D27A677873F982014C652D407DC576C4`）に保持しています。

27fileのrepo取り込み後、通常solutionのmanaged buildは実PID20392・出生FILETIME134359985863828641・held2404・終了0／EOF、58.881秒・警告0／エラー0で完了しました。source284と通常bin／objの原本不変を確認しています。App SHAは `666AF2883E762C2056D170F8A9C6770E7F01877814435BD58EEA818C5E549BB0`、E2Eは `B725F2623A983C502A0FEF2A02EE0910A0287B4A4023A1D6B9DF5495B98D6F56`、外部 `solution-source284-prep-8a4bbf0dc7754237abc04f7d546f351b/build-result.json` SHAは `8B877A2BFFAE2DD3F0D696FAEB617855C9BEF6124713B3FD62BC67C927F8D0C4` です。build-resultのruntime未開始とqualified=falseは原本のまま保持しています。この初回build時点では新DLLの全体UI／E2E、Native AOT、他RIDは未実施でした。後続のsource284通常版の検証は上の履歴に分け、build-result原本のruntime未開始を変更しません。従来source282の全体成功やNative成功をsource284へ拡張せず、下記の旧検証履歴もその時点の記録として保持します。

## バイナリ置換（2026-10-11、Windows managed限定実測）

Coreのdecoded-byte置換と各側の実Avalonia置換ダイアログへ、選択置換・前方／後方全置換・次／前検索を接続した。固定Frhed commit `a0a2f13fe4c37a8f979407e997186680450ad7ca` の51原本を全bytes・実置換数・選択／caretで照合し、旧順序を保持する。全置換の原子的候補と一つのUndo、不正入力・共有予算の拒否、復号bytesによる同一判定は意図的な安全差異である。取消・readonly・未適用Hex・タブ移動後に戻るABAを含む古い完了で本文・履歴・保存点を保持する。

限定 `--binary-replace-only` はmanaged／Windows x64 Native AOTとも17成功／0失敗／0skip、内部GUIは各253成功／0失敗。独立readerは原本51件、GUI104状態、保存56件、通常／内包の複製・包装2群の全bytesとZIP CRCを照合した。managed全体E2Eは165,359成功／0失敗／環境別省略5件、8,257コマンド、実終了0・37分15秒・両stream回収完了。省略はUnixモード、実APFS別名、macOS FIFO/socket、実デスクトップを含む全入力互換性、現在のfilesystemで作れない大小文字衝突である。managed全体UIとWindows x64 Native全体UIは各14,405成功／0失敗で、各全体出力の置換原本・保存bytesも独立readerへ照合した。Nativeのローカル自己検証は制限1,800秒・実836.47秒であり、Windows CIの600秒制限内の成功を証明したものではない。Nativeの全体E2E、他RID／現在版のCI、通常デスクトップのpointer・ネイティブ保存ダイアログ・実OSクリップボードはこの実測に含めない。証拠と復元用ZIPは外部 `C:\Users\IMT\AppData\Local\Codex\TaskArtifacts\DiffBeacon\binary-replace-20261011-a2e71c4b` に保持し、過去の全体／4RID検証へ合算しない。[限定手順と独立reader](../tests/Fixtures/BinaryReplace/README.md)を参照する。


## バイナリ検索と画像初期設定（gzip実装前のWindows基準）

2026-10-09にsource278を検証しました。バイナリ検索の実ボタン・検索ダイアログ・方向・取消・古い完了・読取り専用と本文／履歴／保存点の保持、および画像の6項目の初期設定・新しい比較への継承・旧project／CLIの固定値・保存失敗後の再試行を確認しています。managed AppのSHA-256は `7DA43145C9F2DEA51F43D07BBF59AB0294B11ED1EE7200033F3834A9A2B9BE20`、E2Eは `16D7E16CD1C16043297C66BA85A657C7C446B8E082175705BF3C34DB473BB57D` です。

managed全体UIは13,647件成功・0失敗、全体E2Eは161,998件成功・0失敗・固定された環境別省略5件でした。実E2E PID30184・出生FILETIME134359751819178516・終了0、1,894.882秒で完走し、held終了と両streamの回収を確認しました。独立監査PID35372は終了0で、7,128コマンドのPID／出生・期待終了値、14,256出力のSHA／サイズ、1,413行のsource／設定／工具の前後一致を照合しました。Binary検索154ケースと画像初期設定1,600 checks／69観測／58PNGの独立readerも終了0です。集約監査は64.186秒で終了し、全体の制限時間は設定していません。60秒制限は個別fixture readerの契約です。補助36streamは14,256本とは別に保持しています。

Windows x64 Native AOTの発行と全体UI自己検証も13,647件成功・0失敗でした。実Native PID24920・出生FILETIME134359739984941230・終了0、595.428秒で600秒制限内に完了しています。outer／controller／driver／publisher／Nativeの5役はすべて実終了0で、発行物12ファイルとライセンス3件、5種類の独立照合を確認しました。Nativeの画像readerはGUIのみ1,597 checks／69観測／58PNGです。Native版の全体E2E、変更後の他3RID、通常デスクトップpointer／native dialog、Native独立console出力はこの記録の検証範囲に含みません。

親受入は外部TaskArtifactsの `full-e2e617-lifetime278-b9045542def14908812d17556f00840e/parent-managed-full-E2E278-acceptance.json`（SHA `4D54953C747A0FB5D7997DF08858ACB2B8E1210AAA832112E5F50243B0EE44BF`）と同じ場所の `parent-Native278-acceptance.json`（SHA `6681E137309DA8F8A5312035F6DBDEECAA86A705EC6A0D42D478D313C482CE6D`）です。以降のソース変更へ、この基準の成功を拡張しません。

## フォルダーツリー表示（Windows managed／Native AOT実測）

完了済み全体E2E605の通常6範囲は、1,213ファイル・37ディレクトリ・812,096,578 bytesを15,259,958 bytesのZIPへ保持しました。全entry SHA・サイズ・CRCと原本前後一致を親も独立照合し、ごみ箱への移動と今回の捕捉項目だけの完全消去、元パス／R実体／I管理情報18パスの不存在を確認しました。復元記録は外部TaskArtifactsの `e2e605-cleanup-prep616-d27f670d95df42839ecf1a84b768379a/backup-1d51ce669f1c42fbbeecfc7528db7eeb/six-scope.zip`。NTFS固有情報を持つfolder-copy原本・実リンク16件・残る874,653,795 bytesは保持し、旧E2Eのassertions.jsonも同ZIP内の原本SHAで参照します。全rootや共有キャッシュを清掃したとは扱いません。

2026-10-09に、フォルダー表示と初期展開の全体設定を新しい比較へ引き継ぐ経路を適用しました。既存タブの手動展開を維持し、設定保存失敗時は確定済み表示と設定を保持します。既存5＋新規2ソースの適用後、source271／App160のmanaged増分ビルドは警告0・エラー0でした。App SHA-256は `1C7A507C10D12552CA6B66B4A75FAF6B293E2AA87186701023C0B4DAA6985605`。その後、限定自己検証の接続漏れと検証用タブの初期条件を修正しました。実UI依存はAvalonia 12.1.3（11.3.2はBuildServicesのみ）で、GetTemplateDescendantsを使って所有ポップアップ内の選択項目への実フォーカスを照合しています。当該フォルダー設定工程のApp SHA-256は `9B20431F4D993628B101AC67A5D3D0B8369ADB9F08CE2ABC9491D0C8AADB04F8`。managed実App PID22724・出生FILETIME134359515386503246・終了0・出力回収完了、91.697秒で984件成功／0失敗でした。新設定220件・固定24表示・42PNG、通常／最小850×550／45タブの100 DIP一覧と操作到達、設定保存失敗・取消・古い完了・dirty履歴・旧project復元・全owner解放を独立readerで照合し、Tree／Folder／Optionsの実reader3件も終了0でした。親も通常／最小45タブのPNGとboundsを確認しました。親の受入記録は外部TaskArtifactsの `folder-options-ui617-v8-806f39e5081a4a3fb1a220569540dba3/parent-ui-v8b-acceptance.json` です。新source271のmanaged全体E2Eは下記のとおり完了しました。他RIDと、通常デスクトップのpointer／native dialogは未検証です。

2026-10-09のmanaged全体E2E `full-e2e617` はsource271／App `9B20431F…DB04F8`／E2E `C409B4CE…C1012`で161,993件成功・0件失敗・5件環境別省略となりました。実PID14756・出生FILETIME134359558481817101・終了0、1,875.257秒で完走し、held実終了と両stream回収を確認しました。独立監査PID22468も終了0で、7,125件のPID／出生時刻・期待終了値、14,250出力の全SHA／サイズ、407行のsource／設定／工具freezeを照合しました。親も全14,250出力と407行を再読し、同出生actorの不存在を確認しています。assertions SHA-256は `83932BFA827B1424432A2647D7737F4BB650D71F4E16B631B010056FB8745006`、親受入は外部TaskArtifactsの `full-e2e617-source271-39f54ee6ce784ecf87dd8957ccad2016/parent-full-e2e617-acceptance.json`（SHA `3AC0EE771C1E91A403781D3619D9DDDE90B0A0A02BE5D381E3AC4EA30C99650F`）です。5省略はUnix権限・Mac大小文字別名・Mac FIFO／socket・三者の実desktop／全入力互換・包装時の大小文字衝突であり、Windowsのこの実測を新Native全体E2E・全4RIDへ拡張しません。

2026-10-09のsource271はWindows x64 Native AOT本体の発行と全体自己検証を完了しました。実Native PID31236・出生FILETIME134359530704015296・終了0、549.645秒／600秒で13,645件成功・0件失敗です。EXE SHA-256は `49204326C5F5420AEF55C981DCF618AF38D1E5F777317B08D212F02CE0C574B2`。発行manifest12件のSHA・サイズとライセンス3件、Tree377 checks／64 views、FolderCopy44操作・NTFS7ケース／98close、Optionsの固定24表示・42PNGを独立reader3本の実終了0へ結合しました。親も通常／最小45タブのPNGとsource271・repo lock5・managed payload85／全202ファイル・通常bin/objの不変を確認しました。外側のpublisher／driver／controller／outerは終了1のままです。Core／Providersの外部作業lockにNative compiler 10.0.12とwin-x64グラフが追加され、検証wrapperの事後freezeが拒否したもので、追加を除いた既存依存グラフは原本と完全一致しました。原本の失敗とNative独立console未取得を保持し、成果物の成功を別範囲で受け入れています。外部TaskArtifactsの `native271-readers616-0dfb58641b8941c881598282bf7f8c41/reader3-final-summary.json` と `full-e2e617-source271-39f54ee6ce784ecf87dd8957ccad2016/Native271ParentAcceptance.json`（SHA `3EBBA0829A1C8BE1F3A03A71D3F3064CF056FAD71B17EEA1FA20E047BA328805`）が証拠です。

2026-10-08の `native606` はWindows x64 Native AOT発行と全体自己検証を完了しました。実Native PID21316・出生FILETIME134359422767640836・終了0、543.495秒／600秒で全13,425件成功・0件失敗、最後の入力選択／内包入力保存検証まで完了しています。EXE SHA-256は `DE158B8FE11A1547C417BF44C4200C69D9685BE150AAA01D0C0533996AFEEA75`。publisher／driver／controller／outerも実終了0、発行manifest12件とライセンス3件のSHA・サイズ、source269と314入力・managed payload85の不変を照合しました。Native独立console streamは取得していないため、consoleが空だったとは証明しません。

Native専用の独立照合でもTree377 checks／64 views／58 input facts／PNG3・3 layouts、43→45→43タブ、コピー44／review7／取消4／ADS11／共有予算4／NTFS7・98handle closeを確認しました。従来Tree readerの全体集計での同名assertion拒否は、全13,425行の型・成功を検査したままTree名だけの一意性を検査するV4へ修正しました（SHA `67BF3E7AA3D0196778A5D817B36864216305518304A6945D3F1C6656A9F4FBE1`）。固定期待・Treeの重複／欠落／未知の拒否を維持し、実reader PID8484・出生FILETIME134359438932997586・終了0・出力回収完了です。NativeのFolderCopyは86.926秒、ThreeWayは26.018秒。全体managed E2E `full-e2e605` は変更前のsource269／App411B905C／E2EC409で完了しました。実PID32960・出生FILETIME134359445542694989・終了0・出力回収完了、1,816.902秒で161,993件成功・0件失敗・5件環境別省略です。7,125子命令のPID／出生時刻と14,250出力ファイルのSHA・サイズを親が独立照合しました。省略はUnix権限、macOSの大小文字別名、FIFO／socket、通常デスクトップ等の三者比較範囲、包装時の大小文字衝突です。新しいフォルダー設定source271の検証資格には含めません。他RID・通常デスクトップpointer・native dialogの資格は追加していません。外部TaskArtifactsの `native606-76228c1e27db4fe0aceee372a1b88f00/parent-native606-acceptance611.json` に親の受入れを記録しています。

2026-10-08の検証用タブ寿命変更では、各caseの判定・原本採取・task完了後に所有タブだけを閉じ、Treeの43→45→43と最終45タブを維持しました。App増分ビルドは警告0／エラー0、DLL SHA-256 `411B905C3EAA58F5EC0A04B5F2B2AFB777BD4C3EF34CB90489F6D4523686A2AB`。`tree-ui605`の実App PID23380・出生FILETIME134359410155566959は77.222秒で終了0、764成功／0失敗、timeoutなし・出力回収完了です。前回147.848秒との差は70.626秒ですが環境差を含み、Nativeの600秒完走を保証しません。

固定Tree reader（実PID27512）とFolder reader（実PID2932）はともに終了0・出力回収完了。Tree377 checks／64 views／3 layouts、コピー44・review7・取消4、ADS11・共有予算4、NTFS7／98handle closeを独立照合し、通常／最小45タブPNGを親も確認しました。早期閉鎖91件のstart／completeが対応し、入力成果物とsource269／lock5／保護21／policy7／通常runtime4／managed payload85は不変です。初回outerはAdmin=falseの起動前ゲートで停止しApp未起動、失敗記録を別に保持して昇格経路で一回再実行しました。このmanaged限定検証時点で、全体E2E・全体Native・4RID CIは未検証でした。以下はタブ寿命変更前の実測を含む履歴です。

2026-10-08に、平坦な一覧へツリー切替・ノードの展開／折り畳み・全件／相違／同一フォルダーの展開・左右キー操作を接続しました。元の全entryとコピー計画を維持し、表示から隠れた選択はコピー対象にしません。同じ入力・設定・filter stampで再比較したときは展開を継承し、入力文脈が変わった場合はresetします。候補の投影を表示破棄前に準備し、取消・世代・旧model・設定・filterを再確認してから採用します。

初回の限定UI `tree-ui588` は712件中708成功／4失敗でした。ListBox標準処理より前にツリーの無修飾左右キーを処理するよう修正し、検証も実ウィンドウへのKeyPress／KeyReleaseへ変更しました。修正版 `tree-ui592` は722件成功／0失敗、実dotnet PID15176・出生FILETIME134359331036749043、147.848秒で終了0、timeoutなし・出力回収完了です。左右操作、親子への選択移動、修飾キー、通常／最小850×550／45タブ、コピー・確認、実比較ボタンからの取消・入力往復・filter内容差替え、成功再比較後の潜在展開と選択継承を実測しました。ツリー検証自体は14.387秒、377 checks・64 views・3 layoutsです。

独立readerは実PID9832・出生FILETIME134359333333033102・終了0で、固定literalの表示順・展開・選択、58入力factsとコピー20／30・他入力保持、3 layoutの100 DIPと操作到達、3 PNGのCRC／全画素復号、43→45→43タブ復元を照合しました。親も実App終端とreader receiptを別々に確認し、通常／最小45タブPNGを目視しました。error／uncompared子の実注入、prepareAdoption自体の例外、各操作前のPNG、過去メタデータの全保持はこの検証で確認していません。

通常Release全体ビルドと修正後のApp増分ビルドは警告0／エラー0。現行App DLL SHA-256は `95903FF94F56C234A5B2F252CB5C00723F52308B23692ADB01A70A4EEDDA0072`。source268・lock5・保護21・ポリシー7・旧DLL4の不変を照合しました。外部の `Codex/TaskArtifacts/DiffBeacon/tree-build583-3d09620b4b014f668006b8378b6cad47/parent-keys-build-acceptance592.json`、`tree-ui592-f01ce776b0c54b7796a2c664ed17c627/parent-actual-ui-acceptance592.json`、`tree-reader592-3b931da39ba04e87a4b7488a022db135/parent-reader-acceptance593.json` に証拠を保持しています。

全体E2E `full-e2e593` の再実行は80,335成功／1失敗／4skip・5,498命令で実終了1でした。子timeoutは0、出力回収とsource／payloadの不変を確認しました。失敗は外部配置したE2E DLLの祖先だけを探索するfixture locatorで、ImageLines読込み前に停止したものです。原本のgzip・展開JSON・headerの固定SHAとGit blobは正常で、後続は未実行です。初回driverのTEMP／出力先不一致も別の失敗記録として保持しています。再実行内のfolder-copy-guiは151.954秒・722成功／0失敗で180秒制限内に終了しました。6 locatorをCWD祖先→DLL祖先の共通探索へ修正し、E2E増分ビルドは警告0／エラー0。source269、外部E2E DLL SHA `C409B4CE289D53E0736F6DAF6BDD8E7FB02CBEB193EEC230C73FE3D5A12C1012`、App `95903…0072` は不変です。修正後の全体E2Eと4RIDは未検証です。Windows x64の新Native発行 `native594` はEXE生成と全体自己検証を実行しましたが、600秒で打切り、実PID23188・出生FILETIME134359382526016912・終了-1でした。publisher15236／driver32224は終了1、最終UIレポートと発行manifestはありません。最終進捗は入力選択検証start・539.054秒・12,019成功／0失敗の部分結果です。Tree377 checks／64 views／PNG3枚は完了しましたが、全体成功として扱いません。フォルダー検証で蓄積した50タブの閉鎖に44.242秒を使うことを実測し、検証ケースのタブ寿命を見直す候補を調査しています。前回出力はリンクを含むため圧縮を停止して保持しており、原本の復元情報と現在状態を検証してから整理します。

旧WinMergeは表示modeと初期展開方針をアプリ設定へ保存し、個別ノード展開は画面内memoryだけで再比較へ継承します（`Src/DirView.cpp:117`、`:2746`、`:2949`、`:4158`、`Src/OptionsInit.cpp:109`、`:147`）。旧project XMLにツリー字段はありません。この時点ではアプリ設定永続化・初期展開方針が未対応でしたが、上記source271で追加しました。旧レジストリ設定の取込みは未対応です。workspace単位の個別展開保存は旧互換の必要条件とは扱いません。

## 4ペイン結果のGUI接続（Windows managed全体・win-x64 Native UI実測）

2026-10-08の作業ツリーで、固定GNU行生成・元diff対応・EOF／ghost配置・結果編集を通常GUIへ接続しました。二者・独立三者・祖先三者を分け、結果タブと第四ペインの編集・採用・Undo/Redo・保存点を共有します。未解決の短縮表示と本文付き保存を分け、独立中央の採用、祖先自動採用の一括履歴、snapshot保存と公開直前の入力保護を実装しています。[設計と互換境界](MERGE-SESSION.md)を参照してください。

2026-10-08のWindows managed基準run `normal503`（後続診断変更前）は全体UI12,940成功／0失敗、全体E2E161,993成功／0失敗／5skip・7,125命令で実終了0・出力回収完了を確認しました。後続変更後の `normal509` は全体UI12,940成功／0失敗・実終了0と独立readerの受入を確認し、全体E2Eも161,993成功／0失敗／5skip・7,125命令で終了0を確認しました。`normal503` の成功を後続変更後の全体E2Eへ広げません。受入根拠は利用者LocalAppData配下の `Codex/TaskArtifacts/DiffBeacon/qualification503-20261008` に保持します。

同OSの `native506` はwin-x64 Native AOT発行でPublisher終了0・manifest自己検証成功を確認しました。全体UIの集計は12,940成功／0失敗で、580.0682995秒と600秒枠内でした。Native自己検証processの個別terminal rawは未取得です。exe SHA-256は `BF1D482886480D7F77829F214C9A5B6FC3DFCF3C68203116B85DA85266B641E4`。保持ZIPの全4,153 entryのSHA／サイズ／CRCと独立GUI reader `native506-reader516` の対象subsetを照合済みです。後続のNative全体E2E `native-e2e521` は2,055成功／4失敗／2skip・992命令で実終了1となり、後続項目は未実行です。フォルダーコピーGUIの失敗を期待終了やskipにしません。当作業ツリーの4RID CI、通常デスクトップ・ネイティブ保存／入力選択dialog・実OS IMEは未検証です。Nativeの記録は同TaskArtifacts配下の `native506-20261008` と `native506-reader516` に保持します。

先行 `488`／`500` の断続的なフォルダーコピー拒否は `native506` では再現しませんでしたが、`native-e2e521` の診断で比較root外のTemp directoryのWriteだけが変化した拒否を2件確認しました。Creation／Size／全属性／Volume／Indexは一致し、既存のroot外祖先mtime非追跡方針が観測内／path再openの厳密照合まで伝わっていませんでした。初回Capture・基本snapshot・再確認・親更新に非追跡方針を伝える4ファイル修正を適用し、root内・file・stagingの厳密照合、SD／EA・予算・取消・cleanupを維持しています。修正後のRelease buildは警告0／エラー0、通常App `metadata-phase552` の限定フォルダーGUIは344成功／0失敗・実process終了0、180秒枠内で完了しました。既存コピー／全ADSの独立readerと、新8ケースの固定DATA・前後snapshot・実creation／mtime／属性を照合済みです。8ケースは同一process・thread内の注入で、root外のWriteだけの変更は3観測位置で許可し、root内／fileのWrite・root外のCreation／属性の変更と取消は拒否・保持しました。成功probeには変更前identity／SD／EAの独立採取がなく、それら全字段の不変資格は広げません。修正後のwin-x64 Native AOT実行ファイルは生成しましたが、発行を包む検証スクリプトのprocess捕捉が失敗し、Publisherの統合検証は未合格です。同じexe（SHA-256 `1E725FD5B00F941BCDB2CA09225B7F2D96F014A93A58BEAD83298BF98DB003E3`）を別processで実行した `native568` は、11,640成功／0失敗の後、入力選択検証の途中で600.1346105秒の制限終了となりました。実終了−1・出力回収完了と最終ui-report不在を確認しており、全体UIの成功とは扱いません。完了したFolderCopyは10939→11283成功／0失敗で、コピー44件・review7件・取消4件・ADS11件と予算4件・metadata7件／98handle close、新8ケースを独立照合しました。最小850×550・45タブのPNGも確認しました。新しい局所reader自身のOS PID／birth／生streamは未採取で、実行ツールの終了0と、Nativeの実process／固定SHA／42入力成果物の照合に限定します。SD／EA転写の全16組合せ、修正後の全体UI／全体E2Eと4RID CIは未完了です。最小証拠は同TaskArtifacts配下の `native567-8b8f82ed0dfb431e8ae4ad3d0d153a4f/direct-native568`、`native-timing-reader570-5fe587fb3b1a4966822d890afbff6735/parent-acceptance572.json` と `native-folder-reader573-8d3787e8a776409c8d531513b1241e52/parent-local-acceptance574.json` に保持しています。根拠は同TaskArtifacts配下 `metadata-phase552-20261008/parent-ui-acceptance554.json` と `parent-reader-acceptance558.json` です。失敗前診断・確認エラー本文採取の判定と20秒待機も維持しました。独立Text readerの全体E2E globで44件と期待38件が衝突する問題と、全体にscope外の既知skipがあると拒否する外側集計条件を修正しました。固定38命令＋legacy2のraw／実終了を厳密に結び、未知・重複・欠落・対象skipは拒否し、scope外は原典の固定Name／理由とsuite実体・命令へ結合します。適用済み標準readerは `normal509` の完成成果物で1,240条件を照合して終了0となりました。Mac側の固定skipは静的契約であり実測資格は後続CIで確認します。HTML／包装は入力比較経路を保持しています。

先行コミット `f7891ec74accd64a4931645e3692f5cbb7fde40d` の[CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37713883772)はWindows両構成とMac ARMが成功、Mac Intelは入力選択E2Eの120秒制限超過で失敗しました。正常native実測163.030秒に基づき対象selectorだけ240秒へ修正しました。Macの修正後再検証は未完了で、失敗を期待終了やskipとして扱っていません。

## 中央も編集する独立三者テキスト

三側のPhysical／Untitled／実在Archive葉と選択pairをv6 projectに保持し、全6方向の差分コピー、全3側の編集・保存、三側原文HTMLとパッチなしの包装を実装しています。内包の通常保存は原本を保持した作業版へ保存し、外部保存した側だけPhysicalへ切り替えます。継承readonlyはfalseの明示だけ編集を許可し、未保存の内包本文／dirty Untitledはworkspace保存と包装を拒否します。操作は[README](../README.md)、保存・採用の境界は[設計のデータフロー](../DESIGN.md#データフロー)、再現入力と別processの照合は[独立Text](../tests/Fixtures/IndependentText/README.md)と[実在Archive Text](../tests/Fixtures/IndependentArchiveText/README.md)を参照してください。独立不在入力・provider変換・patch包装、旧エディター全操作の移植は残っています。マージ結果のGUI接続と検証状況は上記の節を参照してください。

Physical／Untitledまでの基準版SHA `cb666d5b2c59af1b854cc5caef2b6a26fc1090b0` は [4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37625308016) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37625307942) が成功し、4RIDのNative AOT・全体E2E・headless UIを独立に照合済みです。Windows各構成はE2E161,856成功／0失敗／5skip・7,070命令・UI11,369成功、macOS各構成はE2E161,927成功／0失敗／11skip・7,089命令・UI11,275成功でした。公式ZIP・固定source・実行receiptと全822 root PNGの照合根拠は `artifacts/local/folder-threeway-next/text-next73/ci135/accept147/run167/receipt.json` と同directoryの `runtime-summary167.json` です。基準版の通常全体E2E成功は `whole131` にも保持しています。先行全体実行の未知version fixture不一致はv7での拒否検証へ更新後に解消しており、失敗原本は保持しています。

実在Archive葉を含む独立三者TextのSHA `8fffcad8499c76ccfdbd64c8cd6f31b1653339e6` は、[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37667096037) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37667096063) が成功し、同じコミットの公式全8ZIP・固定source・原本・実行記録を独立に照合済みです。Windows各構成はE2E161,942成功／0失敗／5skip・7,112命令・UI11,532成功、macOS各構成はE2E162,013成功／0失敗／11skip・7,131命令・UI11,438成功でした。全4構成で49進捗記録、既存27検証phase、発行物の機械形式・ライセンスと全834 root PNGを確認しています。旧固定readerに加え、追加12 readerの実終了0・出力回収・sourceとZIPの前後不変を確認しました。証拠は `artifacts/local/folder-threeway-next/text-next73/ci251-parent-accept328.json`、`ci251/runtime/actual-whole-accept251-attempt2.json`（SHA `43AD2393C122048A3A9EDAB928BF8EA12A24ED9E7472D6C16E3A15BEE19D94F9`）と `ci251/runtime/HANDOFF.md` です。whole照合用プロセスの精密birth・分離raw streamは未採取で、追加12 readerの精密birth・分離rawとは区別します。

先行Mac検証では、同size／mtimeの原本差替えfixtureを初回読込み前に固定時刻へ準備していなかったため、実体のnsと.NET時刻の精度差で独立検証が失敗しました。上記SHAでは生成runの原本を初回読込み前に固定UTCへ準備し、同じSHA・サイズと実体のmtime nsを照合しています。macOS両構成の差替え6件ずつは実体の `1704067200123456700` nsと一致し、差替え前後のサイズ一致・SHA差を確認しています。失敗原本とログは保持し、readerの期待条件を緩めていません。

後続の通常GUIでは「三側の入力を選択」からPhysical／Untitled／実在Archive葉を組み合わせ、検証後に新しいタブへ開く入口を追加しています。基本32件、保存版・読取り専用・古い完了16件、アーカイブ15件、実暗号化親と異なるパスワードの兄弟1件、閉鎖寿命9件、コピー・保存・包装8件の計81ケースをWindows managedで独立受理しました。閉鎖時に一覧読込み／pickerの終了より先に完了が返る問題は修正前に再現し、修正後の元Task完了・配列消去を独立照合しました。同段階の全体E2Eは161,958成功／0失敗／5skip・7,117命令、全体UIは12,744成功／0失敗・実終了0です。根拠は `artifacts/local/folder-threeway-next/text-next73/fulle2e344-parent-accept369.json`、`fullui344-parent-accept357.json`、`accept361/HANDOFF.txt` です。これらの未コミットGUI追加は上記8fffの4RID資格に含めません。

さらに保存版のflat／二重内包ZIPの2ケースでは、実ボタンによる三側作業保存・project保存／再読込み・パッチなし包装・通常アーカイブの全展開・展開project再読込みを確認しました。実アプリPID16940の終了0・GUI58検査、別readerの3,552検査と4PNGの独立復号が成功し、BOM／混在改行・全asset・全ZIP entry／CRC／SHAを照合しました。根拠は同証拠rootの `savedarchive366-receipt.json` と `savedarchive371-reader/receipt.json` です。この限定実証は後述の全体検証と区別します。限定手順と独立照合の条件は [E2E手順](../tests/DiffBeacon.E2E/README.md) と [Saved Archives fixture](../tests/Fixtures/IndependentTextInputSavedArchives/README.md) を参照してください。

その後、内包Archiveコンテナーを表示中の親タブから入力選択を開く経路を修正しました。修正前のcontainer／leaf検証の例外（実終了2）と、初回Load前に初期chainが消える実process PID17240の観測を `artifacts/local/folder-threeway-next/text-next73/container381-parent-baseline.json` に保持しています。初期ModeがArchiveならcontainerとして検証し、RootPathの遅延TextChangedを同期PropertyChanged／TextPropertyへ替えて初期chainを保持します。1段／2段の非numeric・legacy7コンテナーについて、公開入口から初期未選択の比較拒否、同chainの一覧Load、同root pickerの保持、別root選択時のchain解除、明示leaf選択後の比較とreadonly／本文／原本保持を確認しました。旧flat／二重内包保存2件にこの2件を加え、実App PID1020終了0・GUI153成功／0失敗、別reader PID6964終了0・4ケース／5,318検査／6PNG・source不変を確認しました。実証は [実アプリreceipt](../artifacts/container380/root-fix/receipt.json)・[生stdout](../artifacts/container380/root-fix/stdout.txt)・[reader process](../artifacts/container380/reader-process/receipt.json)・[reader結果](../artifacts/container380/reader-process/result.json) です。旧58GUI／3,552readerの2件は歴史証拠として保持し、現在4件の実証とは区別します。この限定実証は後述の全体検証と区別します。

後続の全体検証 `whole377` では、全体UIの実App PID10464は終了0・12,896成功／0失敗・552,276 ms・63進捗記録でしたが、Archives／Cipherの独立readerが既存生成scriptのsource pin不一致で失敗し、9 reader中7成功として受理を拒否しました。全体E2Eの実driver PID18464も終了1・161,959成功／2失敗／5skip・7,118命令で、同じsource pinの失敗です。GUIの製品検査成功だけで全体成功とは扱いません。失敗原本と閉包は [UI受理拒否](../artifacts/local/folder-threeway-next/text-next73/accept389/rejected-acceptance.json) と [E2E受理拒否](../artifacts/local/folder-threeway-next/text-next73/accept387/final-acceptance.json) に保持しています。先行の受理済み通常版と上記8fffの4RID成功は、その当時のsourceに対する歴史証拠です。

source pinの修正では、CC0の `Archives/Sources/regenerate.py` をGit HEADの原本LF・14,253 bytesへ揃え、近い `Sources/.gitattributes` を76 bytesの `-text !eol` としました。以前のローカルCRLF・14,543 bytesとの違いをraw bytes・HEAD blob・Git属性で実測し、内側manifestのsource pin 3フィールドと、それを包含する2つのown-pinsのmanifest識別を合わせて更新しています。生成原本ZIP、期待本文のbytes／CRC／BOM／改行、readerの判定と拒否条件は変更していません。原本・復元・照合の記録は [source pin修正台帳](../artifacts/local/folder-threeway-next/text-next73/provenance392-applied/restore-ledger.json) と [包含pin修正台帳](../artifacts/local/folder-threeway-next/text-next73/provenance399-coupled-pins/restore-ledger.json) です。外部source pinを更新するときは包含pinも更新し、raw bytes・HEAD・Git属性を照合します。生成scriptの文字コード／改行の原本pinと、生成する本文のBOM／混在改行の期待値を別々に保持し、通常検証では再生成しません。再生成は既存fixtureを上書きせず新しい空directoryで行います。

修正後の `whole400` はArchives／Cipherの限定E2Eを別driverで実行し、それぞれ実終了0・4成功／0失敗／0skipとsource／runtime不変を確認しました。出力内のGUI 15＋1ケースも、別processの独立readerで確認しています。根拠は [Archives実driver receipt](../artifacts/local/folder-threeway-next/text-next73/whole400/archivese2e-20261007T2313191992999/receipt.json) と [Cipher実driver receipt](../artifacts/local/folder-threeway-next/text-next73/whole400/ciphere2e-20261007T2313191506651/receipt.json) です。続くWindows managed全体UIの実App PID35648は終了0・12,896成功／0失敗・63進捗記録で、追加85ケースと369 PNGを独立照合し、[UI受入](../artifacts/local/folder-threeway-next/text-next73/accept404/final-acceptance.json) と [閉包証明](../artifacts/local/folder-threeway-next/text-next73/accept404/closure-certificate.json) はacceptedです。全体E2Eの実driver PID11224も終了0・161,961成功／0失敗／5skip・7,118命令で、[E2E受入](../artifacts/local/folder-threeway-next/text-next73/accept403/final-acceptance.json) はaccepted（SHA `EA2FAC76E798C1094AA7FB4DD3B2C41FBAECB79CBDA5AE5CDA499B9788D65AE8`）です。[UI実App receipt](../artifacts/local/folder-threeway-next/text-next73/whole400/fullui-20261007T2317394163612/receipt.json) と [E2E実driver receipt](../artifacts/local/folder-threeway-next/text-next73/whole400/fulle2e-20261007T2317394896578/receipt.json) で実終了・出力回収完了とsource／runtime不変を確認し、受入後の再確認も完了しました。`whole377` の失敗原本は保持しています。この結果は当時のWindows managed検証です。最新GUIのGitHub結果とその修正検証は次段のとおりです。

コミット `182672fdeaab1388d3d5c791dd559a29d815e6e4` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37705641060)は失敗し、[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37705641068)は成功しました。win-arm64はNative自己検証と全体E2Eが成功しました。win-x64はAOT発行後の自己検証が600秒で終了し、最終集計はありません。macOS両構成はNative自己検証12,802成功／0失敗まで通過しましたが、新しい入力選択の限定E2Eに30秒が適用され、各4件の失敗と後続GUI成果物不足が発生しました。

限定自己検証の制限時間を共通の `CommandTimeoutPolicy` に集約し、通常起動とmacOS起動の両方で同じ制限を適用します。入力選択Basic／Criticalの `--self-test-independent-text-inputs` だけ240秒、その他の登録済み自己検証は120秒です。通常CLIの30秒、`folder-copy-large-stream`／`folder-copy-gui` の180秒、CI全体のWindows600秒／macOS1,200秒は維持しています。固定起動gateのSHAと無変換Git属性も不変です。入力選択の描画検証では、`CaptureRenderedFrame` が描画前後に既に実施する待機と重複した5か所の呼出しを除去し、425件の判定と32ケース／84レイアウトを保持しました。修正後の4RID Native AOTとmacOS実行は、次のCIで別途確認します。

修正後のWindows managed全体UIは12,896成功／0失敗、全体E2Eは161,961成功／0失敗／5skip・7,118命令でした。実プロセスの終了0、出力回収完了、入力・ソース・実行ファイルの前後一致を確認しました。UIの独立readerは85ケース・369 PNG、E2Eの独立readerは旧命令と判定の保持、新規6命令／19判定と保存済みArchiveの差分を照合しています。これらのmanaged結果をNative AOT・macOS・通常デスクトップの検証済みとして扱いません。

固定fixtureのcheckout保護として、root `.gitattributes` に `IndependentTextInputSelectionArchives/**` と `IndependentTextInputSelectionCipher/**` の `-text !eol` を追加済みです。[適用receipt](../artifacts/local/folder-threeway-next/text-next73/attributes409-applied/receipt.json) はinput ZIP・manifest・reader・固定source bytesの無変更を記録し、実効属性のtext unset／eol unspecifiedも確認しています。ステージ済みの固定資料110ファイルはGit blobとfilter適用後のcheckout bytesがrawと一致しました（[ステージ照合](../artifacts/local/folder-threeway-next/text-next73/commit413/staged-verification.json)）。新規Windows／macOS checkoutでの原本照合は、最新GUIのGitHub 4RID検証で確認します。

通常デスクトップ、ネイティブ保存／ファイル選択ダイアログ、実OS pointerはheadless検証の資格に含めません。独立不在入力・provider変換・patch包装と、旧エディター全操作は未完了です。新しいマージ結果のGUI接続は上記の全体検証へ進めています。CLIや親一覧の検証範囲は [実在Archive Textの記録](../tests/Fixtures/IndependentArchiveText/coverage.md) に保持しています。

## macOS E2E の起動記録

2026-10-08のコミット `f7891ec74accd64a4931645e3692f5cbb7fde40d` の[CI run 37713883772](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37713883772)はWindows両構成・Mac ARM64が成功し、Mac Intelの全体E2Eだけ失敗しました。Mac IntelのNative全体UIは12,802件成功・0件失敗、その後のE2Eは2,364成功・4失敗・8skipでした。原本assertionsの4失敗は入力選択GUIの120秒timeoutと、未生成ui-reportを後段が読もうとした連鎖です。実PID80531・開始2026-10-08T02:28:28.324647ZのLaunchEvidenceはstartup gate解放・実終了・両stream回収を確認し、kill終了137を成功扱いしていません。現行 `CommandTimeoutPolicy` は同selectorを240秒へ設定済みですが、その変更を含む新Mac実行は未検証です。公式55,170,591 bytes ZIPのdigestをメモリ上で照合し、4,890,552 bytesのassertions原本だけを199,763 bytesのZIPへ圧縮保持しました。外部TaskArtifactsの `ci-osx-x64-failure-826302c7f5a4422f913abf5f8b51622c/parent-ci-failure-readback.json` が原本と現行policy SHAを結びます。抽出した今回のTempだけをごみ箱経由で清掃し、元パスと今回のR／I項目の不存在・既存ごみ箱25項目の不変も確認しました。

以前のSHA `32b8cce957bf12a21fb8aec7706d1590aeb43550` のMac Intel E2Eは `Process.StartTime` 取得例外で実終了を回収できず、採用しませんでした。圧縮原本と失敗assertionは `artifacts/local/folder-sync-next/github-current-head-inspection-01` に保持しています。

検証用launcherは固定startup gateのnonce／実PIDを照合して実OSの開始時刻を取得し、同PIDのexecでアプリを起動します。部分出力と完了フラグを同一の確定snapshotへ揃え、回収失敗をnativeの成功へ置き換えません。上記基準版の4RID資格では両Macの実LaunchEvidence・実終了・全stream回収を独立照合済みです。任意の取消／pipe解放の実時間まで検証したことは意味しません。操作・記録の契約は [E2Eの手順](../tests/DiffBeacon.E2E/README.md) を参照してください。

## バイナリの任意byte選択・clipboardと直接入力（managed／全4RID Native AOT実測）

絶対byte位置のinclusive選択を4096ページ跨ぎで保持し、実Copy／Cut／Paste／FastPaste／選択dialog、直接Hexニブル・ASCII入力、Delete／Backspaceへ接続した。repeat間skipは元suffixを跨ぎ、選択Pasteは常に置換Insert、Overwriteの最後のskipも容量検査する。共有64MiB／256操作と既存Redoを含めた候補を事前準備し、CutはOS公開／flush成功後と、readonly・全入力・project・provider・mode・owner・operation・取消・revision・選択世代の再照合後だけ確定する。未適用Hexを自動適用しない。二者rightのProject側2／Core側1の対応と、通常／内包全側の保存点・workspace／包装を維持する。

固定Frhed原codec634件（encoder263／decoder371）と独立IEEE4件を実アプリ別processで全byte照合した。原版の浮動値return未定義動作・scanf失敗・overflow・型不一致は実行しない。製品は有限IEEE値を安全に生成し、50文字以上の数値prefixを閉鎖の有無によらず拒否する。tokenは最大54byte先読みと2passに限定し、1MiB未閉鎖反復のGUI／CLI結果を独立readerへ照合した。Windows OEMは有界1byte CharToOemBuffAをliteralにだけ適用し、token生成bytesとescapeを保持する。実ACP／OEMCPは932／932、全256API返値は成功でbyte保持だった。macOSは明示1252／437の互換方針であり、Windows OS API実測と区別する。

最終build14は警告0／エラー0、限定E2E7は1,308成功／0失敗／0skip・651命令、clipboard GUIは151成功／0失敗／PNG5枚、既存range GUIは110成功／0失敗。全通常／内包保存bytes、原本ZIP全entry／CRC、相対asset、workspace／包装・展開再読込みをPython標準readerで独立照合した。850×550・45tab以上のHex先頭／ASCII本文と中央SaveAsを別scroll位置で実PNG・座標へ照合した。新ASCII欄で旧Range検証の最下端scrollがHex先頭38pxを隠す失敗を検出し、最低100pxと中央全欄可視を維持してHex先頭へ検証座標を是正した。同じbuild14の実管理者tokenによる全体UI03は10,919成功／0失敗・root描画PNG183枚／生成入力を含むrecursive PNG1,502枚となり、実process終端0・creation／PID・前後runtime SHA一致を確認した。ClipboardとRangeの全保存byte・原本ZIP／CRC・workspace／包装を独立readerで再照合した。成果物は `artifacts/local/binary-clipboard-selection/handoff.json`。

同じ最終ソースのmanagedとWindows x64 Native AOTは、全体E2Eが各161,447成功／0失敗／3skip・6,843命令、全体UIが各10,919成功／0失敗・描画PNG552枚となった。各13,686のindex付きstdout／stderrとNative発行物13fileのSHA／sizeを照合し、実管理者token・process終端0・実行前後runtime一致を確認した。採取した1,933 source／fixtureのうちNative restoreでbyteが変わった2 lockはHEADの依存graphと一致し、他1,931fileはbyte保持。固定codec／OEM／Clipboard／Range readerで全byte・原本ZIP／CRC・workspace／包装展開再読込みを再照合した。親がNativeの850×550多数tabのHex先頭／ASCII／SaveAsとRangeの5 PNGを目視し、同名managed PNGとのSHA一致も確認した。証拠は `artifacts/local/binary-clipboard-parent/full-managed01-independent.json`、`native01-independent.json`、`clipboard-readers-managed01-independent.json`、`range-readers-managed01-independent.json`、`binary-readers-native01-independent.json`、`native-managed01-parent-visual-receipt.json`。Windowsの3skipはUnix mode、Mac実APFS、大文字小文字衝突の環境条件であり、失敗と区別する。

所有rawは識別headerと宣言長でHGLOBAL余白を除き、未知外部rawは実formatの全bytesを明示選択する。最大raw16MiB＋header／text112MiB（Unicode224MiB）のplatform transferを両列挙順で実読込みし、Windows合成OEM有無・終端・allocator余白を共通有界予算464MiB＋332byteへサイズ注入した。CF_LOCALEは付随metadataの余白を予約しただけで実format注入していない。最大実HGLOBAL／GlobalSize・合成format列挙は未実測。利用者OSclipboard／AppDataは変更しない。clipboard sourceSha256はfixture内容のSHAであり製品source SHAではない。旧検索・挿入位置の再整列・旧Hex全操作の同等性・Binary HTML／patchは残工程で、全WinMerge移植の完了とは扱わない。

固定SHA `e03aca4249763d7a5821ce35c514486da04538d7` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37222885296)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37222885318)は成功した。4構成合計E2E645,808成功／0失敗／10skip、27,380命令・54,760のindex付きstdout／stderr、UI43,676成功／0失敗・PNG2,208枚を照合した。公式8ZIPのAPI digest・全entry SHA／CRC／size、発行物・3ライセンス、元codec／OEM・Clipboard／Range／CopyAll／三者Binary／TARの固定readerを確認した。cleanrunnerの別writer／readerでは画像に加え、全256値＋NUL／ASCII／FFの259byte rawとtext・formatを実OS clipboardへ照合した。HGLOBAL header／GlobalSize paddingの観測は含まない。Mac両構成で各14の通常／最小・多数tabのPNGを親が目視した。ZIP内の絶対／相対assetパスを混同した検証adapterの失敗ログを保持し、元readerを変更せず修正後に各RIDの9検証を再照合した。証拠は `artifacts/local/binary-clipboard-parent/ci/all-four-receipt.json` と `parent-final-revalidated.json`。全体は展開せず730,055,821byteの公式ZIPで保持する。通常desktop・実OS pointer・ネイティブ保存dialogと後続の未コミットFolder変更は、このCIの実測に含まない。

## バイナリ全体コピー（managed／Windows x64 Native AOT全体実測）

二者両方向と三者全6方向の「全体コピー」を追加した。旧Frhed `copy_all_from` に合わせ、先頭からsource全byteを上書きし、destination長はmax(source,destination)、短いsourceでは末尾保持、空／同一bytesでは履歴を増やさない。未適用Hexは有効／不正とも明示適用前に拒否する。実確認dialogのキャンセル／続行と待機中の破棄・再比較・owner交代・編集世代・readonly変更を検査し、古い確認で変更しない。

Releaseビルドは警告0／エラー0、限定E2Eは12成功／0失敗／0skip、限定headless GUIは66成功／0失敗、同じApp SHAの全体UIは10,658成功／0失敗。全8方向の保存byteとUndo／Redo・保存点、16MiB入力／共有64MiB／256操作の拒否、内包Binary作業保存・workspace／包装のassetと展開再読込みを実アプリで確認した。固定期待byte・原本ZIP entry／CRC・全assetはPython標準readerで独立照合した。成果物は `artifacts/local/binary-copy-all/handoff.json` と限定runのPNG／JSON／stdout／stderr。ネイティブ保存dialog、実OS pointer、Native AOT／全4RIDはこの限定実測に含まない（後続の全体実測は次段落）。通常保存経路と内包原本への非書戻しの境界は維持する。

後続の最終版でmanagedとWindows x64 Native AOTの全体E2Eは各160,129成功／0失敗／3skip、全体UIは各10,658成功／0失敗。各6,187 commandの12,374 stdout／stderr、544 PNG、Native発行物13fileを独立照合した。261 sourceのうち2 lockはNative restore後にHEAD依存graphへ一致し、残り259fileのbyte保持を確認した。証拠は `artifacts/local/binary-copy-all-parent/full-managed01-independent.json` と `native01-independent.json`。実際の最小850×550・45タブで中央SaveAsと3Hexへの到達を確認した。全体コピーの固定SHA `413a67e789d88d3fa149a71af5134d780479e18e` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37205395791)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37205395786)は成功。4構成合計E2E640,534成功／0失敗／10skip、24,754コマンド・49,508stream、UI42,632成功／0失敗、PNG2,176枚を独立照合した。公式8ZIPのdigest・全entry SHA／CRC／size、固定Binary／CopyAll／TAR reader、発行物、別writer／readerのOS画像clipboard、Macの選択PNGを確認した。証拠は `artifacts/local/binary-copy-all-parent/ci/all-four-receipt.json`。このCIは後続の範囲編集を含まず、通常デスクトップ・ネイティブ保存dialogは未検証。

## 中央も編集・保存する三者バイナリ（ローカル／全4RID検証済）

三者編集・候補採用保護のcommit `a87f4e8ae3e914f78ef1ef56f9e1be6947cc39e0` は [GitHub run 37201634956](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37201634956) の全4RIDとCodeQLで成功した。Windows各160,118成功／0失敗／3skip、Mac各160,127成功／0失敗／2skip、UI各10,592成功／0失敗。公式API digestと8 ZIP全entryを照合し、固定GUI reader・発行物・別writer／readerのOSクリップボード記録・runner採取inventoryを確認した。Mac両構成の通常／最小850×550 PNGを目視し、前回の重なりは再現しなかった。証拠は `artifacts/local/binary-threeway-parent/ci/all-four-receipt.json`。このCIは後続の全体コピー変更を含まない。headless描画と通常デスクトップ・ネイティブ保存dialogは区別する。

通常ファイルと内包Sourceの左・中央・右を独立編集し、全6方向の差分範囲／全体コピー、共有Undo／Redo、側別保存点、中央の通常保存／外部SaveAsへ接続した。workspace version 5の中央相対assetとレポートなし包装も復元できる。各入力16MiB、履歴合計64MiB／256操作を維持し、コピー範囲は対象pairだけで検査する。中央は編集する第三入力であり、祖先に対する競合分類は追加しない。未適用Hexを比較候補の採用で失わないよう、世代と全editor textを同期照合する。

Archive／Image／Folder／Tableの完成候補も読込み前のBinary `StateStamp`・操作・取消・入力・mode・providerで採用を検査する。拒否時は旧panel、中央Hexの未適用入力、適用済bytes、共有履歴と文書・保存点を保持し、候補を解放する。Tableはlocal文書snapshotで初回比較を完了してから同期採用する。修正前のArchive実操作で編集保持・共有状態・候補解放の3失敗を再現し、採用拒否後の状態表示も現在の操作だけへ反映する。全体GUIの多数45タブ・900×600で見出し9行が本文を押し出す2失敗を再現し、見出し欄を縦スクロールへ制限した。選択中タブの縮小後到達も1失敗を再現して単一postの位置更新で修正した。修正後の限定GUIは111成功／0失敗、同じ最終版の全体GUIは10,592成功／0失敗となった。多数45タブを保持した実際の最小850×550でも全3Hexの高さ・差分一覧の非重なり・中央SaveAsへのスクロール・選択見出しへの到達を確認した。三者Binary／既存Binary／内包projectの限定E2Eは22／57／278成功・失敗0・skip0で、物理中央入力もPython標準readerで全bytesを照合した。全runnerは実管理者・終端0・実行前後binary SHA一致を確認した。証拠は `artifacts/local/binary-adoption-repair/handoff.json` と同runのPNG／JSON。通常物理BasePathと内包左右を組み合わせた三者BinaryのDTOは保存・包装でき、旧schema拒否検証を全中央bytesの独立読込みと展開再読込みへ移行した。

同じ最終build13で三者限定E2E22成功／0失敗／0skip・GUI66成功／0失敗、既存二者Binary57成功／0失敗・GUI65成功／0失敗、内包Text67成功／0失敗・GUI138成功／0失敗となった。原本3 ZIPの全entry／CRC／bytes、全6方向保存、中央asset、CLI複製・包装・展開再読込みをPython標準readerで独立照合した。実管理者token、各終端0、App／driverの前後SHA一致と5 lockのHEAD graph同値を確認した。証拠は `artifacts/local/binary-threeway/completion-receipt.json`。多tab・900×600・長い出力pathでHexと差分一覧の非重なり、scroll後の中央SaveAs到達を実PNGとBoundsで確認した。前回CIで観測したMacフォントの重なりは後続CIで再確認した（本節冒頭）。最終修正後のmanagedとWindows x64 Native AOTは、それぞれ全体E2E160,118成功／0失敗／3skip・全体UI10,592成功／0失敗となった。各6,182 commandの12,364 stdout／stderrと542 PNG、発行物13fileを独立照合した。実管理者・終端0・実行前後のbinary一致を確認し、257 sourceのうちNative restoreで2 lockの改行だけが変わり、全lockのHEAD依存graph同値と残り255fileのbyte保持を確認した。証拠は `artifacts/local/binary-threeway-parent/full-managed02-independent.json` と `native02-independent.json`。後続の全4RID GitHub検証は本節冒頭に記載した。任意位置の挿入削除・旧Hex全操作・Binaryレポートの共通メタデータ経路は未完了。旧GenerateReportは本文を生成しないstubのため、byte本文HTML／patchを旧実装済み機能として扱わない。

## 内包バイナリの作業編集・保存（ローカル全体検証済）

内包Binaryは継承readonlyに従って同じ側の16進編集・差分範囲コピー・共有Undo／Redoができ、通常保存で原本アーカイブから独立した作業版を確定する。親一覧・preview・子再比較にも保存版を使う。外部SaveAsでは成功した側だけを通常ファイルへ切り替え、保存中の後発編集はdirtyを維持する。workspace version 5の相対 `.assets/<SHA>.bin` とレポートなし包装へ保存し、Textのversion 4互換を維持する。各Binary16MiB、共通作業store128MiB／256文書、共有編集履歴64MiB／256操作を上限とする。元アーカイブへの自動書戻しは行わない。

比較設定を適用すると編集可能な内包Binaryがreadonlyへ戻る不具合を実操作で再現（47成功／2失敗）し、継承指定の判定へ統一した。修正後のGUI49成功・0失敗／PNG2、限定E2E57成功・0失敗・0skip／27命令が成功し、全54ログ・原本ZIP・保存byte・workspace・包装とPNGをPython標準readerで独立照合した。実管理者token、プロセス終了コード0、App／driverの前後SHA一致を確認した。証拠は `artifacts/local/archive-binary-working/settings-binary-independent.json`。独立レビューで差分選択からHex位置への移動が失われた回帰を発見し、実GUIで再現後に修正した。後方差分への移動・選択維持・実コピーボタン・未適用Hexの保持を含むGUI53件が成功し、固定6byteと保存時の期待byteも独立readerに直接定義した。証拠は `artifacts/local/archive-binary-working/navigation-regression-independent.json`。初回全体E2Eは160,075成功／2失敗／3skipで、親のコマンドに必要な独立Zデコーダー指定がなかった。全12,338ログを照合し、このrunは未採用として保持した。指定を是正した次runでは全体E2E160,090成功／0失敗／3skip、6,169命令の全12,338ログbytesを独立照合した。一方、全体UIは9,883成功／1失敗で、Binaryから通常Textへの切替後に旧Binaryの保存経路へ到達する回帰を検出した。このrunの全体UIは未採用。通常Text／provider本文の採用時に旧viewを解放し、読込み失敗・取消・採用直前の旧Binary編集では前のownerを保持するよう修正した。実GUIで修正前55成功／5失敗、修正後65成功／0失敗、限定E2E57成功／0失敗を確認した。通常保存・外部保存のText全byteとprovider原本はPython標準readerで固定期待値へ照合した。証拠は `artifacts/local/archive-binary-working/full-managed02-e2e-independent.json`。修正後の通常版とWindows x64 Native AOTは各全体E2E160,090成功／0失敗／3skip、6,169命令／12,338全ログ、headless UI10,325成功／0失敗／PNG525となった。189ソースファイル（表記重複を含む369照合行）のbyte保持、全依存lockのHEAD正規化一致、Native発行物13ファイルと3ライセンス、実管理者token・終端0・実行前後SHA一致を独立照合した。証拠は `artifacts/local/archive-binary-working/full-managed03-independent.json` と `native-independent.json`。AOT初回はMSVC環境不足で失敗し、既存DevShellとリンカーを設定した新しい出力先で成功した。固定SHA `0359432641b1eca873c701dfb06ec1b631dc1aa0` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37192314085)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37192314112)は成功した。8 ZIP・105,890 entryの公式digest／CRC／SHA／サイズ、全24,682命令の49,364 stdout／stderr、UI全41,300成功・2,100 PNG、Binary作業版と多層TARの全ファイル出力を独立照合した。Windows各E2E160,090成功／0失敗／3skip、Mac各160,099成功／0失敗／2skipで、全構成UI10,325成功／0失敗。各CPUのPE／Mach-O、各3ライセンスとMac bundle、別writer／readerによる実OSクリップボードも確認した。原本ZIPは全展開せず698,909,484 bytesで保持する。証拠は `artifacts/local/archive-binary-working/ci/all-four-receipt.json`。これは宣言した自動検証範囲の受領であり、全旧機能・全製品レイアウトの完了ではない。artifact ZIPが保持しない展開後の空directoryはrunnerでの固定reader成功ログだけを確認でき、取得ZIPで存在を再照合できない。次CIにはupload前の実体一覧を同梱する。Mac ARM64の実描画PNGで多tab時のHex／差分一覧の重なりを確認し、後続の三者Binary変更で修正中。Binary HTML／patch、三者Binary、任意位置の挿入削除と旧Hex全操作の同等性は未完了で、ネイティブ保存ダイアログ・実OS pointerは今回のheadless検証に含めない。

## 多層圧縮TARの読取り（ローカル全体検証済）

TARに複数のgzip／bzip2／Zを重ねた入力と、tgz／tbz／tbz2／tazに外側の圧縮を追加した入力を読めるようにした。終端をTARとして検証し、全層の終端・CRC、共有深度・復号量・作業量の制限を維持する。通常一覧・entry取得・展開・ZIP再梱包、標準プロバイダーと明示Sourceの経路を接続した。多層形式への書込みは未対応。

2026-10-04のRelease buildは警告0・エラー0。通常版の限定E2Eは1,363成功・0失敗・0skip、484回の実アプリ実行と968ログの全byteを照合し、実行前後のApp／Providers／driver SHAが一致した。固定正常22件・拒否22件とSource用ZIP4件を使用し、Python標準readerが全entry・時刻・内容・展開・再梱包を独立照合した。readerは正常22件のtyped-rootと正常Source2件を必須にし、記録の欠落・重複を拒否する。入力SHA確認のwork64上限と、SHA確認後のアーカイブ処理で拒否されるwork1000上限を別々に確認した。証拠は `artifacts/local/archive-tar-wrappers/e2e-03-independent.json`。この変更を含む通常版とWindows x64 Native AOTの全体E2E160,090成功／0失敗／3skip、全体headless UI10,325成功／0失敗を独立照合した（上記Binary checkpointの証拠）。後続の正常22原本GUIは155成功／0失敗となり、読取り開始時・完成候補採用直前・Refresh採用直前の実中止と、確定表示の保持・再操作・古い候補のDisposeを確認した。全層の独立復号とGUI候補の全63項目・mtime・元bytesを照合し、限定E2E1,366成功／0失敗／0skip・485命令の970全ログ、実管理者・終端0・App／driver前後SHA一致とPNGも独立照合した。証拠は `artifacts/local/binary-threeway-parent/tar-e2e01-independent.json`。decoder処理中のGUI取消タイミングをこの境界検証から推測しない。新GUI検証を含む全体／4RIDは後続で、全機能移植の完了とは扱わない。

## 実在する内包テキストの作業保存（先行checkpointの検証記録）

実在する内包Textは継承した読取り専用指定に従い、通常保存で原本アーカイブとは別の作業本文を更新する。親一覧・preview・子の再比較も保存済み本文を使う。外部SaveAsの成功時だけ保存した側を通常ファイルへ切り替える。元アーカイブへの自動書戻しは行わない。後続のBinary作業編集・保存の到達点は冒頭の節を参照する。

workspace version 4はJSONと相対参照の `.assets/<SHA>.text` を使い、4MiBのJSON上限を保ったまま大きな本文を保存する。各本文64MiB、共有作業128MiB・256文書を上限とし、原本rootのSHAと全階層の整合性を再検証する。包装では原本と作業本文を別々に同梱する。passwordは保存しないが、明示保存した作業本文assetは暗号化しない。公開／読込みしたassetはwindow終了まで上書きを拒否し、異なる暗号化枝の再入力と一時credentialを経路ごとに分離する。共有保存は三者の祖先も同期し、手動マージの本文・履歴・dirtyを保って古い入力の採用を拒否する。

限定E2Eは内包Text67成功・0失敗（32命令）、archive project274成功・0失敗（110命令）、workspace127成功・0失敗・1skip（43命令、非管理者のlink作成特権不足）。独立readerで原本ZIPの全CRC・SHA、作業本文の全bytes、三者HTML、異なる二つの暗号化枝、相対assetと包装を照合した。初回全体E2Eは158664成功・8失敗・3skipで、新たに有効なversion 4を旧テストが未知としていた8件の期待値をversion 5へ更新した。失敗成果物を保持し、修正後の通常版とWindows x64 Native AOTは各158672成功・0失敗・3skip、5658命令と11316 stdout/stderrの全bytesを照合した。両版の全UIは10259成功・0失敗、各523PNGを独立復号し、原本・作業asset・三者HTML・暗号化二枝・包装・CLI再読込みと適用patchも照合した。ソース362件のbytesとdriver2件は不変で、Native発行12file・3licenseのSHA／サイズも一致した。SHA `58c55469b80f589fc4bd0cca4c57fd5de5e00d15` のCI `37182667198`はWindows両構成とMac ARM64が成功したが、Mac x64のE2Eは158604成功・4失敗・2skipで未採用。Mac x64のUI10259条件は成功している。公式digestと全entryのCRC／SHA／サイズを照合した失敗ZIPから、限定GUI2命令が30秒で強制終了し、未生成のUIレポートを読む独立reader2件も失敗したことを確認した。自己検証の子プロセス予算を見直し、同OSの再実行で完了を確認する。失敗根拠は `artifacts/local/archive-present-text-working/ci-osx-x64-failure-analysis.json`。通常desktop・ネイティブ保存ダイアログは未検証。独立レビューのasset保護・暗号化枝・祖先同期と最終captionを修正し、追加の不正正規表現によるマージ再開始失敗も実行再現後に修正した。最終限定GUIは138成功・0失敗で、再開始失敗時の本文・履歴・dirty・stale保持、window終了時の解放を含む。11PNGと原本・本文・相対assetを独立照合した。証拠は `artifacts/local/archive-present-text-working`。これは全機能移植の完了ではない。

## 不在側の未命名文書と外部保存

不在Text側の元のreadonly指定を、実在アーカイブの固定readonlyとは別に保存・多段継承する。同じ比較側で直接編集と選択差分コピーを行い、初回保存は外部SaveAsへ進む。原子的公開と再読込みの成功後だけ保存した側を通常ファイルへ切り替え、保存中に増えた編集はdirtyのまま保持する。親タブをすべて閉じた後も原本を保護し、取消・古い完了・公開後の採用失敗では本文と不在証拠を保持する。未保存の不在本文をworkspace／包装へ黙って落とさず拒否し、単体HTMLへ未保存表示と本文を反映する。継承情報のない既存projectは保守的にreadonlyとして復元する。実在entryの編集と作業文書への通常保存は上の後続checkpointを参照する。旧通常Saveは展開した実ファイルを更新し、元アーカイブへの自動書戻しは行わない（`Src/MergeDoc.cpp:1096`、`Src/7zCommon.h:27`）。

GUI限定検証は94成功・0失敗。保存側に実在するcontainer階層がある場合のSaveAs後再比較を実行再現し、採用成功時だけ保存側のpassword cacheを新しい階層へ更新した。対向側と原本を保持する。元root保護の脱落を実アプリで再現し、外部保存後・親閉鎖後・パス消去後の再指定を拒否する回帰検証を追加した。公開前の新入力タブとreadonly変更も既存出力を保持する。独立したPython標準readerで、二段ZIPの全entry・CRC・全byte、取消／古い完了の既存出力、保存・公開後の採用失敗・実在0byteの本文、未保存／包装HTMLの全文と改行、包装の全fileと相対参照を照合した。実アプリE2Eは240成功・0失敗で、同じGUI経路・包装展開・CLI HTML再読込み・独立した本文への包装patch適用まで確認した。管理者実行の通常版とWindows x64 Native AOTの全体E2Eは各158606成功・0失敗・3skip、5626命令と11252ログの全byteが一致した。両方の全UIは10215成功・0失敗、各520PNGを独立復号した。ソース355fileのうち353fileは不変で、2つのcompiler lockだけが既存GitのAOT graphへ復帰し、製品依存関係と検証driverは不変。Native発行12file・3licenseのSHAとサイズも一致した。新しい文脈の独立出荷レビューで見つかった保存後再比較の不具合は実行再現・修正・再検証し、修正確認で追加指摘はなかった。SHA `eb0aeb050e670f7c2ed8aa3cdfb22dce8ceb0e5b` の4RID CI `37175679220` とCodeQL `37175679228`は成功した。Windows各158606成功・0失敗・3skip／5626命令、Mac各158615成功・0失敗・2skip／5629命令。各UI10215成功・0失敗／520PNG、原本・HTML・包装全file・適用patch、発行物・ライセンス・Mac bundleと別processの実OS clipboardを独立照合した。8ZIP・683015978bytes・99138entryの全CRC／SHA／サイズと公式digestが一致し、全面展開量は0。証拠は `artifacts/github/37175679220/terminal-summary.json`。この結果は不在側の編集・外部保存を含む同SHAの検証であり、後続の実在entry作業版の実装を含めない。証拠は `artifacts/local/archive-unnamed-editing`。ネイティブ保存ダイアログの測定とは区別する。

## 片側不在の内包入力（4RID検証済み）

実在する親rootと不在の格納名を分けるversion 3のtyped入力を追加した。片側だけのText／Binary／Archiveを開き、不在containerを空一覧として二段先のleafまで進める。実在する0byte fileと不在を保存・HTML・包装patchで区別し、全親containerのCRC・EOF・SHAと不在の起点を再検証する。不在側を実在する親の再梱包・全件展開へ渡さない。元アーカイブへの書戻しは未対応のまま固定readonlyを維持する。

追加した実アプリE2Eは203成功・0失敗（81命令）、独立readerは8比較の原本・本文・全包装fileを照合し、6patchの適用結果を独立本文の全byteと照合した。GUI限定検証は62成功・0失敗で二段の不在階層・readonly・原本保持・出力保護を確認した。不在captionを本文見出しにも表示し、実controlとPNGで確認した。全UI検証は10,183成功・0失敗、518PNGと元container・HTMLの独立照合も成功した。証拠は `artifacts/local/archive-missing-input`。管理者実行の通常版とWindows x64 Native AOTは、全体E2Eが各158,569成功・0失敗・3skip、5,611命令と11,222ログの全byte照合に成功した。Nativeの全UIも10,183成功・0失敗、518PNGと原本・HTMLを独立照合し、12発行file・3ライセンスとソース351fileを照合した。2つのcompiler lockだけが既存GitのAOT graphへ戻り、製品依存関係と検証driverは不変。最初の非管理者実行はリンク保護など12skipのため採用せず、原本を全22,580file照合した約58MBのZIPへ保持した。ごみ箱の完全消去は中断し、約818MBと管理情報が残るため清掃完了とは扱わない。SHA `545d3a674a73ed3ac3d41f3648b4aa24b191858f` の4RID CI `37167958727`とCodeQL `37167958698`は成功した。Windows各158569成功・0失敗・3skip／5611命令、Mac各158578成功・0失敗・2skip／5614命令。各UI10183成功・0失敗／518PNG、8比較と6適用patch、全発行物・3license・Mac bundleと別processの実OS clipboardを再照合した。8ZIP・677587837bytes・98646entryの全CRC／SHA／サイズと公式digestが一致し、展開量は0。証拠は `artifacts/github/37167958727/terminal-summary.json`。このSHAは未命名側の編集・SaveAs追加前のcheckpointであり、後続変更の検証を含めない。通常desktop・ネイティブ保存ダイアログ、新しい文脈の独立出荷レビューは未完了。全機能移植の完了ではない。

## 内包アーカイブの子比較・保存・包装

左右に実在するentryを子タブのText／Binary／Archiveとして開く。自動判定、別名書出し、三者・通常文書との混在Text比較、version 2のtyped workspace、leaf本文の単体HTML、root snapshotを含む包装・leafパッチを接続した。物理rootと格納名・leaf・確定SHAを分け、固定readonlyと全タブの入力・filter・workspace出力保護を維持する。復元後は「比較」の明示操作を待ち、各階層のpasswordをmasked欄で再入力する。passwordと復号本文は保存しない。候補は準備後に親・世代・token・activeタブを照合して追加し、取消・別タブ・refresh・親closeで古い完了を破棄する。

通常版のRelease buildは警告0・エラー0、全体E2Eは158367成功・0失敗・3skip、5530命令・11060 stdout/stderr fileを照合した。全UIは10174成功・0失敗で、Sourceの実操作53件を含み、517PNGのCRC・寸法・復号長・filterを独立照合した。通常／最小windowの一覧・previewは291／93px、scroll後の比較button到達も確認した。全container CRC・leaf／全byte書出し・二者／三者HTMLの原文と改行、暗号化workspaceへのpassword／復号本文の不在をPythonで再照合した。プロジェクトは110命令、独立した7比較・3外部leafパッチ適用と、包装全file・展開・相対参照再読込みを確認した。ソース348件・managed App payload551件・driverの検証前後一致を保持する。

Windows x64 Native AOTも全体E2E 158367成功・0失敗・3skip、5530命令、UI 10174成功・0失敗、517PNGの独立照合が成功した。payload12件／3licenseのSHA・サイズ、source348件の照合、既存compiler graphへ復帰した2lockfile以外の346件のbyte不変とdriver2件の不変を確認した。通常デスクトップと実OS clipboardはこのローカル検証に含めない。

このGUI／typed workspace／包装を含むcommit `417e574d2cf81d5c87acd2cf49e7e9a5f28af6c8` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37160106400)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37160106402)が成功した。Windows両構成はE2E 158367成功・0失敗・3skip、5530命令、Mac両構成は158376成功・0失敗・2skip、5533命令。全4構成でUI 10174成功・0失敗、各517PNGを独立復号し、Sourceの全byte／CRC・本文と改行・7包装・3外部leafパッチを再照合した。実OS clipboardは各構成の別writer／reader processで一致した。8ZIP／676298066bytes／97338entryの全CRC・SHA・サイズとGitHub digest、Native payload・3license・Mac bundleの13内部fileと実行属性も確認した。証拠は `artifacts/github/37160106400/terminal-summary.json`。全4RIDは展開保持していない。通常interactive desktop・ネイティブダイアログと、新しい文脈の独立出荷レビューは未完了である。

初回全体の旧workspace検証が新たに有効になったformatVersion 2を未知としていたため、未知versionを3へ修正した。全UIでown archiveの上書き拒否が共通guardに先取りされ例外型が変わったため、own rootsのIOException検査を先にし全入力保護を後段へ維持した。失敗入力・ログ・JSONと、その修正後の全体／UI再実行を分けて保持する。独立PNG照合の初回がtop levelだけ148枚を列挙していたため、既存契約と同じ再帰・fixture除外で517枚を確認した。

深いcontainerの再梱包／全件展開、片側だけのentry、画像／provider子比較、Source Binary／ArchiveのHTML・report／patch包装、暗号化SourceのCLI report／包装、通常desktop・ネイティブダイアログと新しい文脈の独立出荷レビューは未完了。証拠はartifacts/local/nested-archive-source/gui、最新Nativeはartifacts/native-runs/archive-project-gui-20261004。これは全機能移植の完了ではない。

## 内包アーカイブの明示取得

Providersのimmutable `ArchiveSource` と、実アプリCLIの `--archive-source-list`／`--archive-source-entry` を追加した。物理rootと内包chainを別に保持し、全外側containerの後続entry・CRC・footerまで検証した後だけ次段へ進む。深度・復号量・名前・件数・TAR header・作業量を共有し、rootのSHA変更、descriptorへの上書き、readonlyとlinkを拒否する。各内包保持は256 MiB以下、通常streaming TAR全体の上限は縮めない。以下はservice／CLI checkpoint時点の記録である。GUI子タブ・workspace・包装・HTML接続は上の最新ローカル検証を参照し、深いcontainerの再梱包／展開は引き続き未対応。

Release警告0・エラー0、Windows通常版とx64 Native AOTのSource限定E2Eはそれぞれ管理者processで323成功・0失敗・0skip、127 App commands。通常版の全体E2Eは158094成功・0失敗・3skip、5420 App commands。headless UIは両版10121成功・0失敗で、各512 PNGのCRC・寸法・復号長・filterを独立照合した。Native発行物12件のSHA・サイズと3ライセンス、検証前後の339 sourceを照合し、Native restoreによる2 lockfileの既存compiler graph復帰と製品依存の不変も確認した。

Python標準libraryが18固定入力と11正常階層の全内容・CRC、16取得file、280MiB TARのraw SHAと各140MiB entryを独立照合した。固定入力一式は約327KBで、巨大raw TARを保存していない。別の実アプリ経路で、root／内包のsolid 7z・RAR5、暗号化7z／ZIP／RAR5、未知名TAR.ZとSHA付きdescriptorの再取得を両版各22命令で確認した。最初の非管理者実行は310成功後にlink作成特権不足でrunnerが中断したため失敗として保持し、管理者再実行でlink入力／出力保護も検証した。通常デスクトップや実OSクリップボードは今回のローカル検証に含めない。証拠は `artifacts/local/nested-archive-source`。

このservice／CLIを含むcommit `d8e9669481d3b872988eeef19e69e5558134d4c0` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37147393452)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37147393447)は成功した。Windows両構成の全体E2Eは158094成功・0失敗・3skip、5420命令、macOS両構成は158103成功・0失敗・2skip、5423命令。全4構成のUIは10121成功・0失敗、各512PNGを独立復号し、実OSクリップボードも別writer／reader processで一致を確認した。8ZIP・671689114byte・95670entryの全CRC／SHA／サイズとGitHub digest、固定原本、Source独立proof、Native payload／license、Mac bundleの内部全file・実行属性を照合した。全4RIDを展開保持せず、ZIPと小さい採用receiptを `artifacts/github/37147393452` に保持する。Source CLIの実取消、新しい文脈の独立出荷レビュー、通常デスクトップ・ネイティブダイアログの検証は未完了であり、新しいGUI／プロジェクト変更はこのservice／CLIのCIに含まない。

## ZIP・7z・RARの明示的な多重圧縮

初回の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37135868237)はWindows／MacのARM64が成功し、x64両構成はアーカイブの中止自己検証で失敗した。UIキューへ中止を予約した小さな入力が中止前に完了していたため、実際の中止ボタンを比較操作作成後・読込み開始直前の観測点から押す方式へ変更し、各取消の失敗名に境界を明記する。元の表示保持・modal・採用直前・refresh取消の検証は維持する。失敗ZIP2個の全entry CRC／SHA／サイズを照合し、`artifacts/github/37135868237`へ保持した。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37135868265)は成功。修正後の成功runと初回の失敗を区別する。

中止検証の修正後はRelease警告0・エラー0、通常版UI10,121成功・0失敗、全体E2E157,722成功・0失敗・11skip。5,274 App呼出しと10,548 stdout／stderrを照合した。Windows x64 Native AOTの発行とUI10,121成功・0失敗、発行13ファイルのSHA／サイズも確認した。発行後の補助検証はCore／ProvidersのlockへNative用ILCompiler情報が追加され終了1となったが、追跡済みGitのNative graphと一致し、compiler情報だけを除いた通常版graphは事前SHAへ正確に戻ることを確認した。製品依存を変えないこの差分だけを厳密に検査する形へ補助検証を修正し、311 sourceのbytes不変・2 lockの既知restore差分を照合して終了0。元の終了1は保持する。証拠は`artifacts/local/archive-wrapper-cancel-fix`。UI観測点だけの変更に対しローカルNative全体E2Eは繰り返さず、修正後の4RID CIで全体E2Eを実行する。

ZIP/JAR/EAR/WAR/XPI・7z・RARを`.gz`・`.bz2`・`.Z`で包んだ明示名を、GUI Auto・標準archive provider・CLIの一覧／比較／エントリ保存／再梱包／全件抽出へ接続した。各層を終端まで検証し、深度・中間内容・累積復号量・実読込み作業量を共有する。暗号化入力の初期失敗は任意の左右パスワードで再試行でき、取消・失敗・古い完了で確定表示を保持する。ZIPの宣言CRCとサイズは展開前に固定し、不一致時は既存出力を置き換えない。

Release buildは警告0・エラー0、通常版の限定E2Eは1,118成功・0失敗・0skip、全体E2Eは157,722成功・0失敗・11skip、headless UIは10,121成功・0失敗。34正常鎖の全層と再梱包／抽出bytes、中央CRC／サイズ不正の拒否を独立Python／公式Zで確認した。UIの8状態で一覧・プレビュー・表示元を保持し、7z／RARの全11nodeの型・サイズ・UTC時刻を固定原本へ照合した。証拠は`artifacts/local/archive-wrapper-integration/managed-acceptance.json`。Windows x64 Native AOTもUI10,121成功・0失敗、全体E2E157,772成功・0失敗・3skipで終了し、5,293 App commandsと10,586 streams、発行12payload＋manifest、313 sourceの不変を確認した。Nativeの証拠は`artifacts/local/archive-wrapper-integration/native-full-acceptance.json`。新規reviewerはスレッド上限で起動できず、新しい文脈の独立レビューは未完了。内包entryの子タブ比較、裸の圧縮ファイル、TARの複数wrapper統一、wrapper出力作成は後続工程で、全移植完了とは扱わない。

中止検証修正SHA `10ea3e7dad8363689f5c33ffc379f6565c1523c8` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37139908624)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37139908632)は成功。全構成UI10,121成功・0失敗、Windows各157,772 E2E成功・0失敗・3skip／5,293命令、Mac各157,781成功・0失敗・2skip／5,296命令。全stdout／stderrとJSONが一致し、timeout／起動失敗0。各構成512 PNGを独立復号し、5取消境界、8表示保持状態、11 embedded metadata、34正常wrapperと34再梱包／抽出出力、2 central metadata拒否を確認した。発行manifest全payload SHA／サイズ、Mac bundle内部全fileと実行属性、別writer／readerの実OS clipboardも照合した。8 ZIP・94,138entryのCRC／SHA／サイズとGitHub digestが一致し、670,829,040 bytesの圧縮状態で保持する。全体E2E・発行物は展開していない。証拠は `artifacts/github/37139908624/terminal-summary.json`。このCIへ後続の未commit Source実装は含まない。

差分色の不透明度を0～1（既定0.7）でGUI・通常CLI・プロジェクト設定・単体／包装HTMLへ接続した。重ね合わせの不透明度とは別の値として保持し、原画・差分分類・共有履歴を変更しない。描画候補が未採用・取消・失敗の場合は保存値を変えず、alphaだけの変更では矩形選択・浮動貼り付けを保持する。固定原本12ケース146状態と、未選択86状態の通常CLI・HTML・包装・展開再読込みを使う。ローカル通常版・Windows x64 Native AOT版のheadless UIは各9279成功・0失敗。Native発行12ファイルの全SHA・サイズと原本146状態の全BGRAも独立照合済み。証拠は `E:/DiffBeacon-artifacts/local/image-alpha`。Windows x64 Native全体E2Eは149863成功・0失敗・3skipで終了し、18524ファイルの全SHA・サイズ照合後に約873 MiBを約43 MiBのZIPへ圧縮保持した。通常版全体E2Eも149818成功・0失敗・10skipで終了し、新しい文脈の独立レビューに未解決Critical／High／Mediumはない。この変更の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37081748402)はWindows両構成が成功し、Mac両構成は最小ウィンドウで差分色不透明度スライダーがツールバーの表示範囲からはみ出すため自己検証が失敗した。Windows両構成の全体E2Eは各149863成功・0失敗・3skip（実ログ確認）、Mac両構成は自己検証6300成功・2失敗で全体E2E未実行。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37081748401)は成功した。Macの失敗証拠は各ZIPの全706entryを照合して保持し、前の矩形編集コミットのCI成功と区別する。

最小ウィンドウの操作欄はテーマの操作部品を実測して高さ下限を決め、低い画像パネルでは縦余白を28 DIP詰める。通常の6条件（通常／最小サイズと実テーマ／64／80px Slider）を保持し、Windowsの明示的な追加寸法実験では約32px少ない可用高さを与える。修正前の画像viewport 0pxを、修正後は27px×3面へ改善し、スライダー・モード・PNG保存の全到達判定に成功した。最新通常DLLの全UIは追加実験込み9299成功・0失敗（終了0）、全体E2Eは149818成功・0失敗・10skip（終了0）、Release buildは警告0・エラー0。Windows x64 Native AOTでは通常6条件を含むUI9295成功・0失敗、原本146状態316面の全BGRAと発行物12ファイルの全SHA・サイズが一致した。発行とmanifest完成後の検証wrapperは最終ログ保存のI/Oエラーで終了1となったため、実処理の成功と区別して記録した。証拠は`E:/DiffBeacon-artifacts/local/alpha-toolbar/managed-ui-compact-proof.json`、`native/attempt4-native-ui-proof.json`とPNG。通常6条件だけの件数と区別する。修正SHA `f578d3c7240f1a27a3032cc9ae9b360ffcb2b85e` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37087078586)では全構成のheadless UIが9295成功・0失敗となり、原本146状態316面の全BGRAと各36 PNGを独立照合した。全体E2EはWindows両構成が各149863成功・0失敗・3skip、Mac ARM64が149872成功・0失敗・2skip、Mac x64が149870成功・2失敗・2skip。Mac x64の2失敗は全ページ画像HTMLで累積描画上限を拒否する1コマンドの30秒タイムアウトに由来し、入力・既存HTMLは保持された。このrunは失敗、[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37087078589)は成功。成功3構成の発行manifest全12ファイルとOSクリップボードの別writer／readerも照合済みで、失敗Mac x64の発行物アップロード・OSクリップボードは未実行。7 ZIPの全79376entryを照合し、証拠は `artifacts/github/37087078586/terminal-summary.json` に保持する。全ページHTMLは予算成立後に描画する経路へ修正した。Windows通常DLLの同入力で拒否7,580ms→274ms、挿入削除限定E2E12,542成功・0失敗・0skip、headless UI9,369成功・0失敗、ビルド警告0・エラー0。単体／包装の正常多ページ・原画と整列全BGRAを独立PNG復号で確認し、正常・上限拒否・OS強制終了・実CancellationToken取消で一時データが残らないことを観測した。証拠は `artifacts/local/image-report-preflight/terminal-summary.json`。新しい文脈の独立コードレビューに成立する未解決指摘はない。修正後の通常版全体E2Eは150,137成功・0失敗・10skip、終了0。Windows x64 Native AOT全体E2Eも150,182成功・0失敗・3skip、終了0となり、同じ全ページHTML拒否は53msで終了した。後処理IO故障注入と修正後の4RIDは未確認。

画像の位置ずらしを比較・コピー核、GUI矢印操作、通常`--image`、プロジェクト保存・復元、GUI／プロジェクト／包装HTMLへ接続した。原本DLLの12ケース43状態を3回採取して全bytes一致を確認し、正規化・支持矩形・左上拡張・位置を戻さないUndo・原画PNGを固定E2EとGUI自己検証で照合する。位置調整とワイプは下記の6モードに接続した。[位置ずらしの契約](IMAGE-VIEWER.md#静止画像コピー核の診断)を参照する。

位置ずらし対応のSHA `d0644bcbf04ff12ba2dfd44ef2a4af275679b956` は [4構成のGitHub Actions](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36941324269) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36941324655) が成功した。Windows両構成の全体E2Eは76780成功・0失敗・3skip、Mac両構成は76789成功・0失敗・2skip、headless UIは全構成3093成功・0失敗。原本の12ケース43状態と93画面、GUI HTML4画像、既存回転反転の14ケース124状態346画面を各構成の成果物へ独立照合した。8成果物はZIPのまま保持してentryを照合し、抽出はmanifestと代表PNGだけ、Native発行物・全体E2Eの一括展開は0 bytes。証拠は `artifacts/verification/image-copy-cli/ci-36941324269-summary.json` と `E:/DiffBeacon-artifacts/github/36941324269`。通常デスクトップ操作とMac署名・公証はこの検証に含めない。

この変更は新しい比較アプリの実装と Native AOT 発行経路の追加であり、旧 WinMerge の全機能との互換性を完了したものではない。旧 C++ / MFC の `Src/Merge.rc` と各実装は残っているが、新しい `DiffBeacon.slnx` の通常ビルド経路には含まれない。旧機能の存在と、新しいアプリで実装・検証された機能を区別する。

画像の行・列整列、逆座標と構造コピーをGUI・通常CLI・プロジェクト設定・単体／包装HTMLへ接続した。原本58ケース304状態の全ghost画素・座標対応・コピー・Undo／Redo・位置と方向・原画PNGを照合し、強調の別原本12ケース146状態で通常／選択削除色、透明実画素、alpha0/.3/.7/1を検証する。最新通常DLLの限定E2Eは9110成功・0失敗・0skip、headless UIは7513成功・0失敗、Release buildは警告0・エラー0。GUIの固定alpha .7は原本12ケース62状態を実Bitmap全BGRAへ照合し、最小ウィンドウのモード選択とPNG保存も確認した。通常デスクトップと、GUIのalpha0/.3/1選択はこの検証に含まない。証拠は `E:/DiffBeacon-artifacts/local/image-insertions/integration-managed-4` と `integration-ui-3`。全体E2Eは通常DLL145951成功・0失敗・10skip、Windows x64 Native AOT145996成功・0失敗・3skip。Native AOTの発行は警告0・エラー0、headless UIは7513成功・0失敗で通常DLLと画像観測SHAが一致した。全体E2Eの入力・出力・ログは全ファイルSHA-256照合済みZIPで保持し、展開分を除去する。変更後のGitHub初回検証（commit `5194e6e9c`、run `36952571103`）はWindows x64成功、CodeQL成功、Mac両RID失敗。Mac ARM64はAOTコンパイル後の自己検証で入力属性の期待値が不正なため失敗した。対象PNGのSHAは原本と一致する。検証のNormal／ReadOnly併用を修正し、設定後のOS属性と読取り専用フラグを確認する。GUI検証の修正後（commit `40648ef6a`、run `36954000118`）はWindows両RIDとCodeQLが成功し、Mac両RIDは失敗した。Mac ARM64の自己検証7539成功・0失敗を確認したが、E2Eに同じ属性期待値の不備が残り2件失敗した。E2Eも設定後の実属性を保存する方法へ修正し、次の変更SHAで4RIDを再検証する。

矩形画素操作の診断CLIへ半開矩形の削除とBGRA貼り付けを接続した。無改変原本32ケースのうち現行入力・安全契約に適合する20ケース、共有履歴・Undo／Redo・16方向組合せ・原画PNG・全比較gridと領域・入力保持・schema／履歴／作業量上限を実アプリで検証し、限定E2Eは708成功・0失敗・0skip。除外12ケースは証拠JSONへ明示する。この段階の検証は診断CLIと画素APIが対象。後続のGUI接続とクリップボードの実測は次段落に記載する。Windows x64 Native AOTの全体E2Eは146705成功・0失敗・3skip、通常版とNative版のheadless UIは各7539成功・0失敗。Native発行12ファイルのSHA・サイズを照合した。通常版の全体E2Eは146660成功・0失敗・10skip。変更後の4RID検証は未実施。限定証拠は `E:/DiffBeacon-artifacts/local/image-rectangles/e2e`、全体の入力・出力・ログは全15876ファイルのSHA照合済みZIP（912321302 bytesから43342084 bytesへ圧縮）と容量索引に保持する。

矩形編集のGUIへ選択・Copy／Cut／Delete・選択範囲の移動とCtrl複製、浮動貼り付け、右辺／下辺／右下角のResizeを接続した。最新通常DLLのheadless自己検証は8351成功・0失敗（追加812項目）。原本Resize96ケースと矩形操作20ケース、全BGRA・共有履歴・PNG保存再読込みを確認した。独立レビューの5件（Resize待ち中Escape、選択範囲移動、フォーカス先、画像原点、非同期マウス解除）を修正し、実Key／Pointerと採用前の待機ゲートで検証した。代表PNGも確認済み。証拠は `E:/DiffBeacon-artifacts/local/image-rectangles/gui/managed-2`。同出力先は失敗候補をZIP照合後に再利用したため、最新の `ui-report.json` と今回生成した観測／PNGを証拠とし、残存入力すべてを最新実行の生成物とは扱わない。headlessのクリップボード検証は製品のAvalonia／native API境界を注入し、実OS操作と区別する。commit bcda5ede591e7b619c43118f9ac10d2f58d42862 のGitHub Main run 36961800413とCodeQL run 36961800407は成功。Windows両RIDはE2E各146705成功・0失敗・3skip、Mac両RIDは各146714成功・0失敗・2skip、全RIDのUIは8351成功・0失敗。各OSの別writer／reader processで固定24 BGRA bytesとPNGを照合し、Windowsの正の高さ・bottom-up CF_DIBも独立確認した。実OSの成功項目はWindows各16、Mac各12、失敗0。8 ZIPを一度だけ取得し全68766 entryのCRC・SHA・サイズ、Windows各12／Mac各26発行ファイルとMac tar各13 entryを照合済み。証拠は E:/DiffBeacon-artifacts/github/36961800413/summary.json と8 ZIPに保持し、必要展開は7229188 bytes、発行物の展開は0 bytes。通常デスクトップの保存ダイアログ・旧FreeImageとの実相互運用は未実測。最終版の通常全体E2Eは146660成功・0失敗・10skip、Windows x64 Native全体E2Eは146705成功・0失敗・3skip。Native発行は警告0・エラー0、UIは8351成功・0失敗、発行12ファイルのサイズ／SHAと通常版との画像観測6ファイルのSHA一致を確認した。全体E2Eの入力・出力・ログは31712ファイルを全entry照合済みZIPへ圧縮して展開元を除去した。実測集計は `E:/DiffBeacon-artifacts/local/image-rectangles/gui/final-local-verification.json` と `native-ui-proof.json`、格納先は容量索引に保持する。

独立レビューで発見した全ページの描画予算漏れは、整列後canvasに対する累積256Mの処理前検査へ修正した。整列前240M／整列後360Mの10ページTIFFで旧ビルドの誤受理を再現し、修正後の終了2・空stdout・既存HTMLと入力保持、選択1ページの成功を確認した。また、幅不一致を先にfalseとする原本の判定順を保持し、横方向の三者整列の誤拒否を修正した。原本fixtureは変更していない。検証済みの過去出力は容量索引のZIPへ格納する。

テキストと表の行比較のDiff算法は依頼によりGNUベースの一種類に統一する。原本との対応検証と処理上限を備えた現行実装を採用し、旧WinMergeの算法選択ドロップダウンと別算法の移植は完了条件から外す。画像行の挿入削除検出にはWinIMerge既定Myers一種類を移植する。行内のWordDiffや比較フィルターは引き続き対応範囲に含む。

画像行Myers核は原本の分類順・signed char量子化・32bit hash・discard・同点規則を移植した。[固定原本](../tests/Fixtures/ImageLines/README.md)14,797ケースの全scriptと行hashを実アプリの`--image-line-script`へ照合し、予算修正後の通常DLL・Windows x64 Native AOTの限定E2Eは各59215成功・0失敗・0skip。Release buildとAOT発行は警告0・エラー0。入力へ期待scriptを渡さず、寸法・負の閾値・原本で整数量子化が未定義になる閾値・後半ケースの不正入力、予算境界と残量不足を検査した。独立レビューで見つかった処理完了後の累積予算検査を、比較開始前の残量引渡しと各処理中の検査へ修正し、部分stdoutと入力改変がないことを再検証した。修正前の通常DLL全体は135938成功・0失敗・10skip、修正後の管理者実行AOT全体は135994成功・0失敗・3skip、AOT headless UIは3093成功・0失敗。両版の行比較観測SHAは一致し、発行12ファイルのSHA・サイズも一致した。証拠は `E:/DiffBeacon-artifacts/local/image-insertions/line-progress.json` と容量索引のZIP。この記録は行比較核の段階であり、後から追加した整列診断・座標対応の全状態の実行検証、画像GUI・構造コピーへの接続、変更後の4RID検証は未完了。

フォルダー確認付きコピーの先行版`source-freeze-06`は、Windows通常版の全体E2E161,626成功／0失敗／4skip、全体headless UI11,074成功／0失敗となった。18の独立readerと実processの終端を確認し、30,888成果物のSHA・サイズを再照合した。通常／最小850×550・45タブのPNGで一覧とコピー操作への到達も確認した。証拠は`artifacts/local/folder-sync-next/whole-verification/managed06-parent-revalidated.json`と`managed06/handoff.json`。後続のルート重複・特殊コピー先・利用中filter・ADS格納元とextended表記の保護修正は、この先行版の実測に含まない。後続の`source-freeze-10`では保護修正を含むWindows通常版の全体E2E161,640成功／0失敗／4skip、全体headless UI11,083成功／0失敗。18独立reader、6,989実processの終端、32,461成果物の主stream SHA・サイズを確認した。証拠は`artifacts/local/folder-sync-next/review-fixes/managed10/final-verification.json`と`files-final.json`。これらは次の全file ADS経路を追加する前の実測であり、追加経路の全体実測は次段落に記載する。追加後の`source-freeze-13`は限定E2E104成功／0失敗／1skip、6CLI・11GUIの全streamを独立readerで照合した。中止表示と共有descriptor／UTF16名の境界を追加した`source-freeze-16`はRelease警告0／エラー0、限定headless UI203成功／0失敗。旧44GUI・7reviewと追加4中止・11stream・4予算GUIの全bytes／SHA／rootを含むmetadataを独立照合した。中止表示の修正前3失敗と検証側の期待不良1件は`managed14`へ保持し、確認待ち前の中止を避ける検証順序も修正した。追加後の全体・同OS AOT・4RIDはこの限定結果に含めない。証拠は`artifacts/local/folder-sync-next/windows-stream-implementation/managed13/parent-limited-acceptance.json`、`managed16/gui-reader-result.json`と`standalone-stream-gui-reader.json`。

全file ADS・中止表示・共有予算を含む最終版では、Windows通常版とWindows x64 Native AOTの全体E2Eが各161,653成功／0失敗／4skip・6,967命令、全体headless UIが各11,123成功／0失敗となった。各20の独立readerで全stream・metadata・保存bytesと固定期待を照合し、実process終端0と製品runtimeの前後一致を確認した。Mac起動記録を修正したmanaged19ハーネスは、コンパイル済みDLL／PDBと全ソースを照合して同じWindows x64 Native製品への全体E2Eに使用した。通常／最小850×550／多数tabのコピー操作をPNGで確認した。証拠は`artifacts/local/folder-sync-next/windows-stream-implementation/managed17/final-verification.json`と`native19/final-verification.json`。実Macの起動・取消・pipe解放、最新4RID、通常desktop・実OS pointer・ネイティブdialogと完全なShell metadataは未検証／未対応であり、このWindows実測に含めない。

SHA `2a5f01ae9fb3cd9264da4bd25d2680bedbe76057` のCI `37260364221`はWindows x64／ARM64が成功した。Mac ARM64はNative発行後の全UI11,094成功／2失敗で、最小／多数タブのフォルダー一覧が90pxとなり必要な100pxを満たさなかった。Mac Intelは10,919条件成功／0失敗の後、フォルダー検証開始時に全体600秒の制限へ到達し、最終UI集計とE2Eは未完成だった。同じ製品コードの後続SHA `5a37db2ba2e8b8690660c9ee8df8138fd3854f8b` のCI `37260736983`はWindows両構成成功、Mac両構成は全UI11,094成功／2失敗で終了した。この後続Intel実行はtimeoutではない。フォルダー表示時だけ共通設定欄を縮め、スクロールで入力・比較・除外・フィルターへ届く検証を追加した。Windows限定UIは218成功／0失敗で、追加15条件と最小一覧132pxを確認した。Mac CIの全体枠は1,200秒へ変更し、個々の操作制限と合否条件を維持する。

修正後のWindows通常版とWindows x64 Native AOTの全体E2Eは各161,653成功／0失敗／4skip・6,967命令、全体headless UIは各11,138成功／0失敗となった。通常版UIの取得済みruntimeとソースは今回のコンパイル対象に一致し、Native UIも実行して確認した。全体E2Eの独立readerは通常版9本・Native9本、Native UIは11本すべて実終了0。採用したFolder readerは既存44ケース・7保護ケース・4中止ケースの全bytes／metadataを維持し、旧3PNG期待を6枚へ更新してPNGのCRC・zlib scanline・寸法と15操作の座標を照合する。親は通常／最小の三者画像viewportとPNG保存ボタン、最小／45タブのフォルダー一覧とスクロール後の設定欄を実PNGで確認した。Nativeの実publishは終了0だったが、発行後guardはSDK暗黙ILCompiler追加によるCore／Providersの2lock byte変更を拒否して終了1となった。元bytesの再構築・既存依存graph・発行物全SHA／sizeと他1,971fileの保持を照合し、発行結果を別の親receiptへ採用した。各全体実行の前後1,973 source／fixtureと569 runtime fileは不変。全体E2E内の旧reader拒否runは実終了-1で打切った失敗記録として保持し、成功結果に含めない。直近原本は`E:\DiffBeacon-artifacts\local\folder-layout-validation26`、発行物は`artifacts/local/folder-sync-next/folder-layout-native25`、旧Mac原本ZIP／PNG／JSONは`github-latest-handoff20`へ保持する。修正後の4RID・実Mac起動と取消／pipe回収、通常desktop・実OS pointer・ネイティブdialogと完全なShell metadataは残工程。

SHA `92c2b1a6ee15807b97616f7899e216ebcf7f5e3c` の[CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37339611969)はWindows両構成が成功した。Windows x64の公式2ZIPのdigest・31,313全entryのCRC／SHA／size、E2E161,653成功／0失敗／4skip・6,967命令の全stdout／stderr、全UI11,138成功／0失敗、Native発行物・3ライセンス、実OS画像／バイナリclipboardの別writer／readerを照合した。親はフォルダー通常／最小／45タブの6PNGを目視し、一覧と4コピー操作・scroll後のfilter／除外への到達を確認した。証拠は`E:/DiffBeacon-artifacts/local/folder-layout-validation26/github-layout27/win-x64/rid-receipt.json`と`artifacts/local/folder-sync-next/validation26-control/layout27-ci-win-x64-parent-visual-receipt.json`。Mac両構成はそれぞれNative発行・全UI11,111成功／0失敗の後、E2E161,777成功／1失敗／8skipとなった。各7,020命令のStartupGate・実終端・出力回収に異常はなく、失敗は特殊input helperが`plain`文字列をtrueと扱って片側linkの反対側に通常fileを誤生成したことによる。固定のLeftOnly／RightOnlyと全原本保持の期待を維持して引数解析を修正した。同OSの修正後E2Eと最新全4RIDの受理は再検証完了まで未確認。Mac原本は同保存先の`osx-arm64/cause-receipt.json`、`osx-x64/cause-receipt.json`と公式ZIPへ圧縮保持する。修正後のWindows限定E2Eは104成功／0失敗／1skip・全62命令の実終端を確認し、独立readerは39CLI・44GUI・7review・4中止と全本文／metadata、6PNG・15toolbar条件を受理した。Release buildは警告0／エラー0。証拠は`artifacts/local/folder-sync-next/mac-special-input-fix28`とE:の同名限定run。

Mac特殊入力producerの引数解析を修正したSHA `5cce1a13235da96c6c813e902b52936b0613a930` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37349541157)とCodeQLは成功しました。Windows各161,653 E2E成功／0失敗／4skip・6,967命令、全UI11,138成功／0失敗。Mac各161,778 E2E成功／0失敗／8skip・7,020命令、全UI11,111成功／0失敗です。4構成の公式成果物を全entryのCRC／SHA／size・実process終端・全stdout／stderr・Native形式とライセンス・別processの実OSclipboardへ照合し、約959MBの圧縮保持で受理しました。MacのLaunchEvidenceは全件の終了と出力回収を確認し、親は両Macのフォルダー6PNGを目視しました。証拠は `artifacts/local/folder-sync-next/windows-metadata-integration29/all-four-ci5cce.json` とE:の `folder-layout-validation26/github-mac-fix28` です。この確定SHAには後続のWindows metadata主経路と清掃予算修正を含みません。

Windows metadata主経路の追加途中では、NTFS directoryのnative indexサイズと既存logical 0の不一致を実アプリで再現し、directoryだけlogical 0へ統一しました。読取query群のcontext同期を前後と変更境界へ整理し、各native queryの取消と公開直前のcontext検査を保ったまま、同じ233 GUI条件を約333秒から93秒へ短縮しました。限定E2Eは104成功／0失敗／1skip・62命令、独立readerは5 metadata拒否GUIの固定literal・creation・全membershipと70 checked closeを追加照合しています。通常／extended drive表記で内部の非対UTF16名を保持して公開することも実CLIで確認しましたが、直接CLI引数とJSONの非対surrogate往復は未対応です。証拠は `windows-metadata-integration29/foldercopy-latest-parent-acceptance30/acceptance.json` と同rootの実process receiptsです。後続の未公開破棄予算先払いと永続E2E readerを含む全体／主App AOT／4RIDは、この限定結果には含めません。

Windowsのcreation・owner/group/DACL・EA・file identityを取得する新部品は、固定した部品ソースの通常版とWindows x64 Native AOTで6ケース・各12独立readerの実終了0と全DATA／ADS／意味のあるmetadataを照合した。主コピー経路への接続、metadata setterと公開、取得中のpath差替え対策と完全なShell metadata保持は未完了で、この部品実測を製品の転送対応として扱わない。証拠は`artifacts/local/folder-sync-next/windows-metadata-product-capture27/native-run05-handoff.json`と原本SHA付きのsource cohort。

| 旧機能・処理分岐 | 新しい実装の範囲 | 判定・残作業 |
| --- | --- | --- |
| テキストの二者比較・三者マージ | 差分行・行内強調、差分移動、両方向コピー、編集、保存、競合表示、CLI、左・祖先・右・結果の4ペイン、差分単位の順序付き採用・Undo/Redo・未解決状態・行の由来、比較セッション内の手動同期点追加・解除、通常／番号付き行ブックマーク | [マージ結果セッション](MERGE-SESSION.md)を実装。旧エディターの構文強調、矩形選択、移動行、全ショートカット、由来の行番号マージン表示・セッション永続化は未完了 |
| フォルダー比較 | 二者／三者、側別kind／filtered／走査状態と階層、内容／SHA／日時、除外表示、ツリー折り畳み・初期展開方針・新比較への全体設定引継ぎ、Ctrl/Shift複数選択、確認付き左右「すべて／差分」コピー、同一ボリューム内で移動先を上書きしない通常ファイル移動、選択した差分通常ファイルのOSごみ箱移動、取消・部分結果と再比較、共通CLI | Windows全file ADS転送・全stream鮮度・共有予算・中止表示を含む通常版とWindows x64 Native AOTの全体検証を前段の記録で確認。通常／最小／45タブの操作到達は確認済み。三者の基準版4RID資格と、source271のツリー／全体設定のmanaged・Windows x64 Native資格は前段の記録へ結ぶ。ファイル移動は同一ボリュームの通常ファイルのみ。ごみ箱移動は通常ファイルのみで、ディレクトリとリンクは対象外。ディレクトリ移動、既存移動先の置換、EFS／SACL／非NTFS等の旧Shell操作の完全な同等性、今回の変更に対するWindows/macOS実行および4RID CI実測は未完了。[操作と対応範囲](FOLDER-COPY.md) |
| 表形式 | 原文区間付き解析、raw WordDiff共通文字量・best-pair・三者01/12/20行合わせ、セル編集・Undo/Redo・同セル内前後検索・固定文字範囲・一件/全置換、CSV原文バッファーの横断検索・構造置換・raw offset範囲固定、区切り文字・引用符・引用内改行指定、GUI/CLI/HTMLの共通モデル、先頭行の列見出し表示と設定の保存・開いている表タブ間の共有、全ペイン・全行からの列幅自動調整（32列ページ間共有）、行対応からの列除外（GUI/CLI/workspace）、行対応キー用の静的列範囲（GUI/CLI/project/workspace/report-project） | [表の操作](TABLE-EDITOR.md)。旧callerのraw CSV入力変換、残るWordDiff設定とフィルター座標、PCRE/Rx互換・矩形文字編集、一般行変換の残る旧言語構文・parser全体・生成改行の原行写像、同期点は未完了 |
| Hex / バイナリ | バイト比較、ページ単位の16進編集、差分範囲／全体コピー、任意位置の挿入・上書き・削除、直接ニブル／ASCII入力・任意byte選択、検索・選択置換・前方／後方全置換、Copy／Cut／Paste／FastPaste dialog、repeat／skip・Insert／Overwrite・AsText・endian・ANSI／OEM、共有Undo／Redo、通常／別名保存、内包作業版の保存・復元・レポートなし包装 | 表示・編集は各16 MiB、共有履歴64 MiB／256操作上限。同じオフセットで比較。挿入位置の再整列・全旧Hex操作の同等性は未完了。置換はmanaged全体E2E／UI、Windows x64 Native全体UIと限定E2Eを実測した。Native全体E2E・他RID／現在版のCIは別途確認する。Binary HTML／patchは未対応。旧HexのGenerateReportは空実装なのでBinary HTMLを旧機能移植の完了条件には数えない（Src/HexMergeDoc.h:74）。新clipboardの実OS／Native／全4RIDは後続検証と区別する |
| 画像 | 二者・三者表示、原本領域強調と差分／競合移動、重ね合わせ、倍率・閾値、ピクセル差分、GIF/WebP/APNGのフレーム選択・前後移動・同期移動、TIFF／BigTIFFの主ページ比較、ページ・表示設定のプロジェクト保存、全フレーム／選択組のCLI比較とPNG埋込みHTML、包装レポート、GUI／開発用CLIの静止画領域コピー・共有Undo／Redo・PNG／単ページTIFF別名保存、回転・反転と手動位置ずらし、行列の挿入削除の表示／コピー／設定保存、編集済み原画のHTML、GUI矩形選択／移動／複製・Copy／Cut／Delete・浮動貼り付け・3辺Resize、差分色の不透明度と設定保存 | [画像の契約](IMAGE-VIEWER.md)。入力・画素・枚数・復号量とHTMLの上限を維持。TIFFの未検証構成と残りの旧画像形式、ベクター、OCR、元形式／多ページ保存、旧全体設定の一括移行は未完了。XOR／Alpha／ANIM・点滅・共通設定と通常CLI／HTML／包装の接続は実装し、最終通常版の全体E2E・UIが成功した。変更後のNative／4RID検証はSHA dc20aaの全構成で成功した（TAR.Z節の実測を参照） |
| Web / XML / HTML / Office | XML正規化、HTML静的本文、HTTP応答のソース・本文、DOCX/PPTX/XLSX本文 | 標準プロバイダーを明示選択。ブラウザーのDOM・JavaScript・画面・リソースツリー、Officeの書式・旧形式・PDF/OCRは未完了。詳細はプロバイダーREADME |
| Archive / プラグイン | 7z/RAR/ZIP/TAR/TAR.GZ/TAR.BZ2/TAR.ZとZIP派生/7z/RAR/TAR/短縮TAR拡張子の明示的gz/bz2/Z鎖の内容比較、初期再試行、暗号化ヘッダー/内容・solid読込み、プレビュー・エントリ保存・全件抽出、非暗号化7z/ZIP派生/TAR系作成・再梱包、単一ファイルの裸gzip/BZip2/Z作成・再梱包、保存済み比較文書・HTML/patch/projectの包装、Sourceの多段Text／Binary／Archive子比較・片側不在入力・未命名Textの外部保存・実在Text／Binaryの作業保存・相対asset workspace・leaf HTML／patch包装、実行ファイル用JSON契約 | 旧submoduleの通常ビルド依存は解除。TAR.Zはmanagedで読書きし、通常アプリに外部圧縮ツールは不要。全形式の詳細レポート・一時ZIPのクリップボード包装、深いcontainerの再梱包／全件展開・画像／provider子比較・複合wrapper作成、CAB/LZH/ISO等の全旧読込み形式、属性・全日時保存、旧ActiveX/DLL ABIは未完了。7zのCRC省略と値0の区別は現行ライブラリの公開APIでは未確認。変換結果を元ファイルへテキスト保存しない |
| シェル統合 | 通常のデスクトップ起動、CLI、macOS `.app` 生成 | Explorer / Finder 拡張、旧コンテキストメニュー、インストーラー登録は未実装 |
| 多言語 | 新 UI は日本語を中心に実装 | 旧翻訳カタログとローカライズ切替、RTL、全ダイアログの同等性は未完了 |
| 詳細フィルター | 大文字小文字、4種の空白処理、空行、行正規表現、ASCII数字・CStyle/CSharp/Python/XMLコメントの除外、順序付き置換、旧`.flt` include/exclude、名前・拡張子・サイズ・日時の条件式、Windows Shell `Prop` 系、フォルダー比較の絶対パス `FullPath` 系、フォルダー `.flt` の `AllEqual(...)` | 比較前処理は原文を保持し、GUI・CLI・フォルダーへ適用。同梱12 `.flt` の読込みを確認。内容検索、`FullPath` 系以外の汎用path属性、残る汎用関数・算術、PCRE固有構文、全旧構文のコメント処理、複数行置換、全表示フィルターは未完了 |
| プロジェクト / レポート | source-generated JSONの全タブ保存・復元、複数組`.WinMerge` XML読込み、HTML / JSONレポート、相対JSON参照とGUI起動時プロジェクト読込み、テキスト・表・JSONの二者／三者HTML、画像の二者／三者全／選択HTML | 順序・選択位置、説明・各readonly・再帰・フォルダー方式・除外・表設定を保持し適用。HTMLは祖先アンカー整列・行内差分・表セル・JSON正規化・画像画素を単体GUI／CLI／包装へ適用。旧filterは対応範囲以外を明示拒否。未対応オプション・プラグイン名は保持・警告し実行しない。旧middleの第三比較ペインと祖先マージの厳密な対応、画像の全旧形式とWeb描画レポート、全旧オプション、結果本文・採用状態の永続化は未完了 |
| Native AOT Windows x64 / ARM64 | 対応 RID と同 OS 発行スクリプト、CI マトリクス | 両アーキテクチャのGitHub runnerで発行・UI自己検証・CLI E2E成功。実測結果は下表 |
| Native AOT macOS x64 / ARM64 | 対応 RID、`.app` / tar、CI マトリクス | Intel / Apple SiliconのGitHub runnerで発行・UI自己検証・CLI E2E成功。署名・公証・公開は実施しない |

WinMerge XML の要素と window-type の対応は `Src/ProjectFile.cpp`、`Src/MergeCmdLineInfo.h` を参照した。全`paths`を順に読み、`left` / `middle` / `right`、説明・readonly、`subfolders`、表設定、`ignore-case`、`ignore-blank-lines`、`ignore-numbers`、`ignore-comment-diff`（互換別名`ignore-comments`）、`white-spaces`を適用する。`white-spaces`の旧モード1は空白量の変更を無視、2は全空白を無視として区別する。コメント構文は左・祖先・右の最初の対応拡張子から選択し、未知の形式では除去しない。旧`filter`はファイルフィルターであり、行正規表現へ置き換えない。旧`compare-method`は0を内容、2をSHA-256、4を日時とサイズへ対応し、それ以外は未適用として元値を保持する。HTTP/HTTPS URL・外国OSの絶対パスは保持し、環境変数を展開して相対パスをプロジェクト所在基準で解決する。

プロジェクト保存は原子的に置換し、読取り専用属性・リンク経由を拒否する。プロジェクトのreadonly指定は編集・差分コピー・フォルダーコピー・全保存経路で入力を保護し、読取り専用フォルダーの子への出力も拒否する。バイナリ別名保存とアーカイブのエントリ保存・再梱包にも祖先・反対側を含む保護を接続する。旧JSONの省略項目は既定値を復元し、明示null・不正enumを拒否する。置換ルールは専用converterで省略時のMatchCase/UseRegex/Enabled=trueを維持する。

コピー処理はルート外のパス、`.` / `..`、既存の symlink / junction を拒否し、ファイルを一時ファイルへ書いた後に置き換える。途中のキャンセルでは、既に完了したファイルや作成したディレクトリが残る。検査とファイル操作の間に別プロセスがパスを差し替える敵対的な状況まで防ぐ OS ハンドル単位の保護は未実装。

全機能移行完了の判定には、上の未完了項目の実装と、Windows / macOS の対象アーキテクチャ上で再現できる検証結果が必要。CI 定義の追加だけで、ビルド・起動・配布の成功を確認したことにはならない。

アーカイブの旧互換範囲は実装根拠で区別する。`Src/7zCommon.cpp:391`の作成UIは7z・ZIP派生形式・TAR/TAR.Z/TAR.GZ/TAR.BZ2/TGZ/TBZ2を提供し、RAR/LZH/CAB作成はコメントアウトされている。`Src/ArchiveDlg.cpp:28`は選択文書・レポート・パッチ・プロジェクトを包装し、`ArchiveSupport/Merge7z/Merge7zCommon.cpp:243`は全件抽出、`Src/7zCommon.cpp:480`は多段アーカイブを再判定する。読込み形式は`ArchiveSupport/Merge7z/Merge7zCommon.cpp:721`以降の登録を参照する。暗号化出力・分割出力は調査した旧作成経路に指定がなく、既存機能の移植漏れとは断定しない。分割読込み・MSI・リンク保存の厳密な旧動作は未確定であり、追加実測が必要。

## TAR.Zの読書き

GUIの自動判定・比較・プレビュー・エントリ保存・全件展開・作成・再梱包、通常アーカイブCLI、標準`archive`・`tar`・`tar-metadata`プロバイダーと比較プロジェクトの包装でTAR.Zを扱う。`tar.Z`／`taz`は同じ圧縮TARとして認識し、外部7z DLL・compress実行ファイルを通常アプリから呼び出さない。標準TARプロバイダーはリンクを追わず型・名前・リンク先を比較し、アーカイブの保存／展開サービスはリンクと特殊entryを拒否する。

読込みは9～16bitのblock／nonblock、出力は16bit block形式。固定39原本には幅の変更・辞書満杯・CLEAR、現行block24件、歴史的nonblock10～16bitの14件と独立literal nonblock9を含む。歴史的v4.1のmaxbits9原本2件にはmodern decoderと幅境界が異なる旧自己互換があり、この2件を正常goldenとして使わない。出典・全SHA・CC0入力・public-domain原文・再生成は[TAR.Z fixture](../tests/Fixtures/Archives/TarZ/README.md)へ集約する。ZにはCRC・宣言長・明示EOFがなく、内側TARの構造・終端と作業上限を検査しても、すべての意味的改変や末尾padding欠損を検出できるわけではない。

限定E2Eと全体E2Eは固定原本の全entryを照合し、writer出力を別buildの公式ncompressとPython標準tarfileで独立に復号・照合する。ローカルではfull7zでも全TAR bytesを照合する。通常.NET build・発行アプリに検証用C/compilerを追加しない。最終Release buildは警告・エラー0、通常版とWindows x64 Native AOTのheadless UIは各10081成功・0失敗。最終Native AOT全体E2Eは156652成功・0失敗・3skip、4880命令のstdout／stderrと実終了コードが一致し、起動失敗・timeoutは0。検証中の259入力と実行ファイルのSHAを照合した。標準TARプロバイダーの復号を背景で実行し、復号中と結果採用直前の実中止ボタンで前回本文と入力を保持する。

SHA `dc20aaade17913cf0d4445386c6386f7523c52e1` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37126824667)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37126824669)は成功した。全構成UI10081成功・0失敗。Windows各156652 E2E成功・0失敗・3skip／4880命令、Mac各156661成功・0失敗・2skip／4883命令で、全実exit・stdout／stderrが一致し、起動失敗・timeoutは0。53固定原本のraw bytes、39原本の全entry、12writer出力の公式decoder復号、GUIの復号中／採用直前の取消、Wipeの最新要求2→8と選択3paneの全BGRA、重ね合わせ・不透明度・レイアウト、全発行物と別writer／readerによる実OSクリップボードを独立照合した。8 ZIP・89522entryのCRC・SHA・サイズとGitHub digestを確認し、全体E2E・発行物は展開せず654472724bytesのZIPとして保持する。証拠は`artifacts/github/37126824667/terminal-summary.json`と`artifacts/local/archive-tar-z-integration/corrected-ci-receipt.json`。通常デスクトップの操作とネイティブ保存ダイアログは未検証であり、全旧アーカイブ形式の移植は未完了。

先行SHA `ba7193aacc34190ad20c5cfd4c6e9033d82366d9` の[GitHub検証](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37125361234)では、親のArchives属性がCRLF原本をLFへ変換し、固定manifestのSHA検査が失敗した。修正ではTAR.Z専用の近い属性へ移して継承した`eol`を解除し、期待SHAと原本bytesを維持した。上記の修正後CIで、実Git blob・runner上の固定原本と全実行経路の一致を確認した。

## 原文区間を保持する WordDiff

旧 `stringdiffs.cpp` の単語 O(NP)・区間生成・文字絞込みを managed Core へ移植し、テキスト比較の変更ブロック全体へ接続した。旧ブロック呼出しの連結と行offsetを参照し、正の区間を各行本文へ投影する。離れた変更を GUI・CLI・単体／包装 HTML で共有する。全ブロックで詳細比較予算を共有し、上限時は変更行全体を強調して件数を表示する。

原本4 translation unitを変更せずMSVCで実行した6,048ケースと全UTF-16文字分類を採取した。CRT C / Windows分類を固定して全OSで使用する。実測で発見したタイトルケース・上付き数字の差を修正した。これは元ソース実行との比較であり、旧GUI配布バイナリの全設定・全字素規則との一致ではない。NULファイルは既存TextDocument契約により拒否する。詳細な出典と失敗条件は [WordDiff E2E](../tests/Fixtures/WordDiffs/README.md) を参照する。

表の変更ブロックはraw WordDiffの共通文字量・best-pair・4096本文上限・三者20写像へ接続した。全ブロック共有予算と不変条件による安全なzipを追加し、CLI三者指定と単語/EOL/予算設定を接続した。WinMergeの句読点区切りモードと区切り文字列も比較設定へ加え、CLIの`--no-separators`／`--separators TEXT`を`--compare`・`--word-diff`・`--table`に追加した。GUIの詳細フィルターから設定でき、比較プロジェクト／workspaceへ保存・復元する。三者の統合区間でも復号一致アンカーを保持し、引用表記の違いだけで無変更側の行が追加・削除になる回帰を修正した。後続のGNU行算法接続は下記に記載する。旧callerのraw CSV一時文書・引用内改行escape、残る設定UI・全フィルター座標、全Unicode/パーサー分岐とプロジェクトの追加設定は引き続き未完了。元関数goldenの出典は[表行対応fixture](../tests/Fixtures/LineAlignment/README.md)。

再開後の通常DLL全E2Eは5,778成功・0失敗・8スキップ、表行対応の限定E2Eは1,948成功・0失敗、headless UIは230成功・0失敗。Windows x64 Native AOTは5,809 E2E成功・0失敗・3スキップ、230 UI成功。SDK10.0.401、buildとAOT発行のCS/IL/MSB警告0。独立レビューの成立した三者一致回帰を修正し、実CLI/HTMLとGUIで照合した。入力・対応JSON・PNG・ログは `artifacts/verification/table-line-alignment` に保持する。

この表行対応を含む `118f76461e58f45e4894512c71d9fcd14ed095b5` は [GitHub run 36820491881](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36820491881) の4構成すべてでNative AOT発行・headless UI・E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36820491875)も成功。Windows x64/ARM64は各5,809 E2E成功・0失敗・3スキップ、Mac Intel/ARM64は各5,818成功・0失敗・2スキップ。UIは全構成230成功、SDK10.0.401、CS/IL/MSB警告0。先行の `af6ee107d` は両Macで失敗し、ARM64成果物で3goldenの改行変換によるSHA不一致を確認した。採取bytesを保持する `.gitattributes` を追加し、ハッシュ検査を維持したまま解消した。JSON・PNG・ログ・発行物は `artifacts/github/36820491881` に保持する。通常デスクトップ起動・Explorer/Finder操作・署名と公証は実測していない。

ローカル Windows x64 の Native AOT では元ソースの有効5,832ケースを全件実行し、区間・終了コード・入力バイト保持が一致した。孤立surrogate216ケースはUTF-8文書経路の直接照合から除外する。Native CLI E2Eは3,862成功・0失敗・3スキップ、実描画UIは224成功。WindowsのUnix権限・Mac大小文字別名・大小文字を区別する包装入力は対象外として理由を保持した。SDK10.0.401、ビルドとAOTのCS/IL/MSB警告0。補助確認を再利用したsource確認に加え、PNGを目視して一致文字のForeground=nullによる不可視も修正した。通常デスクトップ操作・全旧字素規則・旧配布GUIは未実測。成果物は `artifacts/verification/table-worddiff`、`artifacts/e2e/word-diff-native-full`、`artifacts/verification/win-x64` に保持する。

## 通常テキストへ接続した GNU 行算法

原本の `analyze.c` と `io.c` を変更せず実行して279ケースの入力同値クラス・変更scriptを採取し、Coreへ行算法を移植した。開発用CLI `--gnu-line-script` と実プロセスE2Eで、原本が作った同値クラス配列を入力し277ケースのscript一致を確認した。逆順4,095／4,097行の2ケースは共有8M予算の上限に達するため、全入力を一変更ブロックへ退避する契約を確認する。出典・再生成手順は [GNU行fixture](../tests/Fixtures/GnuLines/README.md)、開発用CLIは [開発手順](DEVELOPMENT.md) を参照する。

通常 `TextDiffer` のpatience/HirschbergをGNU行算法へ置換した。原文の共通端と文書状態を反映した比較キーを照合して本文を切り出し、本文・EOL・最終改行の構造キーを共有同値クラスへ分類する。ハッシュの文字走査・衝突比較・配列確保も行算法と同じ予算へ計上し、上限では未解決本文をまとめて変更扱いにする。共通端と全文同値の線形確認は予算0でも行い、全元行とフィルター前の行番号を保持する。GUIは上限時に行対応の省略を表示し、CLIは使用量・退避理由を返す。

原本default 273ケースの元bytesから通常CLIへ接続し271 script完全一致、逆順4095/4097の2件だけ8M予算退避を確認した。通常GNU限定E2Eは3399成功・0失敗、通常DLLの全体E2Eは12367成功・0失敗・8skip。独立レビューでP1/P2なし、新しい文脈の120追加入力も原本script・元行順序・本文・span境界など1442検証すべて成功。headless UIは233成功・0失敗で、反復行の原本対応と4097行の全行保持・上限表示を確認した。SDK10.0.401、最終Rebuildの警告0。成果物は `artifacts/verification/gnu-text` と `artifacts/verification/gnu-review`。最初の限定実行は更新前の検証器が従来の全体検証を走ったため採用せず、全solution Rebuild後に対象ケース選択とコード／程序集SHAを照合して再実行した。

Windows x64 Native AOTは12398 E2E成功・0失敗・3skip、headless UI233成功・0失敗、コンパイラーのCS/IL/MSB警告0。リンク拒否の経路も管理者実行で検証し、実行主体・アプリSHA・Python実体・終了コードを保存した。通常DLLの8skipと区別する。表の初期行対応への後続接続は下記に記載する。旧callerのUTF-8一時文書・引用内改行escape・全Unicodeとフィルター設定の厳密な互換は残る。この通常テキストGNU追加分は `5ea416e1778d3a6b8713c43bdb074c6a054e99df` の [GitHub run 36845332993](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36845332993) で全4構成に成功し、[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36845332985)も成功した。Windows各12398 E2E成功・0失敗・3skip、Mac各12407成功・0失敗・2skip、headless UIは全構成233成功、SDK10.0.401、CS/IL/MSB警告0。表のGNU接続はこのrunに含まれない。

## 復号セル行へ接続した GNU 行算法

表の初期patienceアンカーをGNU行算法へ置換した。比較設定を適用した復号セル値と外側EOLを構造キーにし、引用表記だけの差は同値として扱う。祖先との全体一致組を保持し、右→左は各統合変更区間の範囲だけを比較する。GNU分類・算法と後段のraw WordDiff・投影・採点・三者合成で同じ予算を消費し、最初の退避理由と使用量を返す。旧callerのraw CSV一時文書・引用内改行escape・全フィルター座標との完全互換は未確認。

原本A/B・LFの225ケースから各変更が片側だけの143ケースを選び、全元bytes・SHA・scriptを保持して通常表CLIとHTMLを照合し、143件すべて一致した。両側変更のraw採点はこのscriptから推定せず、既存88件の別oracleを維持する。GNU表限定E2Eは2952成功・0失敗、既存行対応限定E2Eは1948成功・0失敗、通常DLL全体E2Eは15318成功・0失敗・8skip。headless UIは235成功・0失敗で、反復行のGNU対応とHTMLの元行を確認した。Release buildは警告0・エラー0。初回は検証器が原本A/Bを小文字a/bと誤認し入力確認256項目だけ失敗したが、元fixtureと期待mapを維持して修正した。初回と修正後の成果物を別々に保持する。

新しい文脈の独立レビューは82ケース・84CLIプロセスを実行しP1/P2なし。非ゼロで異なる開始座標を持つ三者区間の明示期待値とHTML、GNU後の投影や後続blockでの予算退避、引用表記だけ違う無変更側一致も成功。取消はこの独立レビューでは静的確認のみ。成果物は `artifacts/verification/gnu-table` と `artifacts/verification/gnu-table-review`。Windows x64 Native AOTは15349 E2E成功・0失敗・3skip、235 UI成功、CS/IL/MSB警告0。管理者実行・Python実体・アプリSHA・未コミット状態とソース指紋を保存した。この表接続を含む `cbb100bab6aca1bdee42794793c5c40cfcb5a32f` の [GitHub run 36848623334](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36848623334) は全4構成で成功し、[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36848623256)も成功した。Windows各15349 E2E成功・0失敗・3skip、Mac各15358成功・0失敗・2skip、headless UIは全構成235成功、SDK10.0.401、CS/IL/MSB警告0。全8成果物を取得し、発行manifestの64ファイルのサイズとSHA-256は全件一致した。成果物と検証器を `artifacts/github/36848623334` に保持する。
## 画像のドラッグ操作

縦／横ワイプを追加した通常版はReleaseビルド警告0・エラー0、限定E2E1,506成功・0失敗・0skip、全headless UI9,697成功・0失敗、独立Python PNG133成功・0失敗。原本40ケース400状態、読取り専用二／三者の実pointer、TIFFページの境界縮小、取消・古い候補・失敗・capturelost、押下中に内部保存APIへ捕捉した単体／包装HTMLの選択強調と原画PNGを照合した。通常のrelease後GUI保存は原本と同じくワイプ解除後の表示であり、この内部API検証を通常保存入口の実証とは扱わない。選択強調が包装HTMLで通常色へ戻る実不具合は、entry別の一時表示snapshotで修正した。独立レビューで見つかった成功再描画による最新要求の喪失と、領域診断の45,068,506bytes出力は修正前に実再現し、最新要求保持と32MiB超過時の終了2・stdout空へ修正した。追跡レビューに未解決指摘はない。証拠は`artifacts/local/image-wipe-product/final-summary.json`。最終通常版の全体E2Eは151,642成功・0失敗・10skip、4,333コマンドの実exit・stdout／stderrが一致、timeout0。Windows x64 Native AOTも発行・UI9,697成功、全体E2E151,687成功・0失敗・3skip、4,348コマンドの実exit・全出力が一致、timeout0。固定333ソース・実行DLL／Native EXEの前後SHA、発行12ファイルのSHA・サイズ、原本alpha146状態316面・316PNGとレイアウトを照合した。Native自己検証は約95秒で完走した。自動restoreでCore／Providersの2lockはRID情報を含む既存HEADの内容へ戻り、4lockすべてのHEAD正規化一致と発行後維持を別記録した。証拠は`artifacts/local/image-wipe-integration/managed-final-proof.json`と`artifacts/local/image-wipe-native/final/final-summary.json`。この変更の4RID CIは次段の固定SHA 3b6f41bで完走し、下記の旧4モードSHAの結果と区別する。

ワイプを含むSHA `3b6f41b75e2eff6abc24f14c5fc2a4308b5867c4` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37102142914)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37102142897)は成功した。全構成UI9,697成功・0失敗。Windows各151,687 E2E成功・0失敗・3skip／4,348コマンド、Mac各151,696成功・0失敗・2skip／4,351コマンドで、実exit・stdout／stderr全一致、timeout0。各構成のワイプ40ケース400状態、None6ケース、GUI133 PNG（解除後baseline40を含む）と50面全BGRA、alpha原本146状態316面・316 PNG、レイアウト、実OSクリップボードの別writer／readerを照合した。Mac自己検証はARM64約195秒、x64約185秒で600秒枠内に完走し、最後の表検索と原子的な最終進捗を確認した。発行manifest・Mac tar・ライセンス、8 ZIP・81,426entryのCRC／SHA／サイズを照合し、全面展開せず約642MBで保持する。証拠は`artifacts/github/37102142914/terminal-summary.json`。これはワイプの固定コミット検証であり、その後の未コミットoverlay核のソース／DLLを証拠には使っていない。通常デスクトップのOS pointer capture・ネイティブ保存ダイアログは未検証。

原本WinIMerge v1.0.54の6値（NONE=0、MOVE=1、OFFSET=2、VERTICAL_WIPE=3、HORIZONTAL_WIPE=4、RECTANGLE_SELECT=5）のうち、左右／三者表示で全6値を移植した。既定はMOVEで、共有アプリ設定として全画像タブへ保存・反映する。倍率・位置等の既存プロジェクト設定とは独立する。支持矩形の負座標preview、ゼロ方向切捨てのrelease差、全paneの位置正規化、readonly・多ページ、既存矩形／浮動／Resize優先、Escape中のoffset capture保持、原画・Undo履歴と位置の分離を維持する。表示移動・thumbは絶対位置、wheel・line/pageは各paneへのdeltaとしてSideBySideの範囲内で同期する。

ワイプ画素核は固定原本C++の40ケース400状態・全BGRAを2回採取し、独立検証1,442件が成功した。[wipe fixture](../tests/Fixtures/ImageWipes/README.md)は約38KBのgzipで保持する。静的重ね合わせは無改変 `RefreshImages` の49ケース・全BGRAを2回採取し、独立検証1,483件が成功した。[overlay fixture](../tests/Fixtures/ImageOverlays/README.md)は約17KBのgzipで保持する。ワイプはApp・GUI・CLI・HTMLへ接続した。この原本採取時点では静的重ね合わせのNone原本6件を製品経路へ照合し、Alpha／XORの残り43件を後段の原本互換移植に保持した。時刻依存の[overlay fixture](../tests/Fixtures/ImageTemporalOverlays/README.md)は77ケース83状態・全BGRAを2回採取し、親の独立検証も1,459件成功した。ANIMのblendごとの時刻読取り・中央の中間byte切捨て・blink／highlight／wipe順を保存する。動的animation／blinkの製品接続は後続のPhase3で確認した。原本OS操作の実測は未完了。

その後のoverlay Stage1では、None／XOR／Alpha／ANIMとblinkの純画素核を開発用`--image-overlay-script`へ接続した。入力は原画・設定・個別epoch queueだけで期待値を渡さず、静的49ケース49状態と時刻依存77ケース83状態を照合する。限定E2Eは3,587成功・0失敗、親の独立照合は9,641成功・1,303frame全BGRA一致。新しい文脈のレビューも独立96ケース・870項目が成功し、成立した指摘はない。固定337ソース・4lock・App／E2E DLLの前後SHAを照合した全体通常E2Eは155,228成功・0失敗・10skip／4,390コマンド、timeout0。既存UI9,697成功・0失敗と独立PNG133件も維持する。診断内の8MiB入力・32MiB全出力・256M共有作業量、予算境界・取消・後半不正入力とstdout空を確認した。初回のhash大小表記・pair gridの符号差は全画素差0と区別して是正し、失敗入力と出力も保持する。証拠は`artifacts/local/image-overlay-product/stage1-summary.json`と`artifacts/local/image-overlay-integration/stage1-final-proof.json`。この段階の新overlayは通常GUI・設定・HTML・包装・timerへ未接続で、Native AOT／変更後4RIDは未実行。上記3b6のWipe CIとは別の未コミット検証である。

overlay Phase2では通常GUIへNone／XOR／Alpha／ANIM・差分点滅と共有設定を接続し、Release警告0・エラー0、全体headless UI9,893成功・0失敗（新overlay192項目）、限定画素核3,587成功・0失敗を確認した。親の独立照合は原本1,303frameの全BGRAとPNG181枚が一致し、検証時の135 C#ソース・5lock・2原本と実行物を含む252行のSHAも一致した。通常／最小ウィンドウ・操作欄64／80pxの36条件で実controlの到達を確認し、周期の4桁表示を修正した。4000×4000の三者Noneは既存描画64Mで受理し、Alpha追加描画256,001,287は時計読取り前に拒否して確定表示を保持する。完了済み8出力rootの9,944ファイルを全SHA・サイズ照合し、約436MBから74MBへ圧縮保持した。証拠は`artifacts/local/image-overlay-integration/parent-phase2-summary.json`と`artifacts/retained/image-overlay-integration/phase2`。この断面の全体E2E・Native AOT・HTML／包装接続は未実行である。続くPhase3で、設定取消後の候補採用と、offset／rotation更新後の描画失敗時に確定overlayが失われる2種類の不具合を実headlessで再現した。取消1ケースとoffset／rotation失敗2ケースをAlpha／ANIMでそれぞれ観測し、全6ケースの前後PNG36枚の全BGRAも独立照合した。修正とHTML／包装／通常CLIへの接続は進行中で、移植完了とは判定しない。

Phase3ではframe／overlayを同時採用し、取消／失敗時にも確定表示を保持する。選択組のtyped capture、全ページのtuple別時刻採取と選択clamp継承、全画像entryの包装SHA照合、保存後のsnapshot更新、通常CLIへ接続した。独立レビューで指摘された再比較／設定復元時の不透明度scopeと点滅checkboxの不一致は、変更前の実UIで再現して修正し、最終6観測すべてでcontrolと採用設定の一致を確認した。最終Releaseは警告0・エラー0、全体headless UI10,059成功・0失敗、全体通常E2E155,760成功・0失敗・10skip、4,502実コマンド・timeout0。親の独立照合で表示／report170・既存wipe133・前後36の計339 PNG、旧6故障の表示保持、保存後包装5entryと原画／表示6 PNGが一致した。通常CLIの原本HTML30ケース150 PNG・領域25ケース63 BGRAと、全体E2Eのstdout／stderr9,004本も一致した。ソース・実行DLL142行の検証前後SHA、runtime495ファイル、原本／canonical14ファイルと5lockを凍結して照合済み。証拠は`artifacts/local/image-overlay-product/phase3-summary.json`、`phase3-freeze-source-proof.json`と`artifacts/local/image-overlay-integration/parent-phase3-final-full-cli.json`、各PNG proof。10skipはWindowsのUnix／Mac／case alias制約とsymlink作成権限による。Nativeの実測は次段の記録を参照する。通常OS pointer・ネイティブ保存ダイアログは未検証であり、通常版の成功から推定しない。

同じ最終ソースのWindows x64 Native AOTは発行警告0・エラー0、headless UI10,059成功・0失敗、全体E2E155,805成功・0失敗・3skip、4,517実コマンド・timeout0で完走した。既存開発シェルからの発行と管理者プロセスを確認し、通常版で権限不足だったsymlink保護も成功した。残る3skipはUnixFileMode、Macの実APFS、case collisionのホスト制約である。発行manifestの全12ファイルとmanifest自身、171ビルド入力の前後SHA、原本1,303frame・31,056 BGRA bytes、GUI339 PNGとscope6観測、包装5entry、通常CLIの150 PNG・63 BGRA、実stdout／stderr9,034本を独立照合した。自己検証は約113秒、全体E2Eは約218秒。証拠は`artifacts/local/image-overlay-integration/native-final-summary.json`と各proof。固定commit `21824be` の[GitHub run 37118662889](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37118662889)ではWin x64／ARM64とMac ARM64が成功し、[同commitのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37118662810)も成功した。Mac Intelはワイプの選択変更後の描画検証で147.6秒・終了2となり、全体E2E・OS clipboardは未実行。失敗した複合条件の各実値は当時記録していない。旧要求だけの完了を待てる経路をWindowsで二段の候補gateにより再現し、旧要求完了・位置2・選択0・captureあり、別の最新要求が未完了というJSONを保持した。自己検証を最新の`CurrentWipeOperation`の完了まで待つよう修正し、待機中の表示保持と完了後の位置8・全BGRAを検査する。修正後のMac IntelはSHA dc20aaのCIでUI10081成功・0失敗となり、最新要求2→8と選択3paneの全BGRAも一致した（TAR.Z節の実測を参照）。成功3RIDの発行物・全命令・BGRA／PNG・別writer／reader OS clipboardと、失敗RIDの例外・直前PNGは`artifacts/github/37118662889`へ独立照合して保持した。実OSクリップボード、通常デスクトップのpointerとネイティブ保存ダイアログはローカルで未実行。

ワイプ単独移植時点では、縦／横ワイプはUIで選択し共有設定へ保存・読込みできる。重ね合わせと画素差表示では具体的利用条件を表示して操作モード欄を無効にし、確定modeは保持する。ワイプの一時表示と選択強調を保存開始時のsnapshotとして単体／全ページ／包装HTMLへ反映し、原画と分類・履歴は保持する。共有256M予算へ複製と入替えの作業を加算し、多ページは描画前に累積上限を検査する。両表示のドラッグ／同期、原本overlayとの同等性、旧全体設定の一括移行は後段の残作業である。原本の通常PNG保存はraw原画、レポートPNGはactive wipeを含みガイドを含まないため、プロジェクトの`imageSettings`へ一時wipe状態を混ぜない。

実アプリの`HeadlessImageDragChecks`はpointer/key・2/3paneのoffset preview/release、モード変更、取消境界、readonly TIFF、同期viewport、設定失敗をPNG/NDJSON/JSONへ保存する。固定offset原本12ケース43状態は既存`HeadlessImageOffsetChecks`と限定／全体E2Eで引き続き照合する。通常デスクトップのOS pointer capture・設定ディレクトリへの実保存と、headlessの実イベント／fixtureパス検証は区別する。最新検証の成否は親が保存するphase evidenceを正本とし、本節では未実行のNative/4RID検証を成功扱いにしない。

今回の4モード・入力範囲・同期スクロールと画像HTMLの予算事前確認を含む通常版は、Releaseビルド警告0・エラー0、headless UI9,416成功・0失敗。ドラッグ121件に、旧レビュー回帰36件、画像外余白の押下・確定8件、共有メニュー3件を含む。先の3指摘と追加2指摘を実操作で再現して修正し、独立レビューに未解決指摘はない。証拠は `artifacts/local/image-drag-modes/blank-viewport/final-boundaries` と `postfix-review/independent-final-review.json`。通常版の全体E2Eは150,137成功・0失敗・10skip、終了0で、固定ソース320ファイルと実行DLLのSHAが実行前後で一致した。証拠は `artifacts/local/image-drag-integration/managed-full-proof.json`。Windows x64 Native AOTもUI9,416成功・0失敗、全体E2E150,182成功・0失敗・3skip、発行／検証の終了0。固定ソース・実行ファイル・harnessの前後SHAと4,316コマンドの実exit・全stdout／stderrを独立照合した。発行物全12ファイル、alpha原本146状態316面の全BGRA・316 PNG・9条件のレイアウトも一致。全ページHTMLの上限拒否は53ms、包装拒否65msで、入力・既存出力を保持する。証拠は `artifacts/local/image-drag-integration/native/native-full-independent-proof.json` と `native-final-proof.json`。この変更SHA `283e8959febd90a98ff38391a62d3df9398f3feb` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37096525522)は、Windows両構成とMac x64が成功し、Mac ARM64の自己検証180秒timeoutによりrun全体は失敗した。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37096525516)は成功。成功3構成はUI各9,416成功・0失敗、全体E2EはWindows各150,182成功・0失敗・3skip、Mac x64は150,191成功・0失敗・2skip。全コマンドの実exit・stdout/stderr、発行物全SHA・サイズ、alpha原本146状態316面・316 PNG、9条件のレイアウト、実OSクリップボードの別writer/readerを照合した。Mac x64の以前の全ページHTML拒否timeoutは59ms・exit2で解消した。Mac ARM64は画像ドラッグを通過し最後の表検索の入力生成まで到達したが、最終UI集計はなく全体E2E・OSクリップボードは未実行。実停止命令は未観測で、前回の同構成の自己検証周辺も約178秒だった。検証全体の時間枠と進捗観測を変更し、次の変更SHAで完走を確認する。証拠は `artifacts/github/37096525522/terminal-summary.json` と `failure-osx-arm64.json`。全7 ZIP・61,520entryのSHA・サイズを照合し、約472MBで保持、全面展開はしない。通常OSのpointer capture・ネイティブ保存ダイアログはheadlessと区別する。

## 画像のフレーム比較

先頭だけを表示していた画像ビューを、GIF/WebPの選択フレーム比較へ接続した。左右それぞれの番号指定・前後移動、最大現在番号を起点に短い側を末尾へ留める同期移動を追加した。GUIと新しい `--image` CLIは同じ画素復号・閾値判定を使い、CLI既定では全同番号フレームと枚数差、明示指定では選択した左右の組だけを比較する。入力スナップショット、上限、原文保持、取消時の旧表示保持と古い完了の破棄を維持する。操作と上限は [画像の契約](IMAGE-VIEWER.md) を参照する。

自作の26画像・30期待フレームは入力SHA-256と全画素の独立期待値を保持する。70ケースの画像限定E2Eは500項目成功・0失敗で、静止PNG、GIFの前フレーム合成・背景復元・前状態復元・透明更新、lossless WebP、後続だけの差分、枚数差、閾値・alpha・寸法、入力・枚数・画素・比較キャンバス・復号量の各上限と引数拒否を確認した。新しい文脈の独立レビューは31プロセスのうち20固定期待ケースを全件成功、残る11件は切詰め入力の観測として記録し、未解決P1/P2なし。WebPは別のPillow経路でも固定RGBA期待値とCLIの全画素SHAを照合した。Pillowは再生成・独立検証用で、アプリやCIの依存へ追加しない。

通常DLL全体E2Eは15817成功・0失敗・8skip、Windows x64 Native AOT全体E2Eは15848成功・0失敗・3skip、headless UIは247成功・0失敗。初回のUIで枚数が異なる同期移動を拒否していた問題を修正し、同じ入力の成功を確認した。その後、PNGで窮屈だった番号・閾値入力欄を90から140へ広げ、通常DLL全体E2Eは同じ15817成功・0失敗・8skip、Windows x64 Native AOT全体は15848成功・0失敗・3skip、UIは両方247成功・0失敗を再確認した。最終Release buildとAOT発行のCS/IL/MSB警告0、管理者実行・Python実体・アプリSHA・未コミット状態とソース指紋も保存した。コード・入力・出力・終了コード・ログ・JSON・PNGを `artifacts/verification/image-frames`、独立レビューを `artifacts/verification/image-frames-review` に保持する。

フレーム対応のSHA `4f47ba714ba0af49e2e216efd6b282aa38ec0dc1` は [GitHub run 36854029148](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36854029148) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36854029156) が成功した。Windows両構成は15848成功・0失敗・3skip、Mac両構成は15857成功・0失敗・2skip、4構成とも247 UI成功・SDK10.0.401・コンパイラー警告0。取得した8成果物のmanifest列挙64ファイルのSHA・サイズはすべて一致した。証拠は `E:/DiffBeacon-artifacts/github/36854029148`。Cドライブ容量不足による途中取得とMac x64の部分ファイル復旧は、移動記録 `artifacts/verification/image-frames/ci-artifact-relocation.json` と取得先の `osx-x64-recovery-ledger.json` に保持する。このSHAには後述の画像HTMLは含まれない。

デコーダーの成功は完全な画素の取得を示し、コンテナー全体の構造検証とは区別する。GIFの末尾やPNGのIEND等が欠けても成功する実測を記録した。この段階ではAPNGが未移植だったが、下記の追加実装で対応した。この段階での通常デスクトップのネイティブ操作、TIFFの全ページ、全WebP設定、色管理・EXIF方向、ページ設定の保存は未検証または未移植。画像マージとHTMLの追加範囲は後述する。

入力欄調整後の初回AOT自己検証は画像項目の後、アーカイブ展開の一時ディレクトリ移動でアクセス拒否になった。親ACLには実行ユーザーのFullControlがあり、同じnative実行ファイルで別出力への247項目と、その後の同じ発行スクリプトの247項目は成功した。原因は未確定で、失敗結果と再実行記録を保持し、恒久的な解消とは扱わない。

## APNGの合成フレーム比較

静止画の先頭だけへ退避していたAPNGを、合成済みの各フレームのGUI・CLI・単体／包装HTMLへ接続した。App内のmanaged処理が制御チャンク・CRC・sequence・位置・枚数を検証し、個別PNGの復号には既存Skia経路を使う。SOURCE/OVERとNONE/BACKGROUND/PREVIOUS、アニメーションから除外された既定画像、透明SOURCEの色成分を扱い、共通の入力・画素・復号量・キャンセル予算を維持する。先頭fcTLがacTLより前にある有効入力を拒否する不具合も実アプリで再現して修正した。[fixture](../tests/Fixtures/Images/Apng/README.md)の全原画期待値はデコーダーから逆算しない。

ローカルWindows x64では限定E2E836成功、全体managed E2E27177成功・0失敗・10skip、Native AOT全体27222成功・0失敗・3skip、headless UIは双方927成功・0失敗。GUI原画40件はmanaged/native全bytes一致し、3枚のAPNG画面も目視した。Release buildとAOT発行は警告・エラー0、96ソースファイルと発行manifest10件のSHA・サイズも一致した。証拠は `E:/DiffBeacon-artifacts/local/apng-port/final-order-verification.json` と入力・ログ・JSON・PNGに保持する。

APNG最終SHA `892f362d1327e85268a9486991ef302b84a27edc` の[四RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36912489623)はWindows両構成とMac ARM64が成功し、Intel Macだけ発行後のUI自己検証が60秒で打ち切られた。Intel MacでもAPNGの全40 raw BGRA・期待値manifest・3画面を取得できたが、後続の表検証途中で終了し、全UIと全体E2Eの成功には数えない。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36912489412)は成功。発行時の自己検証上限を180秒へ変更した後のIntel Mac再検証は未完了。実測fixtureは8bit RGBAであり、palette・16bit・Adam7・ICC・EXIFの対応をこの結果から推定しない。遅延・繰返しの再生、複数フレーム編集、元APNG保存、ページ設定の永続化は未完了。通常デスクトップ起動とOS固有ダイアログもheadless UIとは区別する。

## TIFFの主ページ比較

先頭だけを扱っていたTIFFを、classic TIFF／BigTIFF・両byte orderの主IFDごとのGUI・CLI・単体／包装HTMLへ接続した。ページごとの寸法とstrip／tileの展開予算を保持し、全主IFDの参照範囲・循環・巨大宣言を復号前に拒否する。AppだけにTiffLibrary／JpegLibraryとMITライセンスを追加し、旧C++／FreeImage／submoduleへの実行時依存を増やさない。

[自作31入力](../tests/Fixtures/Images/Tiff/README.md)は正常17・拒否14。正常入力のBGRAはliteral期待値であり、デコーダーから逆算しない。managed限定E2Eは1079成功・0失敗・0skip、headless UIは1089成功・0失敗（TIFF162項目）。ページ寸法・全画素、圧縮と色の代表構成、全／選択・三者・短い側の反復、HTML原画と包装展開、取消・古い完了・入力と既存出力の保持を照合した。scanのないJPEGが旧実装で成功していた不具合を実CLIで再現し、拒否へ修正した。旧JPEGのtable数と参照範囲も確保前に検査する。証拠は `E:/DiffBeacon-artifacts/local/tiff-port/managed-limited-rebuilt` と `managed-ui-final`。

ローカルWindows x64の全体managed E2Eは28255成功・0失敗・10skip、Native AOTは28300成功・0失敗・3skip、headless UIは双方1089成功・0失敗。TIFF原画46件とobservationsはmanaged／nativeで全bytes一致し、3画面も確認した。Release buildとAOT発行は警告・エラー0、発行manifest12件のSHA・サイズも一致。Native全体E2Eは管理者実行とPython指定を記録する。証拠は `E:/DiffBeacon-artifacts/local/tiff-port`。四RID CIとIntel Macの180秒上限は未検証。正常旧JPEG6、全JPEG entropy、LZW dictionary幅切替／predictor、planar、SubIFD、色管理と方向補正、旧FreeImageの全構成との互換性は未検証。複数ページ編集・元TIFF保存・旧全体設定の永続化も未完了。詳細な上限と対応構成は[画像の契約](IMAGE-VIEWER.md)へ集約する。

## 画像プロジェクトの表示設定

画像プロジェクトを開き直すと先頭・閾値0へ戻り、CLI project-copyで設定が消失する経路を修正した。新しいJSONの`imageSettings`がページ、閾値、倍率、重ね合わせ不透明度、強調、全／選択レポートと表示方式を保持する。GUIで復号を完了した状態を保存し、CLI・包装レポートも同じ設定を使う。既存の設定省略プロジェクトは従来の既定値を維持し、CLIの明示指定を優先する。包装の相対参照と保存設定を展開・再読込み後も保持する。

変更前の実CLIでは設定が消失し、選択組のHTMLが2ページになった。追加の限定E2Eは614成功・0失敗。最終Release buildは警告0・エラー0、全体E2Eはmanaged 28868成功・0失敗・10skip、Windows x64 Native AOT 28913成功・0失敗・3skip、headless UIは両方式1103成功・0失敗。2／3入力の全値・既定値・境界・CLI上書き、独立PNG復号による全BGRA、包装展開、不正設定・実ページ範囲外の拒否と入力／既存出力の属性保持、再比較・workspace復元・取消・古い完了を確認した。記録は `E:/DiffBeacon-artifacts/local/image-project-settings`。差分閾値360.62445840513925がGUIで丸められる不具合も実測して修正し、ページ切替時と最小正double値を含め判定値の保持を確認した。発行manifest12ファイルのサイズ・SHAと実行中の製品ソース857ファイルを照合した。全体E2Eの証拠は全entry SHA照合済みZIPとして保持し、GUIのJSON・PNGと再現記録は展開した状態で残す。この設定変更の四RID CIは未実行。

旧`ImgMergeFrm.cpp`のLoadOptions／SaveOptionsは倍率・閾値等をOptionsMgrの全体設定へ保存する。今回のJSONプロジェクト保存は、その全体設定の移行完了を意味しない。画像ドラッグの4モードだけは独立した共有アプリ設定へ追加したが、旧全体設定の一括移行と全表示オプション、未保存原画・Undo履歴・選択領域のセッション保存は未完了。利用手順と上限は[画像の契約](IMAGE-VIEWER.md)を参照する。

## 画像の回転・反転

GUI・通常CLI・HTML・包装へ表示変換を接続する。水平反転・垂直反転・反時計回り回転の順と、原画保存・共有履歴を保つ。静止画のコピーは表示座標で行い、確定した画素を逆写像して原画へ戻す。回転だけではdirty・Undoを変更せず、読取り専用画像と複数ページの表示にも適用できる。プロジェクトには各入力の角度・水平／垂直反転・ブロックサイズを保存する。位置合わせ・挿入削除・矩形編集・多ページ編集・元形式保存は未完了。

WinIMerge v1.0.54の公式DLLから32表示ケース・256コピーケース、計2,816状態を採取した。alpha0のhidden RGBも含む全BGRA、寸法、領域、共有Undo／Redo、保存点とPNG原画を固定期待値とし、実アプリ別プロセスへ照合する。GUIは代表14操作列と実ボタン・ページ切替・取消・古い完了を操作する。強調画面の共通canvasへ追加する透明paddingと、PNG保存・HTML原画の寸法は別に照合する。正本・SHA・再生成は[変換fixture](../tests/Fixtures/ImageTransforms/README.md)、操作は[画像の契約](IMAGE-VIEWER.md#回転反転)を参照する。最終Release buildとWindows x64 Native AOT発行は警告0・エラー0。限定変換E2Eは46391成功・0失敗・0skip、設定限定は614成功・0失敗。全体は通常版75258成功・0失敗・10skip、Native版75303成功・0失敗・3skip、headless UIは両版2429成功・0失敗。代表14ケースの124状態・346画面画素、各28原画PNGとGUI HTML4PNGを独立Python復号へ照合し、両版の観測JSONは全bytes一致した。発行12ファイルのSHA／サイズと248ソースの不変を確認し、画面PNG各4枚も目視した。証拠は `E:/DiffBeacon-artifacts/local/image-transforms/final-local-verification.json` と容量索引の照合済みZIP。旧935状態の比較では新しい表示設定だけをidentityと検査し、元の全property照合を維持する。通常desktopとOSの保存ダイアログ、offset、矩形編集、多ページ編集はこの検証に含めない。今回の変更のMac・Windows ARM64はGitHubで検証する。

画像設定の前段SHA `6d21a11607224d6e49bba717f3f8148e7610b533` は[4 RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36925383370)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36925383199)が成功。Windows E2E各28913成功・0失敗・3skip、Mac各28922成功・0失敗・2skip、headless UI各1103成功・0失敗。Native76ファイル・Mac tar26ファイルのSHA／サイズ、MITライセンス12コピーを取得後に照合した。これは今回の回転・反転変更を含まない前段の検証であり、通常desktop・OS clipboardの実測とも区別する。

## 原本の画像差分領域処理

WinIMerge v1.0.54の原本関数を無改変で採取し、二／三者のblock比較・8近傍の連結領域・行順ID・block矩形・LeftOnly/MiddleOnly/RightOnly/Conflict分類をC#へ移植した。`ImageRegionDiffer` と開発用 `--image-regions` が対象。固定164件のraw BGRAをPNGへ包装し、実アプリの復号から全pair grid・領域ID・矩形・分類・個数を原本と完全一致照合する。通常buildへC++、FreeImage、submoduleを追加しない。原本・GPL・入力・期待値・SHA・再生成手順は[fixture](../tests/Fixtures/ImageRegions/README.md)、診断コマンドは[画像の契約](IMAGE-VIEWER.md#原本の差分領域処理の照合)に集約する。

原本はBGRAユークリッド距離と01∨21候補を用いる。後続の通常GUI・`--image`・HTMLへの接続は次節に記載する。位置合わせ、変換、挿入削除、ベクター、OCR、画像コピー／保存・マージと全旧復号形式は引き続き未完了。取消チェックは存在するが、OSシグナルによるこの核の中断は未実測。

原本採取と再採取は164件・bytes一致・523検証成功。通常DLLの核限定E2Eは1902成功・0失敗・0skip、全体E2Eは19029成功・0失敗・9skip。Windows x64 Native AOT全体E2Eは19069成功・0失敗・3skip、headless UIは260成功・0失敗、Release／AOTコンパイラー警告0。Native版は管理者実行でリンク拒否も確認し、入力・出力・終了コード・原本全grid・ソース／程序集／発行物のSHAを `artifacts/verification/image-regions/Verify-LocalEvidence.ps1` の96項目で照合した。採取証拠は `artifacts/verification/image-regions-reference`。以前の原本調査担当による追加読取り確認では成立P1/P2なし、新しい文脈の独立レビューとは区別する。この核の `5eae173c2817632e22468b50775f25d7ce148d12` は [GitHub run 36865234830](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36865234830) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36865234885) に成功した。Windows両構成は19069成功・0失敗・3skip、Mac両構成は19078成功・0失敗・2skip、UIは全構成260成功。8成果物のmanifest64ファイルのSHA・サイズが全一致、SDK10.0.401・コンパイラー警告0。証拠は `E:/DiffBeacon-artifacts/github/36865234830` と `artifacts/verification/image-regions/ci-36865234830-summary.json`。旧maintenance 459aのwin-x64自己検証60秒timeoutは新runで再発していないが、原因・恒久対策は未確定。

## 通常画像へ接続した原本強調と三者分類

原本MarkDiff/GetDiffColorFromPositionをC#へ移植し、二者・三者の通常GUI、`--image`、単体HTML、包装HTMLへ同じ領域分類と強調を接続した。中央入力は保存形式の `BasePath` を第三画像へマッピングし、祖先として扱わない。最大成分差をBGRAユークリッド距離へ置換し、同期移動は対象ページがない入力の直前選択を保持する。全ページCLI／HTMLは短い入力の最後のページを繰り返し、枚数差を別に検出する。元画素は共通canvasへ複製して保持し、強調・選択色・透明度・paneごとのonly除外を原本どおり計算する。通常の全ページ描画量にも256M上限を設け、細長い入力同士の共通canvas増幅を拒否する。

無改変原本から72件の強調BGRAを採取・再採取し、bytes一致・745検証成功。golden SHAは `853A08102656CD1F726E39647D98AF47CF4C8918C7F3EB051D078FEA87BC057A`。[強調fixture](../tests/Fixtures/ImageHighlight/README.md)が出典・ライセンス・限定範囲を記録する。新しい依存パッケージ、C++、FreeImage、submoduleは通常buildへ追加していない。

ローカルRelease buildは警告0。原本強調限定E2Eは2956成功・0失敗、旧画像HTMLの原画／左右mask検証は1325成功・0失敗・リンク1skip、描画予算を含むフレーム限定は517成功・0失敗。headless UIは469成功・0失敗、alpha0.7の24件を実Bitmapの全BGRAへ照合し、領域・競合移動、強調解除、HTML、三者の個別／同期選択・取消・古い完了破棄、原本保持を確認した。PNGの目視で三者の選択色とページ位置を確認した。最初のUI失敗は期待SHAが大文字、HTMLが小文字なのに大小文字を区別した検証器の不備であり、全画素照合は通っていた。検証器を修正して再実行し、初回証拠も保持した。成果物は `artifacts/verification/image-three-way`。通常DLLの全体E2Eは22007成功・0失敗・9skip。Windows x64 Native AOTは22047 E2E成功・0失敗・3skip、469 UI成功・0失敗、コンパイラー警告0。Native実行は管理者・Python3.14・実行ファイルSHAを記録し、リンク拒否も検証した。`Verify-LocalEvidence.ps1` の244項目でソース・程序集・原本SHA・全体結果・24件66paneのGUI画素・発行manifest10ファイルを照合し全成功。この統合の `d650faccaccebb23eb802ff83ed630e5e448de30` は [GitHub run 36874016002](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36874016002) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36874015978) が成功した。Windows各22047 E2E成功・0失敗・3skip、Mac各22056成功・0失敗・2skip、全構成469 UI成功。各原本72件198描画frameとGUI24件66paneのSHAが一致し、normal8・region164・保護と描画上限拒否も確認した。8成果物のmanifest64ファイルのSHA・サイズ全一致、SDK10.0.401・コンパイラー／CodeQL警告0。Node非推奨annotation4件は別に記録する。証拠は `E:/DiffBeacon-artifacts/github/36874016002` と `artifacts/verification/image-three-way/ci-36874016002-summary.json`で、この限定結果だけで画像の全機能移植完了とは判断しない。

## 静止画像の領域コピーと共有履歴

原本の選択／全領域コピー、三者auto、全pane共有Undo／Redo、dirty／savepointを `ImageEditSession` へ移植し、開発用 `--image-copy` と透明RGBを保つPNG別名保存へ接続した。無改変原本143ケース・935状態は採取と再採取がbytes一致、5142検証成功。golden SHAと出典、zero-filled BGRA拡張・raw pasteのadapter境界は[コピーfixture](../tests/Fixtures/ImageCopy/README.md)、操作・処理上限・出力保護は[画像の契約](IMAGE-VIEWER.md#静止画像コピー核の診断)を参照する。FreeImageの新規画素／paste、元BPP／palette／format、アニメーション／多ページ保存は完全互換を確認していない。GUI編集・編集済みrawのHTML接続は次節の実装へ進めた。

2026-10-02のRelease buildは警告0・エラー0。限定E2Eは4336成功・0失敗・リンク1skip、通常DLL全体は26342成功・0失敗・10skip。原本全状態と5種類のPNG保存を独立BCL復号・再読込みへ照合し、Undo直後savepoint・既存出力属性・入力／script／readonly保護とJSON／作業／履歴容量拒否を実測した。Windows x64 Native AOT全体は26387成功・0失敗・3skip、管理者実行でリンク拒否も成功、headless UI回帰469成功・0失敗。sourceと通常程序集は検証中不変で、原本再照合・発行manifest10ファイル・実行環境を `VerifyEvidence.py` の574項目へ照合し全成功。証拠は `artifacts/verification/image-copy-cli` と `E:/DiffBeacon-artifacts/local/image-copy-cli-d650-20261002`。最初の管理者runner起動は終了記録がなく、診断ログと捕捉範囲を追加した再実行で管理者と終了0を確認した。最初の原因は未確定で、製品コードを変更した復旧ではない。GUI編集、履歴128件の厳密な境界、処理中OSシグナル取消、通常デスクトップはこの単位で実測していない。4RIDのGitHub検証は以下の記録に示す。

コードコミット `33a67d1798beb1794b922b345dd4cdc44d06386e` は [GitHub .NET CI 36884448976](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36884448976) と [CodeQL 36884449144](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36884449144) が成功した。

| RID | CLI E2E成功 | 失敗 / スキップ | headless UI成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 26387 | 0 / 3 | 469 | 発行・実行成功 |
| `win-arm64` | 26387 | 0 / 3 | 469 | 発行・実行成功 |
| `osx-x64` | 26396 | 0 / 2 | 469 | 発行・実行成功 |
| `osx-arm64` | 26396 | 0 / 2 | 469 | 発行・実行成功 |

全構成SDK10.0.401、コンパイラーのCS/IL/MSB警告0。原本143ケース・935状態、5種類のPNGをCIのBCL復号と別のPython標準ライブラリで全BGRA照合し、Windows属性・Mac Unix 0600、Undo直後savepoint、入力／script／readonly／実リンク拒否、JSON・累積作業・履歴容量の拒否理由と既存出力保持も成功した。既存強調72ケース・領域164ケースとUI469項目も維持した。Windowsの3skipはUnix権限・Mac大小文字別名・包装の大小文字衝突、Macの2skipは包装の大小文字／NFC衝突入力を同一ファイルへ解決する環境条件で、画像コピーのリンク拒否は4構成すべて実行した。

取得した8成果物のmanifest64ファイルのSHA・サイズが全一致。証拠は `E:/DiffBeacon-artifacts/github/36884448976`、集計は `artifacts/verification/image-copy-cli/ci-36884448976-summary.json` に保持した。upload-artifactのNode非推奨annotation4件はコンパイラー警告と区別する。全watch・download・verifierは終了0。GUIの画像編集接続と通常デスクトップ／ネイティブ保存ダイアログの実測は、この結果に含まれない。

## 静止画像GUIの編集・PNG／TIFF保存

二者の両方向・三者の6方向の選択／全領域コピー、競合以外の自動コピー、共有Undo／Redoを通常画像ビューへ接続した。保存は原画PNGまたは単ページTIFFの別名保存とし、成功した側だけのパス・比較済みパス・snapshot・保存点を更新する。閾値再比較は編集を保持し、HTMLは未保存の原画も反映する。未保存画像の包装は拒否する。仮sessionと描画をまとめて採用し、取消・古い完了では直前の状態を保持する。保存中の編集・再比較・破棄を拒否し、全タブの入力・フィルター・workspace・readonly・リンクを置換直前にも確認する。

2026-10-02の最終Release buildは警告0・エラー0。通常DLL全体E2Eは26342成功・0失敗・10skip、通常DLLとWindows x64 Native AOTのheadless UIは各812成功・0失敗。無改変コピー原本の代表12操作列・66状態で全原画・領域・履歴・dirtyを照合し、両実行形式の観測JSONはSHAも一致した。PNGは独立BCL復号と再読込み、HTMLは現在の原画と採取後の編集分離を確認した。最小850×550でも画像viewportとスクロール後の保存操作を確認した。最終Native AOT全体E2Eは26387成功・0失敗・3skip、管理者実行のリンク拒否も成功した。証拠は `E:/DiffBeacon-artifacts/local/image-copy-gui-33a67`、過去の途中結果は `artifacts/retention/index.json` から参照する。Native発行は `Publish.ps1 -SkipVerification` とし、UI自己検証をEドライブへ別途出力した。GUI接続のMac／ARM CIと通常デスクトップ操作はこの記録時点では未実測。元形式／多ページ編集保存、FreeImageの完全互換と残りの画像操作は引き続き未完了。

GUI接続のコミット `6734dc1101190279fc2ca3a445e0699c8a74efb4` の [GitHub run 36900262981](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36900262981) はWindows両構成が成功し、Mac両構成はNative AOT発行後のUI自己検証で失敗した。Windowsは各E2E26387成功・0失敗・3skip、UI812成功。Macは最小850×550で画像viewportの高さが0となり、309成功後に停止したため全体E2Eは未実行。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36900262982) は成功した。取得した検証成果物4個は `E:/DiffBeacon-artifacts/github/36900262981` に保持し、発行物のSHA照合はこの失敗runでは未実測。画像モードの共通設定欄の最大高さを比較ペインの30%から20%へ縮め、同じ最小サイズ・viewport下限の検証を維持して再検証する。

この高さ修正後のローカルRelease buildは警告0・エラー0、通常DLLの全体E2Eは26342成功・0失敗・10skip、Windows x64 Native AOTは26387成功・0失敗・3skip。両実行形式のUI812項目が成功し、850×550の三者viewportは各61 px、代表12操作列・66状態の観測SHAも一致した。ソース72ファイルと発行manifest10ファイルのSHA・サイズを照合した。証拠は `E:/DiffBeacon-artifacts/local/image-copy-gui-mac-layout` に保持する。Macでの修正確認は新しいコミットのCIを必要とし、このローカル結果に含めない。

## 画像の全フレームHTML

画像HTMLのコミット `675e61255d1cfbcbb64c29763bcee40821e533de` は [GitHub run 36859703730](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36859703730) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36859703759) が成功した。Windows両構成は17168成功・0失敗・3skip、Mac両構成は17177成功・0失敗・2skip、4構成とも260 UI成功・SDK10.0.401・コンパイラー警告0。画像HTML固有項目は各構成196成功。取得した8成果物のmanifest列挙64ファイルのSHA・サイズがすべて一致した。証拠は `E:/DiffBeacon-artifacts/github/36859703730`。全体取得時のデバイスエラーはWindows成果物の個別再取得で解消し、最初の失敗ログも保持する。これはCI実行失敗とは区別する。upload-artifactのNode非推奨annotation4件はコンパイラー警告に含めない。以下のローカル記録と同じコードを4RIDで検証した結果である。

このSHAは後から追加した原本ブロック・三者領域処理を含まない。通常デスクトップのOS操作はheadless UIと区別する。

以下はSHA 675e時点の二者HTML検証記録であり、後続の三者／原本強調接続は上記を参照する。二者画像の全同番号フレームまたは表示中の選択組を、左右・ピクセル差分PNGを埋め込む自己完結HTMLへ出力した。GUIは表示中の原本スナップショットと確定閾値を使い、選択位置を変えない。CLIの `--report-project` は画像にだけフレーム組と閾値を指定でき、包装は同梱原本の確定内容から同じ生成器を呼ぶ。base64増幅を含むUTF-8本文32 MiBと、既存の入力・画素・枚数・復号量・包装合計上限を維持する。この時点では三者画像の詳細を明示拒否していた。操作は [画像の契約](IMAGE-VIEWER.md) に集約する。

単体とZIP包装の埋込みPNGを独立したBCL PNG復号で全画素・SHA・CRCまで照合する。既存26画像・30フレームの固定期待値、後続差分・枚数差・寸法・透明合成・閾値、選択組、説明escape、包装展開と相対プロジェクト再読込み、32 MiB超過と既存出力保持を確認する。新しい文脈の独立レビューでは別に作ったPNG/GIFと実CLI30呼出しの243項目成功・0失敗・リンク1skipを確認した。

レビューで、GUIが入力プロジェクトのパスをレポート・包装の保護へ渡さず、プロジェクト自身を上書きできるP2を検出した。実GUIで両方の上書きと元SHAの変更を記録してから、ロード・保存したパスを保持して共通ガードへ渡すよう是正した。修正後は画像・テキストのレポートと包装を拒否し、元SHAを保持する。別名保存後の通常更新は成功する。修正成立の追加確認は同じレビュー担当の静的・証跡照合であり、新しい独立実行とは区別する。

最終コードの通常DLL全体E2Eは17128成功・0失敗・9skip、Windows x64 Native AOT全体E2Eは17168成功・0失敗・3skip、headless UIは両方260成功・0失敗。通常版の9skipには権限不足で作れなかった画像レポートのリンク検証1件を含み、Native版は管理者実行で入力・出力・親ディレクトリのリンク拒否も成功した。最終Release buildとAOT発行のコンパイラー警告0。全発行ファイルのSHA・サイズ、GUI保護前後のSHA、ソース・程序集の不変を `artifacts/verification/image-reports/Verify-LocalEvidence.ps1` で照合した。証拠は同ディレクトリ、独立レビューは `artifacts/verification/image-reports-review` に保持する。追加したGUI検証で非同期読込みを同期的に待って停止した実行は失敗記録として保持し、UIイベントを処理しながら待つ修正後の260成功を採用する。通常デスクトップ、生成中のOSシグナル、一回のネイティブ復号・PNG符号化内の中断、包装合計1 GiBの厳密な境界、全旧画像形式・三者画像・画像マージは未検証または未移植。この画像HTMLを含むMac・Windows ARM64のSHA単位検証はGitHubで行う。

## 表のセル内検索・置換

同セル内の前後一致と折返し、固定した文字選択範囲、一件・選択ペイン内の全置換と一括Undoを追加した。decoded値で一致を計画し、既存の `ReplaceCell` を通して必要なセルだけを再引用する。capture展開を制限し、未反映セル編集・古い選択・計画中の対象変更を拒否する。原文反映後に再比較を取消した場合も原文とモデルを復元する。長い一覧セルは512文字・高さ64までのプレビューとし、編集・検索・保存・HTMLの全文を保持する。詳細は[表の操作](TABLE-EDITOR.md)を参照する。

通常DLLの全E2Eは2954成功・0失敗・8スキップ、headless UIは220成功・0失敗。ローカルWindows x64 Native AOTは2985 E2E成功・0失敗・3スキップ、220 UI成功。最終の単独ビルド・Native AOTコンパイラー警告0を確認した。UTF16BE BOM付きの一括置換保存を別Native AOTプロセスでレポート化し、Python標準CSVの独立期待値・全18 HTMLセルと一致した。入力・出力・失敗時のJSON・PNG・再現契約・ログは `artifacts/verification/table-search`、`artifacts/verification/table-search-readonly-final`、`artifacts/e2e/table-search-readonly-final`、`artifacts/e2e/table-search-native-win-x64` に保持する。初回は10万文字セルの全文描画でUI比較がtimeoutし、描画を制限した後に同入力の成功を確認した。実デスクトップ・旧raw buffer横断検索・PCRE/Rx完全互換・矩形文字編集は未検証または未移植。新規文脈のレビュー枠はagent thread limitで未取得、既存担当の補助確認を独立レビューと称さない。

初回の[GitHub run 36800893396](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36800893396)（`1a20d97470444fc22d46f2d2cf7581985bc46528`）はWindows両構成で成功したが、両MacはNative AOT後の表セル描画のUI確認で失敗した。同SHAの[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36800893386)は成功。操作欄を縦スクロールし、ビューの半分以下に収めて一覧領域を確保した。UI検証では描画を確定し、700px高のウィンドウで行へ移動後のセル座標が実viewport内にあることも確認する。旧操作欄では一覧高0、修正後58/セルY23をローカルで観測した。単なるTextBlockの存在確認だけでは、この表示不具合を検出できない。失敗と修正後のPNG/JSONは同契約ディレクトリに保持する。

追加の実画面検証で、原文反映後に読取り専用へ変更して中止すると、通常writerが原文復元も拒否することを確認した。反映した原文がそのままである場合に限る復元callbackを使い、原文とモデル、元ファイル、変更後の読取り専用状態を保持する。通常DLLとWindows x64 Native AOTの220 UI成功にこのケースを含める。失敗と修正後の記録は `artifacts/verification/table-search-readonly-before`・`table-search-readonly-final`、契約・ログは `artifacts/verification/table-search` に保持する。

Macの700px画面では一覧高21・セルY15で文字の下端が切れており、セルが少しでもviewport内にある旧確認では見逃していた。共通の設定・操作欄もスクロール可能にし、960×700でセルの高さ全体が一覧内にある確認へ変更した。最新のローカル通常DLLとWindows x64 Native AOTは220 UI成功、通常DLLの全E2Eは2954成功・0失敗・8スキップ。Windowsで一覧高63・セルY28・文字高17を観測した。結果は `artifacts/verification/table-search-full-viewport-final`・`artifacts/e2e/table-search-full-viewport-final`、ビルド・発行ログは同契約ディレクトリに保持する。

最新の表示修正を含むコード `67ef483608befbd7fc9825535a3ef31cf7f040d8` は、[GitHub run 36805372990](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805372990)の4構成すべてでNative AOT発行・headless UI・CLI E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805373087)も成功。SDKは全構成10.0.401、CS/IL/MSBコンパイラー警告0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 2985 | 0 / 3 | 220 | 発行・実行成功 |
| `win-arm64` | 2985 | 0 / 3 | 220 | 発行・実行成功 |
| `osx-x64` | 2994 | 0 / 2 | 220 | 発行・実行成功 |
| `osx-arm64` | 2994 | 0 / 2 | 220 | 発行・実行成功 |

4構成とも960×700でセルの文字高全体が一覧内にあることを確認し、Mac ARM64のPNGでも欠けずに表示されることを目視確認した。全構成の入力・出力・JSON・PNG・manifest・ログ・run情報・集計を `artifacts/github/36805372990` に保持する。集計の再現手順は `artifacts/verification/table-search/Verify-GitHubResults.ps1`。各Macの `.app` と実行権限を保持するtarを同runのArtifactsから取得できる。UIはheadless操作・描画であり、通常デスクトップの実測とは区別する。署名・公証・公開は実施していない。

## 表の行合わせ・セル編集

原文区間付き文書と共有行対応モデルを導入し、GUI・`--table`・単体/包装HTMLの判定を統一した。完全一致アンカーの間で上限付き類似対応、三者では祖先対応と左右挿入対応を統合する。セル編集は一つの原文区間だけを変更し、Undo/Redo、readonly・祖先・ghost・古い座標の拒否、既存保存へ接続した。検索は前後・折返し・case・regex・word・ペイン指定、画面は32列ページと行仮想化。同セル内検索、固定文字範囲、一件/選択ペイン全置換と一括Undoに加え、CSV原文バッファーの区切り・引用符・改行をまたぐ検索、raw offsetでの範囲固定、構造置換と一括Undoに対応した。先頭行を列見出しとして固定表示し、旧 `AutoFitColumn(-1)` に相当する全ペイン・全行からの列幅調整も追加した。ページ表示は幅計算を限定せず、列幅はペイン間・ページ間で共有し、長い値は画面幅相当で上限を設ける。旧方式の全スコア・PCRE/Rx互換・矩形文字編集は未完了。[表の契約](TABLE-EDITOR.md)を参照する。

通常DLLの全E2Eは2954成功・0失敗・8スキップ、headless UIは139成功・0失敗。ローカルWindows x64 Native AOTの発行と139 UI成功を確認した。入力・出力・行対応JSON・実テキスト入力/ボタン操作・UTF16BE BOM保存後のbytes・PNGを`artifacts/e2e/table-full`・`artifacts/verification/table-repaired`へ保存する。疎な表は各側2万実セル/1万列/10001行で、表示コントロール数を制限し、従来2億座標相当の検索を実セルのみで完了した（ローカル通常DLLの1実測25 ms、比較用benchmarkではない）。

ローカルWindows x64 Native AOT全E2Eは2985成功・0失敗・3スキップ。[GitHub .NET CI 36796867483](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867483)は実装SHA `d14a9a9932c771a53e7bb8840b9dc4a84e32ea46` の4構成で成功。Windows x64/ARM64は各2985成功・0失敗・3スキップ、Mac Intel/ARM64は各2994成功・0失敗・2スキップ、UIは各139成功。SDK10.0.401、CS/IL/MSB警告0をログ・manifestで確認。同SHAの[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867472)もワークフロー成功。全構成の入力・出力・PNG・JSON・ログを`artifacts/github/36796867483`へ保存した。WindowsのUnixモードとMac固有経路、ファイルシステムのcase/NFC alias作成可否によるスキップを集計へ保持する。GUIでUTF16BE BOM付き保存した表を別のNative AOTプロセスで再読込みし、Python標準CSVパーサーで三者の全セル値・順序を照合して一致した（`external-reload-oracle.json`）。新規文脈のレビューはagent thread limitで未実施。既存担当の補足確認でクリック例外・疎な表示/検索の膨張を修正し、記録を`artifacts/verification/table-next`に保持する。headless操作と通常デスクトップの実測は区別する。

2026-10-11の列幅自動調整変更後、Release buildは警告0・エラー0、`--table-headers-only`は18成功・0失敗でした。全体headless UIも14,063成功・0失敗で、先頭行見出しと全行・全ペインを計測する列幅調整、32列ページ間の幅共有、viewport上限、原文と比較結果の保持を確認した。全体E2Eは165,055成功・0失敗・5スキップ。スキップはWindowsで利用できないUnix/macOS機能、実OS操作、case alias作成可否などの環境差による。E2EはPython 3.14.7と固定Z decoderを明示して実行した。入力・出力・PNG・JSON・ログは `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\e2e-20261011c\full-e2e` に保存している。検証用リンクを含むため、このE2E出力は圧縮・清掃せず保持する。

## 実行した検証

2026-10-11 JST、WinMergeのText行ブックマークを移植した。左右各ペインで通常マークと番号付き10枠を使い、一覧、前後の周回移動、個別／一括消去を行える。行挿入でマークを移動し、削除された行のマークを除去する。限定UI自己検証は18成功・0失敗で、マーク操作が原文を変えないことも確認した。全体managed UIとWindows x64 Native AOT自己検証は各14,086成功・0失敗。AOT manifestの自己検証時間は797.697秒。最新コードでのmanaged全体E2Eは165,117成功・0失敗・5環境スキップ。Windows Native AOT発行と自己検証は確認したが、最新コードのAOT E2E、macOS・他RID・4 RID CIは未実施。

2026-10-11 JST、WinMergeの比較設定に単語境界の区切り方式を接続した。`ComparisonOptions`へ `BreakOnSeparators` と `WordBreakSeparators` を追加し、WinMergeの既定区切り設定を `TextDiffer` の行内spanと `WordLineAlignment` のraw採点へ渡す。`--compare` と `--table` で `--no-separators`／`--separators TEXT` を選べ、HTMLには両設定を記録する。カンマ既定・独自のピリオド・区切り無効の各spanと表対応を検証した。既存の直接 `WordDiffer` 呼び出しと6,048件の旧golden既定は維持した。GUIの詳細フィルターで設定でき、比較プロジェクト／workspace保存・復元とGUIレポートへの反映を実測した。設定文字列は最大256 UTF-16単位でU+00FF以下だけに適用し、それより大きい文字は固定分類を使う。WinMergeの全Unicode境界互換は未確認。

2026-10-11 JST、WinMergeのCSV原文バッファー検索・置換を表UIへ追加した。「CSV原文を検索」では区切り・囲み引用符・escaped quote・複数セル／レコードにまたがる一致をraw offsetと長さで示し、該当セルの復号値へ範囲を投影する。一致置換・選択ペイン内全置換は原文区間へ適用して再解析し、行構造の変化も共有Undo/Redoへまとめる。引用符不整合・改行禁止・読取り専用・取消・検索条件変更を拒否し、再比較取消後は書いた原文を戻してモデル・入力ファイル・履歴を保つ。Release buildは警告0・エラー0、`--table-raw-search-only` は5件成功・0失敗・0スキップ、raw限定headless UIは56成功・0失敗で、書込み後の再比較取消によるrollbackも個別に確認した。最新managed全体E2Eは8,213コマンド・165,123件成功・失敗0・環境スキップ5、全体managed UIは14,149件成功・失敗0。Windows x64 Native AOTの発行時自己検証は14,152件成功・失敗0（843.516秒）で、publish manifestの12ファイルのSHA-256・サイズ、元package lock 5件、repo bin／obj不変も確認した。最新コードのAOT全体E2E、通常デスクトップの実操作（Computer UseがGetCursorPos access deniedで起動拒否）、macOS・他RID・4 RID CIは未実施。

前候補の検証記録：managed全体UIとWindows x64 Native AOT発行時自己検証は各14,063成功・0失敗。managed全体E2Eと同Native AOT exeの全体E2Eは各165,091成功・0失敗・5環境スキップ。5件の環境スキップは、Windowsで公開されないUnixFileMode、APFS case aliasとmacOS FIFO/socket、headless対象外の実デスクトップpointer/dialog・広範な三者互換、case-collisionを作れないfilesystemによる。AOT限定E2Eは行対応1,974成功、WordDiff888成功（各0失敗・0skip）。AOT出力は `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\aw1011-48d\full-aot\assertions.json` に記録し、34,323 files／2,009,800,746 bytes・テスト生成reparse point28件を保持した。この候補の結果は履歴として残し、最新コードのAOT全体E2Eへ読み替えない。

2026-10-01 JST、WordDiffと変更ブロックの行内強調を移植したコード `760900e5a4efec5c5175f446036b3eda91c03e2c` は、[GitHub run 36811104557](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36811104557) の4構成でNative AOT発行・headless UI・CLI E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36811104538) も成功。SDKは全構成10.0.401、CS/IL/MSBコンパイラー警告0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 3862 | 0 / 3 | 224 | 発行・起動成功 |
| `win-arm64` | 3862 | 0 / 3 | 224 | 発行・起動成功 |
| `osx-x64` | 3871 | 0 / 2 | 224 | 発行・起動成功 |
| `osx-arm64` | 3871 | 0 / 2 | 224 | 発行・起動成功 |

今回追加した135代表goldenの終了コード・全区間・文字コード・入力保持、fallback・引数拒否・NUL拒否、ブロック投影・共有予算・HTMLを全構成で実行した。Windows x64では別途、有効5,832旧ソースケースをすべてNative AOTと照合した。全構成のJSON・入力・出力・PNG・manifest・ログ・run情報は `artifacts/github/36811104557`、全件照合は `artifacts/verification/table-worddiff/native-golden-profile-all` に保持した。両MacのWordDiff PNGを実際に目視し、一致文字と離れた変更の表示を確認した。

WindowsのスキップはUnix権限・Mac大小文字別名・包装の大小文字衝突用入力、Macの2スキップは包装の大小文字/NFC衝突用入力を別ファイルとして作れない実ファイルシステムによるもの。リンク拒否経路はローカルの管理者プロセスとGitHubの全対象で実行した。8成果物を確認し、両Macの `.app` とtarを含む `DiffBeacon-osx-x64` / `DiffBeacon-osx-arm64` は同runから取得できる。署名・公証・公開・通常デスクトップの手動操作は実施していない。runnerのupload-artifact Node20廃止通知とMac ARMの容量通知はコンパイラー警告と区別する。

以下は前工程の検証記録。

2026-10-01 JST、テキスト・表・JSONの二者／三者共通HTMLレポート、単体プロジェクトCLI、GUI中止・保存保護を追加したコード`c005d377c4748657b262a91c85570406b7724de6`を[GitHub Actions run 36793389828](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389828)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、CS/IL/MSBコンパイラー警告0件。[CodeQL workflow](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389800)も同コードで成功した。これはworkflow完了の記録であり、全機能の同等性を証明するものではない。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 2070 | 0 / 3 | 111 | 発行・実行成功 |
| `win-arm64` | 2070 | 0 / 3 | 111 | 発行・実行成功 |
| `osx-x64` | 2079 | 0 / 2 | 111 | 発行・実行成功 |
| `osx-arm64` | 2079 | 0 / 2 | 111 | 発行・実行成功 |

単体HTMLと包装内HTMLを実CLI・独立BCL ZIP読込みで検証した。三者では祖先アンカー、異なる位置への挿入、連続空行、片側削除、空祖先を含む全側の本文・元行番号の順序と完全保持を確認した。表は各セル・行列座標、欠落と空セル、引用符・引用内改行、比較設定を検証し、Python標準CSVでも行数・列数と生成HTMLグリッドを照合した。JSONは正規化後の内容・祖先だけの変更・キー順の同等性を確認した。

未選択タブのreadonlyフォルダー配下を含む全入力・フィルター・プロジェクトの保存保護、readonly属性・リンク・不正解析・32 MiB上限による既存出力保持も確認した。GUIは現在の未保存本文と三者・表・JSONを出力し、実際の中止ボタンで未完了レポートを取り消せる。取消テストは処理未完了と既存出力保持の実測であり、描画の特定ループ内や原子的保存の途中で取消した証拠ではない。Windowsの3スキップ、Macの2スキップは前の包装工程と同じOS・ファイルシステム固有項目で、各検証JSONへ理由を保存した。

ローカル通常DLLは2039成功・0失敗・8スキップ、リンク作成権限のあるWindows x64 Native AOTは2070成功・0失敗・3スキップ、画面111成功。初回の限定検証では修正前の期待値を取り込んだテストアセンブリとGUIの入力固定値で失敗したため、原本の直前バイト列を確認する検証へ直し、全体を再ビルドした。フォルダーモードはファイルパスが指定されていても包装対象から明示拒否する。失敗記録は`artifacts/e2e/reports-managed`・`artifacts/verification/reports-managed`、最終結果は`artifacts/e2e/reports-full`・`artifacts/e2e/reports-native-win-x64`・`artifacts/verification/reports-contract`へ保持する。

4構成の入力・出力・JSON・PNG・manifest・ログ・集計を`artifacts/github/36793389828`に保持する。UIはheadless描画・操作で、HTML自体のブラウザー描画やネイティブファイル選択は未実測。agent thread limitのため新規文脈の独立レビューは未実施で、既存担当の補足確認と成立指摘の修正記録を同契約ディレクトリに残す。このrun時点の表レポートは行列座標の対応。後続の表エディター移植は下記に区別する。

以下は比較文書の包装追加時点の記録。

2026-10-01 JST、比較文書とHTMLレポート・パッチ・プロジェクトの包装を追加したコード`ef1e44df438e602560eb0578ff5316d9c0bf7546`と検証修正`9f5e92d0f6c3465c0b64c244a716d5c00c59a408`を[GitHub Actions run 36790193652](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36790193652)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む発行物も同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 1317 | 0 / 3 | 103 | 発行・実行成功 |
| `win-arm64` | 1317 | 0 / 3 | 103 | 発行・実行成功 |
| `osx-x64` | 1326 | 0 / 2 | 103 | 発行・実行成功 |
| `osx-arm64` | 1326 | 0 / 2 | 103 | 発行・実行成功 |

包装後のZIPを独立したBCLデコーダーで読込み、全内容・相対プロジェクト参照・設定・選択順を確認した。実アプリでの展開、プロジェクト再読込み、パッチ適用、7z/TAR.GZ/TAR.BZ2、URL参照、全未選択入力を含む上書き保護、リンク・未保存編集・比較前のパス変更の拒否、処理開始後のキャンセルと一時出力除去も検証した。Windowsの3スキップはUnix権限・Mac大小文字別名・包装の大小文字衝突、Macの2スキップは包装の大小文字・NFC衝突である。各ファイルシステムが二つの入力名を同一ファイルへ解決する衝突ケースは作成できず、理由を記録した。WindowsではNFC衝突拒否、MacではUnix権限・既存大小文字別名の保護を実測した。

ローカル通常DLLは1294成功・0失敗・7スキップ、リンク作成権限のあるWindows x64 Native AOTは1317成功・0失敗・3スキップ、UIは103成功。容量限定の別実測では、入力合計1 GiB超過と生成レポート合計1 GiB超過の2ケースが既存出力を保持して拒否され、一時出力も残らなかった。ピークWorking Setはそれぞれ約16.4 MiB・120.2 MiBで、入力生成手順・ハッシュ・計測・清掃記録を`artifacts/e2e/packaging-capacity-probes/20260930T231514228-ba030c35`に保存した。この容量実測はローカルWindowsのみで、4構成共通の計測ではない。

全構成の入力・出力・JSON・PNG・run情報・ログ・集計を`artifacts/github/36790193652`に保持し、Mac ARM64の包装ダイアログと復元した比較画面を目視確認した。クリップボードのファイル形式はheadlessバックエンドで検証したもので、通常デスクトップでの持続性・ネイティブファイル選択は未実測。新規独立レビュアーはagent thread limitで起動できず、既存担当の読み取り専用確認と成立指摘の解消記録を`artifacts/verification/workspace-managed/packaging-review-after.json`に保持する。

先行run `36789620215`のWindows両構成はリンク保存先の表記比較だけ失敗した。アプリはリンク出力を拒否し、元ファイルとリンクを保持していたが、検証側が作成APIの正規化前後のスラッシュ表記を同一と仮定していた。作成直後の実リンク値との比較へ修正し、原本のバイト列保持も確認した。失敗結果・ログ・`link-spelling-proof.json`を保持し、最終runで同じ経路の成功を確認した。

以下は複数比較プロジェクト追加時点の記録。

2026-10-01 JST、複数比較プロジェクト・読取り専用・表の引用符設定を追加したコードコミット`a17ef62c24d134d6eb0d25ad7501c4d176e0c4ad`を[GitHub Actions run 36786270273](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36786270273)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む発行物を同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 1063 | 0 / 2 | 93 | 発行・起動成功 |
| `win-arm64` | 1063 | 0 / 2 | 93 | 発行・起動成功 |
| `osx-x64` | 1075 | 0 / 0 | 93 | 発行・起動成功 |
| `osx-arm64` | 1075 | 0 / 0 | 93 | 発行・起動成功 |

全タブの順序・選択位置・比較設定の保存／復元、旧単一JSONと複数`.WinMerge` XML、相対パス・URL・未対応設定の保持、256件／4 MiB上限、不正入力・DTD・リンクの拒否、既存ファイル属性とMac Unix権限の保持を実アプリ経路で検証した。省略された設定と置換ルールの既定値がsource-generated JSON読込みで消える問題を、setterを持つ保存DTOと明示的なAOT対応コンバーターで解消した。

GUIでは全タブ復元・未保存確認のキャンセル・設定ダイアログ、表の区切り／引用符／引用内改行、readonly編集・コピー・テキスト／バイナリ／アーカイブの保存先保護を確認した。保存された外部ツールIDは自動登録・実行しない。Windowsの2スキップはUnix権限・Mac大小文字別名だけで、両Mac構成では成功した。全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36786270273`に保持し、Mac ARM64の表・readonly・旧プロジェクト画面を目視確認した。

ローカルWindows x64 Native AOTは1063 CLI＋93 UI成功。通常DLLは1048成功・0失敗・5スキップ（リンク作成権限とOS固有項目）。プロジェクト限定E2Eは127成功・0失敗・1スキップで、リンク拒否も権限のあるNative AOT実行で成功した。新規独立レビュアーの起動はagent thread limitで拒否されたため、新しい文脈での独立レビューは未実施。既存の別機能担当による読み取り専用確認と成立指摘の再現・修正記録を`artifacts/e2e/workspace-review`に保持する。検証側の配列比較が既定値補完を誤って拒否したケースも修正し、最終E2Eで成功を確認した。UI検証はheadless描画・操作であり、通常のネイティブウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

以下はZIP派生・TAR系追加時点の記録。

2026-10-01 JST、ZIP派生・TAR/TAR.GZ/TAR.BZ2の作成・再梱包と全件展開を追加したコードコミット`96bdc45ab8f687fb2d2c33263229d5eca6109269`を[GitHub Actions run 36781192763](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36781192763)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む`DiffBeacon-<RID>`発行物を同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 931 | 0 / 2 | 69 | 発行・実行成功 |
| `win-arm64` | 931 | 0 / 2 | 69 | 発行・実行成功 |
| `osx-x64` | 943 | 0 / 0 | 69 | 発行・実行成功 |
| `osx-arm64` | 943 | 0 / 0 | 69 | 発行・実行成功 |

新規形式の日本語名・空ディレクトリ・内容ハッシュ、暗号化入力の全件展開、既存出力保持、TARチェックサム・リンク・親子衝突、gzipのCRC・欠落フッター・連結メンバー、深い暗黙親パスの上限、GUIで展開開始後のキャンセルと一時出力除去を検証した。Windowsの2スキップはUnix権限・Mac大小文字別名だけで、両Mac構成では成功した。Python 3.13.15の独立デコーダーでもTAR.BZ2の日本語名と全内容を確認した。PythonはE2E専用で発行アプリの依存ではない。各発行物のSharpCompressライセンス同梱も確認した。

全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36781192763`に保存し、Mac ARM64のTAR.GZ比較画面・暗号化ZIPのパスワードマスクを目視確認した。ローカルWindows x64 Native AOTは931 CLI＋69 UI成功、通常DLLは922成功・0失敗・4スキップ（リンク権限とOS固有項目）。UI自己検証は同じ保存先での連続実行も成功した。UI検証はheadless描画・操作で、通常のネイティブウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

先行run `36779758667`はWindows ARM64のOS標準tarによる独立検証で日本語名の失敗と作成プロセスのクラッシュが発生した。同runのアプリ経路とUI 69件は成功し、生成TAR.BZ2を別環境のPythonで読めることを確認したため、独立検証を全構成同じPython標準ライブラリに統一した。失敗の入力・ログと別デコーダーの結果は`artifacts/github/36779758667`に保持している。

以下は暗号化・solidアーカイブ読込みと7z作成追加時点の記録。

2026-10-01 JST、アーカイブ比較・暗号化/solid読込み・非暗号化7z作成/再梱包を追加したコードコミット`a27989e328d3850d3bfcdbc682a7b7e0395835b0`を[GitHub Actions run 36775611278](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36775611278)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 687 | 0 / 2 | 64 | 発行・起動成功 |
| `win-arm64` | 687 | 0 / 2 | 64 | 発行・起動成功 |
| `osx-x64` | 699 | 0 / 0 | 64 | 発行・起動成功 |
| `osx-arm64` | 699 | 0 / 0 | 64 | 発行・起動成功 |

Windowsの2スキップはUnix権限・macOS大小文字別名の検証で、両Mac構成では実際に成功した。暗号化7z/RAR4/RAR5/WinZip AES、solid、CRC値0へ破損させたZIPの拒否、原本・既存出力保持、フォルダー内への再作成、17 MiBエントリのプレビュー・保存を検証した。各発行物へSharpCompressのMITライセンスを同梱した。全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36775611278`に保存し、WindowsとMac ARM64のアーカイブ画面を目視確認した。ローカルWindows x64 Native AOTも687 CLI＋64 UI成功、通常DLLは678成功・0失敗・4スキップ（リンク権限とOS固有項目）。UIはheadless描画・操作で、通常ウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

以下はマージ結果セッション・詳細フィルター追加時点の記録。

2026-10-01 JST、マージ結果セッション・詳細フィルターを追加したコードコミット`1d1e54d3b3db999a95bacdb71506f9bb4d2e8747`を[GitHub Actions run 36770239518](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36770239518)で実行し、4ジョブすべて成功した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 552 | 0 / 0 | 55 | 発行・起動成功 |
| `win-arm64` | 552 | 0 / 0 | 55 | 発行・起動成功 |
| `osx-x64` | 554 | 0 / 0 | 55 | 発行・起動成功 |
| `osx-arm64` | 554 | 0 / 0 | 55 | 発行・起動成功 |

同runの全構成のJSON・入力・出力・PNG、run情報とログを`artifacts/github/36770239518`に保存した。ローカルWindows x64のNative AOTでも552 CLI＋55 UI成功。通常DLLでのE2Eは543成功・0失敗・2スキップで、リンク作成権限がないプロセスのスキップ理由を残し、権限のあるNative AOT実行でその経路も確認した。独立レビューの成立指摘は修正後に再現ケース・連続編集・Undo/Redoで解消を確認した。今回のUI操作もheadless描画であり、ネイティブウィンドウ・ファイル選択・シェル拡張の手動検証は含まない。

以下は初回の基盤移植時点の記録。

2026-10-01 JST、コードコミット`adde5eccfd2dea91a3c485eceb03e5335ca72bfc`を[GitHub Actions run 36759572632](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36759572632)で実行し、4ジョブすべて成功した。SDKはすべて`10.0.401`。通常ビルド・AOT発行のコンパイラー警告は0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 446 | 0 / 0 | 17 | 発行・起動成功 |
| `win-arm64` | 446 | 0 / 0 | 17 | 発行・起動成功 |
| `osx-x64` | 448 | 0 / 0 | 17 | 発行・起動成功 |
| `osx-arm64` | 448 | 0 / 0 | 17 | 発行・起動成功 |

runには`DiffBeacon-<RID>`発行物と`verification-<RID>`の入力・ログ・JSON・PNGを保存している。UI検証はSkiaで実際のアプリ画面を描画するheadless実行であり、Explorer/Finder起動・ネイティブファイル選択・署名・公証を実測したものではない。Windows x64 / Mac ARM64の4ペインPNGを目視し、文字とレイアウトを確認した。ローカルのWindows x64でも同じ446 E2E＋17 UIが成功した。

## 既存ブランチと旧ソース

| 参照 | 判断 |
| --- | --- |
| `origin/feature/4pane-merge` (`74c2ae1`) | 結果ペイン・競合移動の仕様を移植。130コミットすべてを新C#へ移したわけではないため参照を保持 |
| `origin/Release/v2.16.58.2` (`8221725`) | 最終バイト・フィルター・比較中のソート・隠し行などの回帰条件を参照するため保持 |
| `feature/convert-toolbar-icons-to-svg` / `fix/manual-localization-parameters` / `per-monitor-dpi-aware` | 旧MFC/DPI/DocBook固有。新Avaloniaの通常経路で使わず、ローカルのremote-tracking参照を削除。GitHub上のブランチは削除していない |

旧`Src`、プラグイン、翻訳、シェル拡張・インストーラーのC++実装は未移植項目の根拠として保持する。通常の入口は`DiffBeacon.slnx`だけで、旧ルートsolution・ビルドcmd・`.gitmodules`・13トップレベルsubmoduleの作業ディレクトリは除去した。Git内部のsubmoduleオブジェクトは履歴復旧用に残る。残した旧実装は対応表の未完了項目が移植・対象OSで検証できた時点で除去する。

標準プロバイダーの厳密な対象・上限・意味的な制限は [README](../Src/DiffBeacon.Providers/README.md) に記載する。

## バイナリ範囲編集

Frhedの挿入／既存範囲内の上書き（EOF越え拒否）／start-count削除を実Avaloniaダイアログへ移植。固定長Hex編集、全体コピー、共有履歴、通常保存と内包作業保存の境界は維持する。直接ニブル入力、OSクリップボード互換、Binary本文HTML／patchは残工程。Releaseは警告0／エラー0、限定E2Eは12成功／0失敗／0skip、実ダイアログGUIは110成功／0失敗、全体UIは10,768成功／0失敗。全二者／三者側の保存bytes、訂正・取消・readonly・旧dialogのdraft／revision／保存世代／入力／mode／provider／中止／owner／dispose拒否、4095／4096・空入力・全削除、16MiB／共有64MiB／256操作とRedo保持（履歴48MiB＋既存Redo16MiBで80MiB候補を拒否し、全bytes・revision・保存点・Redo再実行を実panelと独立readerで照合）、保存中の後発編集を確認した。Python標準readerで固定全bytes・全workspace asset・原本ZIPの全entry／CRC・包装展開再読込みを独立照合。1280×850と850×550のPNG・座標でHexと中央保存ボタンへのスクロール到達を確認。証拠は `artifacts/local/binary-range-edits/handoff.json`。実管理者・終端0・実行前後AppSHA一致を確認し、限定scope metadataも明示する。親の固定ソースによる全体Managed／Windows x64 Native AOT検証は、各E2E160,140成功／0失敗／3skip、6,192コマンド・12,384stdout／stderr、UI10,768成功／0失敗、PNG547枚を独立照合して終了0。Managedは266ファイルの全bytesを保持、Nativeは264ファイルを保持し、Core／Providersの2lockはAOT restoreでHEADと同じ依存グラフへ復帰した。範囲編集の保存bytes・全workspace asset・原本ZIP／CRC・包装展開再読込みを、無改変の独立readerで全体E2Eと全体UIの両方から照合した。発行物13ファイルのSHA・サイズと実行前後Native／harness SHAも一致。証拠は `artifacts/local/binary-range-edits-parent/full-managed02-independent.json`、`native01-independent.json` と `range-readers-managed02-independent.json`／`range-readers-native01-independent.json`。範囲編集commit `9d6be1a33dd27293c5c97999d7e714d7ec053a5b` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37212361194) attempt 2と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37212361220)は成功した。合計E2E640,578成功／0失敗／10skip、24,774命令・49,548stdout／stderr、UI43,072成功、PNG2,188枚、公式8 ZIP719,846,182 bytesのdigest・全entry CRC／SHA／size・reader・発行物を独立照合した。Windows x64初回の600秒timeoutは失敗証拠を保持し、同じSHA再実行の成功と区別する。証拠は `artifacts/local/binary-range-edits-parent/ci/all-four-receipt.json`。このCIは後続のBinary clipboard変更を含まない。既存image clipboardのsourceSha256は画素fixtureのSHAであり、製品sourceのSHAを意味しない。実OS pointer／保存dialog／新Binary clipboardはこの範囲編集CIで検証していない。

## Windows NTFSコピーのClose後属性と診断（最新通常版の限定実測）

独立した二つのFILE_OBJECTを取得し、主handleのrename／EA変更とClose後に副handleで最終属性を確定する。Normalのsentinelと転写対象属性を区別し、圧縮親からの継承属性は実観測へ保持する。両checked Close成功後だけmetadata検証完了を記録し、公開後の失敗でもPublishedと出力を保持する。取得前のopen／closeと未公開破棄の予算予約、取消、全entryのcleanup診断と準備取消時のclose診断を維持した。

2026-10-06 JST、Release buildは警告0／エラー0。最新主Appの限定E2Eは126成功／0失敗／1OS専用skip、限定GUIは239成功／0失敗。実Mainの普通／圧縮親・新規／置換・Normal／Hidden／Readonly／Archiveの16コピーを固定SHAのPython readerへ独立照合し、元保持・全DATA／ADS・EA・creation／mtime・SDと最終Close後の全DTO属性を確認した。5拒否＋2注入診断GUIの98checked Close、主コピーreaderの64checked Close、全78命令の実PID／birth／終端と156stdout／stderr全byteを親が照合した。通常／最小／多数タブ6 PNGと診断2 PNGを目視した。主E2E processはPID15208・実終了0、GUIはPID29964・実終了0。source1963件／runtime551件は実行前後不変。部品の通常／Native各40ケース・209独立readerは別cohortであり、主App Native AOTへ資格を広げない。

証拠は `artifacts/local/folder-sync-next/windows-metadata-integration29/limited31-parent-acceptance.json`（SHA `BA6F027F0874723F26D74B7E3F424CD8F88F0636D1B580B1EC4A40E1494E7704`）と `E:/DiffBeacon-artifacts/local/windows-metadata-integration29/foldercopy-20261005T2013387717412`。固定Main reader SHAは `648899F0F4CD0E2E0EECF45A704BD45A8E4080136D3B04246969712C5D72D7A3`。この限定検証だけでは全体E2E／UI・主App Native AOT・4RID CIへ資格を広げない。native Close失敗注入・任意owner／SACL／EFS／SMB・外部変更用FILE_OBJECTとの競合、実OS pointer／native dialogは未実測とする。

## Windows NTFSコピーの同一コードによる全体検証

2026-10-06 JST、通常版とWindows x64 Native AOT版の全体E2Eは各161,675成功／0失敗／4skip、各6,983コマンド・13,966 stdout／stderrを全byte照合した。全体headless UIは各11,159成功／0失敗。source inventoryは1,963記録（重複表記を統一すると1,962 files）、通常版runtime 551 files／Native runtime 13 filesが各実行前後不変。原本入力・独立reader・公式Z decoderの固定SHA、実PID・birth・終了・保存ログを受理した。通常／最小／多数タブのフォルダー操作と、三者画像viewport・スクロール後のPNG保存への到達をJSONと代表PNGで確認した。

通常版の証拠は `artifacts/local/folder-sync-next/windows-metadata-integration29/fulle2e34-parent-acceptance.json`（SHA `936B2F46E6AB005626E29DFA8BDCB354B4F7FA32A5F8C1770CCC2A62ACBC1F93`）と `fullui34-parent-acceptance.json`（SHA `D31D412F3DFE3DF6D91EC54653BC5E9F0D184C8795E9AF887187C9FB0A49EABB`）。Nativeの全体E2Eは同じdirectoryの `nativee2e36-parent-acceptance.json`。通常版DLL SHAは `DE2E471DB2DF12843AD5A33CD033DFAD003799B84FCAAF517C1E8E40D9CC08AB`、Native exe SHAは `832683572535F13B6645DAEFF47AF64F1226B6F2DFC53249FC1CEB5AD16279B3`。

Native発行物12 filesのSHA／size、SharpCompress・TiffLibrary・JpegLibraryのlicense同梱、AMD64 PE・CLR directory不在を確認した。発行の全体UI自己検証は600秒枠で409.56秒・実終了0。同発行でsource変更とlock差分はなく、同じexeの後続全体E2Eも実終了0のため、同一UI検証を重複実行しない。初回は既定のリンカー探索で失敗し、NuGetによるCore／Providers lockの工具・RID graph追加だけを実controllerのメモリ原byteへ復元した。失敗cohortと成功cohortは別directoryに保持する。成功cohortは既存VS DevShellと実MSVC14.51.36231／SDK10.0.28000.0、process内の `IlcUseEnvironmentalTools=true` を使用し、製品projectや利用者の設定を変更していない。

この観測は最新のWindows x64のみ。最新コードのWindows ARM64／macOS Intel／macOS ARM64と実OS clipboardは次のGitHub CIで確認する。以前の4RID成功SHA `5cce1a13235da96c6c813e902b52936b0613a930` を、この追加metadataコードの成功へ流用しない。ネイティブ保存dialog、実OS pointer、native Close失敗注入、任意owner／SACL／EFS／SMB／敵対的な外部FILE_OBJECT競合の資格も追加しない。

## 三者フォルダー比較と長いWindowsパス（managed／Windows x64 Native AOT全体実測）

中央フォルダーを含む三側の存在状態と、中央↔左・中央↔右・左↔右の三組を一覧へ表示し、全6方向へ選択をすべて／差分だけコピーできるようにした。内容比較は三側を一度ずつ読み、GNU行算法の共通予算を使う。旧固定SHA `74c2ae18765f644786c386cb4c4adc58e6de9ced` から採取した既定GNU・UTF-8の63原本、378組の比較と独立readerを保持する。採取用C++／MSVCを通常buildや製品の依存へ追加していない。中央も編集できる三者Text、任意文字コード／フィルター／pluginの全旧互換を、このフォルダー検証の成功には含めない。

Windowsの既知DOS drive／UNCルートは標準絶対パスへ正規化してSystem.IOへ渡し、UIに入力した原表記を保持する。最初のNative検証はcanonical 266文字・raw 272文字のDOS中央ルートで実際に失敗した。修正後、同じ原入力を新Nativeの別processへ渡して通常の差分終了1を得て、全比較データが原normal-path結果と一致し、入力bytes・属性・mtimeを保持した。270文字以上のcanonical rootとlong DOS UNCも恒久E2Eへ追加した。未知のデバイス名前空間の書込み拒否、第三側・別タブreadonly・全入力／workspace／asset／linkの出力保護と取消・古い完了の破棄を維持する。

2026-10-07 JST、Release buildは警告0／エラー0。通常版とWindows x64 Native AOTの全体E2Eは各161,762成功／0失敗／5skip、各7,030命令の実PID・birth・実終了と14,060 stdout／stderrの全byteを独立照合した。同じsource inventory 1,996記録（unique 1,995 files）へ結合し、通常版runtime551／Native runtime13 filesは各実行前後不変。両版の三者独立結果は63原本／378比較、22公開、46 CLI・うちlong alias14、16 GUIコピー・5保護、GUI103 assertion成功。共有の可用性はCLIとGUIそれぞれの記録へ結合し、共有不可の場合に省略する条件を区別する。

両版の全体headless UIは各11,303成功／0失敗、45stageの全順序と最後のThreeWay完了、212 root PNGのCRC・有界zlib復号・寸法を照合した。この件数はlocalhost管理共有が利用可能なローカルWindows結果であり、CIで同数を無条件に要求しない。通常版379.756秒、別計測Native509.252秒で、Nativeの実PID22096・birth・実終了0も記録した。通常／最小850×550／多数タブで三入力、全方向・コピー、先頭行と100 DIP以上の一覧へ到達でき、設定欄のスクロール後に各入力・比較・除外／filterへ到達することを実controlsとbounds／PNGで確認した。Native最小画面も親が目視した。

Native発行の自動UIは11,303成功／0失敗・454.911秒で600秒枠内。発行時のCore／Providers lockへの同版10.0.12工具graph追加2件だけをcontrollerの原byteへ復元し、復元後source全件と21保護fileを照合した。App lockは不変、他のsource変更と未知lockは0件、managed runtimeの差分6件は別境界として記録する。独立readerは工具nodeを固定Appの同TFM／RID graphへ全field完全一致させる。発行exeのAMD64 PE・CLR directory不在、payload全12 filesとmanifest、SharpCompress／TiffLibrary／JpegLibraryのlicense同梱を確認した。自動UIと別計測UIの資格を区別し、同exeの全体Native E2Eへ結合した。

通常版全体の証拠は `artifacts/local/folder-threeway-next/whole53/fullui53-parent-acceptance.json` と `full-e2e53-parent-acceptance.json`。Nativeは `native53/publish57-parent-acceptance.json`（SHA `405F627CB0D653F5FB63447CFDA058108240CC601592D756A8342973D9E4B7A6`）、`native-ui57-parent-acceptance.json`（SHA `6273CA82B72EA570237AE3B213D036E194356454006721A3A29C0A1AE6A75F60`）、`native-e2e59-parent-acceptance.json`（SHA `B48FBCBE1BA520A197DBD61E9909A7412C09AE30FC728401A1F717497A6E90D0`）。通常App DLL SHAは `17F622008A10220170AC0E4FD16375E0CF4D501AC820BBF540766801AF5A7A32`、Native exeは `9C32A201454F26FD40A5237B182D30F9DD5E02CE81C2430E9E00608990BFF05B`。Native初回失敗、独立readerのgraph文脈不一致による拒否、原入力・ログ・バックアップも別recordとして保持する。

三者フォルダー実装のコミット`11bfca9215aa41417d028691972898e01adfc0e5`は、[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37591140044)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37591140026)が成功した。同じheadの全8ZIP（計763,064,165 bytes）を圧縮状態で保持し、公式digest・サイズと全entryのCRC／SHAを照合した。親readerは実PID20816／終了0で4RIDすべてを受理し、各18固定reader・26 phase、Windowsのmetadata原計算、macOSの実起動gateと全出力回収へ結合した。各RIDの実OS clipboardは画像とBinaryの別writer／readerプロセスで成功した。

| RID | 全体E2E成功／失敗／skip | headless UI成功／失敗 | UI全体時間 | 実アプリ命令数 |
| --- | --- | --- | --- | --- |
| win-x64 | 161,762／0／5 | 11,303／0 | 410.618秒 | 7,030 |
| win-arm64 | 161,762／0／5 | 11,303／0 | 452.343秒 | 7,030 |
| osx-x64 | 161,833／0／11 | 11,209／0 | 490.622秒 | 7,049 |
| osx-arm64 | 161,833／0／11 | 11,209／0 | 452.197秒 | 7,049 |

全45段階と最終集計が存在し、Windows600秒／macOS1,200秒の枠内だった。814枚のPNGを画素まで独立復号し、三者の通常／最小画面と全6方向の固定原本コピー・保護を照合した。証拠は`artifacts/local/folder-threeway-next/ci65/config70.json`、`accept70-receipt.json`（SHA `B1A59F4EF13624459BEF318ABBA155AC39928B9B7C430E7EB733F8FBB85F6BB3`）、`accept70.stdout.json`（SHA `751069BA4B39B50AC5AD7D98E88E7413DE8C9A74A14BCC10AA067F0708E4A753`）。独立レビューでも実Git全2009 blob・採取138 source・計28,158命令の生stream・Mac LaunchEvidence・親readerの実終了と出力SHAに不一致はなかった。元producerが記録しない旧Python readerのPIDは捏造せず、固定sourceの実終了0述語と保存rawstreamへ結合した。独立readerのローカル再実行はしていない。通常デスクトップのpointer・native保存dialog、SMB／非NTFS／任意owner・SACL・EFS・敵対的外部FILE_OBJECT競合、folderコピーのUndo／Binary HTMLなど未対応項目もこの資格へ含めない。Windowsの5skipはOS／filesystem固有項目と三者の実desktop・全入力互換の範囲外を理由付きで保持した。今回のコードはまだ全WinMerge機能の移植完了を意味しない。

最初の同SHA CI `f26b0c0147236c306742665f5d2a343a81f5e032` では、Windows二構成の全体E2Eが原文SHA検査1件で失敗した。win-x64は161,761成功／1失敗／5skip・7,030命令で、参照adapter `build/LegacyGnuReference/OriginalTranslationUnit.c` のcheckout改行変換が原因だった。固定sourceのcheckout filterを再現すると、WindowsではLFのadapter2件、MacではCRLFの採取元15件が記録SHAと不一致になる。provenanceの17採取元を無変換属性へ固定し、staged Git blobと両OS filterで全17件一致を確認した。本文・原本・slice・golden・provenanceと通常C# codeは変更していない。修正後の同じNative exeによる限定三者E2Eは88成功／0失敗／1範囲外skip。修正後の`11bfca921`の4RID CIは上表のとおり成功し、原文17件のGit blobもprovenance SHAと一致した。失敗した旧runの全体成功や省略されたOS clipboard／native artifactを資格に流用しない。

## フォルダー `.flt` 数値変換（2026-10-20）

Coreの`FileFilters`に`ToInt`・`ToDouble`・`ToNumber`を追加した。関数名は大文字小文字を区別せず、公開parserは引数をちょうど1個要求する。値は1回だけ評価し、文字列だけを受け取り、ASCII commaを全除去した後に全入力を消費して変換できなければ`None`を返す。直下arrayだけをmapし、nested arrayは再帰変換しない。`ToInt`は符号付きInt64、`ToDouble`は固定したWindows MSVC `std::stod`の結果、`ToNumber`は文字列に`.`・`e`・`E`を含む場合だけDouble、それ以外はInt64を返す。Dynamic値のmetadata/context伝播も追加し、整数とDoubleの型を公開比較で識別できる。

固定fixture `FolderNumericConversionOracle`は246ケース・3評価例外を保持し、golden SHAは`52d07df1d5b0da74350a3d946c344ea8e6fc5aada7513fdedebccb852c2e125a`。公開E2Eは198 string候補（各関数66）を型付き期待値へ投影し、typed binding・embedded NUL・body専用の48件は公開CLIで表現可能と装わずoracle専用に分ける。別の直接`std::stod`実測は長いhex mantissaとの指数相殺で±5200を受理し、±5201を`out_of_range`にすることを確認した。この6入力に対して別processを各2回起動し、返り値／例外・消費長・bitsが一致した。公開E2Eは4入力を実アプリCLIへ投影し、二者／三者双方で通した。

NaN文字列化の独立MSVC probeで、8つの`std::stod`入力表記を`std::to_string`へ渡し、さらに9個の直接IEEE-754 bit patternを別プログラムで文字列化した。各プログラムを別process 2回で実行し、stdout SHAは`3B87441DE28F35744723AB914B2A2C73A596538BF0B095D71E09430A28D3751D`／`E0FEAC43DEFE550227A2BC12C3E5A522BA4C363A8A73D8D707E39BAD2086BC01`で一致した。`0xfff8000000000000`は`-nan(ind)`、signaling NaNは符号付き`nan(snan)`、ほかのquiet NaNは符号だけを付けた`nan`となることを確かめた。`FloatingString`はこの分類を再現し、公開E2Eで`nan(ind)`と符号付き`nan(snan)`の`ToDouble`→`Trim`／`Strlen`を二者／三者の双方で照合する。

検証はRelease build 0警告／0エラー、`--folder-properties-only` **5,398成功／0失敗／2 skip、1,723命令・5,400 assertion**。skipはmacOSの実FIFOとWindows標準filesystemでのcase-sensitive distinct fileで、いずれも環境限定である。5つのroot `packages.lock.json` は`numeric-20261020-impl01/lockfiles-before.json`と`lockfiles-after.json`の双方に対し、SHA・size・属性・creation/write tickが一致した。E2E cohortは`<作業フォルダー>\DiffBeacon\nan-fix-final-20261020-r1\e2e-final`、NaN文字列化probeは`<作業フォルダー>\DiffBeacon\nan-string-20261020-r1`に保持した。この検証は公開managed CLIの評価trace、全体E2E、UI自己検証、Native AOT、macOS／他RIDの結果を含まない。
