# Glob malformed byte boundary — failure matrix before cases

- Exact fixed Glob constructor/match/matchAfterAsterisk/matchSet, TextIterator, UTF8Encoding, Unicode+PCRE Unicode table; pin Git commit/blob/source bytes. Minimal class bindings must not replace native function bodies.
- All33 malformed inputs, both sensitivities, wildcard at beginning/end, literal/high-byte/malformed patterns, empty subject/pattern, valid ASCII/Unicode/NUL and syntax errors.
- TextIterator can report -1 for malformed UTF8. Unicode::properties accepts int and its negative boundary must not be assumed safe. Capture each case in a distinct native process so a native fault cannot erase later case evidence; preserve exact exit/stdout/stderr/EOF/handle information rather than converting crash to false.
- Exceptions at syntax-invalid patterns are distinct from false. A wildcard may skip evaluation/decoding paths; do not assume all malformed inputs are rejected.
- Independent Python iterator/glob model covers exact bounded cases and reports any undefined or unmodeled native boundary explicitly. Neither replacement Unicode decoding nor generic regex may define expected native results.
- No parser/optimizer/product/GUI/CLI or broad Glob-equivalence claim; TCHAR/locale behavior only if exercised.
