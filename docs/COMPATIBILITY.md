# Compatibility baseline

The target is upstream jq 1.8.2. This implementation is incomplete
and has not passed an upstream conformance suite. Its command
tests document the current behavior, including historical
limits and divergences that future increments must resolve explicitly.
See `COMPATIBILITY_MATRIX.md` for the per-row inventory, status, tests,
and evidence; that file is authoritative when this overview differs.

Current scope covers the jq language, documented builtins and arities,
the module system, byte-level JSON input and rendering, and jq command
options through the hosted API, with deterministic execution where inputs
are fixed. Pretty-printed JSON with LF bytes is the default output;
duplicate object keys resolve last-wins; `ascii_upcase`/`ascii_downcase`
use ASCII-only case conversion per the reference definition.
Their full upstream semantics and overloads are pinned per matrix row,
not implied by their presence here.

Tracked gaps to investigate and close (see the matrix for row status,
tests, and evidence):

- Cartesian argument-combination order uses last-argument-outer; upstream
  vectors indicate first-argument-outer in places. Tracked for a dedicated
  cartesian-order increment; do not relabel without oracle evidence.
- Numbers use doubles with integral storage for integers;
  literal precision, ordering, and non-finite rendering follow docs/NUMERIC_PROFILE.md, with deliberate decimal-build divergences recorded there.
- Regex support uses PCRE.NET. The reference Oniguruma syntax, flags,
  captures, offsets, and substitutions are covered except `l`
  (longest match) and `\C`, which stay explicitly rejected; the full
  differential matrix remains open.
- Bessel math (`j0`/`j1`/`y0`/`y1`/`jn`/`yn`) stays registered but unavailable,
  like the reference missing-capability path.
- Parser-support helpers (`_assign`/`_modify`) and host-identity queries
  (`get_search_list`/`get_prog_origin`/`get_jq_origin`) stay intentionally
  unexposed; direct calls fail at compile time with no `builtins/0` entry.
- Tool switches (`--run-tests`, `--debug-dump-disasm`, `--debug-trace[...]`)
  stay explicitly rejected, never silently ignored.
- Slice path components render as start/end objects (best effort);
  diagnostics columns count UTF-16 code units with LF line breaks.
- Differential comparison is opt-in against an independently installed
  executable with recorded version, configuration, hash, seeds, and cases;
  ordinary builds and tests never require it.
- Canonical virtual paths reject controls and traversal above root. Hosts own
  file access policy; native OS filename support is not yet a compatibility claim.

## Current resource policy

Budgets are cumulative per execution, not a measurement of total managed memory.
Current constants include 16 MiB input, 32 MiB output (plus a 64 MiB JSON buffer
allowance), 8 Mi UTF-16 code units per string, 256 MiB cumulative allocation
allowance, 262,144 value nodes, JSON/parser depth 64, filter length 1 Mi characters,
and 4,096 tokens/command arguments.
Regex patterns and native work have additional limits in `JqRegexCache`.

These limits remain useful for embedding. A complete implementation should expose
an explicit execution policy and distinguish exhaustion/cancellation from
catchable jq errors; finite policy limits must not excuse missing language features.

## Conformance work

Track each language feature, builtin name/arity, CLI option, manual section,
and upstream test family in a compatibility matrix. Do not count a skipped test
as supported behavior. Compare exact stream ordering and status, and use byte
comparisons where formatting is the feature under test. Record the reference
version and environment for every differential result.
