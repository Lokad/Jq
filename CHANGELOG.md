# Changelog

## 0.3.0 — Unreleased

- Use internal UTF-8 strings and object keys throughout evaluation, JSON/raw I/O,
  string primitives, interpolation, formats, date parsing and regex subjects.
  Remove the production JsonNode dependency and subject transcoding buffers.
  Native error/halt payloads retain their ownership through pending writes.
- Encode only final environment values, preserving first key order and last-wins
  duplicates. Preserve bounded buffer reuse and terminal admission refusal.
- Breaking: `JqRegexCapture` contains participation and UTF-8 byte bounds only.
  Providers no longer supply UTF-16 coordinates. Jq lazily projects byte ranges
  through a shared subject index for scalar offsets and string extraction.
- Breaking: `IJqHost.OpenReadAsync` receives `JqPathRequest(Path, CurrentDirectory)`.
  Resolve traversal and confinement in the host; preserve links, dot segments and
  trailing-slash intent. `JqPath` now describes caller current directories.
  Remove `JqPathException`; lookup failures use typed host results. Module cache
  entries and source attribution retain distinct lookup spellings.
- Breaking: host open/read/append failures carry `JqOperationError(Kind, Message)`;
  use `JqOpenedFile.Failure(error)`, `JqByteReadResult.Failure(bytesRead, error)`
  and `JqAppendResult.Failure(error)`. Hosts no longer select append exit codes.
  Streaming input preserves complete values before a terminal read failure;
  expected host failures stay outside jq handlers and diagnostics include their cause.
- Guard recursive binding and definition continuations during parsing, including
  inactive branches; report excessive nesting as a compile error.
- Retain incomplete JSON token scan progress across fragmented reads while
  preserving early syntax errors, cancellation and shared memory admission.
- Add resumable benchmark campaigns with immutable attempts and source/binary
  identity checks; remove obsolete speedup summaries from raw benchmark reports.
- Add .NET 11 library assets while retaining .NET 10 support and runtime tests.
- Move all benchmark tooling to .NET 11 and separate its tests from runtime tests.
  Use BenchmarkDotNet 0.16.0-preview.2 for .NET 11 toolchain support.
- Report one mean `Lokad.Jq / jq` timing ratio, sorted descending. Remove stale
  measurements and before/after allocation claims from the benchmark documentation.
- Add realistic compound benchmarks for API shaping, incident logs, indexed
  enrichment, operational summaries, configuration updates, snapshot differences,
  nested TSV extraction and application-adapted regex cleanup.
- Reduce iterator overhead for single-result field, index and operator reads;
  avoid async machinery when iterator advancement completes synchronously.
- Compare string payloads directly and reuse proven private containers during
  collection construction, preserving Unicode ordering, ownership and depth checks.

## 0.2.0 — 2026-10-27

