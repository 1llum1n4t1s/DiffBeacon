# Abs native oracle: implementation-before-capture failure matrix

This matrix is fixed before adapter/cases/golden creation.

| Failure class | Inputs / expected boundary to measure |
| --- | --- |
| Integer overflow / type widening | INT64_MIN, MIN+1, MAX, negative/positive integers and zero; retain native int64 type and measure MSVC minimum overflow result |
| Floating-point sign / exceptional values | positive/negative finite, +/-0, subnormal, DBL_MAX, +/-infinity, positive/negative quiet NaN; capture exact IEEE bits |
| Invalid implicit coercion | none, bool, numeric/non-numeric string, Timestamp, Regex, FileContentRef; reject as monostate |
| Incorrect recursive lifting | empty, mixed, integer/double and nested arrays; only first level maps and nested array becomes none |
| Argument evaluation | tagged literal, tagged throwing argument; exactly one evaluation, exception propagates |
| Arity bypass / extra evaluation | null/empty/2/3 args, throwing extra node; native registration 1..1 rejects before evaluation |
| Capture contamination / partial process | fixed raw Git blob SHA, exact byte extraction, two separately launched native runs, full stdout/stderr EOF, real exit and handle completion |
| Golden tautology | independent Python reader derives mathematical values/typed rejection/trace separately and checks raw native stdout equality and all hashes |

The adapter uses the pinned native FunctionNode argument-count validation block and Abs registration record with typed literal bindings. It does not execute the parser, Optimize, folder scanner, or managed product.
