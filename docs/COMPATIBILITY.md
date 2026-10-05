# Compatibility baseline

The target is upstream jq 1.8.2. This implementation is incomplete
and has not passed an upstream conformance suite. Its command
tests document the current behavior, including historical
limits and divergences that future increments must resolve explicitly.
See `COMPATIBILITY_MATRIX.md` for the per-row inventory, status, tests,
and evidence; that file is authoritative when this overview differs.

Current scope covers the jq language, documented builtins and arities,
host-supplied modules, byte-level JSON input and rendering, and jq command
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
  reference-ordered folds. The onig/manonig sweep passes 66/66, but six focused
  jq 1.8.2 comparisons expose missing longest-match support, incorrect `m`/`s`/`p`
  flag handling, and rejection of jq's literal `\C` escape. These are compatibility
  gaps, not permanent scalar-model exclusions; jq uses Oniguruma Perl NG rather
  than PCRE2. See the focused regex comparison in `UPSTREAM_VECTOR_CAMPAIGN.md`.
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
  artifact with locked restore. The latest packaged-artifact value sweep
  passes 703/734 after the policy increment, with all 31 misses classified
  (`docs/UPSTREAM_VECTOR_CAMPAIGN.md`).

Value-model contract (binding before further expansion): UTF-8 bytes at
the IO boundary, scalar-based string semantics, the `JsonNode` model with
C# null as JSON null, insertion-ordered objects with last-wins duplicates,
storage-agnostic numeric projections, and cumulative budgets. Delivered identity
fixes and explicitly scoped numeric/regex policies preceded byte-parity work.
Further compatibility changes require focused evidence against the reference.

Remaining work:

Release validation needs SourceLink from a real committed public checkout
and observed Windows/Linux CI passage. The configured future repository is
`https://github.com/lokad/Jq`; it is not published yet. Local SourceLink mappings
and checksum verification are available, while hosted resolution remains pending.
No release, publication, or availability is claimed.
Isolated committed checkouts pass all 3,643 tests, Release pack, detached consumers
and local symbol checks on Windows and Ubuntu 24.04 x64 under WSL. These local
runs do not establish hosted CI passage.

Compatibility evidence remains narrower than the full upstream suite. The
filter-file NUL gap now has seven focused jq 1.8.2 comparisons and local
regressions. Permanent numeric and diagnostic policies and identified regex gaps
remain; closing inventory rows does not resolve those reference differences or
prove all edge cases.

Paths, exit categories, and parser-support internals have focused oracle
coverage and are implemented. Their diagnostic and resource-policy caveats
remain explicit in the matrix. Inventory labels do not establish full
conformance: the recorded value campaign still has 31 classified misses,
and its registry section describes the current helper dispositions.

Also intentionally different by design (not gaps): `--unbuffered` (inert),
`-V`/`--build-configuration` (assembly identity, never upstream identity),
`-C`/`-M` (color output intentionally unsupported: `-C` rejected, `-M` inert),
`-b` (binary-safe no-op),
tool switches (`--run-tests`, `--debug-*`, rejected), host-identity
queries (`get_search_list`, `get_prog_origin`, `get_jq_origin`), plus
automatic `~/.jq` import, home-directory module lookup and expansion,
`$ORIGIN`/executable-origin module lookup, native OS filename support, and
strict lone-surrogate rejection. Each carries pins or policy notes in its
matrix row.

ANSI color output is an intentional scope exclusion for this embedding library,
not an unfinished compatibility feature. It is not planned as a required host
capability.

Host-supplied modules are supported. `import`, `include` and `modulemeta` read
application-provided content through `IJqHost`, using hosted paths and search
directories. Automatic `~/.jq` import, home-directory lookup and expansion, and
`$ORIGIN`/executable-origin lookup are intentional scope exclusions, not future
compatibility work. Remaining module semantics and caveats are listed in the matrix.

## Current resource policy

Budgets are cumulative per execution, not a measurement of total managed memory.
Current constants include 16 MiB input, 32 MiB output (plus a 64 MiB JSON buffer
allowance), 8 Mi UTF-16 code units per string, 256 MiB cumulative allocation
allowance, 262,144 value nodes, JSON/parser depth 64, filter length 1 Mi characters,
and 4,096 tokens/command arguments.
Regex patterns and native work have additional limits in `JqRegexCache`.

`JqExecutionPolicy` exposes stricter positive execution allowances with fresh
counters per call; the default overload preserves these limits. Execution
exhaustion reports status 5 and cannot be caught by jq handlers. Cancellation and
host failures propagate separately. Command binding keeps its independent default
allowance; structural and native ceilings remain fixed. See
[EXECUTION_POLICY.md](EXECUTION_POLICY.md) for configured versus fixed limits.
Finite limits do not establish upstream conformance or excuse missing semantics.

## Conformance work

Track each language feature, builtin name/arity, CLI option, manual section,
and upstream test family in a compatibility matrix. Do not count a skipped test
as supported behavior. Compare exact stream ordering and status, and use byte
comparisons where formatting is the feature under test. Record the reference
version and environment for every differential result.
