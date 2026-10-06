# Changelog

## 0.1.0-preview.1 â€” unreleased

Embeddable jq 1.8.2 runtime with host-mediated IO. Compatibility is
incomplete; see `docs/COMPATIBILITY_MATRIX.md` for the per-row inventory
(95 implemented, 8 intentionally different, 1 partial, no unimplemented
rows) and `docs/UPSTREAM_VECTOR_CAMPAIGN.md` for the
differential record (703 of 734 value vectors pass with every miss
triaged).

Delivered since the scaffold baseline:

- Replace PCRE.NET with managed `Lokad.Utf8Regex.Pcre2` 0.3.0. Use its PCRE2
  syntax and flag primitives directly, retain jq capture/replacement values, and
  document per-search regex work/time limits and cancellation boundaries.
- Remove the Lokad.Cli dependency and source generator; bind command options
  locally while preserving the existing option and diagnostic behavior.
- Establish the jq parser, runtime, formatter, regex support, resource
  limits, and command regression tests in an independent library.
- Supply a small `IJqHost` API and standalone invocation/path/descriptor
  types.
- Add .NET solution, NuGet metadata, locked central dependencies,
  benchmark entry point, documentation, and Windows/Linux GitHub Actions
  validation.
- Value and path identity: bindings, variables, and function parameters
  share references with pointer-identity path tracking, so whole-input
  aliases succeed like the reference in path, assign, and update
  positions.
- Numbers: double domain with integral storage locked as the permanent
  policy, with literal precision, overflow clamping, and non-finite
  rendering pinned per `docs/NUMERIC_PROFILE.md`.
- Regex: duplicate capture names accepted with reference-ordered folds;
  the onig/manonig sweep passes 66/66. Six additional reference comparisons
  expose flag mapping, literal-escape and longest-match gaps; these are
  retained as intentional differences under the selected Utf8Regex profile;
  see `docs/REGEX.md`.
- Paths: pointer-identity tracking plus byte-exact slice rendering and
  raw slice objects; exotic keys fail staged in every position on every
  container like the reference get/set/dels steps.
- Exit categories: success, compile (3), input/quota (5), missing files
  (2), halt codes, and `-e` modes all match the reference command over a
  48-scenario sweep (halt codes win, `-e` follows the last value).
- Rendering and diagnostics: lowercase hex escapes, truncated long
  operands in type errors, reference Unknown-option wording with
  failing-flag cluster resolution, and UTF-16 diagnostic columns with
  JSON-reader wording kept as policy.
- Filter files reject literal NUL bytes anywhere before compilation with the
  reference status-2 diagnostic; escaped NUL string literals remain valid.
- Combination orders confirmed against the reference executable:
  C-builtin calls slowest-last, native operators, user arguments, range
  bounds, objects, interpolation, index, and slice slowest-first.
- Math: managed Bessel functions verified within 1e-12 of the reference.
  Cube-root tests account for the final-bit platform difference confirmed in
  official Windows/Linux jq 1.8.2 binaries.
- Parser-support internals: direct `_plus`/`_minus`/`_multiply`/`_divide`/
  `_mod`/`_equal`/`_notequal`/`_less`/`_lesseq`/`_greater`/`_greatereq` calls
  share the operator evaluation, `_assign` folds setpath over enumerated
  paths per value, and `_modify` threads first-only updates like `|=`
  (all oracle-confirmed; upstream likewise rejects `_repeat`, `_while`,
  and `_until` as not-defined).
- Performance: 12 non-regex local benchmarks with UTF-8 input-to-output
  coverage and allocation diagnosis, plus an opt-in 38-case jq comparison
  catalog. Ordinary JSON preserves parsing progress across host reads;
  JSON output reuses bounded storage while awaiting each host append;
  `select` shares surviving inputs and preserves aliases. Limits and
  cancellation remain enforced. Completed objects avoid repeated prefix clones;
  identity/literal/variable leaves use one lazy iterator; main filter tokens are
  reused within an execution; raw output reuses strict UTF-8 storage. The opt-in
  `--allocations` command measures managed GC bytes and collection counts across
  the comparison catalog and six separate diagnostics. Qualified measurements
  and their embedding versus CLI boundary are recorded in `docs/BENCHMARKS.md`.
- Packaging: out-of-tree consumer proof re-verified against the packed
  artifact with locked restore; committed opt-in verification covers ten
  consumer checks and optional caller-supplied value corpora.
  Public repository metadata targets `https://github.com/lokad/Jq`; normalized
  Release symbols and LF C# source checkouts support opt-in PDB identity,
  committed-source checksum and SourceLink mapping verification, with an explicit
  option to download and verify every hosted source. CI-built packages at
  `9f223bf` pass detached consumers, saved corpora, symbol identity and all
  hosted source checks on Windows and Ubuntu 24.04 x64. Both hosted jobs pass
  all 3,727 tests and Release pack; see `docs/PACKAGING.md`.
- Execution policy: immutable stricter allowances for input/output, allocation,
  values, strings, and regex work/time, with fresh counters per execution.
  Regex limits and setup/file quota failures terminate outside jq handlers.
  JSON strings and object keys preflight decoded length and allocation before
  creating their managed strings.

Before a release, finalize its API, version, supported scope and publication
mechanism. Full upstream compatibility remains unproven. No
release, publication, or package availability is claimed.
