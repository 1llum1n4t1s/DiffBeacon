# 実行経路の検証

実アプリの子プロセスは、通常CLIを30秒、`--self-test`で始まる描画・操作自己検証を120秒の上限で実行する。自己検証は複数の実画面操作と原本照合を含むため、通常CLIとは予算を分ける。時間超過では子プロセス群を終了し、終了コード`-2`と選択した制限時間をstdout／stderr・`assertions.json`へ残す。上限を延ばしただけで検証成功とは扱わず、実行完了と全条件の合格を確認する。

TAR.Zは`--tar-z-only`で限定実行し、`--archives-only`と全体E2Eにも含む。全39固定原本（block9–16、nonblock10–16、独立literal nonblock9）の全entry bytes・型・サイズ、通常CLI全操作と標準archive／tar metadata provider、壊れたheader/code/TAR、既存出力保護、包装と相対入力再読込みを照合する。[固定原本・出典](../Fixtures/Archives/TarZ/README.md)を参照する。writer出力は`build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local`で別buildした公式decoderを`--z-reference <decoder>`へ明示し、Python標準tarfileで全内容・時刻・writer metadataを独立検証する。Windowsは`ncompress.exe`、macOSは`ncompress`。`--z-sevenzip <full7z>`はローカル第二decoderの追加照合。reference toolとC/compilerは製品・通常.NET buildの依存ではない。入力・出力・独立復号TAR・proof・exit・stdout/stderr・assertionsを保持する。限定は全体の代替ではない。

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

TIFFの限定実行は `--tiff-only`。ビルド完了後、`dotnet tests/DiffBeacon.E2E/bin/Release/net10.0/DiffBeacon.E2E.dll --tiff-only --output E:/DiffBeacon-artifacts/local/tiff-limited` でも実行できる。[自作TIFF入力](../Fixtures/Images/Tiff/README.md)のページ寸法・全BGRA／SHA、byte order、圧縮・色の代表構成、全／選択・三者・短い側の反復、領域・単体／包装HTMLの独立PNG復号を照合する。不正IFD・切詰め・巨大宣言・旧JPEG参照・scan欠損の拒否、既存出力と入力保持を確認する。GUI自己検証は全ページの実bitmap、取消・古い完了、snapshot HTMLをBGRA・PNG・JSONへ記録する。限定実行は全体E2Eの代替にしない。

APNGの限定実行は `--apng-only`。小さな[自作CC0入力](../Fixtures/Images/Apng/README.md)で既定画像の含有／除外、位置・合成・3種のdisposal、透明RGB、全／選択フレーム・三者・短い側の反復を実アプリへ渡す。CLIの画素SHA、単体／包装／再展開HTMLの原画PNGを独立復号して手書き全BGRAへ照合する。不正sequence・CRC・矩形・枚数・欠損・宣言上限の拒否、既存出力・入力の保持も確認する。共有256M作業量の拒否には約36KBの正常APNGを実行時生成し、巨大な復号画素は確保しない。全体実行にも含める。GUI自己検証は自動画像判定・全ページの実bitmap、同期ボタン・取消・古い完了・snapshot HTMLをPNG／JSONに記録する。

静止画像GUI編集はアプリの `--self-test` で実比較タブ・ボタン・領域移動・PNG保存を操作する。コピー原本fixtureの代表12操作列・66状態を全原画BGRA、領域、共有履歴、dirtyへ照合し、PNG独立復号・再読込み、編集済みHTML、未保存包装拒否、読取り専用・全タブの入力保護、取消・古い完了・保存中の再比較拒否を検証する。最小ウィンドウのviewportとスクロール後の保存ボタンも確認する。`image-copy-gui-observations.json`、元golden、入力・PNG・HTML・画面・`ui-report.json`を保持する。原本143ケース・935状態のCLI全件照合と通常デスクトップの操作検証は別の検証範囲である。

画像コピーの限定実行は `--image-copy-only`、全体E2Eにも含む。原本143ケース・935状態をBCL PNGへ戻し、実CLI `--image-copy` の原画全BGRA・寸法・region grid／分類・全pane共有履歴／dirty／savepointへ照合する。PNG別名保存を独立復号・通常画像CLI再読込みで照合し、無効入力・読取り専用・出力保護とscript／作業／履歴／JSON上限を検証する。入力・script・stdout／stderr・終了コード・observations・assertionsを保持する。[コピーfixtureの境界](../Fixtures/ImageCopy/README.md)を参照する。GUI編集・処理中のOSシグナル取消・元形式／多ページ保存はこの限定検証に含めない。

