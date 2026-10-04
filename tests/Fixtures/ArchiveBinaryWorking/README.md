# Binary作業版の実アプリ回帰

このfixtureは自作です。固定アーカイブや既存goldenを変更せず、各fresh run内でZIPと普通fileを生成します。左leafの全bytesは `00 01 02 03 FF 80`（SHA-256 `B4F9B7810BB2A4355B4B8445F708BF14DD6F6D0DB29A8C6BE2B955E3CB73AD98`）、右は `00 09 02 04 FF 81`（`53AA8C5D73154A1AF0C72CADF2909ED0E9A2EF5FFAF509AA5119A32723DDA8DC`）。内側ZIPには実在空fileとTextも置き、不在entryとの区別と形式切替を確認します。ZIPの時刻はrunごとに変わるため、原本SHAは生成直後に記録します。

`HeadlessBinaryWorkingChecks` は実BinaryPanelのHex適用、範囲コピー、共有Undo/Redo、通常Save、別名Save、比較設定ボタンを操作します。`ArchiveBinaryWorkingScenarios` は別プロセスのGUI自己検証とCLI project-copy、reportなし包装、展開・再読込み、拒否時の既存出力保持を実行します。管理者権限を要するWindowsリンク回帰は実tokenを記録し、権限不足のrunは失敗として保持します。

```powershell
dotnet build DiffBeacon.slnx -c Release
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test <fresh-gui> --binary-working-only
dotnet tests/DiffBeacon.E2E/bin/Release/net10.0/DiffBeacon.E2E.dll --output <fresh-e2e> --archive-binary-only --python python
python tests/Fixtures/ArchiveBinaryWorking/verify.py <fresh-gui> <binary-scenario-work-directory>
```

reader第2引数はE2Eの `fixtures/<run-id>/archive-binary-working` です（GUI単独照合では省略可能）。Python標準ライブラリだけで、全原本ZIPの全entry/CRC、原本文/asset/普通保存fileの全bytesとSHA、包装全entryと展開後の相対参照、PNGの全chunk CRCと復号長、dirty/保存点/履歴のfactsを照合します。CLIの16MiB境界と、既読Text assetをBinaryへ参照した17MiB拒否はE2Eに含みます。

原本root、他tab入力、workspace、公開済み・読込み済みasset、SaveAs後の旧rootへの出力を拒否します。取消・古い完了・readonly/path/mode変更・採用失敗・保存中の後発編集・同leaf兄弟dirtyは本文と保存点を保ちます。v4 Text metadata互換を維持し、v5 Binaryは厳密kind、16MiB、Textのencoding/BOMとの分離を検証します。共通作業storeは128MiB/256文書、共有履歴は64MiB/256操作で、拒否時の部分変更を認めません。

Binary HTML、Binary patch、三者Binary表示、任意位置の挿入/削除、元アーカイブへの自動書戻しは未対応です。対応外の包装report/patchは成功扱いせず拒否します。ネイティブ保存ダイアログと通常desktop/OS pointer操作はこのheadlessの注入検証に含みません。全体E2E/AOT/4RID CIの代替にはしません。
