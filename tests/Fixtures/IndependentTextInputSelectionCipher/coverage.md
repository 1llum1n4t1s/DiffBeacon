# input306の実装範囲と未検証

実暗号化outer＋別passwordのinner兄弟を扱う1caseの草案。担当指示で製品build/run・E2E・PNG採取・AOTは未実行。fixture-onlyのPython独立復号成功をGUI成功へ加算しない。

| 条件 | 草案／独立readerの範囲 |
|---|---|
| 実outer暗号化 | 全outer entryの暗号flag／CRC／SHAをPythonで照合。実Loadはmissing→wrong→correct |
| left inner retry | Openでouter保持・空inner、missing／right値拒否→left正値。旧outer全一覧保持 |
| Back／right兄弟切替 | 撤去left欄clear、outer正値保持。right missing／left値拒否→right正値。旧outer全一覧保持 |
| 採用／旧state | 実Compare元Task終端、candidate一件・tab一件、typed right route／root SHA／leaf literal bytes、旧parent全証跡とstore identity世代保持 |
| 非永続化 | 元opener／Compare終端後の保持全TextBox clear、ProjectJsonContext project JSON／receiptのkeyと公開検証値流出検査 |
| 描画 | 1000×680／850×550、masked欄2つ・Load・listをscroll後にPNG／bounds採取。独立PNG全CRC／scanline／geometry照合 |

このsuiteはinput301で未検証と残した実暗号outer＋兄弟切替を補うが、R02/R03全体を完了扱いにしない。private credential配列内部、callback pending中Close／全window寿命、default1GiB境界、全tab/filter/workspace/asset出力保護、OS pointer／native picker／保存dialog、macOS/AOTは引き続き別の実測が必要。

原本・backup・成果物の削除はしていない。fresh run inputだけ初回read前にUTC2024-01-01T00:00:00.1234567Zへ設定する。backupは通常File.Copyを使いmetadataを変更しない。所有範囲は新Cipher fixture／新CipherChecksとartifacts/input306のみ。
