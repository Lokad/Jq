# Upstream value-vector campaign

Scope: the value-output vectors in `tests/jq.test`, `tests/man.test`,
`tests/onig.test`, and `tests/manonig.test` of
the jq 1.8.2 inspection checkout (see `docs/PROVENANCE.md`). Each vector is
a (program, single JSON input, expected JSON values) triple. This campaign
executes every triple through the public library API and compares parsed
values, mirroring the upstream runner: parsed-value equality, one pull per
expected value, extra values fail, and trailing errors do not fail a vector
whose expected values all matched.

Out of scope: vectors needing host capabilities (modules, `input`/`inputs`,
file/line metadata, environment, clock/timezone, `halt`, `debug`/`stderr`,
`builtins` listings, `$__loc__`) and the `%FAIL` compile-error blocks,
which the diagnostics suite covers separately. String comparison is by
decoded value, so encoder escape-case differences cannot false-positive.

## Latest sweep

- Triples: 734 (`jq.test` plus `man.test`); 28 host-dependent skips, 19 `%FAIL` blocks set aside.
- Regex triples: 66 (`onig.test` plus `manonig.test`); no skips.
- Encoding triples: 32 (`uri.test`, `base64.test`, `optional.test`); no skips.
- Result: 704 pass, 30 miss, 0 escapes, 0 timeouts.
- Regex result: 66 pass, 0 miss, 0 escapes, 0 timeouts, covering zero-width
  global matches, combining codepoints, named and non-participating
  captures, sub/gsub replacements, and the `g`/`gi`/`ig`/`gn`/`ix` flags.
- Encoding result: 32 pass, 0 miss, 0 escapes, 0 timeouts, covering URI
  unreserved sets, NUL and multibyte roundtrips, base64 padding variants and
  rejection messages, the 2038 `fromdate` boundary, and `%e` formatting.
- The value sweep was re-run after the truncation, identity-sharing,
  duplicate-name, hex-case, Bessel, and accessor-sharing increments: 704
  pass, 30 miss, 0 escapes. A worktree A/B at the pre-increment base
  reproduced 701 pass and the same 30 misses plus exactly the three
  truncation vectors, proving those increments changed values only where
  intended. Regex/encoding tallies stand (no shared paths changed:
  duplicate names accept only duplicate-name patterns, absent from those
  vectors). A positive control with corrupted expectations proves mismatch
  sensitivity.
- 5 additional vectors produce byte-identical values with a trailing error
  (binding-alternation all-fail and error-after-output cases); the upstream
  runner ignores trailing errors the same way, so these match.

## Triage of the 30 misses

Every miss maps to an already-recorded class or a fixed bug; none is an
unexplained semantic gap:

- Decimal-build numeric rendering (`docs/NUMERIC_PROFILE.md`): `.0`
  suffixes (for example `1+1` rendering `2.0`), lowercase exponent `e`,
  exponent-form preservation (for example `1e-1` versus `0.1`), and
  `have_decnum` else-branches encoding double rounding and overflow
  clamping. Includes the big-integer and `1E+1000` families.
- Caught-error wording and shape (compatibility matrix, diagnostics and
  operator rows): `Cannot ...` casing and value details, JSON-reader
  wording for malformed input, and the `setpath` non-numeric-segment
  message.
- Uncatchable quota and depth policy (compatibility matrix, resource
  policy): oversized string repeats, the depth-64 nesting family
  (`Containment/Object-merge/Equality/Comparison too deep`,
  `Exceeds depth limit for parsing`), and the cumulative value-budget
  vectors. The budget vectors are additionally pinned as staged exit-5
  cases in `JqTests.Memory`.
- Fixed: `map(abs)` over negative zero diverged (`-0` kept instead of
  `+0`); corrected with the upstream vector pinned.
- Fixed: long operands rendered in full where the reference truncates with
  `...` (jq.test:1997, 2001, 2005, including both astral backtrack cases);
  corrected by the `jv_dump_string_trunc` port with the vectors pinned.

## Registry reconciliation

The upstream registry (C `function_list` plus `libm.h` capability names,
bytecoded `empty`/`not`/`path`/`last`/`range`/`builtins`, and every `def`
in `builtin.jq`) was compared name-by-name against `JqBuiltinRegistry`
(upstream C arities count the input; local arities count filter arguments
only). `_assign` and `_modify` now execute locally like their upstream
definitions, and the direct binary-operator helpers share the operator
evaluation. Upstream also rejects `_repeat`, `_until`, and `_while` as
undefined; the local rejection agrees. The host-identity queries
`get_search_list`, `get_prog_origin`, and `get_jq_origin` remain intentionally
unexposed (compatibility matrix, inventory rows). All underscore helpers
are omitted from `builtins/0`, including those callable directly, like
upstream. Behavior, rejection, and omission tests cover these dispositions.
Local extras are legitimate: `_negate` is the parser-internal
unary-minus step (likewise unadvertised) and `lgamma_r` exists on both
sides. Arity parity was checked executably: all 202 registry names accept and
reject at boundary arities exactly per the upstream ranges (544 checks, exit-3
compile diagnostics, no escapes). Re-run this comparison when the target profile moves.

## Regeneration

The triple parser and the throwaway sweep probe are local scratch only:
they read the ignored inspection checkout, are deleted before committing,
and never run in ordinary builds or tests. Committed evidence takes the
form of focused regression pins derived from sweep findings, as with the
`abs` correction above. Rerun the sweep after semantic changes to the
value, ordering, rendering, or budget paths.
