# input301の範囲と残作業

15 caseを実行する草案であり、15 case合格の記録ではない。担当指示により製品build/run・E2E・PNG採取・AOTは未実行。`artifacts/input301/fixture-reader-proof.json` はPython標準libraryで9 rootの原本SHA・ZIP全entry CRC・固定literalを検証したfixture-only証拠。

| 契約 | 草案にある実button経路 |
|---|---|
| password初回任意／各階層retry | 固定two-passwords原本から未入力→誤値→正しいouter→inner未入力→誤値→正しいinner、元Load taskとCompare拒否／採用 |
| 共通outer保持／兄弟innerclear | siblings rootは非暗号化、任意outer欄へ非空既知値を入力。固定暗号化innerを左右に無変更包装し、Back→右Openで保持／clearを検証する。実outer暗号化の証拠と混同しない |
| root clear／4096／Closed | 撤去したTextBoxの参照も保持して値clearを確認、4096→4097 setterまたはPasswordCopy guard、Close gate→元Load/opener終端後の全現欄clear |
| JSON／project非永続化 | source-generated ProjectJsonContextで通常project JSON採取、独立readerが全JSON keyと公開fixture値の非流出を検査。private confirmed配列内部のゼロ化は直接観測しない |
| CRC／危険leaf／directory／不在選択 | 固定後続CRC破損・最小../leaf.zip入力・directory row・実選択解除からCompare拒否、旧確定全一覧保持と新root未検証 |
| chain8／9 | 固定depth8全階層→leaf採用、depth9で第9Openの経路／一覧不変。第8container内のinner.zipを普通leafとして確定する操作は別契約 |
| root／work予算 | 339-byte固定rootを実length-1の検証専用ReadLimitsにする。既定1GiB超rootそのものの拒否は未検証 |
| link | 新runに固定rootへのfile symlinkを作り、実Load拒否。作成不能ならassertion失敗／reader失敗とし、成功扱いにしない |
| stale応答 | 確定済み一覧を持った元Load gate中にroot変更→遅れた元Task完了後の旧一覧保持とCompare拒否。Closed中のLoadも同様に元Task終端を観測 |
| layout | 暗号化innerのmasked欄2つ・Load・100DIP一覧を1000×680／850×550でscroll後capture、PNG全CRC／scanline／boundsを独立確認 |

親が適用する接点はReadLimitsだけ。既定nullのままnew ManagedArchive(ReadLimits)へ渡しdecoderは置換しない。専用mode／全体登録／別process E2E runnerはintegration-proposal.jsonに記載する。

## 残る未検証・再開条件

- 実暗号化outer＋複数兄弟container切替は既存固定Sources原本の構造（一inner.zip）で構成不能。既存CC0 `tests/Fixtures/Archives/Sources/regenerate.py` の `encrypted_zip` は標準libraryで単entry ZipCryptoを独立生成・zipfileで復号検証する実装を持つ。これを複数entry local/central recordsへ拡張した独立generatorで固定fixtureを追加し、Python全entryCRCとliteralをpinしてから再開できる。新dependencyやユーザーsecretは不要。
- private confirmed/selection/candidate credential配列の内部clear、callback pending中のClose、window/parent全寿命はこの所有範囲では追加hook未提案。現PasswordFieldsのclearと別契約として残す。
- default1GiBサイズ上限、全tab/filter/workspace/asset出力保護、後発read retry・別side同時gateはR02/R03全体の合格へ加算しない。input301が扱う小budget／単sideは代替ではない。
- PNG実画面、OS pointer／native picker、macOS link/mtime/AOTは親の実測が必要。古い425cases・B588/D99・27readerを今回の入口の合格へ加算しない。

今回のraw backupは `artifacts/input301/rawbackups/`。通常File.Copyで保存しmetadataは変更しない。fresh run inputだけ初回read前にUTC `2024-01-01T00:00:00.1234567Z` を設定し、独立statのns実値で照合する。成果物／原本／backupの削除はしていない。
