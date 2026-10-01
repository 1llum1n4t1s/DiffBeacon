# .NET 10 / Avalonia 移行の到達点

この変更は新しい比較アプリの実装と Native AOT 発行経路の追加であり、旧 WinMerge の全機能との互換性を完了したものではない。旧 C++ / MFC の `Src/Merge.rc` と各実装は残っているが、新しい `DiffBeacon.slnx` の通常ビルド経路には含まれない。旧機能の存在と、新しいアプリで実装・検証された機能を区別する。

行比較のDiff算法は依頼によりGNUベースの一種類に統一する。原本との対応検証と処理上限を備えた現行実装を採用し、旧WinMergeの算法選択ドロップダウンと別算法の移植は完了条件から外す。行内のWordDiffや比較フィルターは引き続き対応範囲に含む。

| 旧機能・処理分岐 | 新しい実装の範囲 | 判定・残作業 |
| --- | --- | --- |
| テキストの二者比較・三者マージ | 差分行・行内強調、差分移動、両方向コピー、編集、保存、競合表示、CLI、左・祖先・右・結果の4ペイン、差分単位の順序付き採用・Undo/Redo・未解決状態・行の由来 | [マージ結果セッション](MERGE-SESSION.md)を実装。旧エディターの構文強調、矩形選択、同期点、移動行、全ショートカット、由来の行番号マージン表示・セッション永続化は未完了 |
| フォルダー比較 | 再帰比較、片側のみの項目、キャンセル、選択項目のコピー API | 基本経路を実装。コピーはリンクを拒否し、削除 API は提供しない。同期・全状態列・旧シェル操作の同等性は未完了 |
| 表形式 | 原文区間付き解析、raw WordDiff共通文字量・best-pair・三者01/12/20行合わせ、セル編集・Undo/Redo・同セル内前後検索・固定文字範囲・一件/全置換、区切り文字・引用符・引用内改行指定、GUI/CLI/HTMLの共通モデル | [表の操作](TABLE-EDITOR.md)。旧callerのraw CSV入力変換・全WordDiff設定とフィルター座標、raw buffer横断検索・PCRE/Rx互換・矩形文字編集・ヘッダー設定・列条件・同期点・全パーサー分岐は未完了 |
| Hex / バイナリ | バイト比較、ページ単位の16進編集、差分範囲コピー、別名保存 | 表示・編集は各16 MiB上限。同じオフセットで比較し、挿入位置の再整列や旧Hex全操作の同等性は未完了 |
| 画像 | 二者・三者表示、原本領域強調と差分／競合移動、重ね合わせ、倍率・閾値、ピクセル差分、GIF/WebP/APNGのフレーム選択・前後移動・同期移動、TIFF／BigTIFFの主ページ比較、ページ・表示設定のプロジェクト保存、全フレーム／選択組のCLI比較とPNG埋込みHTML、包装レポート、GUI／開発用CLIの静止画領域コピー・共有Undo／Redo・PNG別名保存、編集済み原画のHTML | [画像の契約](IMAGE-VIEWER.md)。入力・画素・枚数・復号量とHTMLの上限を維持。TIFFの未検証構成と残りの旧画像形式、ベクター、OCR、元形式／多ページ保存・位置合わせ／回転・矩形編集、旧全体設定の永続化は未完了 |
| Web / XML / HTML / Office | XML正規化、HTML静的本文、HTTP応答のソース・本文、DOCX/PPTX/XLSX本文 | 標準プロバイダーを明示選択。ブラウザーのDOM・JavaScript・画面・リソースツリー、Officeの書式・旧形式・PDF/OCRは未完了。詳細はプロバイダーREADME |
| Archive / プラグイン | 7z/RAR/ZIP/TAR/TAR.GZ/TAR.BZ2の内容比較、暗号化ヘッダー/内容・solid読込み、プレビュー・エントリ保存・全件抽出、非暗号化7z/ZIP派生/TAR系作成・再梱包、保存済み比較文書・HTML/patch/projectの包装、実行ファイル用JSON契約 | 旧submoduleの通常ビルド依存は解除。TAR.Z、全形式の詳細レポート・一時ZIPのクリップボード包装、多段比較、CAB/LZH/ISO等の全旧読込み形式、属性・全日時保存、旧ActiveX/DLL ABIは未完了。7zのCRC省略と値0の区別は現行ライブラリの公開APIでは未確認。変換結果を元ファイルへテキスト保存しない |
| シェル統合 | 通常のデスクトップ起動、CLI、macOS `.app` 生成 | Explorer / Finder 拡張、旧コンテキストメニュー、インストーラー登録は未実装 |
| 多言語 | 新 UI は日本語を中心に実装 | 旧翻訳カタログとローカライズ切替、RTL、全ダイアログの同等性は未完了 |
| 詳細フィルター | 大文字小文字、4種の空白処理、空行、行正規表現、ASCII数字・CStyle/CSharp/Python/XMLコメントの除外、順序付き置換、旧`.flt` include/exclude、名前・拡張子・サイズ・日時の条件式 | 比較前処理は原文を保持し、GUI・CLI・フォルダーへ適用。同梱12 `.flt` の読込みを確認。内容検索、左右別属性、関数・算術、PCRE固有構文、全旧構文のコメント処理、複数行置換、全表示フィルターは未完了 |
| プロジェクト / レポート | source-generated JSONの全タブ保存・復元、複数組`.WinMerge` XML読込み、HTML / JSONレポート、相対JSON参照とGUI起動時プロジェクト読込み、テキスト・表・JSONの二者／三者HTML、画像の二者／三者全／選択HTML | 順序・選択位置、説明・各readonly・再帰・フォルダー方式・除外・表設定を保持し適用。HTMLは祖先アンカー整列・行内差分・表セル・JSON正規化・画像画素を単体GUI／CLI／包装へ適用。旧filterは対応範囲以外を明示拒否。未対応オプション・プラグイン名は保持・警告し実行しない。旧middleの第三比較ペインと祖先マージの厳密な対応、画像の全旧形式とWeb描画レポート、全旧オプション、結果本文・採用状態の永続化は未完了 |
| Native AOT Windows x64 / ARM64 | 対応 RID と同 OS 発行スクリプト、CI マトリクス | 両アーキテクチャのGitHub runnerで発行・UI自己検証・CLI E2E成功。実測結果は下表 |
| Native AOT macOS x64 / ARM64 | 対応 RID、`.app` / tar、CI マトリクス | Intel / Apple SiliconのGitHub runnerで発行・UI自己検証・CLI E2E成功。署名・公証・公開は実施しない |

