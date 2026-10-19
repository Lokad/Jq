# Changelog

## Unreleased

- Breaking: remove MaximumValueNodes, MaximumAllocationWorkBytes and the deprecated
  MaximumAllocationBytes alias, together with their counters and fuel plumbing.
  Cancellation, shared admission, construction limits and bounded regex searches
  remain; no replacement fuel or compatibility switch is introduced.

- Breaking: remove MaximumInputBytes and MaximumOutputBytes and their counters.
  Streams continue through bounded 8 KiB reads and awaited writes, with 64 MiB
  per-buffer safeguards, shared admission, cancellation and downstream closure.
- Return existing values from identity, variable, selection and child reads without
  accounting scans. Fuse copy admission and depth checks into actual construction;
  retain cursor construction estimates instead of recounting DOM trees.
- Cover 300,000-record JSON, raw, sequence and streaming commands beyond 32 MiB
  of output under a fixed 128 KiB shared allowance, plus cancellation, closure,
  owned cleanup, deep reads, constructor depth and finite former-fuel workloads.
- Remove verified-unused qualified-lookup, stream-leaf, calendar and deletion
  helpers, plus internal path-confinement machinery; keep hosted normalization.
- Extend the detached package consumer to 18 checks, including terminal padded
  admission, subsequent allowance reuse and resource status after stderr closes.
- Preserve resource status 5 when draining queued diagnostics encounters closure
  or a reported write failure. Await owned cleanup and pending borrows; keep
  cancellation distinct and preserve earlier output and caller admission.
- Fail the command on every refused admission, including optional padding, without
  retrying a smaller estimate or evicting storage to continue. Keep coarse growth,
  charged credit, replacement peaks, earlier effects and awaited cleanup.
- Reuse at most 256 bytes of diagnostic credit after 32 small nonempty messages,
  with bounded hot growth.
  Keep cold refunds, empty appends, full queued-burst charges and pending drains.
- Reconcile finished writer replacements before handing off hot serialization
  results whose current capacity fits the existing 4 KiB reuse ceiling. Preserve
  replacement-peak refusal, cold refunds, overlapping results and non-throwing
  return while avoiding repeated pool rejection after mixed-size serialization.
- Reuse one returned serialization buffer after 32 successful serializations,
  retaining at most 4 KiB of admitted capacity per budget. Keep cold refunds,
  overlapping result ownership, failed-buffer cleanup and cancellation checks;
  active byte-buffer leases retain admission after cache closure.
- Reuse already-admitted cursor credit across safe sequence and stream boundaries,
  alongside actual retained trees: at most 256 bytes on cold owners,
  or 512 after 32 small storage charges. Preserve larger refunds and terminal
  cleanup while avoiding repeated scalar/short-text grow-refund calls.
- Expand governed cost diagnostics to 48 cases covering threshold behavior,
  growing subjects, serialization and diagnostics. Verify independent stdout
  and stderr digests, record observed live-scope overlap, and retain separate
  call-count and warmed timing/allocation lanes.
- Amortize gradually growing regex subjects with bounded geometric capacity,
  preserving the existing 4 KiB idle-owner ceiling and old-plus-new peaks.
  Keep large subjects exact and nested leases separate.
- Skip zero-byte diagnostic admissions and unchanged queue refunds while keeping
  empty host appends, cancellation, pending borrows and earlier queued bytes intact.
  Validate disposed context use even when accounting needs no host notification.
- Reuse bounded regex subject buffers and detailed-result allowances, keeping at
  most one idle owner of each kind, capped at 4 KiB each. Preserve nested leases,
  replacement peaks, immediate failure refunds and deferred cache cleanup for
  active borrowers. Batch repeated small charges below 4 KiB with 256-byte credit
  after a cold-owner threshold.
- Add explicit `--governor` diagnostics comparing the execution hook with no hook
  under single/shared-root workers. Keep call counters separate from warmed
  timing/allocation samples, verify independent output digests and full refunds,
  and record every sample as unqualified local cost evidence. Reuse one
  development-only active-owner governor fixture across tests and detached
  consumers; no fixture enters the runtime package.
- Grow reusable raw output geometrically after its exact first allocation, while
  keeping actual retained capacity and replacement peaks charged. Preserve exact
  output, earlier host effects and terminal refusal.
- Reuse one coarse outer-record reservation across safe input boundaries. Keep at
  most 4 KiB of already-admitted credit charged, avoid eager padding for small
  records, and refund it at execution cleanup. Preserve aliases/aggregates and
  the separate sequence/stream retained-value owner and its real refunds.
