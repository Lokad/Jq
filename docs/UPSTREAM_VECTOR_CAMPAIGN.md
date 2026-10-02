# Upstream value-vector campaign

Scope: the value-output vectors in `tests/jq.test` and `tests/man.test` of
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
- Result: 690 pass, 44 miss, 0 escapes, 0 timeouts.
- 5 additional vectors produce byte-identical values with a trailing error
  (binding-alternation all-fail and error-after-output cases); the upstream
  runner ignores trailing errors the same way, so these match.

## Triage of the 44 misses

Every miss maps to an already-recorded class or a fixed bug; none is an
unexplained semantic gap:

- Decimal-build numeric rendering (`docs/NUMERIC_PROFILE.md`): `.0`
  suffixes (for example `1+1` rendering `2.0`), lowercase exponent `e`,
  exponent-form preservation (for example `1e-1` versus `0.1`), and
  `have_decnum` else-branches encoding double rounding and overflow
  clamping. Includes the big-integer and `1E+1000` families.
- Caught-error wording and shape (compatibility matrix, diagnostics and
  operator rows): `Cannot ...` casing and value details, long-operand
  truncation with `...`, JSON-reader wording for malformed input, and the
  `setpath` non-numeric-segment message.
- Uncatchable quota and depth policy (compatibility matrix, resource
  policy): oversized string repeats, the depth-64 nesting family
  (`Containment/Object-merge/Equality/Comparison too deep`,
  `Exceeds depth limit for parsing`), and the cumulative value-budget
  vectors. The budget vectors are additionally pinned as staged exit-5
  cases in `JqTests.Memory`.
- Fixed: `map(abs)` over negative zero diverged (`-0` kept instead of
  `+0`); corrected with the upstream vector pinned.

## Regeneration

The triple parser and the throwaway sweep probe are local scratch only:
they read the ignored inspection checkout, are deleted before committing,
and never run in ordinary builds or tests. Committed evidence takes the
form of focused regression pins derived from sweep findings, as with the
`abs` correction above. Rerun the sweep after semantic changes to the
value, ordering, rendering, or budget paths.
