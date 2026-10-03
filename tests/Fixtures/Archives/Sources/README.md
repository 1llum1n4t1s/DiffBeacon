# 内包アーカイブの固定入力

`regenerate.py` はPython標準libraryだけを使うCC0のfixture生成器と独立検証器です。通常E2Eでは生成せず、固定した入力SHA・サイズ・全entry内容を検証します。ZipCryptoの二階層fixtureは検証専用の異なるpasswordを使います。

`manifest.json` に全入力のSHA-256・サイズ、内包chain、最終manifestの全entry内容、共有復号量の期待値を記録します。後方のCRC破損と内側CRC破損はPython `zipfile` でも拒否します。同サイズの二つのrootはmtimeを一致させてもSHAが異なります。

280MiBのTARは各140MiBの二entryを含み、固定gzipとそれを格納したZIPだけを保存します。生成・独立検証は逐次処理し、巨大raw TARを保存しません。raw TARのSHAと全entryのSHAを独立照合します。

```powershell
python tests/Fixtures/Archives/Sources/regenerate.py --generate
python tests/Fixtures/Archives/Sources/regenerate.py --proof artifacts/source-fixture-proof.json
```

生成後に `manifest.json` の固定SHAをE2E側へ反映し、全原本の `-text` を維持します。採取・生成用のPythonは製品の通常ビルド・実行依存ではありません。