画像領域の限定実行は `--image-regions-only`、全体E2Eにも含む。無改変WinIMerge v1.0.54 C++関数から採取した164件のraw BGRAをBCLのPNGに包装し、実アプリの復号・開発用CLIを通して全pair grid・領域ID・分類・矩形・個数を完全一致照合する。通常buildにC++やFreeImageを追加しない。固定goldenのSHA、入力保持、不正引数、選択組、入力／共通canvas／合計復号量／診断grid上限も確認し、PNG・成功／拒否出力・終了コード・`image-region-observations.json`・`assertions.json`を保存する。[出典と再生成](../Fixtures/ImageRegions/README.md)。通常GUI・HTMLへの統合やOSシグナル取消の実測はこの限定E2Eに含めない。

画像HTMLの限定実行は `--image-reports-only`、`--reports-only` と全体E2Eにも含む。既存画像fixtureの全画素期待値を、単体・ZIP包装から取り出した埋込みPNGへ独立BCL PNG復号で照合する。全／選択、後続差分・欠落・透明合成・閾値・寸法、説明のescape、原本保持、包装展開と相対プロジェクト再読込み、入力・本文サイズ・出力保護・取消を確認する。画像UIは全／選択の設定と表示原本スナップショットの保持も自己検証する。

画像強調の限定実行は `--image-highlight-only`、全体E2Eにも含む。無改変C++原本の72件でalpha・選択色・透明度・三者paneの分類除外を開発用CLIへ照合する。既定強調の入力は通常CLI・単体HTML・包装へ接続し、埋込みPNGを独立BCL復号で全BGRA照合する。UI自己検証はalpha0.7の24件を実Bitmapへ照合し、領域選択・強調解除・レポート・原本保持も検証する。[強調fixture](../Fixtures/ImageHighlight/README.md)を参照する。限定検証は全体や通常デスクトップの実測の代替にしない。

画像フレームの限定実行は `--image-only`。自作GIF/PNGと独立Pillow生成lossless WebPの固定全BGRA/SHAを実CLIへ照合する。先頭一致・後続変更、全ページと選択、ページ数差・欠落、透明部分更新・disposal、寸法・閾値、入力/キャンバス/フレーム数/復号作業量の上限、不正引数・入力保持を検証する。上限の診断も照合し、別の上限で拒否されただけの結果を合格にしない。[fixtureの契約](../Fixtures/Images/README.md)を参照する。全体E2Eにも含み、通常デスクトップの実測と区別する。

GNU算法の限定実行は `--gnu-line-only`。原本の同値クラスと変更scriptを開発用CLIの別プロセスで照合し、予算上限・境界・入力拒否と原行保持を確認する。[採取範囲](../Fixtures/GnuLines/README.md)に原本入力変換と算法の境界を記載する。

通常テキスト比較への接続は `--gnu-text-only` で限定実行する。原本のdefault 273ケースの元bytesをBOM・EOLごと実ファイルへ戻し、通常 `--compare --eol strict --max-work 8000000` の元行番号から変更scriptを復元して照合する。逆順4095/4097だけ予算退避を許容し、他ケースの不一致・退避は失敗とする。フィルター、共通端保持、予算0/1、巨大一行、EOF、EOL、三者マージとパッチ再構成も確認する。`gnu-text-observations.json` と全入力・終了コード・ログ・assertionsを保持する。両GNU検証は全体E2Eにも含み、限定実行は全体の代替にしない。

表の初期GNU行対応は `--gnu-table-only` で限定実行する。原本defaultのA/B・LF全225ケースから、各変更が片側だけの143ケースを選び、元bytes・SHAを維持してCLIの全元行対応とHTMLの全セル・ghost・色を原本scriptと照合する。両側変更のraw採点はこのscriptから推定しない。引用表記だけの差と三者の無変更側一致、予算0/1/120の全元行保持・GNU退避理由、EOL・EOFも検証し、`gnu-table-observations.json` と入力・出力を保持する。全体E2Eにも含み、旧callerの全raw CSV入力変換との互換は宣言しない。

表行対応の三者回帰では引用符・大小文字・空白・置換設定・部分挿入・左右反転の8ケースを別プロセスで検証する。既知の復号一致組とHTMLのEqual表示、予算0の全元行保持を照合し、入力と対応JSONを保存する。

表行対応の限定実行は `--line-alignment-only`。88件の元関数golden、三者の元行保持、CLI/HTMLの共通対応、4096境界・共有予算・不正引数・入力保持を実アプリ別プロセスで検証する。[fixture契約](../Fixtures/LineAlignment/README.md)に全ブロックoracleと初期アンカー分割の検証範囲を記載する。全体実行にも含める。

