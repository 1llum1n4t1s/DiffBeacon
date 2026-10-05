# Folder model / copy E2E

Windows NTFSのmetadata拒否は、別の `folder-windows-metadata-observations.json` に5 GUIケースを保存します。保持量／query量／native操作の予算0は確認前に拒否し、確認後のsource／既存targetのcreation変更だけでも公開を拒否します。`verify-metadata.py OBSERVATIONS OUTPUT_JSON` は固定literal・全membership・bytes/SHA・mtime/attrs・creationと5拒否＋2診断GUIの98件のWin32 handle closeを独立照合します。`--folder-copy-only` と全体E2Eは固定SHAを照合して別reader processを起動し、実PID・birth・終了・ログとproofを保持します。他OS／filesystemは専用skipです。通常OS pointer/dialogやSACL・任意owner/groupを検証したとは扱いません。

2診断GUIはoutput guardへ一次失敗＋cleanup診断、PlanReadyへ取消＋close診断を注入し、実ボタンから表示まで確認します。`folder-windows-metadata-diagnostic-observations.json` と2 PNGに公開0・全入力保持・表示文を保存します。この注入は実OSのCloseHandle／disposition失敗の再現とは別の検証です。既存5件の原本期待を変更しません。

`verify-metadata-copy.py prepare ROOT` は固定byte・EA・時刻・属性からWindows local NTFS用の16ケースを生成します。普通／圧縮親、新規／置換、Normal／Hidden／Readonly／Archiveの組合せを主アプリの `--folder-sync` へ渡し、`verify-metadata-copy.py verify ROOT` で全DATA／ADS、EA、creation／mtime、identity、最終属性、既存DACLまたは親からの継承securityを独立したWin32 readerで照合します。既存出力はWRITE_ATTRIBUTES拒否DACLを持ち、コピー元の保持と旧出力だけのADS除去も確認します。製品JSONのmetadata検証値は実OSの照合結果と比較し、期待byteや時刻の原本にはしません。固定reader SHAは `copy-provenance.json` に保持し、両reader processの実PID・birth・Wait完了・exit・stdout／stderrと起動前後の入力SHAをrun内に保存します。圧縮設定が利用できない環境は該当8ケースを理由付きでskipします。このfixtureはWinMerge原版goldenではなく、Shellメタデータ契約を独立検証するadapterです。任意owner／SACL／EFS／SMB／外部FILE_OBJECTの競合やnative Close失敗注入は検証範囲に含めません。

フォルダーGUIの画像は通常／最小／多数タブの一覧3枚と、設定欄をスクロールした3枚の計6枚です。独立readerは全PNGのCRC・zlib scanline・寸法と、左右入力・比較・除外・filterの15操作の実viewport座標を確認します。最小850×550・一覧100px以上・多数タブ45件の条件と、既存44ケースの全bytes／metadataは維持します。

`model-expectations.json` は自己作成のbyte literals・階層・62ケースの固定期待です。原版関数実行goldenではありません。DirScanの親DIFF/CMPERR集約・filtered登録後再帰停止と、DirActionsのSame/filtered/Error/元側不在gateを一次根拠にし、統合TypeConflictと上限/重複排除は現adapter policyとして区別します。出典とSHAは `provenance.json`。

`--folder-model-only` は実アプリを別processで呼び、二者両方向all/diff・model再帰・未走査physicalcandidate・走査済み片側空と対側child・親子重複・型とsidefilter・budget/不正引数を検証します。Windowsは実FileShare.NoneでchildErrorと親Error/明示goodchild bypassを採取。ほかのOSの同等Errorは未実測skipとして保持します。

`python -B -X utf8 verify-model.py FIXTURE_ROOT WORK_ROOT` は製品DTOを使わず全入力bytes/SHA/空dir/mtime/属性、modelJSON・planJSON・source/destinationの側別kind/size/scan、実process/exit/rawstdoutstderrを独立照合します。source-before/after/commands/expectations/verify-result/rawlogsをrun内に保存します。

