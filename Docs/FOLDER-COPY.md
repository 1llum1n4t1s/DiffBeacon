# フォルダーの比較とコピー

左右のフォルダーを選び、形式を「フォルダー」にして比較します。中央欄にもフォルダーを指定すると三者比較になります。サブフォルダーを比較するか、内容・SHA-256・日時とサイズのどれで比較するかを指定できます。除外パスと`.flt`を使い、「除外項目を表示」で除外された項目も一覧に表示できます。この表示設定と中央パス・中央の読取り専用指定は比較プロジェクトへ保存します。

Ctrl／Shiftで複数項目を選び、方向とコピー対象を指定します。二者比較の移動ボタンは左→右・右→左を選び、三者比較では一覧の方向欄で指定した向きへ移動します。移動対象は選択した通常ファイルのみです。コピーと同じ事前確認の後、選択した全ファイルと入出力の保護対象を検査し、確認画面に表示した方向・件数・パスを確かめて続行します。

三者比較では一覧の方向欄から左→右・右→左・左→中央・中央→左・中央→右・右→中央を選びます。「左のみ異なる」などの分類は他の二側との相違を示し、ファイルがその側だけに存在するという意味には限定しません。内容比較は三側を一度ずつ読み、GNU行算法の共通作業予算で照合します。原本の既定設定と一致する範囲は固定fixtureで検証し、任意フィルターの全旧互換は未完了です。

## プロパティフィルター（Windows）

`.flt`の条件式`fe:`／`de:`／`e:`で`Prop('System.Size')`、`LeftProp('System.FileName')`、`MiddleProp('System.DateModified')`、`RightProp('Hash.SHA256')`を使用できます。引数は括弧付きのcanonical property名の文字列literal一つです。識別子や式の評価結果からproperty名を指定することはできません。名前の大小文字もWindowsの名前解決に従い、`Hash.MD5`／`Hash.SHA1`／`Hash.SHA256`はこの表記で指定します。無効名・引数数／型の誤り・非Windowsでの使用は比較エラーになります。Text／Tableの行変換式へのProp追加や、フォルダーHTMLの拡張は含みません。

`Prop`は二者なら左・右、三者なら左・中央・右の配列です。`LeftProp`は先頭、`MiddleProp`はindex 1（二者では右）、`RightProp`は末尾です。不在側と未対応型は`None`になります。配列とscalarの比較は各要素へ適用し、配列同士の`==`／`!=`は全配列の一致、その他の比較は同じindexの要素へ適用します。最終的な規則の一致は入れ子の配列内も含むboolean trueで決まり、数値1や文字列`true`は一致扱いにしません。`and`／`or`／`not`での配列の論理評価は直下のboolean要素だけを使います。

`.flt`の`FullPath`／`LeftFullPath`／`MiddleFullPath`／`RightFullPath`は、フォルダー走査が各側について取得した絶対パス文字列を返します。パスは走査snapshotの値を使い、Shell property serviceを呼びません。不在側は`None`です。`FullPath`は側順の配列、`LeftFullPath`は先頭、`MiddleFullPath`はindex 1（二者では右）、`RightFullPath`は末尾を参照します。Prop／FullPathの有無によらず、すべての条件式を全側DIFFITEMで一度評価します。無prefix属性は側順配列、側prefix付き属性は指定側の値です。正規表現`f:`／`d:`は従来どおり側別に評価します。`FullPath`はフォルダー比較のroot contextを使うため、rootを持たない`FileFilter.Matches()`から評価することはできません。

`.flt`の`LeftRelPath`／`MiddleRelPath`／`RightRelPath`は各側のrootからの相対パスを`/`区切りで返します。`LeftRelPath`は先頭、`MiddleRelPath`はindex 1（二者では右）、`RightRelPath`は末尾です。選択側に対象がなければ`None`になり、ファイルとディレクトリの両方で使えます。値は比較走査が保持する相対パスから取り、追加のfilesystem／Shell問い合わせは行いません。

存在属性`Exists`／`FileExists`と`Missing`／`FileMissing`はBoolを返し、不在側でもNoneではなく存在の真偽を表します。`LeftIsFolder`／`MiddleIsFolder`／`RightIsFolder`もitem全体のdirectory flagを返し、存在属性とは区別します。式の二重引用符では引用符の二重化とraw backslashを使います。単一引用符のescape拡張と`f:`／`d:`の旧quote規則は維持します。`DateStr`は既存formatterと通常のfixed-offset source contractを使います。年範囲は[-4713,9999]でyear 0を許容し、年10000を拒否します。実mtimeのpre-1970／FileTimestampのunsigned変換とnative year 0の実oracle一致は未認定です。

