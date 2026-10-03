# 比較プロバイダー

## 標準プロバイダー

`BuiltinComparisonProviders.CreateDefault()` は以下のIDを登録した `ComparisonProviderRegistry` を返します。各プロバイダーの `Formats` は自身のIDを含み、`ComparisonRequest(left, right, provider.Id)` を渡します。ファイル・URLの取得は `CompareAsync` を明示的に呼んだときだけです。取得した正規化テキストは比較用であり、元文書への書き戻しには使えません。

| ID | 比較対象 | 正規化・制約 |
| --- | --- | --- |
| `xml` | XMLファイル | 要素・属性の名前空間を展開し属性を名前順にソート。要素順・本文・空白・改行・コメント・処理命令を保持。XML宣言・名前空間の接頭辞・CDATA表記の違いは無視。 |
| `html-source` | HTMLファイルのソース | 改行をLFに統一。属性順・空白・マークアップは保持。 |
| `html-text` | HTMLファイルの静的テキスト | HTMLエンティティを復号し、head/script/style/templateを除外。ブロックタグを改行、連続空白を1文字に整理。 |
| `web-source` | HTTP/HTTPS応答ソース | 明示したURLにGETし、HTMLまたはテキスト応答を取得。ソースの改行をLFに統一。 |
| `web-text` | HTTP/HTTPS応答の静的テキスト | GETした本文に `html-text` と同じ抽出を適用。 |
| `office` | DOCX・PPTX・XLSX | ZIP内のOffice Open XMLから本文・セル・数式を抽出。コンテナーをディスクへ展開しない。 |
| `tar` | tar・tar.gz/tgz・tar.bz2/tbz2/tbz・tar.Z/taz | 名前・エントリ型・リンク先・非圧縮サイズ・内容SHA-256を名前順に比較。格納順・時刻・権限・所有者を無視。 |
| `tar-metadata` | tar・tar.gz/tgz・tar.bz2/tbz2/tbz・tar.Z/taz | `tar` に加え、権限・所有者uid/gid・UTC更新日時を比較。 |
| `archive` | 7z・RAR・ZIP・TAR・TAR.GZ・TAR.BZ2・TAR.Z | 名前・型・実測サイズ・内容SHA-256を比較。コンテナー形式・格納順・時刻・暗号化の違いは無視し、空ディレクトリは保持。パスワード指定はGUI/専用CLIを使う。 |

XMLはW3C C14Nの実装ではありません。DTDと外部エンティティを禁止し、入力は16 MiB、深さは128まで、正規化結果は64 Mi文字までです。DTDを使わないXMLでは要素内の空白を無意味と判断できないため、インデント変更も差分になります。`xml:space` がなくても本文の空白を保持します。`xsi:type`、XML Schemaのtype/ref/base/itemType/substitutionGroup/refer/memberTypes、および明示的にxs:QName/xs:NOTATIONと指定された本文は、その位置の名前空間束縛からURIとローカル名へ正規化します。同じURIのprefix変更を無視し、URI変更は差分にします。不正・未定義prefixの既知QNameは拒否します。未知の属性値・本文はQNameと断定せず、字面と検出したprefixの束縛を併記します。この保守的な扱いではprefixだけの変更も差分になり得ます。未宣言prefixの未知の字面や、スキーマなしでunprefixed値がQNameかどうかの判別は行いません。

HTMLファイル・HTTP本文は8 MiBまでです。ローカルHTMLの文字コードはBOM、BOMがなければ厳密なUTF-8です。HTTPはBOMを優先し、その次にContent-Typeのcharset、宣言がなければUTF-8を使います。HTML meta charsetの推定はしません。未対応の文字コードと復号できないバイト列はエラーにします。HTTPは1取得30秒、リダイレクト5回まで、各転送先もHTTP/HTTPSで認証情報を含まないURLに限定します。圧縮応答にも実際の展開後8 MiB上限を適用します。Cookie・保存済み認証情報は使用しません。

HTMLテキスト抽出は引用符付き属性を考慮する限定トークナイザーです。タグ開始はASCII英字などの開始候補だけを認識し、`1 < 2`や`<3`の不等号は本文に保持します。head/script/style/templateの終了は名前直後のHTML空白・`/`・`>`とタグ終端を確認し、`</scripture>`はscript終了と解釈しません。HTML5のブラウザーDOM、JavaScript実行、CSS、hidden属性、画像alt、生成コンテンツ、画面レイアウトは評価しません。titleなどhead内の文字は本文から除きます。ブラウザーで見える文字の完全な再現ではありません。

