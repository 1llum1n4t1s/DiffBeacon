# UNIX compress / TAR.Z fixture

正常原本39件。blockはncompress 5.1の無改変出力24件（3入力×9–16bits）、nonblockは歴史的ncompress v4.1 `-C` の無改変出力14件（2入力×10–16bits）。残り1件は独立した自作9bit literal列であり、歴史的辞書境界の解析値を期待値に使っていない。

`manifest.json` の固定SHA、元TAR全bytes、全entryの型・名前・内容bytes/SHA・uid/gid/mode/mtimeを正本とする。原本の全TAR bytesは公式ncompress 5.1とfull 7-Zip 26.03の両decoderで一致を確認した。幅遷移、辞書満杯、block CLEARを含む。通常nonblock9のliteral原本は辞書が満杯になるまで9bitを保持するmodern形式を検証する。歴史的v4.1のmaxbits9原本2件は258番codeで10bitへ進む旧自己互換との差があるため正常goldenには含めない。maxbits9を一律拒否する根拠ではない。

元入力はDiffBeacon検証用の自作決定データ（CC0-1.0）。既存採取のseedは`0xD1FFBEAC`、Python `random.Random(seed).getrandbits(8)`をentropy→second phaseの順に使用。smallはASCII反復8192bytes、entropyは131072bytes、two_phaseは反復131072＋entropy131072bytes。元TARはPython stdlib USTARでempty directory、data.bin、日本語.txtを保持した固定原本。再生成の際は元TAR bytesをそのまま使い、別バージョンtarfileでTAR自体を再構成しない。独立literal包装は各元byteをLSB順の9bit codeとして連続出力する。生成算法は`regenerate.py`に保持した。

## 出典とライセンス

- 現行公式ncompress commit `03592d7a4702bb83558b6f1520c2a6a5381374c8`、[compress.c](https://github.com/vapier/ncompress/blob/03592d7a4702bb83558b6f1520c2a6a5381374c8/compress.c)。SHA `29C5A78005921A7881D8C83EA711E826C8C87F354B4A2F47F8C474117F6646D8`。public-domainは`ncompress-UNLICENSE`と`reference-source/UNLICENSE`へ原文保持。
- 歴史的ncompress commit `fb52189600278fdaf6b101caf180e5ee4b0e0ecb`、出典・全SHAは`nonblock-provenance.json`。当時のpublic-domain宣言は`nonblock-source-README`に保持。`reference-source/compress-v4.1.c.orig`は無改変。Windows適応は`compress-v4.1-windows.c`／`windows-adapter.h`（宣言、CRT、binary stdioのみ）。compress()～getcode()の算法原文区間を変更していない。歴史的compileは適応sourceと`patchlevel-v4.1.h`を研究専用directoryへ配置し、当時の名前`patchlevel.h`でビルドする。
- full 7-Zip 26.03の公式配布toolは製品・fixtureへ同梱しない。採取toolのexe/dll SHAはmanifest.toolsに記録した。standalone7zaにはZ handlerがないため代用しない。
- 製品codecはこの原本の期待値を読み込まず、owned managed implementationとして動作する。研究用C／compiler／公式toolは製品依存へ追加しない。

## 再生成と実経路の独立検証

保持済み公式toolを明示し、リポジトリrootから次を実行する。新downloadや既存fixtureの上書きを行わず、指定したartifactsへ出力する。

```powershell
python tests/Fixtures/Archives/TarZ/regenerate.py --ncompress <公式5.1decoder> --legacy-ncompress <公式4.1-C対応encoder> --sevenzip <full7z> --output artifacts/tar-z-regenerated
pwsh -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --tar-z-only --z-reference artifacts/z-reference/local/ncompress.exe --z-sevenzip <full7z> --output artifacts/e2e/tar-z
```

reference buildは固定5.1原本だけをWindows MSVC／macOS clangでhost architectureにコンパイルする。5.1原本は上流のMSVC/binary stdio対応を含むため追加shimなし。macOSの出力は`ncompress`。build.logとbuild-proof.jsonにsource／patchlevel／compiler／decoder SHA・引数・exitを残す。通常.NET buildや製品起動にC/compiler/tool探索を追加しない。

限定E2Eは通常CLI全39原本、全entry bytes、standard archive／tar／tar-metadata provider、create/repack/entry/extract、独立reference decoder＋Python stdlibのTAR metadata、壊れたheader/code/TAR、既存出力保護、包装metadataと相対入力の再読込みを検証する。`--z-sevenzip`はローカル第二decoderの追加照合。CIは`--z-reference`を必須にして各hostで独立writer復号する。限定は全体E2Eの代替ではない。

ZはCRC・宣言長・明示EOFを持たず、全semantic改変や末尾paddingの全欠損検出を保証しない。padding zeroを形式の必須条件にしない。private codecの全read分割位置・Flush故障・leaveOpenを直接叩く単体試験は実施していない。通常TarReader/TarWriterの分割I/Oと公開サービスの上限・取消・atomic保存経路、ソースの契約確認を区別する。

標準tar/tar-metadataの復号・hashは背景タスクで実行し、GUI callerは結果採用直前にも取消を確認する。実アプリのheadless GUIは両provider選択の全canonical本文・metadataと保存拒否、展開32 MiBの反復entryで実「中止」ボタンを操作するearly/late取消を検証する。完成したBuiltin結果、最終両Editorの前回本文保持、取消表示と入力SHAをJSON/PNGへ保存し、偽provider・private codec・friend assemblyの試験を追加しない。
