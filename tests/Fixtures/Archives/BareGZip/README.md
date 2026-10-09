# 裸の gzip 原本と検証境界

ここでいう裸の gzip は、明示した TAR／archive wrapper 鎖へ分類されない gzip を一つの論理ファイルとして読む経路です。原本43件は自作の payload と gzip header／DEFLATE／trailer から生成した合成データで、上流 fixture や旧 WinMerge の実行バイナリを採取したものではありません。原本・生成器は [CC0-1.0](LICENSE.txt) です。公式ソースとの設計上の対応を、出荷済み WinMerge DLL とのバイナリ同等性へ読み替えません。

## 固定原本

seed32件を `bare-gzip-inputs.zip`、追加11件を `extra-inputs.zip` に保持します。ZIPの各entryを読み、CRC・SHA・サイズを確認してから使います。原本は展開して製品へ渡し、元ZIPは変更しません。

| ファイル | SHA-256 |
| --- | --- |
| `bare-gzip-inputs.zip` | `929A192FA6BAAD4A79CAE6789E5B3919AF66F110C2C18A7EF9B700417CF8F76D` |
| `expected.json` | `29092407C235051E8EBCF6F4E209A26BD9782A51987A7676981E0C321851E83A` |
| `generate.py` | `D3BAED7CBFFE0F52EBF71B9249684871E7FDB19C4D698C27D387E7942EB6465C` |
| `extra-inputs.zip` | `0F354B3A5B1E033D9E97E49B3E6E114DECE6055D4D5F96B819A6FC6E043AD7C9` |
| `extra-expected.json` | `47044E7C7D706AA4194D5BAA65C18A57C9CCEF39940980DA4EC2A935F008F1C5` |
| `regenerate-extra.py` | `3AD86FC632A58010B22623342DE926DA8C6AAD416747FCF5C7B5F3804CF585D3` |

`expected.json` は最初memberのFNAME raw bytes・復号名・安全な相対path・fallback要否と、連結した全payloadのhex／SHA／サイズを分けて保持します。名前なしのfallbackはこのJSONでは未解決として残し、E2E側で下記のローカルMerge7z契約を確認します。seedの文字コード28591／65001／932は検証対象の集合であり、製品のサポートをこの3種へ制限する意味ではありません。

| 原本群 | 期待する境界 |
| --- | --- |
| seed32件 | 最初FNAMEの優先、最初の名前なし／空と後続名の無視、空member・全連結payload、case／相対subpath／backslash、Latin1／UTF-8／CP932、危険名、FHCRC／CRC／ISIZE／欠損／予約flag／末尾の拒否 |
| 追加11件 | root932＋inner65001のText／Binary、1252のEuroと1251名、名前失敗より先にCRCを拒否、traversal＋C1／不正UTF-8／不正DBCS、DBCSの末尾`0x5C`を含む`表.bin`の受入れ |

`.tar.gz`／`.tgz`／明示TAR wrapper鎖はTARの検証を優先します。gzipとして正常でも本文がTARでない `misleading.tar.gz` を、裸gzipへフォールバックして成功させません。内包entryの自動再帰展開は追加しません。

## 再生成

リポジトリルートから、Python 3で一意な外部Tempへ出力します。repo内・Codexユーザーディレクトリ内・リンク祖先のある場所・完全一致の `artifacts` フォルダーは使いません。出力先は両生成器とも未作成である必要があります。

```powershell
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('Codex/DiffBeacon/bare-gzip-regenerate-' + [guid]::NewGuid().ToString('N'))
python tests/Fixtures/Archives/BareGZip/generate.py --output "$runRoot/seed"
python tests/Fixtures/Archives/BareGZip/regenerate-extra.py --output "$runRoot/extra"
```

`generate.py` は `seed/inputs` の32原本、`expected.json`、ライセンスを生成します。固定seed ZIP自体の再梱包器ではありません。生成した全raw原本を固定ZIPの全entryおよび期待SHA／サイズに照合し、ZIPの入れ替えはcontainer bytes・CRC・全entryの再確認と固定SHAのレビューを伴う別変更として扱います。`regenerate-extra.py` は固定日時・ZIP_STOREDで追加ZIPと期待JSONを生成します。まず各gzip原本とpayloadの一致を確認し、ZIP／JSONの固定SHAも照合してください。生成内容を直接fixtureへ上書きしません。

