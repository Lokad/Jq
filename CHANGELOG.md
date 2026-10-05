# Changelog

## 0.1.0-preview.1 â€” unreleased

Embeddable jq 1.8.2 runtime with host-mediated IO. Compatibility is
incomplete; see `docs/COMPATIBILITY_MATRIX.md` for the per-row inventory
(96 implemented, 8 intentionally different, no partial or unimplemented
rows) and `docs/UPSTREAM_VECTOR_CAMPAIGN.md` for the
differential record (703 of 734 value vectors pass with every miss
triaged).

Delivered since the scaffold baseline:

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
  longest-match and single-unit matching permanently excluded with engine
  evidence; the onig/manonig sweep passes 66/66.
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
- Performance: corrected benchmark families (13 workloads with UTF-8
  input-to-output coverage and allocation diagnosis) plus a measured
  clone-removal optimization (-8% to -15% allocations on input-heavy
  workloads, no semantic delta).
- Packaging: out-of-tree consumer proof re-verified against the packed
  artifact with locked restore; committed opt-in verification covers ten
  consumer checks and optional caller-supplied value corpora.
  Public repository metadata targets `https://github.com/lokad/Jq`; normalized
  Release symbols and LF C# source checkouts support opt-in PDB identity,
  committed-source checksum and SourceLink mapping verification.
  Isolated committed checkouts pass 3,643 tests, Release pack, detached consumers
  and local symbol checks on Windows and Ubuntu 24.04 x64 under WSL.
- Execution policy: immutable stricter allowances for input/output, allocation,
  values, strings, and regex work/time, with fresh counters per execution.
  Regex limits and setup/file quota failures terminate outside jq handlers.
  JSON strings and object keys preflight decoded length and allocation before
  creating their managed strings.

Still open before any release claim: SourceLink validation from a public
checkout and hosted CI passage. Full upstream compatibility remains unproven. No
release, publication, or package availability is claimed.