Available on [NuGet.org](https://www.nuget.org/packages/Lokad.Jq/0.2.0).

- Breaking: remove the bundled `JqUtf8RegexEngine` and all provider dependencies
  from the core package. Compile `examples/Utf8RegexAdapter.cs` in the application
  and inject it through `IJqRegexEngine`. Ordinary filters run without an adapter;
  executed regex operations report `regex engine is not configured`.
- Update the optional application adapter to `Lokad.Utf8Regex.Pcre2` 0.4.0; forward
  cancellation into provider compilation and both search APIs.
- Numeric-only regex capture names are now rejected by the provider's PCRE2 grammar.

- Admit complete unknown-format diagnostics before construction and share format
  dispatch; use bounded native field errors for destructuring and static reads.
- Honor optional scalar fields in paths and add a compiled regex adapter example.

- Reuse interpolation-free formatted strings as constants, including valid module
  and import metadata, while preserving literal text and escape decoding.
- Retire Base64 encoding scratch after conversion while retaining output admission
  and reusing bounded record credit for repeated small encodes.

- Refund `fromjson` UTF-8 conversion storage after parsing while retaining decoded values.
- Reject unsupported formats before serializing their input, preserving lazy templates.
- Observe cancellation after construction admission, including direct buffer growth,
  while preserving terminal refusal and earlier output.

- Preview failed join accumulators without constructing a full intermediate string.
- Admit active serialization replacement overlap and retire completed storage at
  result handoff, preserving framing and pending host borrows.
- Expand composite date formats into one builder and parse directives without
  temporary strings, retaining the managed date profile.

- Preserve streaming sequence progress, error paths and subsequent records across
  recoverable failures; classify malformed literals before resetting tokens.
- Admit CLI diagnostic text before construction, borrow long-option names, reuse
  binding membership and scan indentation widths once with cancellation.

- Resolve module dot segments and repeated separators through canonical hosted
  paths; admit actual candidates, source labels and needed parent-directory copies.
- Reject malformed base64/percent syntax before allocating decoded buffers, while
  preserving terminal refusal for necessary output and exact URI scratch admission.

- Encode host UTF-16 text with replacement consistently across raw output,
  diagnostics and regex subjects, preserving statuses and strict UTF-8 input decoding.
- Construct ASCII/HTML/URI transforms directly, borrowing unchanged payloads and
  admitting changed output once; remove URI chunk strings and blanket estimates.
- Preserve first-order Bessel negative zero, order reflection and zero poles.

- Borrow canonical path text and admit actual resolution workspace and source
  labels, keeping long hosted paths independent of normalization estimates.
- Preserve negative zero in integral `tonumber` text; scan numeric grammar once
  with cancellation polling.
- Append fixed boolean/null spellings in CSV/shell formatting without per-field
  serialization, preserving numeric rendering and output admission.

- Borrow existing `INDEX` key strings and preserve admission for actual generated
  keys, dictionaries and row copies.
- Preserve negative zero and exact signed 64-bit bounds in range values and slice
  descriptors, including path identity.
- Match jq command names ordinally, preserving invisible characters as distinct
  text rather than ignoring them during recognition.

- Collect `combinations(n)` count-filter results before generating one product;
  preserve empty streams and late failures while borrowing repeated rows.
- Bind each function closure once at its definition site and reuse it for lookups
  and exports, with admission at construction.
- Governor tooling now defaults to count/refund verification. Request `--mode cost`
  for warmed timing/allocation batches; `--iterations` applies only to that mode.

- Admit retained command setup as constructed, keeping repeated flags, duplicate
  bindings and information tails independent of total supplied argument count.
- Borrow unchanged literal-split payloads and reuse bounded substring search.
- Preserve zero signs in `log1p`, `expm1` and `modf`; compute `erfc` tails directly.

- Retain two idle compiled regex patterns while preserving active pattern,
  subject and result ownership, allowing long streams of distinct patterns.
- Evaluate `bsearch`, `sub` and `gsub` value arguments before validating input,
  preserving empty streams, argument effects, input consumption and halt status.
- Transpose zero-width scalar/object rows and use length/index error boundaries.

- Compare shared containers by identity while preserving scalar/separate-tree NaN semantics.
- Track paths through selected-value identity, preserving pass-through scalars,
  immediate round trips and affix slice segments.
- Borrow results for empty right container operands and empty string affixes.
- Search complete substrings in ordinal candidate windows, avoiding repeated
  long matching tails while retaining overlaps, reverse lookup and cancellation.

- Share interpolation boundary rules for comments, continuations and nested strings.
- Admit constructed compiler storage instead of charging borrowed source length;
  keep real string/token preflights and reuse temporary string workspace.
- Follow jq count operations and range arity rules without numeric-string coercion;
  preserve generator effects and borrowed initial range values.
- Enforce jq's ASCII source grammar and exact Unicode escapes, preserving quoted
  Unicode data and literal backslashes in interpolation templates.

- Treat JSON input files as one logical stream: continue split values across
  sources and recognize one initial UTF-8 BOM in JSON, stream and sequence modes.
- Render debug envelopes around borrowed values, avoiding discarded tree copies.
- Bound type-error operand previews using the shared writer; retain complete
  explicit error/halt payloads and output. Match native numeric time-type errors.
- Search strings with ordinal primitives while preserving Unicode scalar offsets,
  overlaps and cancellation; search backwards for the last match.
- Skip inactive streaming machinery in raw mode and localize sequence pull state.

- Recover failed input opens within the shared pull cursor and preserve system
  status outside jq handlers, including explicit reads and slurp.
- Load selected file-valued arguments in option order before later options and
  program-file loading; remove deferred binding.
- Decode sequence values incrementally, preserving complete values before
  truncated tails and returning without unnecessary reads. Discard ignored
  prefixes and scan only new bytes for record separators.
- Borrow qualified function names during lookup and create compiler scope
  collections only when needed; avoid function seeding for empty prefix inspection.

- Keep intrinsic `$ENV`/`env` separate from named arguments, and load file
  arguments named `ENV` or `ARGS` into `$ARGS.named` normally.
- Resolve input paths when requested and admit automatic invocation directories
  without charging for copies of borrowed `PWD` text. Skip invalid module search
  candidates while preserving virtual-root policy and required/optional outcomes.
- Inspect fresh module metadata through the shared library parser without loading
  dependencies or retaining inspection-only compiled modules. Preserve executable
  import caching, cancellation, admission and terminal descriptor cleanup.

- Preserve earlier evaluation errors after normal output closure and retain
  refused admission status 5 when later cancellation stops diagnostic reporting.
- Use boolean regex matching for exact-true `_match_impl` mode. Build scan/split
  results directly from captures, preserve eager failure order and advance
  whole-match scalar offsets without repeated prefix scans.
- Accept a leading UTF-8 BOM in JSON data imports through the shared whole-file
  parser; retain raw text and caller-specific construction depth.

- Breaking host API: `CloseDescriptorAsync` returns `Task<JqCloseResult>`.
  Success and failure both retire the descriptor; close is never retried.
  Return `Success` or `Failure(new JqOperationError(detail))`. Lone failures report
  the file path and host detail promptly; earlier failures remain primary.

- Stop outer input after an error diagnostic's stderr closes or fails, preserving
  the command's earlier failure status.
- Stop module search once a file opens, including when reading it fails. Explicit
  search metadata replaces default paths and skips non-string entries.
- Retire parsed JSON byte prefixes before further reads, retaining incomplete
  tokens and owned values with their existing admission and cancellation rules.

- Breaking host API: make `JqOpenedFile` a value type and remove its unused `Error`
  and `Failure(string)` payload. Return `OpenFailure` (or default) on failure and
  `Success(descriptor)` to transfer an owned descriptor.

- Keep unexpected clock and regex callback CLR faults outside jq error handlers;
  preserve provider-declared syntax, resource and cancellation outcomes.

- Report definitive JSON syntax errors without waiting for EOF or buffering an
  irrelevant suffix; preserve fragmented tokens and non-finite literals.

- Suspend evaluation for pending host reads in `input`, `inputs` and `modulemeta`.
  Preserve lazy output, cancellation, scoped bindings and cleanup through suspension.
- Keep host contract faults terminal during descriptor close, preserving earlier
  failures and uncancelled cleanup.
- Breaking host API: remove unused read failure payloads/status, append error
  diagnostics. Use `ReadFailure` and `Failure(exitCode)`;
  successful reads still carry bytes and EOF, and append failures retain status.
- Accumulate object and string `add` results without repeatedly copying prefixes.
- Bound operand-error preview work, including large strings and object suffixes.
- Accept object values in `from_entries` and `join`; preserve entry aliases,
  explicit null values and body effects before entry validation.
- Reuse immutable builtin metadata and preflight fresh inventory result storage.
- Preserve selected paths through stream selectors and loop transitions,
  including update targets, lazy termination and cancellation.
- Compare path heap values by identity, rejecting equal fresh results without
  recursively comparing their contents.
- Await debug, stderr and sequence-warning writes before continuing evaluation.
  Preserve effect order, backpressure, early closure and bounded serialization reuse.
- Materialize only accepted `paths(filter)` results and retain only the last
  `rindex` match, keeping rejected deep traversal and dense search bounded.
- Parse command arguments in one pass: accept mixed option aliases, preserve
  grouped `-f` argument modes and stop at information switches. Reject unsupported
  inline long values (`--indent=4`, for example) and single-dash long aliases.
- Keep argument-setup structural failures terminal with status 5.
- Keep fresh path/INDEX/JOIN results untracked; preserve saved-binding recovery
  and JOIN path checks before later key or join effects.
- Check cancellation throughout containment searches and avoid per-comparison
  closures and boxed enumerators.
- Use ordinary indexing in `JOIN`, including array searches, slice keys and
  invalid-key errors on null.
- Apply the existing depth guard to destination containers during recursive
  collection construction, including when a scalar consumer follows.
- Retain incremental JSON parsing progress around split non-finite literals,
  avoiding repeated DOM construction and admission as input arrives.
- Correct computed index/slice paths and updates: preserve the enclosing input,
  key-major effects/results, and errors in optional index expressions.
- Share named argument identity with `$ARGS`, avoid duplicate argument/constant
  metadata trees, and release completed object-construction workspace.
- Reuse directly forwarded filter arguments, preserving lexical bindings, lazy
  effects and path identity across long tail calls. Admit runtime scope batches
  before construction and release completed demand while retaining captures and
  pending consumers.
- Borrow visible compiler scopes for interpolation while keeping fragment
  declarations private. Reuse cached executable function modules before
  source reads; preserve search precedence and fresh data imports. Refund source
  buffers when descriptor closure fails before publication.
- Preserve pending source/condition outputs and active control scopes around
  recursive calls. Resolve labels lexically across captures and shadowing;
  allow catch/optional scopes to intercept breaks. Deep recursion retaining
  scopes now obeys the existing depth guard; proven tails remain flat.
- Resolve module candidates lazily and seed compiler functions into one admitted
  dictionary. Preflight path copies, scan components without split inventories,
  and observe cancellation before copying large metadata sets.

- Correct mixed and nested slice deletion. Reuse admitted ancestor frames and
  shared path ranges; borrow raw path keys without deep copies or normalization
  walks. Preserve nonfinite bounds, cancellation, deep paths and pending I/O.
- Copy compound-assignment values at writeback and sort precomputed keys through
  borrowed indices. Preserve generator effects, stable ties and independent
  results. Reject null `join` inputs after required separator effects.
- Correct path-mode labels, optional suppression and catch trackedness. Keep
  caught error payloads borrowed and format uncaught payloads at the boundary.
- Reject object inputs to keyed collections after required key effects. Reuse
  bounded extrema key buffers and object-key ordering scratch; simplify builtin
  argument dispatch while preserving generator order, cancellation and cleanup.
- Stream recursive descent and `tostream` from admitted ancestor cursors,
  preserving path order, close events and cancellation. Flatten leaves directly
  and sort borrowed arrays without preliminary source vectors.
- Write JSON directly from borrowed trees, normalizing nonfinite numbers during
  writing and reusing admitted key buffers for sorted properties. Preserve output
  bytes, depth protection and pending I/O lifetimes.
- Stream `while`/`until`/`recurse` branches lazily, preserving condition
  multiplicity and deferred errors/effects. Admit pending continuations and keep
  proven single-result tails bounded, with cancellation and cleanup coverage.
- Preserve borrowed aliases through pass-through builtins and extrema. Append
  private `add` arrays without copying history; simplify `walk` child cursors and
  remove `with_entries`' intermediate deep copy while preserving validation order.
- Admit transpose columns/padding and JOIN pairs before construction. Preserve
  terminal refusal, pending output borrows, cleanup and bounded record streams.
  Group all JOIN lookups per row, preserving rows whose key stream is empty.
- Preserve borrowed `getpath` root/child aliases. Copy path replacements and JOIN
  lookups once, merge only selected object branches, and generate ranges without
  combination copies or singleton-list adapters. Keep ordering, independent
  results, cancellation and construction depth checks.
- Correct overlapping array searches in `indices`, `rindex` and array-key
  `getpath`. Scan borrowed strings without rune tables; share scalar slicing
  across value/path reads, trim at the edges and poll cancellation during scans.
- Preflight CSV expansion without replacement strings, normalize JSON escapes
  in exclusively owned buffers and decode URI/base64 through one admitted byte
  buffer. Preserve encoding grammar, pending borrows, refunds and buffer reuse.
  Extend bounded-stream, cancellation and governor diagnostics coverage.
- Breaking: remove `JqExecutionPolicy` and its runtime ceiling options, including
  `MaximumValueNodes`, `MaximumAllocationWorkBytes`, `MaximumAllocationBytes`,
  `MaximumInputBytes`, `MaximumOutputBytes`, `MaximumStringLength`,
  `MaximumRegexWork` and `MaximumRegexTime`. Supply shared `IMemoryBudget`
  admission and cancellation to execution; pass a borrowed `IJqRegexEngine`
  directly when selecting a provider. Use uniform recursion protection of 64.
- Breaking: `IJqCompiledRegex.Match` returns a provider-owned `JqRegexMatch`;
  remove `CaptureSlotCapacity`. Providers own syntax validation, execution bounds
  and compiled/workspace/capture storage. Jq admits its cache keys, UTF-8 subjects
  and JSON results, retains borrowers through cleanup, and treats provider resource
  refusal as terminal. Provider storage remains outside shared accounting when
  its API does not accept the host memory budget.
- Breaking: remove unused path conveniences (`FileName`, `Parent`, `IsRoot`,
  `Tmp`, `IsParentOf`, `IsEqualToOrParentOf`, `TryChild`, `JqPathSegment`),
  descriptor `DevNullPath`/`IsDevNull`, and error `MessageWithoutTerminator`.
- Preserve literal backslash-u text during Unicode escape normalization. Borrow
  variable values in path mode, fixing nested path identity against jq 1.8.2.
- Borrow comparison keys without adding JSON depth; accept depth-64 keys in
  keyed collections. Order object keys by Unicode scalar consistently with
  value sorting. Admit and reuse ordering scratch, poll cancellation within
  comparisons, and preserve terminal failures through sorters.
- Clone only retained unique results, scan extrema keys incrementally, and
  remove scalar type-probe boxing, host task/delegate adapters and duplicate
  input/path representations. Preserve key effects, independent outputs and I/O
  ownership. Add ordering cases to governor diagnostics.
- Reuse execution-private argument bindings and index names once, preserving
  first-wins semantics and concurrent command isolation. Emit LF directly from
  the JSON writer and format diagnostics from borrowed values and owned UTF-8 bytes.
- Add shared admission for invocation snapshots, compilation, construction,
  retained values and I/O buffers. Every refusal fails with status 5 outside jq
  `try`/`?`, preserving earlier output, pending borrows and owned cleanup.
  Cancellation, host failures and actual allocation failures remain distinct.
- Support long streams with bounded buffers, awaited writes and downstream
  closure. Borrow existing values on identity/variable/child reads; preflight
  construction, padding and replacement peaks against shared admission.
- Reduce governor traffic with coarse owners and bounded charged buffer credit.
  Reconcile completed predicates, scalar reductions and substitution temporaries;
  retain aliases and escaping values. Reuse serialization, subject and diagnostic
  encoding storage through pending host writes.
- Parse metadata/imports once before linking program and module bodies. Remove
  unused runtime state, helper implementations and redundant regex wrappers.
  Consolidate reference subprocess execution so timeout and cancellation cover
  blocked stdin and await child termination and pipe cleanup.
- Extend Bessel evaluation with converged/scaled recurrences, analytical early
  underflow and cooperative cancellation; fix tiny-argument Y evaluation and
  reflected zero signs. Use checked 64-bit order indices. Base approximations
  and large-argument phase accuracy remain documented limitations.
- Normalize UTC civil fields with wide integer intermediates and validate final
  signed 64-bit epochs, preserving partial extreme days and existing diagnostics.
- Consolidate runtime/compatibility documentation and lead benchmarks with both
  recorded result tables. Preserve historical measurements and their provenance;
  governor counts are separate diagnostics, without a new performance claim.

## 0.1.0 — 2026-10-06

Embeddable jq 1.8.2 runtime with host-mediated IO. Compatibility is
incomplete; see `docs/COMPATIBILITY.md` for the per-row inventory
(95 implemented, 8 intentionally different, 1 partial, no unimplemented
rows). The recorded differential campaign matched 703 of 734 value vectors,
with every miss triaged.

Delivered since the scaffold baseline:

- Replace PCRE.NET with managed `Lokad.Utf8Regex.Pcre2` 0.3.0. Use its PCRE2
  syntax and flag primitives directly, retain jq capture/replacement values, and
  document regex semantics and cancellation boundaries.
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
  rendering pinned per `docs/RUNTIME.md`.
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