Officeは10万ZIPエントリまで、読むXMLは各16 MiB・合計64 MiB、抽出結果は64 Mi文字までです。同名ZIPエントリや宣言値と実サイズの不一致は拒否します。DOCXは本文・ヘッダー・フッター・脚注・文末脚注の段落、タブ、改行を抽出します。PPTXはpresentation.xmlのsldIdLstを順に読み、presentation.xml.relsの内部slide参照を解決して表示順で段落を抽出します。見出しは表示位置であり、ファイル名・rIdの変更だけでは差分にしません。未参照スライドは比較対象から除外し、外部・未解決・重複した参照は拒否します。XLSXはworkbookのシート順とシート名、セル位置の行・列順、共有文字列を解決した値、セル種別、数式・数式属性・キャッシュ値を比較します。共有文字列の格納順・参照番号の違いは無視します。セル位置rを省略したシート、Strict XLSXは未対応です。DOCX/PPTXの標準的なTransitional/Strict本文名前空間に対応します。

Officeの書式・スタイル・図表・画像・埋め込みオブジェクト・コメント・PPTXノート・変更履歴の意味的解釈・数式再計算は未対応です。日付はXLSXに保存された数値として比較し、書式を解釈しません。数式キャッシュは古い場合があり、その差も表示されます。旧バイナリ形式DOC/XLS/PPT、暗号化Office、ODF、PDF、OCRは未対応です。

tarは10万エントリ、各内容256 MiB、ヘッダーとパディングを含む展開後ストリーム1 GiBまでです。パスを抽出・実行せず、シンボリックリンク・ハードリンクも参照しません。同名エントリは曖昧な比較を避けるため拒否します。PAXの任意拡張属性、ACL、所有者の文字列名、スパース形式の意味的な等価性は比較しません。

## Managed アーカイブサービスの検証契約

`ManagedArchive` の実装前に想定した失敗と期待結果です。GUI/CLIと別プロセスの実アプリE2Eで検証します。

| 失敗条件 | 期待結果 |
| --- | --- |
| パスワード未指定・不一致、暗号化ヘッダー、破損・未対応圧縮 | 読み取り失敗。パスワードを診断・外部プロセスへ渡さない。 |
| solid アーカイブの複数エントリ | 同じ reader で順次復号し、全内容のサイズ・SHA-256 を取得する。 |
| エントリ数、入力サイズ、単一・合計展開サイズ、プレビュー・出力サイズ上限 | 宣言値と実測値の両方で拒否し、既存出力を保持する。 |
| 重複名、ルート外パス、絶対パス、リンク、分割アーカイブ | 拒否し、格納名からファイルシステムへ自動展開しない。 |
| キャンセル、読み取り専用出力、出力先リンク、入出力同一 | 原本と既存出力を保持し、今回作った途中出力だけを削除する。 |
| 作成・再梱包・全件展開 | 一時出力を閉じてから置換。全件展開は未存在のディレクトリへ全検証後に公開。RAR作成・暗号化出力は未対応として明示する。 |

`ManagedArchive` は SharpCompress 0.50.4（MIT）と.NET標準APIを使う純managedサービスです。`ReadManifest(path, password, cancellationToken)`は`Format`（`7z`・`rar`・`zip`・`tar`・`tar.gz`・`tar.bz2`・`tar.Z`および明示wrapper鎖の`zip.gz.Z`等）と、正規化した相対パス、ディレクトリ種別、実測サイズ、内容SHA-256、暗号化フラグ、更新日時を返します。エントリはパスのordinal順、ディレクトリはサイズ0・ハッシュ空文字です。全内容を順次復号するためsolidでも読み落としません。TARの安全な先頭`./`は除去し、ルートディレクトリ自身は比較一覧に含めません。