`.flt`の`AllEqual(arg, ...)`は1個以上の値を受け取ります。最初の値が配列なら全要素を入れ子も含めて同じ型・値か比較し、後続引数は評価しません。scalarなら引数を左から評価し、最初の不一致で停止します。文字列は大小文字指定に関係なくordinal比較し、日時リテラルとproperty timestampは同じtimestamp型として比較します。二者・三者で`Name`／`Size`や`Prop(...)`が返す配列には不在側の`None`も含まれます。空配列は一致します。`AllEqual`を含む規則は全側を一度評価します。scalar式と明示したproperty contextはrootを持たない`FileFilter.Matches()`で使えますが、`FullPath`にはフォルダー比較rootが必要です。

たとえば次は、どの側にも存在するファイルのサイズが1 MiBを超えず、左に存在しないdirectoryへ再帰しないincludeフィルターです。

```text
def: include
fe: Prop('System.Size') > 1MiB
de: LeftProp('System.FileName') == None
```

Shell値はI4／UI4／I8／UI8をsigned Int64（UI8のbit列を維持）、R8をDouble、LPWSTRを文字列、BOOLをboolean、FILETIMEを原本と同じmicrosecondsのtimestampへ変換します。文字列vectorは配列、byte vectorは小文字hexです。EMPTYとその他の型（R4／NULL／I2／BSTR等）は`None`です。directoryへのhash取得失敗は空文字列になります。property値は一つの比較itemについて全規則で共有し、再比較で取得し直します。property取得前の全側の存在を使い、directory規則で除外した親の配下を探索しません。Propの有無によらず、条件式のName／Size／Date／Exists等は全側配列として一度だけ評価します。Name／Size／Dateの不在側はNone、Existsはfalse、Folderは不在側にも相対親directoryを返します。IsFolderはitem全体のdirectory flagを全側へ返し、現行aliasのmodified／isdirectoryはDate／IsFolderへ写像します。正規表現f:／d:の規則だけは各側へ適用します。

固定Windows原本は[FolderPropertyFilters](../tests/Fixtures/FolderPropertyFilters/README.md)、旧`AllEqual`の独立native oracleは[FolderAllEqualOracle](../tests/Fixtures/FolderAllEqualOracle/README.md)です。実アプリCLIの限定E2Eは`--folder-properties-only`、GUI自己検証は`--self-test <output> --folder-properties-only`を使用します。CLIは固定Shell／hash値・二者／三者の存在mask・directory除外と不正入力を確認します。FullPathの二者／三者root、bare／side指定、欠落側、混合式、別規則、directory除外の動的ケースと、AllEqualのscalar／全側Size・Prop配列／欠落側None／配列先頭と短絡／case directive／不正arityも同じ限定E2Eで検証します。GUIはPROPVARIANT変換・再帰／浅いtruth・実storeの値と状態・実比較ボタンからの二者／三者表示を確認します。比較の走査／取得はUI外で実行します。GUI自己検証ではAllEqualのSize／property配列・SHA-256取得・配列先頭による未評価hash尾・不一致時の短絡を確認します。実hashの最初のchunk後に内部gateを注入してdispatcher応答と実中止、前回表示／操作復帰、取得中の入力変更による古い候補の不採用と次の正常比較も確認します。通常の全体E2E・全体UI自己検証にも含め、限定実行を全体／AOT検証の代替にしません。

フォルダー`.flt`の`AnyOf(expression)`は引数を一度だけ評価します。配列の直下にboolean trueが一つでもあればtrue、空配列・全false・非booleanだけ・入れ子内のtrueだけならfalseです。入れ子と直下のtrueが混在する場合はtrueです。scalar booleanはそのまま返し、その他のscalarは`None`です。引数は正確に1個で、関数名の大小文字を区別しません。`AllOf`と同じ全側metadata contextを伝播し、`AnyOf(Exists)`は不在側があっても存在側を採用、`AnyOf(Size > 0)`は存在側の一つでも正なら一致します。`not`・`AllOf`・`AllEqual`・`Len`と合成できます。一般行変換のAnyOfとは別の関数です。[固定native oracle](../tests/Fixtures/FolderAnyOfOracle/README.md)は62件で、独立readerと自己検証の注入property contextで型・直下配列・評価回数・例外伝播・arityを照合する構成です。CLIと実GUIは二者／三者・不在側・実Shell値を検証し、任意の空／入れ子配列は自己検証の注入範囲です。今回の追加は検証実行前であり、過去のAllOf／Lenの成功件数には含めません。

