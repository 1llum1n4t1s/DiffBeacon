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
- GNU 行算法の変更は Core の `GnuLineDiffer`・`GnuLineMatcher`・`TextDiffer`・`CompareTables`、三者マージ・パッチ、CLI の `--compare`・`--table`、GUI・HTML の呼び出し元を照合する。限定 E2E の `--gnu-line-only`・`--gnu-text-only`・`--gnu-table-only` の検証範囲は [E2E の手順](tests/DiffBeacon.E2E/README.md) に従い、全体 E2E も実行する。共通端・全元行の保持、共有予算・退避理由・取消を維持する。原本 fixture の出典・SHA-256 と `.gitattributes` の `-text` を保ち、再生成は [GNU fixture](tests/Fixtures/GnuLines/README.md) に従う。採取用 C++/MSVC は通常ビルドの依存に追加しない。
- 行内差分の変更は Core の `WordDiffer`・`TextDiffer`、GUI の強調と省略件数、CLI の `--word-diff`・`--compare`、単体・包装 HTML を照合する。[DESIGN.md](DESIGN.md#データフロー) の原文区間・ブロック投影・共有予算を維持し、[WordDiff E2E](tests/DiffBeacon.E2E/README.md) と UI 自己検証で離れた変更・改行横断・上限時の表示を確認する。生成済み `WordCharacterProfile.cs` の分類表は直接編集せず、[fixture の出典と再生成手順](tests/Fixtures/WordDiffs/README.md) に従う。
- 画像経路の変更は App の `ImageComparisonEngine`・`SpecializedViews.ImagePanel`、CLI の `--image` を照合する。[画像の契約](Docs/IMAGE-VIEWER.md) の入力・キャンバス・フレーム数・復号作業量の上限と復号完全性の境界を維持する。画像 E2E（限定実行は `--image-only`）と UI 自己検証で後続フレーム・枚数差・透明度・disposal・閾値・取消・古い完了の破棄とリソース解放を確認する。[画像 fixture](tests/Fixtures/Images/README.md) の入力 SHA・独立した画素期待値を維持し、限定実行は全体 E2E の代替にしない。 領域照合経路は `ImageRegionDiffer`・`ImageRegionCommands`・CLI の `--image-regions` を照合し、限定 E2E の `--image-regions-only` と全体 E2E で原本の全pair grid・領域ID・分類・矩形、入力保持と上限拒否を確認する。[領域 fixture](tests/Fixtures/ImageRegions/README.md) の原本・golden の SHA-256、ライセンスと `.gitattributes` の `-text` を維持し、再生成は同手順に従う。採取用 C++ / FreeImage を通常ビルドへ追加しない。
- アーカイブ経路の変更は [Providers README](Src/DiffBeacon.Providers/README.md#managed-アーカイブサービスの検証契約) の失敗条件と検証契約を確認し、GUI・CLI・標準プロバイダーの呼び出し元を照合する。SharpCompress のライセンス同梱と、E2E fixture の出典・ライセンス・SHA-256 の記録を維持する。
- プロジェクト・包装経路の変更は `WorkspaceStore`、GUI の保存・復元・包装、CLI の `--project-copy`・`--package-project` を照合する。[DESIGN.md](DESIGN.md#データフロー) の相対参照・スナップショット・出力保護の境界を維持し、[包装 E2E](tests/DiffBeacon.E2E/README.md) の展開・再読込み・パッチ適用まで確認する。クリップボードの OS 操作は headless 検証と区別する。
- レポート経路の変更は GUI の `SaveReportAsync`、CLI の `--report`・`--report-project`、包装の `--report` を照合する。[DESIGN.md](DESIGN.md#データフロー) の本文確定・出力保護・上限・キャンセルの境界を維持し、[レポート E2E](tests/DiffBeacon.E2E/README.md#形式別-html-レポートの実行経路) と UI 自己検証で確認する。画像HTMLは `ImageReport`・`ImagePanel.CaptureReport`・`ProjectReport`・`ComparisonPackage` を照合し、限定 E2E の `--image-reports-only` で埋込みPNGの独立画素検証・包装展開と再読込み・出力保護を確認する。全体 E2E も実行し、UI 自己検証では全／選択と表示原本スナップショットの保持を確認する。
- 表の経路の変更は Core の `ParseTable`・`CompareTables`・`WordLineAlignment`・`ReplaceCell`、GUI の `ComparisonPane.Table`・`TablePanel`、CLI の `--table`、単体・包装 HTML を照合する。[表の契約](Docs/TABLE-EDITOR.md#比較と原文の契約) を維持し、E2E で元行対応・全セル保持・上限を、UI 自己検証でセル編集・Undo/Redo・保存再読込み・検索・読取り専用と古い座標の拒否を確認する。行対応の限定 E2E は `--line-alignment-only` を使い、三者の復号一致アンカー・4096境界・全ブロック共有予算・CLI/HTML の対応を確認する。限定実行は全体 E2E の代替にしない。旧関数採取 fixture の出典・SHA-256 と `.gitattributes` の `-text` を維持し、再生成は [行対応 fixture の手順](tests/Fixtures/LineAlignment/README.md) に従う。採取用 C++/MSVC は通常ビルドの依存に追加しない。
- 機能を変えたら利用者向け説明と移行対応表を更新する。設計変更は DESIGN.md、開発手順は Docs/DEVELOPMENT.md に記載し、同じ説明を複製しない。