`ReadEntry`は全検証後に選択したバイト列を返します。`WriteArchive(destinationPath, entries, cancellationToken)`と`Repack(sourcePath, destinationPath, password, cancellationToken)`は出力拡張子から7z・zip/jar/ear/war/xpi・tar・tar.gz/tgz・tar.bz2/tbz2/tbz・tar.Z/tazを選びます。`ManagedArchiveWriteEntry(Path, Content, LastModifiedTime)`の`Content`は`ReadOnlyMemory<byte>?`、nullはディレクトリです。既存の`WriteSevenZip`/`RepackToSevenZip`は同じ処理の7z指定APIです。7zは非solid LZMA2、全出力は非暗号化です。更新日時を伝達しますがZIPは1980〜2107年・2秒精度へ制約し、元形式の精度・タイムゾーンの完全保存は保証しません。属性・ACL・圧縮方式・solid設定・元暗号化は保存しません。

`ExtractAll(sourcePath, destinationDirectory, password, cancellationToken)`は未存在のディレクトリへ全件を保存します。兄弟の一時ディレクトリへ読み出し、全件の検証後に移動して公開します。既存ディレクトリ（空でも）・ファイル・リンクを拒否し、失敗やキャンセルで今回の途中出力を除去します。安全な格納名だけを保存し、実行ファイルを起動せず、リンクを作成しません。更新日時を反映しますが属性・全メタデータは保持しません。

暗黙の親を含む10万パスノードと、NFC辞書キー＋元表記の合計16 Mi文字も制限します。TAR補助メタデータは各1 MiB、復号したヘッダー・パディングを含む総量は1 GiBまでです。全ヘッダーのchecksum、PAXのsize指定、2ブロック終端を検証します。GZipは各連結メンバーのヘッダー・CRC・ISIZE・フッターの存在を検証し、途中欠損を拒否します。TAR本文は形式上CRCを持たず、意味的な内容改変を検出するものではありません。

既定上限は10万エントリ、入力1 GiB、各内容256 MiB、展開合計1 GiB、プレビュー16 MiB、出力1 GiBです。`ManagedArchiveLimits` で指定できます。終端containerのプレビューcaptureは選択したファイルだけ、再梱包captureは一度に1ファイルだけを保持します。明示wrapper入力は各層を完全検証する中間MemoryStreamも保持し、下記の共有予算とpeak制約を使います。入力も出力もリンクを含むパスを拒否します。格納名の絶対・親参照・空セグメント・制御文字・Windows予約名・末尾空白/点・重複（大文字小文字も区別しない）、リンク属性とRARリダイレクト、分割ボリュームを拒否します。`ReadManifest` は格納先を作成せず、再梱包の途中出力は指定した出力先ディレクトリに作成し、完了後に置換します。読み取り専用出力は拒否します。上限超過・失敗・キャンセルでは原本・既存出力を保持し、途中出力を削除します。

ZIPはライブラリの`CheckCrc`付き抽出でCRC値0とWinZip AESの認証も検証します。他の形式は公開CRCが0以外なら実測CRCと照合します。暗号化RARのCRCは秘密鍵で変換されるため比較しません。7zのCRC省略と値0の区別は公開APIでは未確認で、暗号化RARとともに完全性は復号器の検証範囲に依存します。あらゆる破損の検出を保証しません。パスワード指定時のライブラリ例外は本文とinner exceptionを残さない定型診断へ置き換えます。CPUを使う同期APIなのでUIからはバックグラウンドで呼び、CancellationTokenを渡します。ライブラリの内部処理からI/Oへ戻るまではキャンセルが遅れる場合があります。

GUIはマスク付きパスワード、再比較、先頭4096バイトのプレビュー、エントリ書出し、全件展開、形式を選ぶ非暗号化再梱包を提供します。`ReadEntryPreview`は保持する先頭だけを制限し全内容を検証、`ReadEntryForExport`は既定256 MiBまでです。フォルダーからの作成は出力ファイルを入力一覧から除外し、同じ出力先での再作成にも対応します。GUI保存は左右の原本を上書きしません。