フォルダー`.flt`の`NotEach(expression)`は正確に1引数を一度評価し、scalar booleanを反転、その他のscalarを`None`にします。配列なら直下の各要素へ適用し、長さ・順序・`None` slotを保ちます。要素が入れ子配列なら、その直下にboolean trueがあるかを反転します。`[true,false,1] → [false,true,None]`、`[[true],[],[[true]]] → [false,true,true]`で、内部要素への再帰変換や深いtrue探索は行いません。空配列は空配列のまま、空の入れ子配列slotはtrueです。論理`not`は結果全体を一つの真偽値へ縮約してから反転するため別の意味です。関数名は大小文字を区別せず、引数なし・複数引数・不完全構文を拒否します。

`NotEach`は全側のmetadata配列を一度評価します。`NotEach(Exists)`や`AnyOf(NotEach(Exists))`は不在側がある項目、`NotEach(Size > 0)`は存在側の少なくとも一つが0 byteの項目に一致します。`AllOf(NotEach(Size > 0))`は全側が実在して0 byteのときだけ一致します。不在側のSize比較は`None`で、falseとして反転しません。`not NotEach(Exists)`は全側が存在するとき一致し、`NotEach(not Exists)`は先に`not`で全側のExistsを一つに縮約します。

通常metadataの配列は不在側を含めて二者2／三者3 slotです。`Prop`はWindowsの既存Shell変換（scalar、文字列vector、byte vectorのhex等）だけを扱い、配列リテラルや任意のboolean／入れ子配列を通常GUIへ入力する機能は追加しません。任意配列の長さ・順序・未定義と浅い入れ子判定は自己検証のproperty-context注入範囲です。式16,384文字・入れ子64・規則512・ファイル1 MiBの既存上限を維持します。[FolderNotEachOracle](../tests/Fixtures/FolderNotEachOracle/README.md)は原典のdirect function 63件と別のarity block 4件で、旧parser／Shell／GUIを通した証拠ではありません。製品CLIは二者／三者・不在側・合成・不正arity、Windowsの実Shellサイズ比較を扱い、GUI自己検証は実比較ボタンと不正フィルター後のモデル／表示行保持も確認する構成です。追加分はbuild／E2E／自己検証の実行待ちです。

フォルダー`.flt`の`AllOf(expression)`は引数を一つだけ受け取ります。引数が全側の結果配列なら直下の要素がすべてboolean trueのときだけ一致し、空配列は一致、false・非boolean・入れ子配列を含む配列は不一致です。scalar booleanはその値を返し、その他のscalarは`None`です。条件式に`AllOf`を使うと、`AllOf(Size > 0)`のように全側への条件を表せます。

フォルダー`.flt`の`Len(expression)`は引数を一つ受け取り、配列は直下のslot数、文字列はUnicode code point数、`None`は0、その他のscalarは1を整数で返します。入れ子配列は展開せず、絵文字のsurrogate pairは1、結合文字やZWJは個別に数えます。`Len(Name)`は不在側を含めて二者なら2・三者なら3、`Len(LeftName)`は左の名前の文字数、不在側のscalar属性は0です。`Len(Prop('System.Size'))`も全側のslot数を返し、`AllOf(Len(Name) == 3)`や`AllEqual(Len('abc'), 3)`として合成できます。関数名は大小文字を区別せず、引数なし／複数引数／不完全な構文は拒否します。不正Unicodeの旧helperとの互換は対象外です。[FolderLenOracle](../tests/Fixtures/FolderLenOracle/README.md)の38件と、`--folder-properties-only`の実アプリCLIで二者／三者・不在側・Unicode・合成・不正構文を照合する構成です。

移動はWindowsとmacOSの同一ボリューム内で、既存の移動先を上書きせず実行します。ディレクトリ、別ボリューム、既存移動先は拒否し、コピーしてから元を削除する経路へ切り替えません。複数ファイルの途中で失敗した場合は、既に移動した項目を戻さず、結果を表示して実ディスクを再比較します。

