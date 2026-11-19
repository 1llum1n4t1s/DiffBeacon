# プラグイン

WinMerge の COM / ActiveX / DLL / スクリプトプラグインは、この Native AOT の実行ファイルには載らない。macOS にも COM は無い。代わりに、別プロセスの実行ファイルと JSON で話す。

## 比較プロバイダー

`ExecutableComparisonProvider` が、利用者が絶対パスで指定した実行ファイルだけを起動する。シェル経由では起動しない。

標準入力へ 1 行の JSON を書く。

```json
{"protocolVersion":1,"leftPath":"/abs/left","rightPath":"/abs/right","format":"txt"}
```

終了コード 0 のとき、標準出力は次の JSON である。

```json
{"protocolVersion":1,"summary":"...","leftText":"...","rightText":"..."}
```

`protocolVersion` が 1 でない応答は拒否する。標準出力は 4 MiB、標準エラーは 64 KiB まで。制限時間の既定は 2 分。自動探索やダウンロードはしない。

## 登録

GUI の「プラグイン > 外部ツールを追加」と、コマンドラインの外部プロバイダーが、この形式の実行ファイルを登録する。未登録の旧プラグイン名は、比較プロジェクトに文字列として残り、実行しない。
