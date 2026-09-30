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

XMLはW3C C14Nの実装ではありません。DTDと外部エンティティを禁止し、入力は16 MiB、深さは128まで、正規化結果は64 Mi文字までです。DTDを使わないXMLでは要素内の空白を無意味と判断できないため、インデント変更も差分になります。`xml:space` がなくても本文の空白を保持します。`xsi:type`、XML Schemaのtype/ref/base/itemType/substitutionGroup/refer/memberTypes、および明示的にxs:QName/xs:NOTATIONと指定された本文は、その位置の名前空間束縛からURIとローカル名へ正規化します。同じURIのprefix変更を無視し、URI変更は差分にします。不正・未定義prefixの既知QNameは拒否します。未知の属性値・本文はQNameと断定せず、字面と検出したprefixの束縛を併記します。この保守的な扱いではprefixだけの変更も差分になり得ます。未宣言prefixの未知の字面や、スキーマなしでunprefixed値がQNameかどうかの判別は行いません。

HTMLファイル・HTTP本文は8 MiBまでです。ローカルHTMLの文字コードはBOM、BOMがなければ厳密なUTF-8です。HTTPはBOMを優先し、その次にContent-Typeのcharset、宣言がなければUTF-8を使います。HTML meta charsetの推定はしません。未対応の文字コードと復号できないバイト列はエラーにします。HTTPは1取得30秒、リダイレクト5回まで、各転送先もHTTP/HTTPSで認証情報を含まないURLに限定します。圧縮応答にも実際の展開後8 MiB上限を適用します。Cookie・保存済み認証情報は使用しません。

HTMLテキスト抽出は引用符付き属性を考慮する限定トークナイザーです。タグ開始はASCII英字などの開始候補だけを認識し、`1 < 2`や`<3`の不等号は本文に保持します。head/script/style/templateの終了は名前直後のHTML空白・`/`・`>`とタグ終端を確認し、`</scripture>`はscript終了と解釈しません。HTML5のブラウザーDOM、JavaScript実行、CSS、hidden属性、画像alt、生成コンテンツ、画面レイアウトは評価しません。titleなどhead内の文字は本文から除きます。ブラウザーで見える文字の完全な再現ではありません。

Officeは10万ZIPエントリまで、読むXMLは各16 MiB・合計64 MiB、抽出結果は64 Mi文字までです。同名ZIPエントリや宣言値と実サイズの不一致は拒否します。DOCXは本文・ヘッダー・フッター・脚注・文末脚注の段落、タブ、改行を抽出します。PPTXはpresentation.xmlのsldIdLstを順に読み、presentation.xml.relsの内部slide参照を解決して表示順で段落を抽出します。見出しは表示位置であり、ファイル名・rIdの変更だけでは差分にしません。未参照スライドは比較対象から除外し、外部・未解決・重複した参照は拒否します。XLSXはworkbookのシート順とシート名、セル位置の行・列順、共有文字列を解決した値、セル種別、数式・数式属性・キャッシュ値を比較します。共有文字列の格納順・参照番号の違いは無視します。セル位置rを省略したシート、Strict XLSXは未対応です。DOCX/PPTXの標準的なTransitional/Strict本文名前空間に対応します。

Officeの書式・スタイル・図表・画像・埋め込みオブジェクト・コメント・PPTXノート・変更履歴の意味的解釈・数式再計算は未対応です。日付はXLSXに保存された数値として比較し、書式を解釈しません。数式キャッシュは古い場合があり、その差も表示されます。旧バイナリ形式DOC/XLS/PPT、暗号化Office、ODF、PDF、OCRは未対応です。

tarは10万エントリ、各内容256 MiB、ヘッダーとパディングを含む展開後ストリーム1 GiBまでです。パスを抽出・実行せず、シンボリックリンク・ハードリンクも参照しません。同名エントリは曖昧な比較を避けるため拒否します。PAXの任意拡張属性、ACL、所有者の文字列名、スパース形式の意味的な等価性は比較しません。7z・RAR・暗号化アーカイブは未対応です。

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

アプリの標準ビューはこの契約から外部実行ファイルを自動起動しません。PDF・OCR・7zなど、標準プロバイダーが対応していない形式を扱う実行ファイルは別途明示的な登録が必要です。新しい有料サービス・外部API・ダウンロード処理は含みません。
