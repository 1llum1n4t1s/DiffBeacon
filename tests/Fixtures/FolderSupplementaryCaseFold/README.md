# Folder supplementary casefold 原本と公開E2E

固定PCRE2 Unicode 16.0.0の補助面case pair全564方向を保持します。`native-casefold-proof.zip` のSHA-256は `014fa78df132e1897bf93681d27afce7f86b287e262b93af7324cbf61a8370be`。62 indexed entriesをsize／SHA／ZIP CRCで照合し、既存原典61 blobs・無変更42 spansへ接続します。原典commitは `6780924ba35f7f55af0f6f14dd302561175830a6` です。

native採取は `Replace` の564方向×CI／CSの1,128件と、方向を分離した282規則のliteral list×CI／CSの4件、合計1,132件です。別child二回のstdout全bytesが一致し、各receiptの実終了・EOF・timeoutなし・診断なしを保持します。CIはX、CSは元sourceの観測値です。原典loader／Poco／PCRE2／置換関数を実行し、AST保管・UTF-8ファイル層・context・serializer等のadapter境界はZIP内のprovenanceと既存 `LineTransformReplaceLists/README.md` に従います。

## 公開経路への投影

`FolderSupplementaryCaseFoldScenarios` は二者・三者それぞれで直接Replaceを方向別16要素ずつの18区間のArrayへ分けます。直接配列も既存512 token上限を緩めません。直接／listは原本の関数本体phaseに合わせて `@noopt @ci`／`@noopt @cs` を明示します。最適化phaseの検証には数えません。literal listは同方向16規則ずつの18区間へ分け、native282要素のsource／resultの同じscalar区間を期待値にします。既存65,536予算では、282規則の全体実行はscalar検査だけで79,524課金となるため、成功を要求しません。各chunkの原文・結果・規則を順序どおり再結合し、全方向の欠落・重複がないことを独立readerが確認します。両方向を同一listへ収録すると未修正実装でも厳密一致で通るため禁止します。

公開コマンドは直接chunk144件・list chunk144件・派生control22件の合計310件です。native1,132件と公開コマンド数を同一視しません。`derived-cases.json` の11式はscope有効／無効／復帰、quantifier、capture原文と番号、BMP、非case補助面を検査する派生期待値であり、追加native実測ではありません。取消・予算障害注入・escaped補助面regex・補助面class／range・backreference、行式の直接Replaceを成功扱いにしません。

各公開コマンドは独立したleft／middle／rightの `row.bin` を使い、predicate成功時の包含をJSONで確認します。readerは実終了、stdout／stderrのSHA、1行のpath／kind／status／filtered、全sideの有無／size／path／filtered、入力・filter・listの前後SHAと最終bytesを照合します。未修正backendではCIの直接配列とlist chunkのpredicateが失敗し、同じreaderを通過できないことを修正前対照として別runに記録します。

## 実行と配置

repo rootでPython 3を使います。出力を作らず、native証拠とfixture固定bytesだけを確認できます。

```powershell
& $python -E -B -X utf8 tests/Fixtures/FolderSupplementaryCaseFold/verify.py --repo-root . --platform windows
```

E2E成果物を照合する場合は `--public-observations <run>/fixtures/<id>/folder-properties/supplementary-casefold/public-observations.json` を追加します。fixture/binの祖先をrepoと推定せず、既存原典がある実repoを `--repo-root` へ渡します。

`--folder-properties-only`、全体E2E、`--folder-replace-lists-only` の双方でこのsuiteへ到達します。list限定selectorにも新しい共通backend回帰として直接Replace・RegexReplace／RegexExtractの派生controlを含めます。限定selectorを全体E2Eの代替にはしません。

helperはSDKの既定Compile対象です。本fixture全fileはE2EプロジェクトのContentとして `Link=tests/Fixtures/FolderSupplementaryCaseFold/...`／`CopyToOutputDirectory=PreserveNewest` で登録します。helperはAppContext.BaseDirectoryのfixtureを優先し、既存原典のrepo rootは別に解決します。

`fixed-public-plan.json` は310件の固定identity・native row対応・phase・chunk境界です。

