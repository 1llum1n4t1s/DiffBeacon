# DiffBeacon

WinMerge を基にした、ファイル・フォルダー比較アプリです。C# / .NET 10 / Avalonia UI への移植を進めています。**WinMerge の全機能との互換性はまだ完成していません。** 対応状況は [移行一覧](Docs/MIGRATION.md) を参照してください。

## 使い方

アプリを起動し、左と右のファイルまたはフォルダーを選んで「比較」を押します。共通の祖先ファイルを指定すると、3方向マージを実行できます。差分を選択し「選択差分 →」または「← 選択差分」でコピーし、編集した側を保存します。

- テキスト：差分・行内強調、編集、差分移動、検索、空白・大文字小文字・空行・数字・コメント・正規表現の除外、順序付き置換フィルター。
- マージ結果：二者・三者の差分ごとに左・祖先・右を選択し、複数の採用順序を指定。手編集と採用のUndo/Redo、未解決数、行の採用元を表示。
- フォルダー：再帰比較、内容・SHA-256・日時とサイズ、除外パス、選択項目のコピー。
- バイナリ：16進表示・編集・差分範囲コピー・別名保存。
- 画像：左右表示、重ね合わせ、倍率、閾値付きピクセル差分。
- JSON / CSV / TSV：構造の正規化・セル単位の比較。
- ZIP：エントリの内容ハッシュ比較、プレビュー、エントリ保存。
- XML / HTML / Web応答 / Office / TAR：標準プロバイダーで比較用テキストへ変換。外部実行ファイルは明示登録したときだけ使用。
- ファイルフィルター：旧 `.flt` の include / exclude、ファイル・ディレクトリ規則、名前・サイズ・日時の条件式。
- 比較プロジェクトの保存・読込み、単一組の WinMerge プロジェクトの読込み、unified patch、HTMLレポート。

複数の比較は「新しい比較」でタブを追加します。F7 / Shift+F7 で差分を移動できます。テキスト保存では読込み時の文字コードとBOMを保持します。バイナリの別名保存とZIPエントリの保存は、それぞれの形式別ビューの操作を使ってください。画像の編集・保存には対応していません。

## 対応環境

Windows x64 / ARM64、macOS Intel / Apple Silicon に対応する Native AOT 発行物を生成します。macOS の最低バージョンは14.0です。各プラットフォームの実測結果と未検証項目は [移行一覧](Docs/MIGRATION.md) を参照してください。

## 入手と起動

[GitHub Actions](https://github.com/1llum1n4t1s/DiffBeacon/actions) の成功した `.NET CI` 実行から、OS と CPU に合う `DiffBeacon-<RID>` 成果物を取得できます。

- Windows：`win-x64` または `win-arm64` の成果物を展開し、同梱ファイルを一緒に置いたまま `DiffBeacon.exe` を起動します。
- macOS：`osx-x64`（Intel）または `osx-arm64`（Apple Silicon）の成果物内の `DiffBeacon.app.tar.gz` を展開します。tar は実行権限を保持するため、展開した `DiffBeacon.app` を使ってください。署名・公証は発行工程に含まれていません。

コマンドラインでも比較できます。`DiffBeacon --help` で一覧を表示し、`DiffBeacon --compare LEFT RIGHT` でテキストを比較します。Windows では `DiffBeacon.exe`、macOS では `DiffBeacon.app/Contents/MacOS/DiffBeacon` が実行ファイルです。終了コードは0が一致・成功、1が差分・競合、2がエラーです。

## 制限と困ったとき

WinMerge の旧ActiveXプラグイン、ブラウザーのDOM・JavaScript・画面比較、Explorer / Finder 拡張、インストーラー登録は未対応です。プロバイダーで変換した内容は読取り専用で、元ファイルへのテキスト保存はできません。

- 上限エラー：テキスト読込みは既定64 MiB、画像は各1600万ピクセル、バイナリビューの表示・編集は各16 MiBが上限です。変換形式の範囲と制限は [プロバイダーの説明](Src/DiffBeacon.Providers/README.md) を参照してください。
- 保存できない：読取り専用属性や書込み権限を確認してください。読込み後に入力パスを変えた場合は、比較して開き直してから保存します。
- 文字化け：BOMなしの文字コード推定は完全ではありません。読込み・保存の対応とフォールバックは [テキスト処理の説明](Src/DiffBeacon.Core/README.md#テキスト比較と保存) を参照してください。

## 開発と検証

ソースからのビルドと検証・発行は [開発手順](Docs/DEVELOPMENT.md)、作業規約は [AGENTS.md](AGENTS.md)、システム構造は [DESIGN.md](DESIGN.md) を参照してください。

## ライセンス

元となった [WinMerge](https://github.com/WinMerge/winmerge) と同じく [GNU GPL v2](LICENSE.md) に従います。元の著作権表記とライセンスを保持しています。依存ライブラリのライセンスはそれぞれのパッケージに従います。
