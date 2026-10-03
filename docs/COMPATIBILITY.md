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

Closed structural gaps (delivered since the scaffold baseline; see the matrix rows for pins and evidence):

- Value and path identity: `as`-bindings, variables, and function value
  parameters share references with pointer-identity path tracking
  (upstream `LOADV`/`STOREV` plus `jv_identical`), so whole-input aliases
  succeed in path, assign, and update positions.
- Combination orders confirmed by an opt-in oracle run (jq 1.8.2, official
  win64 binary, SHA256 a6fc67fedaf9128a3309a1e2ebb8b986aeccf70122ee46d2cb4849e423f0c627):
  C-builtin calls enumerate with the last value argument slowest, while
  native operators, `range` bounds, user value arguments, objects,
  interpolation, index, and slice enumerate first slowest (pinned by
  range-order, user-argument-order, and operator vectors).
- Numbers use doubles with integral storage under the permanent
  double-domain policy in `docs/NUMERIC_PROFILE.md`; literal precision,
  ordering, and non-finite rendering each carry byte-exact pins, and
  decimal-literal fidelity is out of scope by design.
- Regex uses PCRE.NET with duplicate capture names accepted and
  reference-ordered folds; `l` (longest match) and `\C` stay permanently
  excluded (no standard-API equivalent; the scalar-offset model forbids
  single-unit matching). The onig/manonig sweep passes 66/66.
- Rendering and diagnostics: lowercase `\uXXXX` escapes like the reference,
  `jv_dump_string_trunc` ports for long operands, reference Unknown-option
  wording with failing-flag cluster resolution, UTF-16 diagnostic columns
  with JSON-reader wording as permanent policy.
- Bessel math (`j0`/`j1`/`y0`/`y1`/`jn`/`yn`) implemented in managed code
  and verified against jq 1.8.2 within 1e-12; extreme orders and
  non-finite inputs take documented boundary values.
- Performance: 13 benchmark families with UTF-8 input-to-output coverage,
  per-iteration exit/output validation, and allocation diagnosis, plus one
  measured clone-removal optimization (-8% to -15% allocations on
  input-heavy workloads, zero semantic delta).
- Packaging: out-of-tree consumer proof re-verified against the packed
  artifact with locked restore. Value sweep 704/734 re-run after every
  semantic increment with all 30 misses triaged
  (`docs/UPSTREAM_VECTOR_CAMPAIGN.md`).

Value-model contract (binding before further expansion): UTF-8 bytes at
the IO boundary, scalar-based string semantics, the `JsonNode` model with
C# null as JSON null, insertion-ordered objects with last-wins duplicates,
storage-agnostic numeric projections, and cumulative budgets. Structural
gaps (identity, numerics, regex) were fixed before byte-parity items, and
no new builtins, flags, or options land until the open rows below close.

Remaining finite gaps, ranked, each with its completion criterion (a
documented divergence alone never closes its row):

1. Paths row (partial): slice path components render as start/end objects
   and exotic segments stay best-effort. Done when oracle slice-path
   vectors are pinned byte-exact or a permanent policy note with oracle
   evidence replaces them.
2. Exit categories (partial): success, compile (3), input/quota (5),
   missing operands (2), halt codes, and `-e` modes are covered, with the
   unterminated-tail +1 line offset pinned as permanent policy (the
   reference `fgets` loop only counts consumed newlines). Done when the
   row flips to implemented with that policy note, or the artifact is
   matched with cursor-accounting evidence.
3. Descriptor ownership and budgets (partial): borrowing, lazy opens,
   owned-only closes, backpressure, reuse snapshots, and cumulative
   budgets are covered. Done when an explicit execution-policy API lands
   that distinguishes exhaustion/cancellation from catchable errors, or
   the cumulative policy is locked as the permanent contract.
4. Parser-support internals (2 unimplemented rows): `_assign`, `_modify`,
   `_repeat`, `_until`, `_while` and the `BINOPS` operator internals stay
   unexposed locally with rejection/omission pins, but the matrix still
   marks them unimplemented. Done when an opt-in oracle run confirms what
   upstream direct calls do, then the rows flip (to intentionally
   different if upstream also hides them, else to implemented).
5. Release externals: SourceLink validation from a real committed public
   checkout (no public remote exists yet) and hosted Windows/Linux CI
   passage. Done when both are observed, not before; no release,
   publication, or availability is claimed until then.

Also intentionally different by design (not gaps): `--unbuffered` (inert),
`-V`/`--build-configuration` (assembly identity, never upstream identity),
`-C`/`-M` (terminal-profile capability), `-b` (binary-safe no-op),
tool switches (`--run-tests`, `--debug-*`, rejected), host-identity
queries (`get_search_list`, `get_prog_origin`, `get_jq_origin`), plus
`~`/home and `$ORIGIN` module lookups, native OS filename support, and
strict lone-surrogate rejection. Each carries pins or policy notes in its
matrix row.

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