WinMerge XML の要素と window-type の対応は `Src/ProjectFile.cpp`、`Src/MergeCmdLineInfo.h` を参照した。全`paths`を順に読み、`left` / `middle` / `right`、説明・readonly、`subfolders`、表設定、`ignore-case`、`ignore-blank-lines`、`ignore-numbers`、`ignore-comment-diff`（互換別名`ignore-comments`）、`white-spaces`を適用する。`white-spaces`の旧モード1は空白量の変更を無視、2は全空白を無視として区別する。コメント構文は左・祖先・右の最初の対応拡張子から選択し、未知の形式では除去しない。旧`filter`はファイルフィルターであり、行正規表現へ置き換えない。旧`compare-method`は0を内容、2をSHA-256、4を日時とサイズへ対応し、それ以外は未適用として元値を保持する。HTTP/HTTPS URL・外国OSの絶対パスは保持し、環境変数を展開して相対パスをプロジェクト所在基準で解決する。

プロジェクト保存は原子的に置換し、読取り専用属性・リンク経由を拒否する。プロジェクトのreadonly指定は編集・差分コピー・フォルダーコピー・全保存経路で入力を保護し、読取り専用フォルダーの子への出力も拒否する。バイナリ別名保存とアーカイブのエントリ保存・再梱包にも祖先・反対側を含む保護を接続する。旧JSONの省略項目は既定値を復元し、明示null・不正enumを拒否する。置換ルールは専用converterで省略時のMatchCase/UseRegex/Enabled=trueを維持する。

コピー処理はルート外のパス、`.` / `..`、既存の symlink / junction を拒否し、ファイルを一時ファイルへ書いた後に置き換える。途中のキャンセルでは、既に完了したファイルや作成したディレクトリが残る。検査とファイル操作の間に別プロセスがパスを差し替える敵対的な状況まで防ぐ OS ハンドル単位の保護は未実装。

全機能移行完了の判定には、上の未完了項目の実装と、Windows / macOS の対象アーキテクチャ上で再現できる検証結果が必要。CI 定義の追加だけで、ビルド・起動・配布の成功を確認したことにはならない。

アーカイブの旧互換範囲は実装根拠で区別する。`Src/7zCommon.cpp:391`の作成UIは7z・ZIP派生形式・TAR/TAR.Z/TAR.GZ/TAR.BZ2/TGZ/TBZ2を提供し、RAR/LZH/CAB作成はコメントアウトされている。`Src/ArchiveDlg.cpp:28`は選択文書・レポート・パッチ・プロジェクトを包装し、`ArchiveSupport/Merge7z/Merge7zCommon.cpp:243`は全件抽出、`Src/7zCommon.cpp:480`は多段アーカイブを再判定する。読込み形式は`ArchiveSupport/Merge7z/Merge7zCommon.cpp:721`以降の登録を参照する。暗号化出力・分割出力は調査した旧作成経路に指定がなく、既存機能の移植漏れとは断定しない。分割読込み・MSI・リンク保存の厳密な旧動作は未確定であり、追加実測が必要。

## 原文区間を保持する WordDiff

旧 `stringdiffs.cpp` の単語 O(NP)・区間生成・文字絞込みを managed Core へ移植し、テキスト比較の変更ブロック全体へ接続した。旧ブロック呼出しの連結と行offsetを参照し、正の区間を各行本文へ投影する。離れた変更を GUI・CLI・単体／包装 HTML で共有する。全ブロックで詳細比較予算を共有し、上限時は変更行全体を強調して件数を表示する。

