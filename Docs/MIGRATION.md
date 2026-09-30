# .NET 10 / Avalonia 移行の到達点

この変更は新しい比較アプリの実装と Native AOT 発行経路の追加であり、旧 WinMerge の全機能との互換性を完了したものではない。旧 C++ / MFC の `Src/Merge.rc` と各実装は残っているが、新しい `DiffBeacon.slnx` の通常ビルド経路には含まれない。旧機能の存在と、新しいアプリで実装・検証された機能を区別する。

| 旧機能・処理分岐 | 新しい実装の範囲 | 判定・残作業 |
| --- | --- | --- |
| テキストの二者比較・三者マージ | 差分行・行内強調、差分移動、両方向コピー、編集、保存、競合表示、CLI、左・祖先・右・結果の4ペイン、差分単位の順序付き採用・Undo/Redo・未解決状態・行の由来 | [マージ結果セッション](MERGE-SESSION.md)を実装。旧エディターの構文強調、矩形選択、同期点、移動行、全ショートカット、由来の行番号マージン表示・セッション永続化は未完了 |
| フォルダー比較 | 再帰比較、片側のみの項目、キャンセル、選択項目のコピー API | 基本経路を実装。コピーはリンクを拒否し、削除 API は提供しない。同期・全状態列・旧シェル操作の同等性は未完了 |
| 表形式 | CSV / TSV の解析とセル差分 | 基本機能を実装。旧 table の全区切り指定、引用符設定、編集・検索操作の同等性は未完了 |
| Hex / バイナリ | バイト比較、ページ単位の16進編集、差分範囲コピー、別名保存 | 表示・編集は各16 MiB上限。同じオフセットで比較し、挿入位置の再整列や旧Hex全操作の同等性は未完了 |
| 画像 | 左右表示、重ね合わせ、倍率・閾値、ピクセル差分 | 各1600万ピクセル上限。複数ページは先頭だけ。ベクター、OCR、全画像形式・画像マージは未完了 |
| Web / XML / HTML / Office | XML正規化、HTML静的本文、HTTP応答のソース・本文、DOCX/PPTX/XLSX本文 | 標準プロバイダーを明示選択。ブラウザーのDOM・JavaScript・画面・リソースツリー、Officeの書式・旧形式・PDF/OCRは未完了。詳細はプロバイダーREADME |
| Archive / プラグイン | 7z/RAR/ZIPの内容比較・暗号化ヘッダー/内容・solid読込み、プレビュー・エントリ保存、非暗号化7z作成/再梱包、TAR/TAR.GZ比較、実行ファイル用JSON契約 | 旧submoduleの通常ビルド依存は解除。暗号化出力・分割・CAB/LZH/ISO/MSI等の全旧形式・メタデータ/リンク保存・旧ActiveX/DLL ABIは未完了。7zのCRC省略と値0の区別は現行ライブラリの公開APIでは未確認。変換結果を元ファイルへテキスト保存しない |
| シェル統合 | 通常のデスクトップ起動、CLI、macOS `.app` 生成 | Explorer / Finder 拡張、旧コンテキストメニュー、インストーラー登録は未実装 |
| 多言語 | 新 UI は日本語を中心に実装 | 旧翻訳カタログとローカライズ切替、RTL、全ダイアログの同等性は未完了 |
| 詳細フィルター | 大文字小文字、4種の空白処理、空行、行正規表現、ASCII数字・CStyle/CSharp/Python/XMLコメントの除外、順序付き置換、旧`.flt` include/exclude、名前・拡張子・サイズ・日時の条件式 | 比較前処理は原文を保持し、GUI・CLI・フォルダーへ適用。同梱12 `.flt` の読込みを確認。内容検索、左右別属性、関数・算術、PCRE固有構文、全旧構文のコメント処理、複数行置換、全表示フィルターは未完了 |
| プロジェクト / レポート | source-generated JSON保存、単一組`.WinMerge` XML読込み、HTML / JSONレポート | パス・対応オプション・プロバイダーID・ファイルフィルターパスを保持。外部実行ファイルは保存IDだけで自動登録・実行しない。旧複数組プロジェクトは拒否 |
| Native AOT Windows x64 / ARM64 | 対応 RID と同 OS 発行スクリプト、CI マトリクス | 両アーキテクチャのGitHub runnerで発行・UI自己検証・CLI E2E成功。実測結果は下表 |
| Native AOT macOS x64 / ARM64 | 対応 RID、`.app` / tar、CI マトリクス | Intel / Apple SiliconのGitHub runnerで発行・UI自己検証・CLI E2E成功。署名・公証・公開は実施しない |

WinMerge XML の要素と window-type の対応は `Src/ProjectFile.cpp`、`Src/MergeCmdLineInfo.h` を参照した。`left` / `middle` / `right`、`ignore-case`、`ignore-blank-lines`、`ignore-numbers`、`ignore-comment-diff`（互換別名`ignore-comments`）、`white-spaces` を読み込む。`white-spaces` の旧モード 1 は空白量の変更を無視、2 は全空白を無視として区別する。コメント構文は左・祖先・右の最初の対応拡張子から選択し、未知の形式では除去しない。旧 `filter` はファイルフィルターであり、新しい行正規表現へ置き換えない。旧 OS の絶対パスは保持し、移行先で利用者が選び直す。

コピー処理はルート外のパス、`.` / `..`、既存の symlink / junction を拒否し、ファイルを一時ファイルへ書いた後に置き換える。途中のキャンセルでは、既に完了したファイルや作成したディレクトリが残る。検査とファイル操作の間に別プロセスがパスを差し替える敵対的な状況まで防ぐ OS ハンドル単位の保護は未実装。

全機能移行完了の判定には、上の未完了項目の実装と、Windows / macOS の対象アーキテクチャ上で再現できる検証結果が必要。CI 定義の追加だけで、ビルド・起動・配布の成功を確認したことにはならない。

## 実行した検証

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
