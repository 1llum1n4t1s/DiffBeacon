# Consumer extension failure matrix — before implementation

- Preserve parent golden/capture-index and original evidence. New cases/adapter/reader/golden/receipt index belong to consumers only.
- Every 33 malformed source output: numeric conversions (raw, numeric-prefix+raw), trim left/right/both, runtime contains, InRange/IsWithin string bounds in both sensitivities. Verify None vs false vs exact bytes and argument traces.
- Number conversion: only strings, remove all commas, stoll/stod full byte consumption, empty/partial/overflow exception becomes None. CRT locale must be observed and recorded (bounded initial C locale, not all user locales). Include ordinary signs/exponent/commas/whitespace/NUL/overflow controls and exact double bits.
- Trim byte set is only space/tab/CR/LF; no Unicode whitespace expansion. Shallow source array and nested stringification must retain malformed bytes.
- contains: fixed runtime compute body; preserve byte sequence and unsigned-char ctype lower behavior in observed C locale. Input/needle empty and embedded NUL controls.
- Range: lower→upper→source evaluation, string-only bounds, upper exclusive/inclusive, std::string versus signed-char Poco icompare. Do not broaden timestamp/numeric branches beyond controls.
- matches/recontains: attempt exact pinned Poco regex+PCRE2 native binding with RE_UTF8; malformed subject/pattern, ordinary match/full-match/search and invalid syntax. Do not substitute std::regex or managed regex. If exact dependency binding cannot complete within bounded scope, report native acceptance boundary as unverified, not native refusal.
- like: fixed caller invokes Poco Glob on std::string, actual Glob uses UTF8 TextIterator and Unicode case conversion. Bounded unresolved unless exact dependency graph is bound; throwing adapter stub is not a native golden result.
- Exceptions/trace/source/compiler/PCRE/EXE/process/handle/EOF mismatches fail independent reader. No parser/optimizer/folder/managed/public-filter/GUI/CLI or full-compatible-value-model claims.