通常Release build後 `dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --folder-model-only --output OUTPUT --python PYTHON`。限定成功は全体E2E/UI/AOTの代替ではありません。Copy実行/確認GUI/copy-stamp/partialmutation/出力mtime保存/CLI cancellationは未検証です。inputattrs保持は独立disk観測で、CLI側snapshotのattributes field（現serializer未出力）を検証したとは称しません。

`--folder-copy-only` はコピー実行の別検証範囲です。自己作成 `copy-expectations.json` の39 CLIケースに対し、実アプリの `--folder-sync` / legacy `--folder-copy` を別processで呼びます。両方向all/diff、physical terminal、走査済み空側、親子重複、readonly・型衝突・不正引数・低い比較予算、content/hash/timestampとignore-caseを照合します。256MiB+1の1fileを64KiB単位で作成・コピー・独立全byte照合し、wholebyte読み込みへ置き換えません。

`type-filtered-diff-R` は、左directoryのfilterが物理directoryを消さないため、右fileを同じpathへコピーする実行はkind衝突としてexit2・stdout空・全入力出力0mutationを期待します。元modelの候補literalは変更せず、別の実`--folder-plan` 1commandで同じ候補2件とfiltered destinationの物理kindを確認します。copy-plan候補の生成成功と実行の安全な拒否を区別し、file/directory置換は未移植です。CLI39＋large1の件数に、このread-only plan1件を別に加えます。

実Avalonia controlsのGUI44ケースは複数pointer選択・旧single差分button・新4方向button・確認dialogの続行/取消・同size/mtime source差替え・membership追加・世代変更/反転・旧prepare/旧確認後の新Folder/Binary/Image採用・全tab入力/filter/archive/workspace保護・予算拒否・部分公開と後続NotExecuted・再比較を対象にします。通常1280x850、最小850x550、最小45tabsのPNGと先頭行/viewport座標を保持します。実OSのpointerやnative保存dialogの試験とは区別します。

`python -B -X utf8 verify-copy.py FIXTURE_ROOT WORK_ROOT` は固定期待/reader SHA、全source/destinationのliteral bytes・SHA・mtime・readonly/Unix mode・emptydir・実processとCLI JSON・GUI観測JSONをstdlibで独立照合します。製品DTOは使用しません。source鮮度はPrepare捕捉開始の契約であり、比較時のSHA保証ではありません。file/emptydir mtimeを保持し、populateddirは自然な変化を許します。source CreationTimeをdestinationへ設定しません。旧ShellのADS/extended metadata/EFS完全保持は未実装の残工程で、今回のmainstream byte保持とは別です。

CLIのmtime前提はsource／全fileを2002、既存destination directoryだけ2011へ固定します。コピーしたemptydirはsource時刻、新populateddirは自然時刻、既存populateddirはbefore時刻の保持又は自然変化を許し、source時刻の強制転写を拒否します。変更対象外directoryはbeforeの全metadata保持を確認します。孫fileの置換で全祖先のmtime更新を必須としません。この別baselineにより、元fixtureの同一時刻では識別できなかった誤ったdirectory時刻転写を検出します。

Windowsでは専用inputの圧縮source→通常parentと4MiB+1 Sparse sourceを少量で検証し、通常fileとして全logical bytesを照合します。圧縮/Sparse属性の保持成功とは称しません。MacではFIFO/socketの左右・両側・通常0byteとの混在をcontent/hash/timestampで読込み前Error、linkはSymbolicLink診断・コピー拒否として検証します。特殊nodeの前後lstat保持を独立確認し、専用FIFO/socketのみMac上で寿命を終えて、空rootとJSON/logを保持します。`mac-file-kind-reference.c` は親が追加した原著の検証専用Cです。CIのclang/SDKによるDarwin stat ABI確認用であり、製品と通常.NET buildの依存ではありません。

特殊inputの時刻設定はPythonのno-follow capabilityを成果物へ記録します。Windowsでは専用root内のlstatでregular/fileまたはdirectory、link/reparseなしを確認した項目だけ通常utimeを使い、Macでは特殊nodeのno-followを維持します。source-beforeの採取、特殊属性の実在、全logical bytesの確認を省略しません。

