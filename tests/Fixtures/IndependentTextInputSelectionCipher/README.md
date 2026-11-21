# input306: 暗号化outerと別passwordのinner兄弟

既存CC0 [Sources encoder](../Archives/Sources/regenerate.py) の単entry ZipCrypto local/central記録を複数entryへ拡張した独立fixture。outerのleft.zip／right.zip／after.txtを全て暗号化し、左右innerは異なる公開検証値でleaf.txt／empty.txt／tail.txtを全て暗号化する。原本encoderは変更せずSHAをmanifestへpinする。

Python標準zipfileで全層の全entry内容・SHA・CRC・local／central暗号flagを照合する。全entryで未入力・誤値・他階層／兄弟の公開値を拒否する。本文期待はreaderに独立literalとして保持し、製品decoderの出力から期待を作らない。新dependency、ユーザーsecret、共有cache、AppData、clipboardは不要。

```powershell
python -B tests/Fixtures/IndependentTextInputSelectionCipher/verify.py --output artifacts/input306/fixture-proof.json
python -B tests/Fixtures/IndependentTextInputSelectionCipher/verify.py --gui-report <run>/ui-report.json --output <run>/reader-proof.json
```

実GUI草案は1caseで、実入口→実Load→left Open／再試行→Back→right Open／未入力／left値拒否→right正値→Compareを通す。observerで通知された元TaskをPumpし、共通outer保持・撤去left欄clear・失敗時の旧全一覧・旧parent・typed right route・本文全bytes・閉鎖後の欄clear・JSON非流出を確認する。通常／最小ウィンドウのmasked欄、Load、100DIP一覧はPNG全chunk CRC／scanline／boundsで照合する。

`generate.py` はinputs／manifestが存在しない場所だけで動く。通常検証では生成しない。再生成時はraw backupと復元手順を先に保存し、自動削除／上書きをしない。近いGit属性の内容は `.gitattributes.proposal` に保存し、親が適用する。共有登録はintegration-proposal.json、別process E2E runnerは新file proposal。
