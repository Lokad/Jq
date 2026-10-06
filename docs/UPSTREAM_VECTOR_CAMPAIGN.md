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
  reference executable or an inspection checkout. Packages from Windows and
  Ubuntu CI run `37348034783` at commit `9f223bf` were verified in detached
  consumers on their respective platforms after the runtime performance changes.
  Both retain the same 703/734 value result. Regex and encoding were also
  rerun against that artifact and retain 66/66 and 32/32. The historical 704/30
  result and its 701/33 worktree comparison used the previous comparison rule;
  they should not be compared directly with the current stricter decimal rule.
  Corrupted value, output order, extra-output and decimal-precision controls
  prove mismatch sensitivity.
- 5 additional vectors produce byte-identical values with a trailing error
  (binding-alternation all-fail and error-after-output cases); the upstream
  runner ignores trailing errors the same way, so these match.

## Triage of the 31 misses

Every current miss maps to an already-recorded profile or policy class:

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
## Corrections from earlier campaigns

- `map(abs)` over negative zero diverged (`-0` kept instead of
  `+0`); corrected with the upstream vector pinned.
- Long operands rendered in full where the reference truncates with
  `...` (jq.test:1997, 2001, 2005, including both astral backtrack cases);
  corrected by the `jv_dump_string_trunc` port with the vectors pinned.

## Focused filter-file comparison

On 2026-10-05, an independently downloaded official jq 1.8.2 Windows amd64
executable was run with `-n -c -f` over seven self-contained program files.
Its SHA-256 was
`a6fc67fedaf9128a3309a1e2ebb8b986aeccf70122ee46d2cb4849e423f0c627`;
`--version` returned `jq-1.8.2`. No inspection checkout was executed.

Literal NUL at the beginning, end, inside a comment, inside a quoted string,
and after invalid syntax all returned status 2, empty stdout, and
`jq: program file contains NUL bytes` on stderr. The positive controls
`"\u0000"` and `1 # ordinary comment` returned status 0 with the expected
values. Reference CRLF was normalized to LF for comparison. Seven local
regressions now pin the same statuses, output bytes and diagnostics, plus owned
filter-file closure and an unread borrowed stdin. This closes the matrix's
previously unconfirmed filter-file NUL case; broader compatibility remains
unproven. The upstream change is recorded in the
[jq 1.8.2 release notes](https://github.com/jqlang/jq/releases/tag/jq-1.8.2).

## Focused regex comparison

On 2026-10-05, the independently downloaded official jq 1.8.2 Windows amd64
executable identified above ran six additional programs with `-n -c`.
All returned status 0, empty stderr and the values below (CRLF normalized to LF).
The same programs executed through the detached packaged library consumer:
0 passed, 6 differed, 0 escaped or timed out; its ten package smoke checks passed.
These six comparisons are additional evidence, not part of the saved 66-vector
regex sweep. Runtime behavior was not changed by this audit.

| Program or check | Reference values |
| --- | --- |
| `["C","a","é","😀"] \| map(test("\\C"))` | `[true,false,false,false]` |
| `["C-a","\u0001"] \| map(test("\\C-a"))` | `[true,false]` |
| On `"a\nb"`, collect `.string` from `match("^."; "g" + flags)` for flags `""`, `"m"`, `"s"`, `"p"` | `["a"]` for each flag set |
| Same flag sets with `match("."; "g" + flags)` | Respectively `["a","b"]`, `["a","\n","b"]`, `["a","b"]`, `["a","\n","b"]` |
| `"ab" \| [match("a\|ab";"l")]` | One match: offset 0, length 2, string `"ab"`, captures `[]` |
| `"a bbbb" \| [match("a\|b+";"l")]` | One match: offset 2, length 4, string `"bbbb"`, captures `[]` |

The [jq regex manual](https://jqlang.org/manual/v1.8/#regular-expressions)
specifies Oniguruma Perl NG, `m` for dot matching newlines, `s` for whole-string
anchors, and `p` for both. The current adapter instead uses PCRE's flag meanings.
Its `m`/`p` line anchors and `m`/`s` dot behavior differ in these probes.
It also rejects both `\C` programs and both `l` programs. In this jq profile,
`\C` matches a literal C; PCRE2's single-byte escape is a different feature.
The longest-match probe can select a later start, so a leftmost-longest algorithm
alone would not satisfy the reference. The former permanent-exclusion rationale
and claim of correct flag mapping are withdrawn.

The subsequent Utf8Regex migration deliberately adopts its selected PCRE2
profile, with these flag/escape/longest differences retained and documented in
[REGEX.md](REGEX.md). The six probes above are historical reference evidence,
not a claim of Oniguruma parity for the new engine.

## Managed regex migration verification

On 2026-10-06, an isolated Windows package consumer restored the packed
Lokad.Jq artifact with the published `Lokad.Utf8Regex.Pcre2` 0.3.0 and core
0.3.0 packages. All ten smoke checks pass; the unchanged caller-supplied corpora
retain 703/734 value vectors (the same 31 classified misses), 66/66 regex
vectors and 32/32 encoding vectors, with zero escapes/timeouts. Locked restore
and the full 3,733-test Release suite pass. Both regex packages were inspected
for native/RID assets; none were present. No sibling checkout or reference
executable is needed by the consumer.

This result does not establish full PCRE2/Oniguruma parity. The selected engine
profile, intentional flag/escape/longest differences, per-search limits and
cancellation boundaries are documented in [REGEX.md](REGEX.md). Sparse numeric
capture definitions expose an engine limitation in 0.3.0 and are not covered
by the saved reference corpus; no consumer workaround was added.

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