保持が必要なのは原本・期待値・最小限の再現資料だけです。用途を終えた今回の外部Tempは、共通のWindowsごみ箱経由手順で清掃・残存確認します。共有cacheや他runは対象にしません。

## 独立照合の契約と限界

生成器は製品codecを呼ばず、Python `zlib` のraw DEFLATEとCRC／ISIZEから原本を作ります。seed生成器はPython `gzip` と、各memberを `zlib.decompressobj(31)` で逐次復号する独立経路の全bytes／SHA／EOFを比較します。期待payloadは自作の入力bytesから定め、製品出力からgoldenを生成しません。

Python標準`gzip`が受け入れることだけでは厳密な構造検証の証拠になりません。seed採取時には不正FHCRC・予約flag・末尾zero paddingの受入れが観測されています。製品は全memberのheader・FHCRC・CRC・ISIZE・footer・EOFと共有予算を検査し、zero paddingを含む未検証の後続bytesを拒否する契約です。[固定公式GzHandler](https://raw.githubusercontent.com/ip7z/7zip/9128b80e3a471108678e2b3ec3c985861c8d7b0f/CPP/7zip/Archive/GzHandler.cpp) の後続header失敗／`kDataAfterEnd`も、clean成功や旧WinMergeの実行結果とは区別します。

採用時にはseed32原本のSHA／サイズ／ZIP CRCと構造正常20件の全payload、追加11原本のSHA／サイズ／ZIP CRCと単段・二段のText／Binary payload4件が独立Pythonで確認されています。これは原本の確認であり、製品CLI・GUI・AOTの成功ではありません。

`BareGZipScenarios`は実アプリを別processで呼び、固定期待hex／SHA／名前に一覧・書出しbytesを照合します。再梱包結果は製品と別の.NET ZIP readerで全entry bytesを読みます。workspace v7・作業asset・包装展開／再読込み・旧v1〜v6への新field混入拒否・既存出力保持も対象です。`bare-gzip-independent-proof.json`は成功ケースの識別と期待SHAを記録するE2E harnessの成果物です。この機能専用に、生成された全成果物を再読込みして独立receiptを発行する別process readerは現時点で未実装です。その検証を実行済みとは扱いません。

headless自己検証は実ボタンの初回名前再試行／再比較、階層別設定の保存、previewの末尾検証、長い無視対象FNAMEを持つ既存wrapper、名前予算の境界、取消・古い候補・失敗時の表示保持、rootの出力保護をJSON／PNGへ残します。注入したheadless操作はネイティブダイアログ・実OS pointerの検証とは区別します。

## 格納名とfallback

既定はRFC1952のISO-8859-1（28591）です。明示した具体的なnumeric code pageをSourceのrootと各EntryChainのcontainerへ保存し、host ACPや本文の文字コードから復元しません。文字コードは成功した候補だけ採用します。CRC・予算・危険pathを名前の復号再試行へ分類しません。最初memberに空でないFNAMEがあれば物理basenameより優先し、後続memberのFNAMEで置き換えません。危険FNAMEをbasename化・整形して救済しません。

名前なし／空のfallbackは、SDK一般の`GetDefaultName3`より近いローカル実装 [Merge7zCommon.cpp](../../../../ArchiveSupport/Merge7z/Merge7zCommon.cpp) の `GetDefaultName`（363〜399行）と [Merge7z.cpp](../../../../ArchiveSupport/Merge7z/Merge7z.cpp) の `arc.DefaultName`上書き経路を参照します。gzipのAddExtensionが`*`である場合、物理logical basenameの最後のdotからsuffixを除去し、dotがなければ`noname`です。`file.gz`／magic-onlyの`file.bin`は`file`、拡張子なしは`noname`、`.gz`単独は空名になるため現行の安全なentry-path検査で拒否します。SDKの`~`追加やTrimRightをこのローカル契約へ混ぜません。これも旧DLLの実行同等性を確認したという意味ではありません。

この変更の出力は既存対応形式へのrepackです。裸`.gz`の単一file writer、directory／複数entryの拒否と独立復号を伴うwriter検証は未対応です。`.gz`出力pickerやwriter完了を主張しません。限定・全体の正確な呼出しは [E2E手順](../../../DiffBeacon.E2E/README.md) を参照してください。