`failure-matrix.json` はE2E実装前の失敗条件、`fixed-manifest.json` は自身を除く全fileの固定SHA／sizeです。`.gitattributes` の `* -text` を維持してください。固定ZIP／原典／期待値を製品の結果から再生成しません。再採取が必要な場合はZIP内のcapture／prepare／compile資料、固定source manifest、compiler receipt、原典licenseを使い、新しい独立child二回とZIP全entry照合を採取します。

WinMerge GPL-2.0、Poco BSL-1.0、PCRE2ライセンス原文を同梱しています。原典source全体は既存 `LineTransformRegex/reference-source.zip` と `LineTransformReplaceLists/additional-original` を再利用します。

公開実行readerの `--platform windows|macos` はhelperの実OSから渡し、reader自身の `sys.platform` と全rowのplatformに一致させます。Windowsでは固定 `managed-runner-source.zip` の `Program.cs` と `source-binding.json` をwhole ZIP／全entryのSHA・size・CRCで検証し、provenance／inventoryのProgram SHAと一意なcompiler-source bindingを照合します。実repo／cwdのProgramは認証anchorにせず、binding内15sourceと実compiler入力・runner DLLの照合は外側build/runtime chainで担います。PID・UTC birth／launch／exit・duration・expected exit・保存した実stdout／stderrを検査します。Windows runnerはWaitForExitAsync後に両ReadToEnd taskをawaitしてからexitObservedUtcを採取し、Mac専用LaunchEvidenceはnullです。macOSでは従来のTerminal・RawExitCode・両EOF・pipe解放・dispose・cleanup証拠が必須です。時刻精度と計測開始差には2秒だけ許容します。別OSでの事後reader実行はこの契約では対象外です。

この照合は同じ実hostと固定runner sourceに対する成果物整合検証であり、署名付きOS証明ではありません。source SHAだけで実行したDLLの同一性は証明できないため、別cohortのbuild入力凍結とプロセス実測を合わせて判断します。native採取receiptと製品Windows runnerの証拠は別契約です。旧174件の実測を新310件の成功へ読み替えません。

consumer修正候補: @ciの公開 == はASCII X/xを同一視するため、310命令中152命令（CI direct72・CI list72・derived4種×側数2＝8）に識別力の欠陥があります。全310 consumerを AllEqual(Array(actual, expected)) へ統一し、配列の全要素・長さ・型・文字列全byteをstrict比較します。追加158命令の構造統一と、成立した152命令を区別します。Replace／RegexReplace／ReplaceWithList自身の@ci／@cs、引数、原典期待bytes、chunk16は保持します。旧Array==Arrayも全要素を調べており、any要素の欠陥は主張しません。

readerは同じstrict consumerを再構成します。固定native ZIP／golden、producer、finite Mac schemaと校正契約は不変です。x/X・配列末尾／長さ・型の陰性対照はsource由来の準備期待で、App／compile／E2Eと新reader校正は未実行です。native full parserの互換性は未資格のままです。

Combined managed-source revision: fixed managed-runner-source.zip binds the actual compile Program CFD2 and source-binding. Helper records that payload Program SHA; the reader verifies whole ZIP/entry SHA/size/CRC and unique Program compiler-source identity. Runtime repo Program/cwd is no longer the managed runner source anchor. Historical A2F9 Program69 remains separately labeled; main185/Casing1 is separate. Native source/golden/operands/chunk/derived expectations remain unchanged. The payload authenticates source bytes only; actual runner DLL identity belongs to the future combined build/runtime outer chain. Generation order is helper/reader -> source-binding -> payload -> provenance -> fixed manifest, without embedded self-hash. App/E2E/compiler/new reader CLI not executed.

Private regression phase correction: the source-only binding15 now records the proposed FolderPropertyFilterScenarios.cs bytes for RegexPositionValues, RegexReplaceValues and ReplaceValues. Program CFD2 and the other14 hashes are unchanged. The previous main185/Casing1 lineage is historical. This candidate has not been compiled or product-executed; old normal-reader/negative calibration and the limited 32-command directive counterproof do not qualify this candidate build or full regression. Fresh repository apply/build/full E2E/UI remain required.