CLIは`--archive-list ARCHIVE`、`--archive-compare LEFT RIGHT`、`--archive-entry ARCHIVE ENTRY OUTPUT`、`--archive-repack INPUT OUTPUT`、`--archive-create SOURCE_DIRECTORY OUTPUT`、`--archive-extract INPUT NEW_DIRECTORY`です。暗号化読込みには末尾の`--password-stdin`を使い、リダイレクトされたUTF-8標準入力へ1アーカイブにつき1行を送ります（比較は左・右の2行）。各行は4096文字までで、引数・環境変数・設定・ログにパスワードを渡しません。比較の終了コードは一致0・差分1・エラー2です。作成はパスワード指定を受け付けず、再梱包も出力は暗号化しません。TAR.Zはowned managed UNIX compress codecで読み書きします。Z magicと3byte header、9〜16bit・block/nonblock読込み、幅/CLEAR/辞書を検査し、内側TARの同じ上限・checksum・終端検査へ通します。出力は16bit block形式で、正常TAR終端後だけ残codeを明示完了しatomic保存します。Flushはcode列を終端化しません。裸の単一GZip/BZip2/Z非TARファイルは未対応です。ZにはCRC・宣言長・明示EOFがなく、全semantic改変や末尾padding欠損の完全検出を保証しません。padding zeroを必須制約にしません。[固定原本と独立復号E2E](../../tests/Fixtures/Archives/TarZ/README.md)では公式ncompress別buildとPython標準tarfile、ローカルfull7Zipを使用しますが、製品や通常.NET buildへ外部tool・C/compiler・DLL探索を追加しません。