- Batch small value/slot charges inside their existing coarse owners using at
  most one 4 KiB block of charged credit. Keep cold owners and known copies
  exact. Preserve real reconciliation refunds and terminal refusal.
  Preflight HTML/implode builder capacities together
  and batch literal time-format runs while keeping cancellation checks.
- Reduce governor traffic using owner-local admitted sizes, zero-charge fast
  paths and unchanged-buffer notification elision. Explicit reservation counter
  observations still reflect ancestor invalidation; direct admission calls still
  validate disposed owners. A 1,000-scalar output diagnostic falls from 3,004
  resizes and nine counter reads to four resizes and no reads, preserving its
  estimated peak. Add shared forwarding call counters and boundary regressions;
  this establishes call counts, not a throughput claim.
- Add library-local cooperative memory admission interfaces and execution
  overloads borrowing a shared host allowance. Reusable JSON/raw output capacity
  and information output reserve before allocation, include replacement peaks,
  and stay charged through awaited writes and descriptor cleanup. Cursor buffers,
  streaming token capacity and hosted file reads also reserve before allocation;
  successful read results retain their charge until decoding finishes. Coverage
  uses coarse estimates and conservative record lifetimes, with small metadata
  deliberately uninstrumented. Existing overloads enforce the independent work/I/O/structural policy
  and forward a governed invocation's associated budget.
- Govern retained regex pattern/workspace estimates and operation-owned UTF-8
  subjects and detailed captures. Failed compilation/matching refunds admission;
  nested replacements retain parent charges. This preserves the existing engine
  profile and work/time/cancellation boundaries, with approximate storage sizing.
- Add coarse record and persistent storage owners for UTF-16 materialization,
  decoded JSON, container copies/construction, snapshots and filter/module
  compilation. Preserve aliases, slurp aggregates, sequence queues, streaming
  paths and cached module definitions across safe record refunds. Serialization
  results and queued diagnostic bytes have explicit storage lifetimes.
- Govern invocation snapshots with Try-style factories and owned disposal, while
  borrowing the caller's root and immutable inputs. Command recognition borrows
  that invocation; allocating option/JSON binding now runs within each execution's
  shared scope. Keep ordinary argument errors at status 2 and admission refusal
  at terminal status 5, including JSON arguments and positional JSON.
- Admit path-update/deletion/slice copies and padding, collection backing slots,
  key/sort workspaces, Unicode scratch lists/builders and module metadata copies.
  Preserve output prefixes and exact ordering when shared demand refuses a later
  operation; newly compiled cache keys retain persistent ownership.
- Admit terminal diagnostic and halt UTF-8 buffers before encoding, retain them
  through pending host writes, and avoid large intermediate message copies. A
  refused payload reports terminal status 5 using a small immutable diagnostic,
  preserving earlier output and the caller's existing reservations.
- Complete the allocation-site audit with admission for generated paths/stream
  events, walk/flatten state, constant module metadata copies, scalar array
  wrappers, arithmetic/entry copies and affix substrings. Remove unused legacy
  line-buffer/encoding helpers, a dead newline counter and an unnecessary clone
  in index-error rendering. Keep useful semantic regressions and work guards.
- Preserve actual `OutOfMemoryException` from file-variable loading as an
  allocation failure, with owned descriptor cleanup and admission refunds.
- Validation on Windows: warning-free Release build and all 3,923 tests pass,
  including governed-memory boundary and bounded-call cases. The packed artifact
  passes 18 detached consumer checks with isolated cache, exact package hash and
  locked restore.
  Dependencies and lock files are unchanged. Earlier cross-platform CI,
  differential and performance receipts apply to their recorded revisions.

## 0.1.0 — 2026-10-06

Embeddable jq 1.8.2 runtime with host-mediated IO. Compatibility is
incomplete; see `docs/COMPATIBILITY.md` for the per-row inventory
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
  all 3,727 tests and Release pack. Package verification commands are in README.md.
- Execution policy: immutable stricter string-construction and regex work/time
  bounds, with shared live-storage admission and cancellation.
  Regex limits and setup/file quota failures terminate outside jq handlers.
  JSON strings and object keys preflight decoded length and allocation before
  creating their managed strings.

Version 0.1.0 is available on
[NuGet.org](https://www.nuget.org/packages/Lokad.Jq/0.1.0). Full upstream compatibility
remains unproven; the supported scope and intentional differences are documented above.
