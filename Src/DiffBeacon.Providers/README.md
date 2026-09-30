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
| `tar` | tar・tar.gz・tgz | 名前・エントリ型・リンク先・非圧縮サイズ・内容SHA-256を名前順に比較。格納順・時刻・権限・所有者を無視。 |
| `tar-metadata` | tar・tar.gz・tgz | `tar` に加え、権限・所有者uid/gid・UTC更新日時を比較。 |
| `archive` | 7z・RAR・ZIP | 名前・型・実測サイズ・内容SHA-256を比較。コンテナー形式・格納順・時刻・暗号化の違いは無視し、空ディレクトリは保持。パスワード指定はGUI/専用CLIを使う。 |

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
| 7z 再梱包 | 一時出力を閉じてから置換。RAR 作成・暗号化出力は未対応として明示する。 |

`ManagedArchive` は SharpCompress 0.50.4（MIT）を使う純 managed サービスです。`ReadManifest(path, password, cancellationToken)` は `Format`（`7z`・`rar`・`zip`）と、正規化した相対パス、ディレクトリ種別、実測サイズ、内容 SHA-256（大文字16進数）、暗号化フラグ、記録された更新日時を返します。エントリはパスの ordinal 順です。ディレクトリのサイズは0、ハッシュは空文字です。全内容を順次復号して検証するため、solid でも内容を読み落としません。

`ReadEntry(path, entryPath, password, cancellationToken)` は全エントリの検証後に選択したファイルのバイト列を返します。`RepackToSevenZip(sourcePath, destinationPath, password, cancellationToken)` は元内容を非 solid LZMA2 7z に再梱包します。`WriteSevenZip(destinationPath, entries, cancellationToken)` は明示した `ManagedArchiveWriteEntry(Path, Content, LastModifiedTime)` から7zを作ります。`Content` は `ReadOnlyMemory<byte>?` で、null はディレクトリです。日時は意味を変えずライブラリへ渡しますが、元形式の精度・タイムゾーン情報の完全保存は保証しません。属性・ACL・圧縮方式・solid 設定・元暗号化は保存しません。

既定上限は10万エントリ、入力1 GiB、各内容256 MiB、展開合計1 GiB、プレビュー16 MiB、出力1 GiBです。`ManagedArchiveLimits` で指定できます。プレビューは選択したファイルだけ、再梱包は一度に1ファイルだけをメモリへ保持します。入力も出力もリンクを含むパスを拒否します。格納名の絶対・親参照・空セグメント・制御文字・Windows予約名・末尾空白/点・重複（大文字小文字も区別しない）、リンク属性とRARリダイレクト、分割ボリュームを拒否します。`ReadManifest` は格納先を作成せず、再梱包の途中出力は指定した出力先ディレクトリに作成し、完了後に置換します。読み取り専用出力は拒否します。上限超過・失敗・キャンセルでは原本・既存出力を保持し、途中出力を削除します。

ZIPはライブラリの`CheckCrc`付き抽出でCRC値0とWinZip AESの認証も検証します。他の形式は公開CRCが0以外なら実測CRCと照合します。暗号化RARのCRCは秘密鍵で変換されるため比較しません。7zのCRC省略と値0の区別は公開APIでは未確認で、暗号化RARとともに完全性は復号器の検証範囲に依存します。あらゆる破損の検出を保証しません。パスワード指定時のライブラリ例外は本文とinner exceptionを残さない定型診断へ置き換えます。CPUを使う同期APIなのでUIからはバックグラウンドで呼び、CancellationTokenを渡します。ライブラリの内部処理からI/Oへ戻るまではキャンセルが遅れる場合があります。

GUIは左右のマスク付きパスワード欄と再比較、先頭4096バイトのプレビュー、エントリ書出し、非暗号化7z再梱包を提供します。`ReadEntryPreview`は保存する先頭だけを制限し、全エントリを検証します。`ReadEntryForExport`は既定256 MiBまでの保存用読込みです。フォルダーからの7z作成は出力ファイルを入力一覧から除外し、同じ出力先での再作成にも対応します。GUI保存は左右の原本を上書きしません。

CLIは`--archive-list ARCHIVE`、`--archive-compare LEFT RIGHT`、`--archive-entry ARCHIVE ENTRY OUTPUT`、`--archive-repack INPUT OUTPUT`、`--archive-create SOURCE_DIRECTORY OUTPUT`です。暗号化読込みには末尾の`--password-stdin`を使い、リダイレクトされたUTF-8標準入力へ1アーカイブにつき1行を送ります（比較は左・右の2行）。各行は4096文字までで、引数・環境変数・設定・ログにパスワードを渡しません。比較の終了コードは一致0・差分1・エラー2です。作成はパスワード指定を受け付けず、再梱包も出力は暗号化しません。

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
