# Substr native oracle — failure matrix (before implementation)

- Body arity outside 2..3: None, no evaluation; null argument vector excluded because original dereferences it.
- Evaluation: start then optional length then source. Invalid start still evaluates length, skips source. Throw probes at every argument preserve prior trace.
- Type: Int64 start only. Non-Int64 length is omitted. None source stays None; other scalar types use original getAsString/ToStringValue.
- Byte ranges: empty, zero/end/beyond-end, negative start before/at end, Int64 min/max, zero/positive/negative length, length beyond remaining, negative length truncation.
- UTF-8: complete multibyte sequences, split lead/continuation bytes, emoji and embedded NUL must remain exact bytes. Invalid UTF-8 results represented as lowercase hex; no replacement decoding.
- Arrays: shallow map source only, empty/mixed/nested arrays; array start rejected, array length omitted; no argument broadcasting.
- Evidence failure: source/blob hash drift, adapter/case drift, compilation failure, nonzero exit, incomplete streams/handle wait, two runs disagreement, typed golden mismatch fail verification.
- Scope: no parser, FunctionNode constructor, optimizer, managed evaluation, folder loading/scanning, public .flt, GUI/CLI behavior claimed.