原文 WordDiff の限定実行は `--word-diff-only`。6,048件の元ソース採取 fixture から設定と区間結果の代表135件を選び、実アプリ別プロセスの終了コード・全UTF-16区間・入力保持を照合する。さらにfallback・不正引数・NUL入力拒否・離れた変更のHTML span・改行横断ブロックの行投影・共有inline予算を確認する。出典と採取環境は [fixture契約](../Fixtures/WordDiffs/README.md) を参照する。全体実行にも含める。

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
pwsh -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local
$zReferenceName = if ($IsWindows) { 'ncompress.exe' } else { 'ncompress' }
$zReference = (Resolve-Path -LiteralPath (Join-Path 'artifacts/z-reference/local' $zReferenceName)).Path
dotnet build DiffBeacon.slnx -c Release
dotnet run --project tests/DiffBeacon.E2E -c Release --no-build -- --output artifacts/e2e/local --z-reference $zReference
```

プロバイダーのレビュー境界 5 ケースだけを再現する場合は `--provider-boundaries-only` を追加する。CLI とファイル形式を通す E2E の経路は全体実行と共通である。

詳細テキスト比較・競合選択と設定保存の 42 ケースだけを実行する場合は `--text-advanced-only` を追加する。競合選択は LEFT / BASE / RIGHT 各経路を UTF-16 BOM と UTF-8 BOM の両方で保存し、CRLF と最終改行なし、左右の独立変更を含めて出力をバイト単位で確認する。通常マージは CRLF 祖先に対する左右同一 LF と片側変更 LF の保存を確認する。設定保存は実プロセスの `--project-copy` で全フィールド・置換ルール配列順と旧空白モードの区別を確認する。

複数比較プロジェクトだけを検証する場合は`--projects-only`を追加する。実アプリの`--project-copy`で全entryの順序・選択位置・設定、旧単一JSONの省略値、旧XMLの相対パス／URL、置換ルール既定値、過大／不正入力の拒否、readonly・リンク出力の原本保持を確認する。GUI自己検証は実際の全タブ復元・保存・確認キャンセル・表設定・readonly編集／形式別保存の操作をPNGとJSONに残す。

旧プロジェクトの正式な `ignore-comment-diff` と既存互換の `ignore-comments` を受け入れ、両方ある場合は正式タグを優先する。コメント構文は、左・祖先・右の順に最初のサポート済み拡張子を使う。Python は `.py` / `.pyw`、C# は `.cs`、XML 系は `.xml` / `.xaml` / `.axaml` / `.svg` / `.html` / `.htm`、CStyle は C / C++ / Objective-C のソース・ヘッダー拡張子に限定する。未知の形式だけの場合は本文の差分を誤って消さないよう None とする。ファイル本文・shebang による推定や三者それぞれの異なる構文設定は未対応。`--legacy-comments-only` はこのインポート経路の 10 ケースを実行し、`--text-advanced-only` と全体実行にも含める。

`--app <実行ファイル>` で Native AOT 発行物を検証する。省略時は Release のアプリ DLL を別プロセスで実行する。`--output` の下に入力、出力、標準出力・標準エラー、コマンド、終了コード、アサーション JSON を保存する。シンボリックリンクが作成できない環境は理由付きでその検証を省略する。UI の操作・画像はアプリの `--self-test` と発行スクリプトで別途検証する。

macOS のアプリ起動では、固定 SHA の `process_startup_gate.py` が nonce と実 PID を通知して待機し、harness が実 OS の開始時刻を取得してから同じ PID の `exec` でアプリを実行する。既存の検証用 Python を使い、製品の依存には追加しない。通知と native の stdout を分離し、UTF-8 stdin と EOF を引き継ぐ。30／120／180 秒の実行制限に加え、異常時の終了待機と出力回収は共通の5秒枠で扱う。実終了コード・終端の観測・部分出力を `LaunchEvidence` に保存し、launcher の初期化失敗や回収不完全をアプリの期待終了コードと同一視しない。秘密の stdin は記録しない。固定 script の `-text` 属性を維持する。Windows の起動経路は従来どおりで、実 Mac での成功は対象 SHA の GitHub runner 結果を確認して判定する。

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

### 明示 archive wrapper 鎖

`--archive-wrappers-only` は ZIP派生/7z/RAR＋gz/bz2/Zの固定鎖、全entry export、compare/repack/extract、暗号化stdin、破損後member/終端/深度と出力保護を実App別プロセスで検証します。通常全体と `--archives-only` にも接続します。既存 Build-ZReference のdecoderを明示します。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --archive-wrappers-only --z-reference <absolute-existing-ncompress> --output artifacts/e2e/wrappers
```

