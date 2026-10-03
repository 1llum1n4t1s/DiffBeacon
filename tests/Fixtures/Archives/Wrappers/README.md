# 明示 archive wrapper 鎖の固定入力

`payload.zip` は CC0 の固定データです。Python 標準 `zipfile` で生成し、全5 entry の型、サイズ、全bytesのSHA-256、2001-02-03 04:05:06のZIP日時を `manifest.json` に記録しています。`contained.zip` は通常leafとして保持し、内側entryを再帰展開しません。ZIP派生名、単段/二重/混合/8段の `.gz`/`.bz2`/`.Z`、GZip FNAMEと物理名の差異、正常連結・空memberと破損後memberを含みます。

7z/RAR/暗号化ZIPの終端原本は既存 [Archives manifest](../manifest.json) の固定SHA・MIT・原本URLを引き継ぎます。`.Z` 作成と独立復号は既存 [ncompress reference source](../TarZ/reference-source/) と同じ明示referenceを使います。製品に外部toolを追加しません。各層の全復号bytes/size/SHAと終端原本を固定し、標準 `zlib`/`bz2`/`zipfile` と公式ncompressの別プロセスで照合します。

リポジトリrootから既存referenceを明示して再生成します。変更後はmanifest SHA・oracle・E2E・実効Git属性を照合してください。

```powershell
python tests/Fixtures/Archives/Wrappers/regenerate.py --z-reference <existing-ncompress>
python tests/DiffBeacon.E2E/archive_wrapper_verifier.py --root tests/Fixtures/Archives/Wrappers --decoder <existing-ncompress> --output artifacts/local/wrapper-oracle
```

nearest `.gitattributes` はbinary/manifest/licenseへ `-text !eol` を指定し、祖先の `manifest.json text eol=lf` を上書きします。`git check-attr text eol` と filtered/no-filter `git hash-object` 一致、親stage/commit後のGitblob rawSHAを確認します。E2Eはmanifestと全inputの固定SHA、全entry export、compare、repack、extract、誤password/破損時のstdout空と入力・既存出力保護を保存します。

GUI自己検証は実Auto比較、preview、Refresh、Stop、完成候補の取消・古い候補廃棄、generic masked retryの実Retry/Cancel/×、確定Rows/preview/表示元保持と再操作をJSON/PNGへ記録します。小予算は公開ManagedArchiveから検証します。codec私有API、friend assembly、unit testは追加しません。ZのCRC・宣言長・明示EOFがない限界、ライブラリ内部でI/Oに戻るまで取消が遅れる可能性を保持します。

7z/RARの時刻goldenは `metadata-golden.json` と全nodeの公式生listing（`7Zip.solid.7z.listing.txt` / `Rar5.solid.rar.listing.txt`）に分けて固定します。既存公式7-Zip 26.03のSHAは `6EE3C0ED0B27663C1B948AE85A7C0BB073AED1498983182F3F0DF1F6A8C30B2F`、採取引数は `l -slt -ba -sccUTF-8`、採取TZはTokyo Standard Time（+09:00）、cultureはja-JPです。listingはcollectorのlocal表示であり、goldenは各nodeの7桁小数を丸めずUTCへ変換します。7zのFILETIMEと、このRAR5原本のWindows時刻は100ns精度です。一般RARのUNIX時刻等へこの精度を要求しません。

再採取ではtool/input SHA、`Get-TimeZone`、`CurrentCulture`、実exitとraw stdout/stderr bytesをrun内へ保存します。Python `subprocess.run([tool, 'l', '-slt', '-ba', '-sccUTF-8', input], stdout=PIPE, stderr=PIPE)` のbytesを直接保存し、listingをtext変換して再構成しません。全Path/Folder/Size/Modifiedを順次解析し、collector offsetを明示して `DateTimeOffset(new DateTime(...), offset).ToUniversalTime()` へ正確なticksを渡します。Python datetimeだけでは7桁目を落とすためgolden生成に使いません。`regenerate.py` はこのmetadata原本を再生成しません。

App自己検証は固定goldenと既存原本だけをEmbeddedResourceへ同梱し、公開ReadManifestで全11nodeの型/size/日時を検査します。APIのLocal DateTimeをrunner自身のTZでUTCへ戻したticksがgoldenと完全一致することをJSONへ残します。生listing時刻をそのまま各runnerのlocal期待値にしません。通常codec・通常実行へ採取tool依存は追加しません。