特殊input helperの末尾引数は`plain`／`mixed`を明示的にboolへ変換し、それ以外は入力生成前に拒否します。`plain`の片側linkでは反対側のnodeを作らず、`mixed`だけ反対側に0byteの通常fileを作ります。HEAD `92c2b1a6` のMac両構成の検証では、この引数を文字列のまま真偽判定して余分な通常fileを作る不具合がありました。固定のLeftOnly／RightOnly／TypeConflictと全原本保持の期待を維持して生成helperを修正し、修正後の同OS実測はrunの証拠で確認します。

出典/固定SHAとadapter範囲は `copy-provenance.json`。限定範囲の成功は、全体E2E/UI/AOT/4RIDと独立レビューの代替ではありません。CLI一般取消・temp cleanup failure・hardlink identity/portable path TOCTOU・完全Windows metadata backendを今回の受入済み範囲へ含めません。
# freeze06後のreview回帰（sourceドラフト）

v02追加はWindows GUI `extended-directory-container` の1件です。別タブFolderで専用destination directoryを`\\?\C:\...`表記で読み、実一覧の受理・requested root・bound entry由来のobserved root・UI入力値・prefix正規化を記録した後、操作タブの通常表記によるbase.binコピーを拒否します。新しいWindows GUI追加数は2件（中央ADSとextended Folder）となり、既存byte期待は変えません。未受理を保護成功と数えず、全disk bytes/mtime/attrsと実samefile identityを独立readerで照合します。build/runtimeは次freezeの受入工程です。

v03は新filter入力の是正です。managed09では追加fixtureの単一`#`が不正構文となったため、新CLIとGUI入力のみ既存`##`コメントへ直しました。GUI before/afterは同size・mtimeを保持した内容差替えです。独立readerはreadonlyplanの成功/stderr空と、syncの固定stderr理由「比較に使用しているフィルターファイルを上書きできません。」を必須にし、parser拒否を出力保護の成功と数えません。旧39 CLI/plan1/large/GUI44とbyte期待は変えません。managed09失敗成果物を保持し、この是正のruntime受入は別runで確認します。

既存`copy-expectations.json`の39 CLI・追加plan1・large1・44 GUIとbyte literalは保持します。新しい`review-expectations.json`と`verify-review.py`は、通常nested rootの両方向sync/legacy拒否8件、使用中filter上書き拒否2件とreadonlyplan2件、Mac FIFO/socketのdestination leaf sync・ancestor sync/legacy4ケース、Mac nested case-alias1ケースを追加します。実Macでaliasが成立しない場合は理由付きskipとし、実在する別inodeのcase-sensitive rootsのコピーを確認します。特殊nodeは開かずlstatを採取し、全app/helper終端後、この検証が作ったFIFO/socketだけを寿命終了します。

GUIの新しい`folder-review-observations.json`は通常nested rootの4button、確認中の同size/mtime filter差替え、Windows別タブ中央Binaryの`base.bin:protected`受理とbasecontainer上書き拒否を記録します。中央ADSは`0314FF`、containerは`7A62`の全bytesを独立readerへ照合します。ADSを本当に受理できたことを先にassertし、未受理を保護成功と扱いません。これはADSコピー本体・EFS・全Windows metadata対応を示しません。

`verify-copy.py`は固定SHA付きの追加readerを読み、CLI/GUI双方へ接続します。従来CLI39/plan1/large1とGUI44のcoverageは別のまま維持します。今回追加はWindowsでCLI10＋plan2・GUI6、MacでCLI10＋plan2・GUI5＋OS専用5ケースです。これはsourceドラフトであり、build/runtime受入は親の次freeze/handoff後に別成果物へ記録します。

時刻について、sourceが空directoryで既存destinationにchildがある旧APIの実測は、destination childを保持しsource時刻2002へ転写しました（親artifact `empty-dir-merge-reference-01/parent-revalidated.json`）。一般のpopulated sourceの自然時刻境界と区別し、executorや既存fixed期待をこの追加工程で変更しません。


## Windows file全stream回帰

