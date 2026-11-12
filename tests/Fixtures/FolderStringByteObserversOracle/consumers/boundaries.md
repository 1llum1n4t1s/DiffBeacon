# Native string consumers extension

固定commit 6780924ba35f7f55af0f6f14dd302561175830a6。元observerのgolden/indexを維持し、追加分はこのconsumersフォルダーのみ。全1042ケース。33 malformed原本IDごとに27ケース（3数値化のraw/prefix、Trim3種、両感度contains、両感度InRange/IsWithinのequal/wide bounds、両感度matches/recontainsのmalformed subject/pattern）を採取する。

| consumer | native境界 | 今回の観測 |
| --- | --- | --- |
| ToInt/ToDouble/ToNumber | std::stringのcomma全除去後std::stoll/std::stod。string以外はNone。CRT locale依存。全文消費必須、例外はNoneに吸収 | 初期CRT locale Cをstdoutのenvironment行で確認。malformed単独/数値prefix付きはNone。doubleはIEEE754 bits hexで固定。別locale未検証 |
| Trim/TrimLeft/TrimRight | space/tab/CR/LFのbyte集合だけを削る、getAsStringと浅いmap | malformed bytes保持、NUL/Unicode空白を保持。TCHAR変換なし |
| contains | compute/BinaryOpNode runtime、byte boyer_moore_horspool、case-insensitiveはunsigned char→std::tolower | 観測C localeでASCII lower、malformed bytes検索可能。空needleは原典のfirst!=second判定によりfalse。TCHAR変換なし |
| InRange/IsWithin string | lower→upper→source、std::string比較またはPoco signed-char icompare | malformed source/boundsを比較可能。上端exclusive/inclusiveの差、型不一致None。TCHAR変換なし |
| matches/recontains | 無改変native regex helper→固定Poco RegularExpression→固定PCRE2、RE_UTF8→PCRE2_UTF/PCRE2_UCP | 全33 malformed subjectとpatternはfalse。Poco RegularExpressionExceptionはnative helperがfalseへ吸収。regex自体はstd::stringを受け取るがUTF-8検査境界があり、置換復号して受理したわけではない。TCHAR変換なし |
| like | computeのPoco Glob呼出し、GlobのUTF8 TextIterator/Unicode lower経路 | 別Glob拡張で確認する。consumers goldenにはadapter stub結果を混ぜていない |

原典cppの各関数body/signature、Poco constructor/match/options、PCRE2 amalgamationを無改変抽出して実行。archive-provenance.jsonは既存LineTransformReplaceの固定reference-source.zipをSHA/Git blobで検証し、PCRE2原典・ライセンスを保持する。include-specs.jsonでbyte範囲とhashを固定。型接続とthrow literalだけadapter。unsupported timestamp/Glob等のstubはこのケース群では呼ばない。

入力/結果stringのvalueはraw hex。double結果はbig-endian IEEE754 bit patternの16桁hex。None/falseを区別する。数値化は一般の文化非依存Unicode parseと同じではない。Python readerの正規表現modelは採取した小さな制御pattern集合の独立照合に限り、Python reとPCRE2全機能の同等性を主張しない。

Windows x64 MSVC developer shellで `python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/consumers/reproduce.py --output <未使用D外部run> --compiler cl.exe` を2run実行する。`verify.py` はcollector/managedをimportせず、独立byte/numeric/制御pattern modelとarchive/source/include/compiler/EXE/C compile/CPP compile/exit/PID/birth/handle/EOF/rawの一致を確認する。

初回完全検証: `python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/consumers/verify.py --run <作業フォルダー>\DiffBeacon\string-bytes-consumers-20261020-r1\run1 --run <作業フォルダー>\DiffBeacon\string-bytes-consumers-20261020-r1\run2`。再採取時はgoldenと照合後にこの拡張のcapture-index/manifestだけを更新する。既定readerは外部raw証拠の検証を意味しない。

証拠は同D rootに再検証用として保持。別runへ完全移行後に清掃可能。今回削除/清掃なし。parser/optimizer/managed/public flt/GUI/CLI、全numeric grammar/全locale/全regex featureは未検証。
