# Changelog

## 0.1.0-preview.1 — unreleased

Embeddable jq 1.8.2 runtime with host-mediated IO. Compatibility is
incomplete; see `docs/COMPATIBILITY_MATRIX.md` for the per-row inventory
(91 implemented, 8 intentionally different, 3 partial, 2 unimplemented
parser-support rows) and `docs/UPSTREAM_VECTOR_CAMPAIGN.md` for the
differential record (704 of 734 value vectors pass with every miss
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
- Rendering and diagnostics: lowercase hex escapes, truncated long
  operands in type errors, reference Unknown-option wording with
  failing-flag cluster resolution, and UTF-16 diagnostic columns with
  JSON-reader wording kept as policy.
- Combination orders confirmed against the reference executable:
  C-builtin calls slowest-last, native operators, user arguments, range
  bounds, objects, interpolation, index, and slice slowest-first.
- Math: managed Bessel functions verified within 1e-12 of the reference.
- Performance: corrected benchmark families (13 workloads with UTF-8
  input-to-output coverage and allocation diagnosis) plus a measured
  clone-removal optimization (-8% to -15% allocations on input-heavy
  workloads, no semantic delta).
- Packaging: out-of-tree consumer proof re-verified against the packed
  artifact with locked restore.

Still open before any release claim: SourceLink validation from a public
checkout, hosted CI passage, and the remaining partial rows above. No
release, publication, or package availability is claimed.