旧39CLI・44GUI・268435457byte入力・copy-expectations.jsonの原本SHA/byte期待を維持する。全stream実装の6S+3Dへ移行した計測期待だけを変更し、既存GUIのS2/D2はIO18成功/17拒否にする。新規Windows CLI6ケースとGUI11ケースは別manifest/verify-streams.pyへ記録し、600byte Win32列挙と固定literalの全stream名/全bytes/SHAを独立照合する。S24/D12ならread156/write24/planned180、fresh D0ならread120/write24/planned144。directory ADSはsourceから転送せず既存destinationを保持する。

新規readerは専用run入力をprepareし、実CLI stdout/stderr/終了/PID/JSONとGUI button/confirm結果/PNGをverifyする。AppDataとclipboardを使用しない。新しいfixture literalの変更を製品結果から自動生成しない。copy-provenance.jsonは変更readerのSHAのみ追従し、元copy-expectations.jsonの固定SHAはそのまま。

採用後に通常build、--folder-copy-only限定E2E/UI、whole E2E/whole UI、同OS Windows Native AOTを実施し、入力/全stream SHA/ログ/JSON/PNGを保持する。SMB/非対応filesystem/EFS/FindClose失敗/handle leak/native異常値注入/200000 streamや4000000 UTF16 unitsの実量stress/公開直前の取消は未検証。注入したdescriptor/UTF16共有上限の境界は後述の4GUIで検査する。通常desktop操作とは区別する。

新規Windows CLI/GUIは左右root＋全subtreeについてfile主本文size/mtime/attrsとdirectory mtime/attrsを採取し、独立lstatへ照合する。GUIはbyte-independentな通常System.IO採取をbefore/after-metadata.jsonにも保存し、拒否/取消/same-size ADS差替えの両側metadata保持、成功時の全source metadata保持とdestfile source mtime/portable attributes転写を検査する。既存destination directory時刻は2011、sourceは2002に固定して、populated directoryの自然mtimeとsource強制転写を区別する。directoryのOS sizeは主FileInfo.Sizeへ比較しない。


## Windows全stream共有予算のGUI

旧6CLI/11stream GUIと旧44GUI/7reviewの固定literal・SHA・metadata・process exit0・PNG・出力保持契約を維持し、専用`stream-budgets`へ実all→button/実確認dialogを使う4GUIを追加する。alpha.bin/beta.binは既存SRC/DST全stream literalを同じまま使用する。各file S24/D12、descriptor7、UTF16名75 units。全planはS48/D24/IO360/read312/write48、descriptor14、UTF16名150 units。14/150のexactは2件公開、13/149のone-overは確認前のPrepareで拒否し、plan/resultなし・両root全bytes/metadata保持を独立Win32 stdlib readerで照合する。各file単独ではどの小limitにも入るため共有累積を捕捉する。0byte ADSもdescriptor/nameに含み、`日本😀`のsurrogate pairをUTF16 2unitsと数える。

追加のbudget観測JSON/4PNG・before/after metadata・独立after全stream SHAを保持する。readerは固定SRC/DSTから期待を計算し、製品JSONを期待値へ使用しない。既存6CLI/11GUIの件数は維持し、追加4GUIの結果はstreamBudgetGuiへ別記する。実行受入は通常build・folder-copy限定E2E/UI・全体E2E/UI・同OS AOTで親が確認するまで未検証。200000/4000000実量stress・native異常値注入の受入を意味しない。

## 中止完了と旧結果の反映拒否

確認中、最初のpreflight出力guard、一件公開後の次項目、再比較候補の採用直前に、実中止ボタンを押す4GUIを検査する。完了表示・コピー操作の復帰・個別Published/Cancelled/NotExecuted、原本と既存出力の全bytes・SHA、左右rootと全subtreeのmtime・属性・Unix modeを`folder-cancellation-observations.json`と4PNGへ保持する。`verify-copy.py`は固定literalと独立lstatで照合する。旧44観測は維持し、追加preflight/refreshは別観測へ保存する。旧Folder確認の取得後に明示中止して新比較を採用し、旧回答・完了が新status/adoptionを上書きしないことも確認する。その他の旧確認／準備後の新比較ケースを保持する。成果物の実測結果はrun receiptへ記録し、限定実行を全体E2E・UI・同OS AOTの代替にしない。
