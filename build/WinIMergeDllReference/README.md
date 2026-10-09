# WinIMerge v1.0.54 配布DLLの公開API採取

公式x64配布DLLをそのまま読み込み、静止画像copy fixtureの143ケース・935状態をケースごとの別プロセスで再生する。製品の通常build・実行へDLL/interopを追加しない。採取用MSVCとWindows SDKだけを使い、FreeImageのcheckout/buildや別FreeImage DLLへの置換は行わない。

## 固定原本とライセンス

- 公式WinMerge/winimerge `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421`。
- 公開header `WinIMergeLib.h`は固定commitのGitHub gateway取得、Git blob `d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa`を全bytesで照合。事前のlocal cache ls-treeと同じblob。原本headerを改変しない。
- 原本headerと採取probeはGPL-2.0-or-later。`LICENSE.txt`は既存ImageRegions canonical GPL本文のbytesを使用する。配布内FreeImage licenseも生成artifactに保持する。
- release `383180398`、asset `545555188`、[公式x64 ZIP](https://github.com/WinMerge/winimerge/releases/download/v1.0.54/winimerge-1.0.54-x64.zip)。size `2497470`、SHA256 `0B09DE06BB56F85452CDAE919150FF7F91F22175621F65623515B84107683613`。
- 期待値は既存無改変C++ [ImageCopy golden](../../tests/Fixtures/ImageCopy/README.md)。golden SHA `A1645415624D84B3CB4A328148151A0FAE48B05412CF2E504589CD98C289D65D` を維持する。

## 再採取

Windows x64、MSVC14.51.36231、Windows SDK10.0.28000.0、Python標準ライブラリだけで採取する。大きい生成物はEへ置く。初回だけ公式ZIPを次の場所へ取得し、size/SHA照合後に保持する。再採取は同じZIPを使い、downloadを繰り返さない。

`E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/winimerge-1.0.54-x64.zip`

```powershell
& 'C:/Users/IMT/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe' `
  build/WinIMergeDllReference/generate-reference.py `
  --output E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/run2
```

新しい空のrun directoryを指定する。generatorはZIP全体のsize/SHAを最初に検査し、絶対/親参照/drive/link entryを拒否する。必要なWinIMergeLib.dll/vcomp140.dll/GPL/FreeImage licenseだけを展開し、DLL/公開header/期待値を照合してからcompile/loadする。LoadLibraryExは絶対パスとDLL directory/OS system32に探索を限定する。SetErrorModeでcrash dialogを抑止し、timeoutは子processを終了させる。

既存run directoryと、出力・共有配布ファイルまでのsymlink/junction/reparse経路を拒否する。共有DLLは既存bytesを照合して再利用し、同一ファイルを再書込みしない。出力拒否を確認したrun4では、既存出力の全ファイルのbytesを変更せず終了1となった。リンク拒否の実操作による負の検証はこの記録には含まれない。

各入力は標準PNG32へ包装し、初期GetPixelColorを元BGRAへ全bytes照合する。alpha0 hiddenRGBを含む透明ケースを最初に実行する。offset0・rotation/flipなし・挿入削除NONE・overlayNONE・強調なし・WIC優先無効。原本CreateWindowless/DestroyWindow APIを使い、hidden parent/control/clipboard/OCRは作らない。

## 観測と未照合の境界

公開APIによる全pane原画BGRA・寸法・BPP、差分/競合件数、Undo/Redo可否、dirty/savepoint、void/auto/bool操作結果を照合する。region ID grid、region id/op/rect、history index/count/modcountは公開APIにgetterがなく**未照合**。これらをgoldenから補って実測扱いにしない。原画GetPixelColorはこのscopeではorig32の前処理複製を読むため、強調PNGをoracleにしない。

goldenの`save`はencoderなしのmarkである。実DLLではSaveImageAsへPNG保存してsavepointを作る。これはfilename/orig containerも変える**実保存付きsaveの範囲**であり、純粋mark APIとしては扱わない。追加readonly guardを入れず原本APIのまま呼ぶ。全stateを採取する前に機械的な全pane保存は行わない。最後のstate採取後だけ各paneをSaveImageAsへ渡し、独立PNG8 RGB/RGBA decoderでraw BGRA全bytesを照合する。入力PNG32に限定し、元format/BPP/palette/animationの完全互換を主張しない。

2026-10-02実測: 143ケース・935状態、20,021検査すべて成功、395最終PNGの独立全画素照合成功。run2/run3の観測JSONと入力/plain/raw/実保存PNG/stdoutがbytes一致。初回run1の316失敗は採取側decoderの不透明RGB PNG未対応で、原画/公開観測値の不一致は0だった。run1証拠を残し、decoder対応後に全採取を二回実施した。

配布DLL version1.0.54、SHA256 `36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6`。実import tableはGDI32/ole32/OLEAUT32/KERNEL32/USER32/SHLWAPI/gdiplus/WS2_32のみ。FreeImage/OpenMPの別DLL依存は検出されなかった。歴史的FreeImage source commitは配布metadataから未確定のままで、実配布binaryを測ることで本fixture範囲のzero/raw契約を確認した。一般FreeImage/元format encoder互換には拡張しない。

## 保持物と終了

Eのfailure-contract、公式ZIP、必要DLL/license、source/compiler/DLL PE情報、原本case・入力PNG/BGRA/plain、各state raw/JSON、実PNG、stdout/stderr、assertions/summary/processesを保持する。再現性はrun2/run3の実ファイルbytesを比較して`reproducibility.json`へ保存する。親向け要約は`artifacts/verification/image-copy-cli/next-freeimage-research/dll-runtime-summary.json`。コンパイラobjは各run完了後に絶対範囲・リンク・生存processを確認してPowerShell Remove-Item -LiteralPathで除去する。exeとcompiler logは再現証拠として保持する。

今回のobj清掃は自動承認レビューが`blocked by policy`として拒否したため未完了。安全条件を満たす絶対literal操作も拒否されたので回避・追加再試行せず、次のcompile中間ファイルだけを保持する（各645,331bytes、合計1,935,993bytes）。

- `E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/run1/obj/probe.obj`
- `E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/run2/obj/probe.obj`
- `E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/run3/obj/probe.obj`

probe生存processなし、全exec session完了、リンクなしを確認済み。採取証拠は完了しており、policyが許す実行主体で同じ絶対obj範囲・生存process・リンクを再確認できたときだけ、native PowerShellのLiteralPath指定で用途終了objを除去する。保持理由と削除条件をEの`cleanup.json`と親向けsummaryにも記録する。

出力保護を追加した親の再採取run4も143ケース・935状態・20,021検査・395 PNGが成功し、run3の全ケース4,604ファイルと観測JSONがbytes一致した。`run4-parent-verification.json`に再現性と既存出力拒否を記録する。run4の`obj/probe.obj`も再採取の中間物として保持し、清掃の未完了範囲へ含める。