「左側の差分ファイルをゴミ箱へ」「右側の差分ファイルをゴミ箱へ」は、選択した項目のうち比較結果が差分の通常ファイルだけをOS標準のごみ箱へ移します。三者比較では方向欄で選んだ側を対象にします。等しい項目・除外項目・比較エラーは候補にならず、選択したフォルダー内では差分ファイルだけを処理してフォルダー自体を残します。空フォルダーとリンクは対象外です。確認後に内容とフィルターを再検査し、ごみ箱への移動をOSが確認できない場合は完全削除へ切り替えず停止します。複数件の途中で停止した場合は各結果を表示し、変更があれば実ディスクを再比較します。

小さいウィンドウでは上部の設定欄をスクロールして、入力パス・比較・除外パス・ファイルフィルターを操作できます。

| 操作 | 対象 |
| --- | --- |
| 選択をすべてコピー | コピー元に存在する選択項目。同一・除外項目も対象になります |
| 選択の差分をコピー | コピー元に存在し、同一・元側除外・比較エラーに該当しない項目 |

三者の「差分をコピー」は三者全体の比較結果で対象を判定します。第三側だけが異なり、選んだコピー元とコピー先の二側が同じ場合も候補になります。コピー先の読取り専用指定を検査し、読取り専用の側をコピー元にすることはできます。選択したコピー先以外のルートへの出力を拒否します。

比較モデルに子がある走査済みフォルダーを選ぶと、既知の子を対象にします。モデルに子のない終端、再帰比較をしなかったフォルダー、除外されて未走査のフォルダーを「すべてコピー」で選ぶと、実際の子孫全体を対象にします。準備後に未知の子が増えた場合は、この物理コピーを拒否します。「差分コピー」では元側が除外されている親を通りません。表示の行数だけでコピー範囲を判断せず、確認画面の方向・件数・パス・データ量を確認してください。

確認を続行すると準備済み項目を再検査してコピーします。大きいファイルもstreamで処理します。コピー先に同名の異なる種別がある場合、リンクや読取り専用・別タブの入力など保護対象へ書く場合は拒否します。コピー先だけにある項目は削除しません。途中取消・失敗で既に公開した項目は残り、結果に公開件数と停止理由を表示します。取消後に全体が元へ戻ったとは扱わず、実ディスクの再比較結果を確認してください。中止操作が完了すると中止済みの表示または公開件数を表示し、新しい比較を始めた場合はその結果を保持します。

