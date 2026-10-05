# Upstream value-vector campaign

Scope: the value-output vectors in `tests/jq.test`, `tests/man.test`,
`tests/onig.test`, and `tests/manonig.test` of
the jq 1.8.2 inspection checkout (see `docs/PROVENANCE.md`). Each vector is
a (program, single JSON input, expected JSON values) triple. This campaign
executes every triple through the public packaged library API and compares
parsed values with JsonNode.DeepEquals, preserving decimal precision. It follows
the upstream stream rules: exact expected value count, extra values fail, and
trailing errors do not fail a vector whose expected values all matched.

Out of scope: vectors needing host capabilities (modules, `input`/`inputs`,
file/line metadata, environment, clock/timezone, `halt`, `debug`/`stderr`,
`builtins` listings, `$__loc__`) and the `%FAIL` compile-error blocks,
which the diagnostics suite covers separately. String comparison is by
decoded value, so encoder escape-case differences cannot false-positive.

## Latest sweep

- Triples: 734 (`jq.test` plus `man.test`); 28 host-dependent skips, 19 `%FAIL` blocks set aside.
- Regex triples: 66 (`onig.test` plus `manonig.test`); no skips.
- Encoding triples: 32 (`uri.test`, `base64.test`, `optional.test`); no skips.
- Result (2026-10-05): 703 pass, 31 miss, 0 escapes/timeouts.
  The packaged-artifact runner preserves decimal precision; the earlier 704/30
  sweep accepted one rounded decimal identity. The additional miss is the
  existing decimal-profile difference at man.test:5, not a runtime change.
- Regex result: 66 pass, 0 miss, 0 escapes, 0 timeouts, covering zero-width
  global matches, combining codepoints, named and non-participating
  captures, sub/gsub replacements, and the `g`/`gi`/`ig`/`gn`/`ix` flags.
- Encoding result: 32 pass, 0 miss, 0 escapes, 0 timeouts, covering URI
  unreserved sets, NUL and multibyte roundtrips, base64 padding variants and
  rejection messages, the 2038 `fromdate` boundary, and `%e` formatting.
- The current rerun uses the saved corpus and packed artifact, without a live
  reference executable or an inspection checkout. Regex and encoding were also
  rerun against that artifact and retain 66/66 and 32/32. The historical 704/30
  result and its 701/33 worktree comparison used the previous comparison rule;
  they should not be compared directly with the current stricter decimal rule.
  Corrupted value, output order, extra-output and decimal-precision controls
  prove mismatch sensitivity.
- 5 additional vectors produce byte-identical values with a trailing error
  (binding-alternation all-fail and error-after-output cases); the upstream
  runner ignores trailing errors the same way, so these match.

## Triage of the 31 misses

Every miss maps to an already-recorded class or a fixed bug; none is an
unexplained semantic gap:

- Decimal-build numeric fidelity (`docs/NUMERIC_PROFILE.md`): the decimal
  identity rounding at man.test:5, `.0`
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

The committed `tools/VerifyPackage.ps1` command copies a standalone package
consumer outside the repository and restores the exact packed artifact into a
fresh isolated cache. Its optional vector mode reads caller-supplied JSONL triples
with `prog` (filter text), `input` (JSON text), and `expected` (array of JSON texts).
The expected miss count gates the recorded total; every reported miss still needs
triage. A matching total is not a conformance certificate. See `PACKAGING.md`.

The extracted corpus remains ignored local evidence, not a build dependency or a
bulk fixture committed to this repository. No vector mode runs in ordinary tests
or CI, and the command never reads an upstream checkout. Supply a separately
prepared corpus to reproduce a campaign; regeneration must respect the upstream
harness exclusions. Rerun after semantic changes to value, ordering, rendering,
or budget paths and record the comparison rule as well as the reference profile.