原本4 translation unitを変更せずMSVCで実行した6,048ケースと全UTF-16文字分類を採取した。CRT C / Windows分類を固定して全OSで使用する。実測で発見したタイトルケース・上付き数字の差を修正した。これは元ソース実行との比較であり、旧GUI配布バイナリの全設定・全字素規則との一致ではない。NULファイルは既存TextDocument契約により拒否する。詳細な出典と失敗条件は [WordDiff E2E](../tests/Fixtures/WordDiffs/README.md) を参照する。

表の変更ブロックはraw WordDiffの共通文字量・best-pair・4096本文上限・三者20写像へ接続した。全ブロック共有予算と不変条件による安全なzipを追加し、CLI三者指定と単語/EOL/予算設定を接続した。三者の統合区間でも復号一致アンカーを保持し、引用表記の違いだけで無変更側の行が追加・削除になる回帰を修正した。後続のGNU行算法接続は下記に記載する。旧callerの全設定、フィルター後の座標、全Unicode/パーサー分岐とプロジェクトの追加設定は引き続き未完了。元関数goldenの出典は[表行対応fixture](../tests/Fixtures/LineAlignment/README.md)。

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

旧`ImgMergeFrm.cpp`のLoadOptions／SaveOptionsは倍率・閾値等をOptionsMgrの全体設定へ保存する。今回のJSONプロジェクト保存は、その全体設定の移行完了を意味しない。旧全体設定と全表示オプション、未保存原画・Undo履歴・選択領域のセッション保存は未完了。利用手順と上限は[画像の契約](IMAGE-VIEWER.md)を参照する。

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

## 静止画像GUIの編集・PNG保存

二者の両方向・三者の6方向の選択／全領域コピー、競合以外の自動コピー、共有Undo／Redoを通常画像ビューへ接続した。保存は原画PNGの別名保存とし、成功した側だけのパス・比較済みパス・snapshot・保存点を更新する。閾値再比較は編集を保持し、HTMLは未保存の原画も反映する。未保存画像の包装は拒否する。仮sessionと描画をまとめて採用し、取消・古い完了では直前の状態を保持する。保存中の編集・再比較・破棄を拒否し、全タブの入力・フィルター・workspace・readonly・リンクを置換直前にも確認する。

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

原文区間付き文書と共有行対応モデルを導入し、GUI・`--table`・単体/包装HTMLの判定を統一した。完全一致アンカーの間で上限付き類似対応、三者では祖先対応と左右挿入対応を統合する。セル編集は一つの原文区間だけを変更し、Undo/Redo、readonly・祖先・ghost・古い座標の拒否、既存保存へ接続した。検索は前後・折返し・case・regex・word・ペイン指定、画面は32列ページと行仮想化。同セル内検索、固定文字範囲、一件/選択ペイン全置換と一括Undoへ拡張した。旧方式の全スコア・矩形文字編集・raw横断検索は未完了。[表の契約](TABLE-EDITOR.md)を参照する。

通常DLLの全E2Eは2954成功・0失敗・8スキップ、headless UIは139成功・0失敗。ローカルWindows x64 Native AOTの発行と139 UI成功を確認した。入力・出力・行対応JSON・実テキスト入力/ボタン操作・UTF16BE BOM保存後のbytes・PNGを`artifacts/e2e/table-full`・`artifacts/verification/table-repaired`へ保存する。疎な表は各側2万実セル/1万列/10001行で、表示コントロール数を制限し、従来2億座標相当の検索を実セルのみで完了した（ローカル通常DLLの1実測25 ms、比較用benchmarkではない）。

ローカルWindows x64 Native AOT全E2Eは2985成功・0失敗・3スキップ。[GitHub .NET CI 36796867483](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867483)は実装SHA `d14a9a9932c771a53e7bb8840b9dc4a84e32ea46` の4構成で成功。Windows x64/ARM64は各2985成功・0失敗・3スキップ、Mac Intel/ARM64は各2994成功・0失敗・2スキップ、UIは各139成功。SDK10.0.401、CS/IL/MSB警告0をログ・manifestで確認。同SHAの[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867472)もワークフロー成功。全構成の入力・出力・PNG・JSON・ログを`artifacts/github/36796867483`へ保存した。WindowsのUnixモードとMac固有経路、ファイルシステムのcase/NFC alias作成可否によるスキップを集計へ保持する。GUIでUTF16BE BOM付き保存した表を別のNative AOTプロセスで再読込みし、Python標準CSVパーサーで三者の全セル値・順序を照合して一致した（`external-reload-oracle.json`）。新規文脈のレビューはagent thread limitで未実施。既存担当の補足確認でクリック例外・疎な表示/検索の膨張を修正し、記録を`artifacts/verification/table-next`に保持する。headless操作と通常デスクトップの実測は区別する。

## 実行した検証

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