コピー元とコピー先の比較ルートが同じ場所や一方の子孫にある場合は、コピー準備時に拒否します。三者の残るルートに重なる出力も拒否します。MacのFIFO／socketはコピー元・コピー先・祖先のいずれにも使えません。別タブのWindows名前付きストリームを入力にしている場合は、その格納元ファイルの置換も拒否します。Windowsの通常表記と`\\?\`・`\\.\`のdrive／UNC表記を含む保護フォルダーの子孫も同じ対象として扱います。実在するWindows項目はvolume serialと128-bit file IDでも照合し、ローカルとUNCの実体別名を保護します。公開検査ごとに新しく照会し、その一回の検査内だけで祖先とIDを共有します。1検査の4096パス／4096照会／累積8Mi文字の上限、識別を取得できない場合は書込みを拒否します。Volume GUIDやGLOBALROOT等の別のデバイス名前空間は、出力保護時に拒否します。ドライブ文字またはUNCのパスを指定してください。同期Win32照会の個別timeoutや照会後の外部競合の完全防止は、この保護の実測範囲に含みません。

通常fileのmtimeと対応属性、コピー対象となった空のコピー元directoryのmtimeを保持します。実行計画に含まれた空directoryの日時は、既存のコピー先に子があっても転写します。コピー元に子があるdirectoryの日時は強制転写しません。Windowsの圧縮／Sparseは通常データをコピーする経路で、元の属性・物理割当量を保持する保証はありません。Windowsでは主本文とすべての名前付きデータストリーム（ADS）を一つの一時ファイルへコピーし、全内容の読戻し後に一度だけ公開する経路を追加しています。空のADS・Unicode・末尾空白を含む名前を保持し、既存コピー先だけにあるファイルADSは置換で除去します。コピー元directoryのADSは転送せず、既存コピー先directoryのADSを保持します。Windows通常版で6CLIと11GUIの全streamを独立照合し、共有予算の境界4GUIと中止の4GUIも確認しました。全体E2E・UIはWindows通常版とWindows x64 Native AOTで完了し、いずれも失敗0と全streamの独立照合を確認しました。三者フォルダー実装のコミット`11bfca921`は、Windows x64／ARM64・macOS Intel／ARM64のGitHub CIで全体E2E・headless UI・実OS clipboard検証を完了しています。EFSやACLなど完全なShellメタデータ保持は未対応で、元または既存コピー先がEFSなら準備時に拒否します。現在の未実装範囲は[移行一覧](MIGRATION.md)を参照してください。

Windowsの双方がlocal NTFSの場合、コピー元fileのEAを転写し、既存コピー先のcreationとDACLを保持する経路を追加しています。新規fileは親のsecurityを継承し、コピー元のcreation／ACLは転写しません。owner/groupの変更が必要な置換は拒否します。確認後の元／既存出力のidentity・creation・security・EA変更と共有metadata予算超過も拒否します。独立した副handleで公開用handleのClose後に最終属性を確定し、両Closeが成功するまでmetadata検証完了とは報告しません。途中で失敗しても公開済み出力は保持し、公開済みであることと失敗理由を返します。未公開の一時fileは、取得時に予約した処理予算と所有handleで破棄し、取消や予算枯渇後も清掃を試みます。清掃失敗はコピーの失敗理由へ残します。完全なShell metadata、SACL、ネットワーク上の権限保持は引き続き対象外です。最終属性の修正は通常版／Native AOT部品で各40ケースを独立照合しました。修正後の主アプリ限定E2Eは126成功／0失敗／1OS専用skip、限定GUIは239成功／0失敗です。普通／圧縮親・新規／既存DACL・4属性の16コピーを実行し、最終Close後の全属性・EA・security・DATA／ADSを独立照合しました。変更後の全体E2E／UI／同OS主アプリAOT／4RIDは検証待ちです。

CLIでは比較モデルのコピー候補だけを見る`--folder-plan`と、実際に準備・実行する`--folder-sync`を使います。候補が存在しても、物理コピー先の種別衝突などで実行を拒否することがあります。

```powershell
DiffBeacon --folder-plan left right --direction left-to-right --copy diff --select src --select README.md
DiffBeacon --folder-sync left right --direction left-to-right --copy diff --select src --select README.md
DiffBeacon --folder left right --middle middle
DiffBeacon --folder-sync left right --middle middle --direction left-to-middle --copy diff --select src
```

方向は`left-to-right`／`right-to-left`、中央を指定するとさらに`left-to-middle`／`middle-to-left`／`middle-to-right`／`right-to-middle`、対象は`all`／`diff`です。`--select`は比較ルート内の相対パスで繰り返し指定します。`--mode content|hash|timestamp`、`--filter PATH`、`--exclude PATTERN`、`--no-recursive`、`--show-filtered`も使えます。三者のcontent比較に限り`--max-content-bytes BYTES`で各ファイル組の三側共通読込上限を縮小できます（既定768 MiB・一側256 MiBの上限も適用）。GNU行照合の共有上限超過は比較エラーとして差分コピーから除外します。従来の`--folder-copy SOURCE DEST RELATIVE`は二者の同じ実行経路へ接続しています。実行結果JSONは各項目の公開・失敗・取消・未実行と全体結果を返し、成功は終了0、失敗は終了2です。logicalBytesは元ファイルの全データストリーム合計、destinationBytesは置換前のコピー先全データ、plannedIoBytesは全読込み・書込み予定量です。準備段階の拒否はstderrへ理由を出し、コピーを公開しません。

`--folder-sync`は使用中の`--filter`ファイルを一件も書き込む前に保護し、公開直前にもコピー先との重複を確認します。準備が終わるまではフィルター全体のSHA-256を照合し、実行開始後は準備で確定した比較条件を使います。実行中の外部フィルター編集は条件へ反映しません。読取り専用の`--folder-plan`はフィルター自身を含む候補も表示できます。

再現検証と実測範囲は[開発手順](DEVELOPMENT.md)と[固定fixture](../tests/Fixtures/FolderSync/README.md)を参照してください。

2026-10-07の三者比較を含む通常版／Windows x64 Native AOTの全体検証は、各E2E161,762成功・失敗0、各headless UI11,303成功・失敗0でした。固定原本63件・三組378比較と全6方向コピー、Windowsの長いDOS形式パス、第三ルート／読取り専用の保護を独立readerで照合しました。UI件数はローカル共有へアクセスできるこのWindows環境の結果です。コミット`11bfca921`の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37591140044)も成功し、Windows各E2E161,762件／UI11,303件、macOS各E2E161,833件／UI11,209件がすべて失敗0でした。実OS操作・任意文字コード／フィルターの旧完全互換などの未実測境界は[移行記録](MIGRATION.md)を参照してください。