`archive_wrapper_verifier.py` はPython標準のstrict各member復号と明示公式Zを使い、全層・終端bytes/SHA、ZIP全entry bytes/timeを独立照合して `wrapper-oracle/proof.json` を保存します。小予算・取消・GUI候補採用/Refresh/実Stop/Retry/Cancel/×は App のheadless自己検証にJSON/PNGを残します。限定実行は全体E2Eの代替ではありません。新wrapper出力形式や内側entry navigationは検証済みとして扱いません。

E2E harnessは未知option、値欠損、同じoptionの重複、複数selectorをoutput作成/App起動前に終了コード2で拒否します。wrapper限定はこの前処理もharness別プロセスで検証し、11組のstdout/stderrを保持します。

### 明示した内包アーカイブ

`--archive-tar-wrappers-only` は [TAR多層wrapper原本](../Fixtures/Archives/TarWrappers/README.md) の正常22件・拒否22件とSource用ZIP4件を実アプリで照合します。通常list・entry・展開・repack、標準プロバイダー、typed root／Sourceを確認し、独立readerは正常ケースのID集合・一意性と各typed-root成果物を必須にします。共有作業量は入力SHAの上限拒否と、SHA確認後のアーカイブ処理中の上限拒否を別々に検証します。通常全体と `--archives-only` にも含み、限定実行は全体E2Eの代替にしません。

