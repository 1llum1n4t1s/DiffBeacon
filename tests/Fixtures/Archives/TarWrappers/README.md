# TAR 多層 wrapper 固定原本

44件（正常22／拒否22）と typed source 用ZIP4件。`manifest.json` の入力SHA・外側からの全層復号SHA・終端TAR SHA・全entryの型／内容SHA／サイズ／mode／uid／gid／mtimeを固定期待値とする。`format` は内側からの実層順であり、tgzはtar.gz、tbz/tbz2はtar.bz2、tazはtar.Zへ正規化する。

元TARは `../TarZ/small_repeated.tar`（SHA `73C8B483CFD3C0DAAC791C931FA5DC871ADF7B8BEA2FF6D6251A9DC22451BF9E`）。Zは既存 `../TarZ/literal-nonblock-b9.tar.Z`（SHA `790E51F22C75EBA2ACC2EE85B25D43782651315618EF23A4E5DAADA891A5439D`）と同じ固定nonblock 9bit literal包装である。既存原本は公式ncompress／7-Zip全bytes一致済みで、出典・ライセンスは [TarZ README](../TarZ/README.md) に保持する。ZIP偽装の中身は既存 `../Wrappers/payload.zip` のCC0自作データ。新たな外部toolの実行・取得は不要。

`verify.py` は製品codecを参照せず、Python stdlib gzip・BZ2Decompressor・tarfileを使う。BZip2は各memberのEOFと未消費bytesを検査する。Z独立readerは固定9bit literal列だけを読み、dictionary/CLEAR/幅遷移の一般decoderではない。正常原本では全層復号bytes SHAとTAR metadata・全entry内容を照合し、拒否原本では独立decoder／TAR reader／明示深度・終端契約で失敗を確認する。ZにはCRC・宣言長がないため全semantic改変検出を保証しない。

## 再生成・独立検証

原本を上書きせず出力先を指定する。既存manifestと異なるstdlib出力は再生成失敗とし、固定原本を保持する。

```powershell
python -B tests/Fixtures/Archives/TarWrappers/regenerate.py --output artifacts/local/archive-tar-wrappers/regenerated
python -B tests/Fixtures/Archives/TarWrappers/verify.py --output artifacts/local/archive-tar-wrappers/independent-proof.json
```

実アプリE2Eが作成した `independent-evidence.json` は `verify.py --output <proof.json> --evidence <independent-evidence.json>` で再照合できる。通常list全entry・全exports・typed root/sourceのmtime・展開全bytes／directory・repack ZIP全entry bytesをPython stdlibで確認する。正常22件すべての実経路成果物と正常Source2件の欠落も拒否する。通常archive-listはmtimeを公開しないため、全正常typed rootのsource-listでmtimeを照合する。

`independent-proof.json` は採取時の全件照合結果。E2Eは正常全件の通常archive-list／entry／extract／repack、標準archive／tar／tar-metadata provider、Source descriptorからの全entry照合を行う。prefix previewでも壊れた後続TAR・各wrapper EOFを受理しない。拒否22件、共有深度（通常8層成功、Sourceのentry遷移＋8層拒否）、中間サイズ20479 bytes、累積decoded20480 bytes、入力SHA照合のwork64 bytesと、SHA照合後のアーカイブ処理まで到達するwork1000 bytes（拒否理由も照合）、取消・入力と既存出力保持を確認する。これら実アプリ検証は独立readerの成功とは区別する。

単層TARとaliasの既存経路、Zip／7z／Rarの既存wrapperを退行させない。read対応は多層writer対応を意味しない。裸gz／bz2／Zやxzは本fixtureの対応範囲外。

fixture固有 `.gitattributes` が全包装原本・manifest・独立reader／生成器の改行変換を禁止する。Gitへの登録は親担当。採取内容はCC0-1.0。

## GUI取消と古い候補の破棄

`--self-test <出力先> --tar-wrapper-gui-only` は正常22原本で実中止ボタンを押し、読取り開始時・完成候補の採用直前・Refresh採用直前の取消を確認する。確定panel・rows・previewを保持して再操作できること、新比較の結果だけを採用して旧candidateをDisposeすることを記録する。全体UIと `--archive-tar-wrappers-only` E2Eにも接続している。

`verify-gui.py <facts.json>` は固定原本から全層を独立復号し、GUI候補の全63項目のpath・型・サイズ・SHA・mtime、原本全bytes、各取消観測と旧候補破棄を照合する。22件の欠落・重複を拒否する。JSON・PNG・実プロセスstdout／stderrを保持する。読取り開始時と採用直前の取消は復号中の取消とは別であり、この検証だけからdecoder処理中のタイミングを実測済みとは判断しない。
