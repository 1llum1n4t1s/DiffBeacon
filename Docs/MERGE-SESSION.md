# マージ結果セッション

GUIは固定 `feature/4pane-merge` 原典の分類・見かけ行配置・採用順序を参照し、二者、独立三者、祖先三者を分ける。中央が比較対象の場合は祖先として自動解決しない。二者に中央／祖先の採用はない。

`FourPaneGenerator` は固定GNU行算法を使う。製品の `DecodedBody` は復号済み本文とBOMを区別し、本文先頭のU+FEFFを保持する。比較キーは既存 `ComparisonOptions.NormalizeKey` と共通で、Trim、Unicodeの大小文字・空白設定を原文へ適用しない。`LegacyRawUtf8` は採取したGNU原本との照合用であり、通常GUIの入力経路には使わない。

`FourPaneAlignment` は元diffの文脈と全原文を保持してEOF・ghost・実空行を同期する。`FourPaneMaterialization` は生成済み作業量と保持予約を引き継ぎ、配置、原文対応、結果、祖先自動採用の準備を合計予算内で行う。上限・取消・不正設定では候補を採用せず、旧表示・本文・履歴を保持する。保持予約は実ヒープ使用量の測定値ではない。

`ResultLineBuffer` の物理行と見かけ行数は別であり、編集は正確な文字範囲と明示ownerを使う。結果タブと第四ペインは同じ `ResultEditSession` を共有する。確定した差分や共通本文は編集でき、linked未解決の短縮表示へ直接入力する操作は拒否する。元diffからの採用で隣接本文を消さず、改行なしの文字列同士も原文どおり連結する。保存時は、競合と二者の未解決差分をRIGHT→BASE（独立三者はMIDDLE）→LEFTの本文付きマーカーへ展開する。三者の非競合で未採用の区間は、原型のfallback規則（FirstOnly／SecondOnlyは左、ThirdOnlyは右）で原文へ展開する。未解決数は採用操作まで保持し、通常の保存は未解決を拒否する。

祖先自動マージは末尾から一つのUndo groupで非競合の独立変更を採用する。共通・自動・採用済み・未解決・競合・手編集を区別し、本文と解決状態を一緒にUndo/Redoする。行の採用元表示はCoreのmarker判定を使い、履歴のrevision増加だけで採用済み行を手編集へ変えない。Commonは原型どおり追跡対象の手編集markerを持たない。

IME未確定入力がある間は比較の切替・保存・採用を拒否する。非同期clipboardは開始時のhost・接続世代・本文・version・選択を再検査し、元diffの選択を戻しても古い要求を採用しない。実OS IME／clipboardの操作資格はheadless注入と区別する。

新結果が未保存であることと編集dirtyを分ける。保存は固定snapshotを展開し、祖先付きは祖先（なければ左）、独立三者／二者は左の文字コード・BOMを使う。通常のown入力への明示保存は既存契約を保ち、他タブ入力・原本アーカイブ・readonly・filter・workspace・asset・linkを公開直前に再検査する。入力文書のメモリと保存先を変更しない。保存中の新しい編集や別sessionを保存済み扱いにしない。

CLIの `--merge`／`--merge-select` は同じ `FourPaneMaterialization` と `ResultEditSession` を使い、未解決だけを採用する。`FourPaneCliSerialization` がLEFT→BASE→RIGHTの従来marker、合意した枝の全文・原文改行を保存し、祖先の文字コード・BOM・属性と終了コードを保持する。実DLLの17ケースでmarker全文・空本文・末尾改行なし・BOM・全採用元・独立変更・LF保持と不正指定時の既存出力保持を確認した。HTML／包装は入力比較を保持し、GUI結果へ読み替えない。

回帰は既存実アプリの `HeadlessSelfTest`、独立Text、内包作業レビューと全体E2Eで確認する。source-linked部品、通常DLL、同OS Native AOT、4RID CI、通常デスクトップを別の検証資格として記録する。現在の全体検証状況は [移行一覧](MIGRATION.md) を参照する。