`--archive-sources-only` は実アプリのSource専用CLIへdescriptorを渡します。通常全体と `--archives-only` にも含みます。[固定入力](../Fixtures/Archives/Sources/README.md)を変更せず、ZIP内ZIP、raw TAR、未知名gzip／bzip2 TAR、内側wrapper、深度8／9、二層の異なるpasswordとplaintext空行を検証します。最終全entryのsize／SHA、empty file、directory／missing拒否、後続sibling／内側CRC破損、root同サイズ・同mtime差替え、共有復号量の正確なbyte境界、件数・名前・深度・作業量、descriptor／root／readonly／link出力保護を確認します。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --archive-sources-only --output artifacts/e2e/sources
```

280MiBのTARは各140MiBの二entryを含む固定圧縮入力でlistし、巨大raw fileを保存しません。Python標準libraryは全入力SHA・サイズ、全container CRC／内容、raw TAR SHA、取得した全entry bytesを独立照合し、run内の `archive-sources/independent-proof.json` と標準出力・エラー・終了コードを保持します。Windowsのlink作成特権がない場合はその操作をskipとして区別し、管理者／GitHub runnerで別途実行します。今後の検証用linkとtargetは小さい `<出力先>-source-links-<GUID>` siblingに分け、run内とsiblingの `retained-links.json` に絶対pathを保持します。過去runのlinkは移しません。Source CLIの実CancellationToken取消、GUI子タブ・workspace・包装・HTMLは、この限定実行の検証済み範囲へ含めません。限定実行は全体E2Eの代替にしません。

`--archive-project-only` はversion 2のtyped Source入力で二者／三者・混在・empty、内包ZIP／raw TAR、gzip rootのText 7比較と物理中央入力を持つ三者Binaryを作り、実アプリのproject-copy・report-project・包装・製品展開・再読込み・leafの外部patch適用を確認します。通常全体と `--archives-only` にも含みます。[独立検証](../Fixtures/ArchiveProjects/README.md)はPython標準libraryで全container／包装entryのCRC・bytes、root SHA・chain・leaf、本文・caption、3patch適用結果を照合します。schema・既定version・未知／重複DTOfield・readonly解除・root変更・missing／directory・入力／descriptor／既存出力保護も確認し、products／independent-proof／入力・標準出力・エラー・終了コード・assertionsを保持します。暗号化Sourceのworkspace包装／CLI reportへpasswordを渡す経路は未対応です。

`--archive-missing-only` はversion 3の不在入力について、新規・削除・空ファイル・仮想の内包階層・両側不在・祖先不在の8比較を実アプリで保存、HTML化、包装、展開、再読込みします。通常全体と `--archives-only` にも含みます。[独立reader](../Fixtures/ArchiveMissing/README.md)は元containerの全CRCとSHA、不在の起点、HTMLの全文と改行、包装の全ファイルを照合します。さらに6件の包装patchを実アプリで外部テキストへ適用し、原本から独立に得た右本文と全byteを照合します。不正schema、実在するfile／directory、後続CRC破損、原本SHA不一致の拒否では既存出力の保持も確認します。継承readonlyの真偽値の往復と不正型の拒否、実GUIの同じ側での編集・差分コピー・外部SaveAs・取消・古い完了・保存中の編集・実在0byte・復元・包装を実アプリ別processで検証し、[未命名reader](../Fixtures/ArchiveDraft/README.md)で原本・HTML全本文・包装全byteを照合します。その包装patchも独立生成した入力へ実アプリで適用します。ネイティブ保存ダイアログとheadlessの注入経路は区別します。限定実行は全体E2Eの代替にしません。

GUIは全体 `--self-test <出力先>` の `HeadlessArchiveSourceChecks` で実controlと子タブ追加を確認します。開発時には `--self-test <出力先> --archive-sources-only` で同じSource画面操作だけを限定実行できます。結果JSONのscopeを区別し、全体UI検証の代わりにはしません。内側一覧・Text／Binary・readonlyと原本保存拒否・empty・二階層password・DTO複製・workspace明示復元・三者／混在・HTML・別名export、早期／完成後Stop・parent-close・refresh世代・tab-switch、同size／mtimeのSHA差替えを確認し、PNGと `archive-sources/source-gui-proof.json` を保持します。4096bytesを超えるleafはprefixだけ保持して全CRCを検査します。通常／最小windowの一覧・previewの実viewportとscroll後の比較button、再比較のSource読込み、exportのread前／公開前Stopと既存出力SHA、取消後のpreview操作、全タブのroot・通常入力・filter・workspace出力保護も確認します。通常デスクトップとネイティブダイアログはこのheadless検証から推定しません。

### Binary Copy All

`--binary-copy-all-only` は実アプリ別プロセスの `--self-test <output> --binary-copy-all-only` を起動し、二者両方向と三者全6方向の実ボタン・実確認dialogのキャンセル／続行を検証します。短いsourceの末尾保持・空／同一bytesのno-op・読取り専用・未適用Hex拒否・確認中のowner／StateStamp／再比較／readonly変更・共有履歴上限を確認します。通常保存、内包Binaryの作業保存、project-copy・包装展開／再読込みも実行します。[独立reader](../Fixtures/BinaryCopyAll/README.md)が固定期待bytesと元入力・ZIP entry／CRC・workspace／包装assetを全byte照合します。PNG・facts.json・assertions.json・各プロセスstdout／stderrを保持します。全体E2Eと全体UI自己検証にも含まれます。ネイティブ保存dialog・実OSpointerはこのheadless検証に含みません。

### バイナリ範囲編集の限定検証

`--binary-clipboard-only` は固定Frhed codec634件を実アプリの別プロセスで出力し、[独立reader](../Fixtures/BinaryClipboard/README.md)でraw/原関数SHA、全input/golden/output bytesと4件のIEEE独立値を照合する。実AvaloniaのCopy/Cut/Paste/FastPaste/選択dialog、Hex nibble/文字/キー/pointer、4096ページ跨ぎ、repeat/skip、公開／flush失敗、古い完了、readonly、未適用Hex、共有履歴とRedo、通常／内包全側保存・workspace・包装・展開再読込みも検証する。GUI限定は `--self-test <output> --binary-clipboard-only`。PNG、設定/入力、stdout/stderr、assertions、保存bytesと独立reader出力を保持する。全体E2E/全体UIへ含め、限定成功を全機能完了へ読み替えない。

OEMはWindowsの実ACP/OEMCPと全256入力の有界1byte `CharToOemBuffA` 結果を独立readerで採取し、別processの製品decoderと実FastPaste形式dialogの保存byteへ照合する。macOSは1252/437互換値の検証と区別する。Ctrl/Command+Shift+ZのRedoも実key注入で確認する。

実OSのBinary clipboardは `--clipboard-self-test <output> --binary-write` と別processの `--binary-read`。GitHub clean runner以外で拒否する。Windows CF_TEXT/CF_UNICODETEXT/custom raw、macOS標準text/custom rawと全256bytesを記録し、既存画像clipboard検証を維持する。ローカル利用者clipboardは操作せず、ネイティブpointer・保存dialogの操作証拠とheadless注入を区別する。

`--binary-range-edits-only` は実アプリ別プロセスの実ダイアログ、全二者／三者側、訂正・取消・古い結果拒否、境界・上限・共有Undo／Redo・保存点・保存中の後発編集を検証する。内包作業保存・workspace／包装・CLI複製／展開再読込みを `tests/Fixtures/BinaryRangeEdits/verify.py` の固定期待bytesとZIP CRCで独立照合する。GUI限定は `--self-test <output> --binary-range-edits-only`。全体E2Eと全体UIにも含み、限定実行は全体検証の代替にしない。
