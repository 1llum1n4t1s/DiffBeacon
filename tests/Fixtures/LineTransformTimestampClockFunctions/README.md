# Now / Today clock function fixture

固定原本は `clock-native-portable-v1.zip` (2,765,601 bytes, SHA-256 `23339417df345ce7332b4dfaf4146cad32d51d1fde433beb1d7a42d8a969a1d0`)。source commit `6780924ba35f7f55af0f6f14dd302561175830a6` の original UpdateTimestamp / NowFunc / TodayFunc と real ValueType/context declarationsを用いた実録closureである。

## 最初に拒否する条件

ZIP/fixed/readerのSHA不一致、entry重複／unsafe名／symlink／容量超過／SHA-size-CRC不一致、source/raw/receipt/producer joins不一致、actor絶対pathへのアクセス、未実測項目をgoldenとすることを拒否する。保存されたepoch自体を未来の時計と比較しない。二度の実行epoch一致を要求しない。

## 原典で実測した範囲

Windows ja-JP / Tokyo (LCID1041; 原典profileにはTZ環境変数を記録していない)の自然profile。2 Native process × 2 snapshots = Update4 / Now12 / Today12。各Nowが自己capture前後のUTC区間内、同snapshotの反復値が一定、Todayが同Nowのlocal日付の0時からUTCへ構成されることを検証した。437は入力sourceの不変file rows数（compiled TUs数ではない）。11 original raw spansと35 readonly inherited objectのproducer receipt joinsを含む。compile/link/Native2の4 actual commandsでheld PID/birth/privateJob/beforeResume/OS終了0/both raw EOFを記録している。

adapterはminimal owner＋原典bodyであり、FullAST/publicParser/full owner layout/ctor/copyctor/Optimizeの実測ではない。Mac／他profile／DST／clock backward／midnight crossing／runtime API failureも未実測。原典のUpdate/body実録と製品の公開grammar/plan/Optimize回帰は分ける。

## 独立reader

```text
python -E -B verify.py --archive clock-native-portable-v1.zip --fixed fixed-manifest.json
```

Python stdlibだけでZIPを展開せず、全SHA-size-CRCとsource／raw／receipt joinsを照合する。actor絶対pathはZIP内binding keyとしてのみ使用し、実ファイルを開かない。Native／compiler／linker／actor executorの実行はない。relative fixture位置だけで読める。

`verify.py`は原典`verify_clock_portable.v2.py`とbyte同一でSHA-256 `861486a3b9525b695dee74159f4e25ce8dc2cc01e1b59c52d2d73dd50cc30828`。`fixed-manifest.json`も原典`clock-native-portable-fixed-v1.json`とbyte同一でSHA-256 `acf0580ecddd0cdbff89aae61e2bfcdbbec3c5bf3ab10258e831f34a8d174142`。manifestのportableReaderNameとreader既定引数は原典名を記録したままなので、この正式fixtureでは上の明示引数を必須とする。内容の変更／再生成で旧署名を流用しない。

## 出典・ライセンス・再採取

ZIPは原典closureをbyte同一で保持する。五つの原典licenseをinput blobs/reference-source.zipへ含む。ZIP内manifestのsourceRows/sourceManifest/referenceSourceZipとsource spansが出典／SHAを固定し、readerが照合する。新採取には親の別exact GO／新外部scopeが必要で、今回のreader再実行は再採取ではない。原本fixture全fileのGit属性は `* -text !eol` とし、採用時は近い属性／Git blob SHAも別途確認する。

## 製品E2E draftの検証境界

`LineTransformTimestampClockScenarios.cs`はRunAsync delegate経由の実アプリText／Table／report／workspace／project-copy、normal/@noopt、Now−Now=0、Timestamp型／ToDateTime(timestamp)=none、arity、混在、input/既存output保護をsource-derived工程として検証する。現在CLI JSONは原文／分類を返し変換epochを公開しないため、厳密な起動前／終了後epoch照合とrule別／再compile別capture回数、cancelは未実装の診断入口または担当UI自己検証の証拠が必要。これを原典dynamic構造の合格へ読み替えない。Macも製品supported hostとして扱い、原典same-profile実測の資格とは分ける。

保持理由: 親の正式採用判断／後続検証に必要なdraft。削除条件: 採用／却下と必要最小証拠の保持を親が完了した後、親の共通cleanupで今回外部rootだけを処理する。担当は削除0。
独立レビュー修正: nativeProfileQualificationRequiredはToday/serialDateへの適用条件、nativeCurrentProfileQualificationはqualifiedかつ対象である場合だけtrue。nativeTzComparisonQualifiedは常にfalse。現在のTZ overrideを拒否する条件は保守的工程条件で、原典TZとの一致ではない。readerの-EはPYTHONOPTIMIZEを無視してassertを有効にするreader子interpreter専用であり、共通Run／Mac gateは変更しない。