RAR作成、暗号化出力、分割、CAB/LZH/ISO/MSI等の全旧形式、属性・リンクの保存は未対応です。4 RIDの実測状況は[移行対応表](../../Docs/MIGRATION.md)を参照してください。SharpCompress のライセンスは `SharpCompress.LICENSE.txt` としてビルド・発行先へコピーします。上流の [形式表](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/docs/FORMATS.md)、[使用方法](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/USAGE.md)、[パッケージ](https://www.nuget.org/packages/SharpCompress/0.50.4) を参照してください。

## 外部実行ファイル

`IComparisonProvider` はAOT対応の明示登録用契約です。`ComparisonProviderRegistry` はDLL探索やダウンロードを行いません。`ExecutableComparisonProvider` はユーザーが指定した絶対パスの実行ファイルを、`CompareAsync` 呼び出し時だけシェルなしで起動します。

標準入力にはUTF-8のJSONを1行送り、入力を閉じます。

```json
{"protocolVersion":1,"leftPath":"C:/data/left.docx","rightPath":"C:/data/right.docx","format":"document"}
```

標準出力には次のJSONだけを返し、終了コード0で終了してください。診断は標準エラーへ出力します。

```json
{"protocolVersion":1,"summary":"2箇所の差分","leftText":"左の正規化テキスト","rightText":"右の正規化テキスト"}
```

応答は最大4194304文字、標準エラーは最大65536文字、既定の制限時間は2分です。キャンセル・時間切れ・応答上限超過ではプロセスツリーを終了します。引数は`ProcessStartInfo.ArgumentList`で個別に渡します。任意の実行ファイルの安全性はこの契約では保証しません。対象ファイルを読める実行ファイルだけを明示的に登録してください。

アプリの標準ビューはこの契約から外部実行ファイルを自動起動しません。PDF・OCRなど、標準プロバイダーが対応していない形式を扱う実行ファイルは別途明示的な登録が必要です。新しい有料サービス・外部API・ダウンロード処理は含みません。

標準tar/tar-metadata providerのTAR読取り・復号・hashは背景タスクで実行し、GUIの中止操作を受け付けます。callerは完成結果をEditorへ採用する直前にも取消を確認します。共有TAR検査、リンクを追跡しないmetadata表示、ManagedArchiveのリンク拒否を維持します。実GUIのearly/late中止では、完成した全canonical本文・metadata、最終両Editorの前回本文保持、取消表示と入力SHAをJSON/PNGで確認します。

### 明示 container wrapper 入力

`SupportsInput` は既存入力に加え、ZIP/JAR/EAR/WAR/XPI・7z・RARの終端名を `.gz`/`.bz2`/`.Z` で1段以上包む明示名を認識します。全公開read/preview/export/repack/extractと標準archive providerで同じRead経路を使い、GUI Autoへ接続します。各wrapperのmagicと全member/footer/EOFを検証してから次段へ進み、GZip FNAMEは物理pathやhandler選択に使いません。contained archive entryはleafです。既存単段TARのraw/header予算とconsumer policyは維持します。裸compressed非archive、TAR複数wrapper統一、GUI内側entry navigation、wrapper出力作成は次工程です。

`ManagedArchiveLimits` 末尾の `MaximumWrapperDepth`（既定8）と `MaximumWorkBytes`（8 GiB）は新明示鎖に適用します。各中間内容は `MaximumEntryBytes` 以下、全wrapper復号output＋終端entry outputは同一 `MaximumDecodedBytes` を共有します。wrapper/member/entry/implicit parent件数とpathkey＋元表記/名前metadataも共有し、上限の次1byteを拒否します。作業量は実read（seek後の再read含む）＋復号output＋明示処理したmetadataで、codec内部の厳密CPU命令数ではありません。現在入力と次MemoryStreamだけを保持し中間ToArrayやtemp保存を行いません。終端capture APIの最後のcopyは残るため、peakは終端buffer＋capture＋return copy（概ね最大3×entry上限、成長容量/codec workspace別）です。

BZip2は公開decoderのsingle member strict CRC/footerを使い、全次memberの4byte `BZh1`〜`BZh9`をowned経路で検査します。GZipは既存header/FHCRC/CRC/ISIZE/連結検査、Zはowned codecの構造検査と完全性の限界を維持します。[固定wrapper原本](../../tests/Fixtures/Archives/Wrappers/README.md)と独立Python/公式Z全層oracleを実App E2Eで使います。

GUIは初期検証失敗を空panelとして採用せず、任意の左右masked passwordを持つgeneric再試行を提供します。4096文字以下/空nullで保存・ログへ転記しません。Retry成功時だけ採用し、Cancel/×/Stop/失敗/古い完了では確定Rows/preview/表示元を保持します。Refreshは候補を背景計算し採用直前に取消/世代を再確認、保持したpanelも再操作できます。tab closeでpanel lifetimeと進行operationを解放します。

### 明示した内包アーカイブの入力

`ArchiveSource` は絶対物理root、正規化した `EntryChain`、任意のroot SHA-256をimmutableに保持します。password・復号buffer・仮想captionを含みません。`WithChild(entry)` はcontainerを一段追加します。`ResolveManifest(source, containerPasswords, cancellationToken)` はSHA付きsourceと最終manifest、`ResolveEntry(source, entry, maximumBytes, containerPasswords, cancellationToken)` は完全検証後の最終entry bytesを返します。rootの全SHAを読込み前、同じhandleの読込み後、現在の物理pathで照合し、指定SHAの不一致・変更・path差替えを拒否します。悪意ある並行writerに対するOS snapshotの保証ではありません。

rootは深度0、格納entryへの遷移と各圧縮wrapperはそれぞれ1で、既定8の共通深度予算を使います。全階層の実read・SHA再read・復号・名前・件数・TAR header・metadataは一つの予算を共有します。各内包containerの保持は256 MiB以下ですが、単一のstreaming TAR全体を256 MiBへ縮めません。raw TARはheader／metadata／paddingを含むbyteを一度だけ復号量へ計上し、外側で既に復号したraw TARのpayloadを重ねて復号計上しません。選択entryが正常でも、後方のsibling・footer・EOFの検証が成功するまで次のcontainerや出力を公開しません。中間ToArray・temp展開は使わず、親から次bufferへ所有権を移します。内側wrapperの復号中は格納containerも保持し、最終entryには返却copyが必要です。codec workspaceと成長容量を含む厳密なpeak RAM保証ではありません。

CLIの `--archive-source-list DESCRIPTOR_JSON`／`--archive-source-entry DESCRIPTOR_JSON ENTRY OUTPUT` は最大1 MiBのdescriptorを読みます。項目は `rootPath`、`entryChain`、任意の `rootSha256`／`limits` だけで、未知・重複propertyは拒否します。rootだけをdescriptorから相対解決し、chainを物理pathとして扱いません。`limits` は `ManagedArchiveLimits` と同名のcamelCase項目で、正の整数かつ既定値以下だけを受け付けます。末尾 `--password-stdin` はroot＋chain数の行を外側から順に読み、plaintext層は空行を使います。認証情報は保存・出力せず、各操作の終了時に保持参照を解放します。出力はroot・descriptor・readonly・linkを保護してatomic置換します。

[固定Source入力](../../tests/Fixtures/Archives/Sources/README.md)の独立Python／実CLI E2Eで階層全体のCRC・内容・共有予算を検証します。GUI子タブ、workspace DTO、深いcontainerのrepack／全展開、包装・HTML経路への接続は未実装です。旧root経路へ代入して対応済みと扱いません。
