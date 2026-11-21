# 公式アーカイブ fixture

SharpCompress の公式リポジトリ、version `0.50.4`、commit `c083c6efd843a844b0c8f7878787360e815be781` の `tests/TestArchives/Archives` から、必要な 6 ファイルだけを取得した。ファイルは変更していない。バイナリはテスト入力専用で、内部に含まれる実行ファイルを実行しない。

| Fixture | 検証する形式 |
| --- | --- |
| 7Zip.solid.7z | solid 7z |
| 7Zip.LZMA2.Aes.7z | LZMA2 / AES 7z |
| Rar.encrypted_filesAndHeader.rar | RAR4 の本文・ヘッダー暗号化 |
| Rar5.encrypted_filesAndHeader.rar | RAR5 の本文・ヘッダー暗号化 |
| Rar5.solid.rar | solid RAR5 |
| Zip.deflate.WinzipAES.zip | WinZip AES ZIP |

`LICENSE.txt` は同じ commit のリポジトリルートから取得した MIT ライセンス。fixture ディレクトリに独立したライセンス文書は見つからず、リポジトリのライセンスを保持する。出典 URL・サイズ・SHA256・公開テストパスワードの定義元は `manifest.json` を参照する。パスワード値は manifest に記録しない。

公式 `tests/TestArchives/Original` の `exe/test.exe`、`jpg/test.jpg`、`тест.txt` は、HTTP で取得した内容をメモリ内でハッシュ化した。元ファイルはこの作業ツリーへ保存していない。テキストだけはリポジトリの LF（15044 bytes）と公式アーカイブ内の CRLF（15498 bytes）が異なるため、取得した元テキストを独立に LF→CRLF 変換して期待 SHA256 を算出した。この変換結果は実アーカイブのサイズ・ハッシュと一致した。manifest に元のサイズ・ハッシュと変換内容も保持する。CLI が解凍して得た各ファイルのサイズと SHA256 を、この独立した期待値に照合する。

solid RAR5 は solid 7z と同じ 3 ファイルに加え、空ディレクトリ `Empty` を含む。この二者の比較は `Empty` だけを右側固有の差分として扱い、ファイルの内容差がないことを確認する。CRC 不一致の ZIP は E2E 実行時に BCL で生成し、local / central ヘッダーの CRC フィールドだけをゼロへ変更する。公式 fixture は改変しない。

取得元: [SharpCompress 0.50.4](https://github.com/adamhathcock/sharpcompress/tree/0.50.4/tests/TestArchives)、[MIT ライセンス](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/LICENSE.txt)。
