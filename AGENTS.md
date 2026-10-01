# DiffBeacon の作業規約

## 対象と参照先

- 現行アプリの入口は `DiffBeacon.slnx`。`Src/DiffBeacon.App`、`Src/DiffBeacon.Core`、`Src/DiffBeacon.Providers` と `tests/` の C# プロジェクトを使う。通常ビルドに旧 C++ / MFC プロジェクトや submodule は不要。
- `Src` の大文字小文字を維持する。旧 WinMerge のソース、翻訳、プラグイン、インストーラーは未移植機能の参照用であり、存在だけで現行アプリの対応機能と判断しない。
- 構造と設計上の境界は [DESIGN.md](DESIGN.md)、操作は [README.md](README.md)、詳細な発行手順は [Docs/DEVELOPMENT.md](Docs/DEVELOPMENT.md)、互換性と実測範囲は [Docs/MIGRATION.md](Docs/MIGRATION.md) を参照する。

## 必須の検証

.NET 10 SDK（`global.json`）と PowerShell 7 を使う。E2E のアーカイブ独立検証には Python 3 も必要。実行ファイルの指定方法は [E2E の手順](tests/DiffBeacon.E2E/README.md) を参照する。コード変更後はリポジトリルートで次を実行する。

```powershell
dotnet build DiffBeacon.slnx -c Release
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/local
```

UI の変更は次の描画・操作検証も実行し、出力された PNG と JSON を確認する。

```powershell
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test artifacts/verification/managed
```

- 回帰検証は実アプリを別プロセスで呼ぶ既存 E2E に追加する。想定する失敗と期待結果を先に整理し、入力・出力・終了コード・ログ・`assertions.json` を再現可能な成果物として保持する。実行方式と限定実行は [tests/DiffBeacon.E2E/README.md](tests/DiffBeacon.E2E/README.md) を参照する。
- Native AOT に影響する変更は対象と同じ OS で `./build/Publish.ps1 -Rid <RID>` を実行する。対応 RID は `win-x64`、`win-arm64`、`osx-x64`、`osx-arm64`。このスクリプトは指定 RID の既存発行物を削除するため、生成先に手作業のファイルを置かない。
- CI は `.github/workflows/main.yml` の4構成で AOT 発行・自己検証・E2E、`codeql-analysis.yml` で C# 解析を行う。別アーキテクチャで省略された自己検証、headless UI 検証、通常デスクトップ起動を区別して報告する。`artifacts/` と `bin/`・`obj/` は Git に登録しない。

## 実装上の制約

- 共通設定は `Directory.Build.props`。nullable・AOT 互換性解析を維持し、既存 `packages.lock.json` とパッケージ参照を整合させる。Core は外部パッケージや実行時 DLL 探索に依存させない。
- 保存時の文字コード・BOM・改行・既存ファイル属性、比較の処理上限、キャンセル、リンクとルート外パスの拒否を保つ。詳細な不変条件は DESIGN.md と各コンポーネントの README に従う。
- プロバイダー変換結果を元ファイルにテキスト保存しない。外部実行ファイルは明示登録・選択したものだけを使い、保存したプロジェクトの ID から自動探索・実行しない。
- アーカイブ経路の変更は [Providers README](Src/DiffBeacon.Providers/README.md#managed-アーカイブサービスの検証契約) の失敗条件と検証契約を確認し、GUI・CLI・標準プロバイダーの呼び出し元を照合する。SharpCompress のライセンス同梱と、E2E fixture の出典・ライセンス・SHA-256 の記録を維持する。
- プロジェクト・包装経路の変更は `WorkspaceStore`、GUI の保存・復元・包装、CLI の `--project-copy`・`--package-project` を照合する。[DESIGN.md](DESIGN.md#データフロー) の相対参照・スナップショット・出力保護の境界を維持し、[包装 E2E](tests/DiffBeacon.E2E/README.md) の展開・再読込み・パッチ適用まで確認する。クリップボードの OS 操作は headless 検証と区別する。
- レポート経路の変更は GUI の `SaveReportAsync`、CLI の `--report`・`--report-project`、包装の `--report` を照合する。[DESIGN.md](DESIGN.md#データフロー) の本文確定・出力保護・上限・キャンセルの境界を維持し、[レポート E2E](tests/DiffBeacon.E2E/README.md#形式別-html-レポートの実行経路) と UI 自己検証で確認する。
- 機能を変えたら利用者向け説明と移行対応表を更新する。設計変更は DESIGN.md、開発手順は Docs/DEVELOPMENT.md に記載し、同じ説明を複製しない。
