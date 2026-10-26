# Runtime behavior

`Jq.TryParse(JqCommandInvocation)` recognizes the command name and borrows the
invocation. `ExecuteAsync` binds arguments, compiles the filter and streams results
through `IJqHost`, using the supplied cancellation token and optional shared
`IMemoryBudget`. Each execution has fresh evaluation state. Its argument values are owned once by
`$ARGS`; ordinary named variables borrow the corresponding entries. Named `ENV`
and `ARGS` values stay in `$ARGS.named`; intrinsic `$ENV` and `env` use the host
snapshot. Selected `--rawfile`/`--slurpfile` values load in option order, before
later information switches, argument errors and program-file loading; duplicate
names keep the first binding. Updates copy borrowed branches, keeping reused and
overlapping executions independent.

Source identifiers and syntax whitespace use jq's ASCII grammar; quoted data
retains Unicode. Unicode escapes follow jq surrogate-pair and replacement rules.
Host UTF-16 strings encode lone surrogates as U+FFFD; input bytes decode strictly.

## Host I/O

The embedding application supplies byte I/O, files and modules through `IJqHost`.
Paths use Unix-style separators and resolve against the invocation's current
directory. Its environment snapshot supplies `$ENV` and `env`; `PWD` selects the
default current directory. `JqClock` supplies time and timezone inputs. Hosted
module search uses the importer directory followed by `-L` directories. Explicit
`search` metadata replaces those defaults and skips non-string entries. Search
stops when a file opens; a read failure does not select a later candidate.
Module names validate before canonical resolution; valid dot segments and repeated
separators resolve to the same executable cache key.

Standard descriptors are borrowed. Successful `OpenReadAsync` calls transfer an
owned descriptor to execution; `JqOpenedFile.OpenFailure` transfers none. Execution calls `CloseDescriptorAsync` when exhausted
or abandoned, including on cancellation or failure. Cleanup uses
`CancellationToken.None`. Each close retires its descriptor and returns
`JqCloseResult.Success` or `Failure(JqOperationError)`; execution never retries.
A lone reported close error stops execution with its path and host detail.
Earlier failures, including nonzero write or evaluation statuses, remain primary.

`ReadBytesAsync` copies at most the requested size and releases its buffer borrow
on completion. Reads must make progress or report EOF/failure; EOF may accompany
the last bytes. `AppendWhileOpenAsync` may stay pending to apply backpressure and
must copy bytes retained beyond completion.
Execution awaits writes before reusing buffers and stops production on downstream
closure or failure. Debug renders its envelope around the borrowed value;
debug, stderr and sequence warnings await writes before continuing evaluation. Earlier reported evaluation errors remain nonzero after
normal downstream closure. Cancellation alone does not end a pending buffer borrow.
Pending reads, including `input`, `inputs` and `modulemeta`, suspend evaluation
without blocking the caller. Pure evaluation completes synchronously.

Outer iteration and `input`/`inputs` share one pull cursor. File operands resolve and open
lazily; `-` uses borrowed stdin at its current position. UTF-8 JSON values and raw
lines may span reads. JSON files form one parse stream: tokens can span sources,
and one leading UTF-8 BOM belongs to that stream. Decoding retains completed
nodes, including non-finite literals, and discards parsed prefixes before further
reads. Definitive JSON errors stop further reads; incomplete
tokens wait for more bytes. LF splits raw input; CR is data. Slurp aggregates the cursor;
`--stream` emits path/leaf events. `--seq` uses incremental JSON decoding with RS
framing: complete values can return before another read or a malformed tail;
ignored prefixes are discarded as they arrive. Top-level numbers require a
terminating delimiter. Outer iteration warns and resynchronizes after malformed
sequence input; explicit reads expose those parse errors to jq handlers.
Streaming sequence warnings commit consumed bytes before returning; error events
retain their paths and the following record remains available.

Failed input opens report a system diagnostic and skip to the next source within
the pull. Status 2 stays outside jq handlers. Outer iteration stops after evaluating
that pull's value; explicit `inputs` and slurp may consume further values within
the same evaluation. Memory refusal still takes precedence as status 5.

JSON data imports and `--slurpfile` accept one leading UTF-8 BOM; raw file values
retain it as text.
Base64 alphabet/quantum and URI percent syntax are checked before admitting
decoded buffers. Successful decoding refunds scratch after constructing its result.

## Memory admission

The host implements `IMemoryBudget`, `IMemoryScope` and `IMemoryReservation`.
Nested and overlapping work shares its allowance; scopes draw from their parent.
Major allocations preflight rough capacity estimates, including replacement peaks
while old and new storage overlap. Retained buffers, caches, aliases and aggregates
stay charged through their users. Rough estimates leave minor storage untracked,
intentionally allowing roughly 20% unaccounted memory
with coverage varying by workload. Hosts need headroom for runtime/native memory
and delayed GC.

`TryReserve` and `TryResize` accept nonnegative estimates immediately or return
false without changing accounting; zero is valid. Negative estimates and use of
disposed owners throw. Disposal is idempotent and non-throwing, refunds immediately
without GC, and invalidates descendants. The host serializes owner disposal with
active users and reservation resize/disposal operations.

**Every refused admission fails the command with status 5**, including padding.
jq `try` and `?` cannot catch it. Work stops, pending users and cleanup finish, and
relinquished storage is refunded. Earlier effects remain; status 5 is preserved
even if stderr closes or fails. An immutable emergency diagnostic reports
exhaustion if diagnostic admission also fails.
Later cancellation can stop diagnostic reporting but cannot replace an established
refusal status. Pending borrows and cleanup still finish before storage refunds.

The caller owns the supplied budget. Execution disposes its scope after evaluation,
pending I/O, enumeration and owned-descriptor cleanup finish. Governed invocation
factories admit before copying and return false/null on refusal.
Keep the invocation and borrowed inputs alive through reused/concurrent executions.
Ordinary argument errors remain status 2; admission and structural failures during
argument setup remain status 5.

Compiler tokens and literal copies admit as constructed; borrowed comments and
whitespace add no proportional storage. Temporary lexer workspace ends after
lexing.
Command setup preflights retained bindings, operands, library paths and
environment entries as constructed.
Canonical paths borrow their text; relative strings, normalization workspace and
program source labels admit before construction. Input-path storage remains
owned through the pending open and ends before advancing to another source.

Coarse owners track executions, records, buffers, results and caches. Object
construction workspace ends with enumeration; constant metadata builds private
children once and releases traversal scratch. Completed temporary work reconciles
demand at proven lifetime boundaries; shared values and
escaping errors retain their charge. Other construction within one record can
remain conservatively charged, so a long loop may refuse despite a bounded live
set.

Reads and pass-through results borrow existing values. Writes copy borrowed
branches; private accumulators such as `add` and `fromstream` update their owned
containers directly. Emitted stream values stay independent of later updates.
Right-empty container operations and empty-affix trims borrow unchanged results.
Unchanged ASCII/HTML/URI transforms borrow payloads in fresh result wrappers;
changed strings admit their final size before construction.
`INDEX` borrows existing key strings; generated key text and copied rows retain
their construction admission.
Literal splitting borrows unchanged string payloads in fresh result wrappers
and uses the shared substring search.
`combinations(n)` consumes the count filter before producing one product;
repeated rows borrow the input and retain only a count and odometer state.
Data imports parse directly into their owning array. Executable function imports
reuse linked modules while preserving search precedence. `modulemeta` reads current
library syntax for metadata, dependencies and definition names. Metadata inspection
and data imports read fresh contents. Module exports stay charged with their cache; linked
bindings and temporary merge metadata admit separately. Interpolation fragments
borrow visible compiler scopes and keep their declarations private.

User-function arguments and pipeline sources stream once in demand order.
Each definition environment binds one immutable closure, reused by lookup and exports.
Direct filter-parameter forwarding reuses the captured argument without adding
another environment wrapper. Lexical scope batches admit before construction and
release completed demand. Suspended continuations and captures retain scope
admission until completion or abandonment; proven single-result tails run flat.
Recursive calls under handlers, optional suppression or labels retain their scopes and use the evaluation depth safeguard.
Computed index/slice keys evaluate against the enclosing input before source
results, in the same order for reads, paths and updates. Optional access suppresses
read failures; errors in key/bound expressions propagate. Early output closure
stops deferred work.
Selectors and loop transitions carry selected paths through updates; `last(expr)`
retains its reduction semantics.
Results can restore a selected path by representation identity; applied affix
trims append slice segments.
Iterative tree/path operations retain ancestor cursors rather than whole traversal
inventories. Filtered paths materialize only accepted results. String searches
preserve overlapping scalar indices; `rindex` searches from the end. Recursive consumers still enforce their construction depth.

Comparisons, ordering, serialization and regex subjects reuse admitted scratch
where its lifetime is known. Replacements cover overlapping old/new capacity;
completed serialization replacements retire before result handoff.
Fresh strings, date remainders and runtime/CLI error text admit before copying;
long scans observe cancellation. Type errors render bounded operand previews,
including private string accumulators. Explicit error and halt payloads keep
their full contents and existing ownership.

Already-admitted credit amortizes host calls and stays charged. Cold small charges
and large payload copies stay exact; repeated small headers can round in 256-byte
steps below 4 KiB and one 4 KiB block above it. Credit is acquired on demand.

| Owner | Maximum reusable admission |
| --- | --- |
| Outer record | 4 KiB after a safe boundary; retained values stay charged. |
| Stream cursor | Retained demand plus 256 bytes; 512 after 32 small charges. |
| Serialization and diagnostic encoding | One returned buffer within 4 KiB after 32 results. |
| Regex subject | One returned UTF-8 buffer within 4 KiB. |
| Compiled regex | Two idle patterns; active operations, subjects and results retain their own handles and copied keys. |

Cold or oversized storage is refunded; overlapping borrowers keep separate owners.

## Safeguards and cancellation

Shared admission governs source/token/argument breadth, array padding, strings and
materialized buffers, subject to checked CLR sizes and the guards below.

| Boundary | Limit |
| --- | --- |
| JSON input/output, recursive tree operations, parser, evaluator and module depth | 64 |
| Iterative paths | 10,000 segments |
| Function arity | 4,096, following the reference closure-index representation |
| Host read request | 8 KiB |

Depth guards apply at the operation that parses, copies, compares or renders a
tree. Recursive collection copies include their destination containers in that
depth. Iterative paths and `walk` can process deeper runtime trees within shared
admission; a later recursive consumer still applies its own guard.

Structural/regex exhaustion is terminal status 5, including under `--seq`,
`--stream-errors` and `-e`. Reference path-length errors and invalid regex syntax
remain catchable language errors. Copies check cancellation and depth while
constructing. Cancellation propagates as `OperationCanceledException`; host
contract failures and actual `OutOfMemoryException` remain distinct. Unexpected
clock and regex callback faults escape jq handlers as host failures.

Pass a borrowed `IJqRegexEngine` directly to `ExecuteAsync`. The default is
`JqUtf8RegexEngine.Instance`. The selected adapter receives the active token and
owns cooperation within its engine. The default Utf8Regex 0.3.0 adapter checks
only around synchronous calls and cannot interrupt an active search. Regex syntax,
resource bounds and internal storage admission belong to the provider. Utf8Regex
0.3.0 exposes no shared-budget hook; its internal storage is outside accounting.
See [regex behavior](REGEX.md) for engine details.

## Numbers

Arithmetic, comparison and conversions use IEEE-754 doubles. Literals and input
integers fitting `long` retain exact storage/rendering, but arithmetic projects
them to double: `9007199254740993 | tostring` is exact while
`9007199254740993 == 9007199254740992` is true. Decimal fractions round at parsing;
`1.10` renders `1.1`, `1e3` renders `1000`, and `0.1 + 0.2 == 0.3` is false.
This deliberately differs from decimal-enabled jq builds. `have_decnum` and
`have_literal_numbers` both return false.

Finite doubles use shortest round-trip rendering, integral storage renders exactly,
negative zero renders `-0`, and exponents use uppercase `E`. Numeric parsing and
rendering are culture-invariant. NaN renders `null`; infinities clamp to the finite
double extremes. Scalar NaN keeps number type and never equals itself. Shared
arrays and objects compare equal by identity, including when they contain NaN.
Input and `tonumber` accept signed `nan`, `inf` and `infinity`, ignoring ASCII case;
`tonumber` permits no surrounding whitespace and preserves negative-zero text,
including `"-00"`. Its manual digit scans poll cancellation; synchronous BCL
parsing checks cancellation at its boundaries.
Numeric path identity uses this immediate/double profile; decimal-backed jq
literals can have different identity.
Range values and slice descriptors preserve signed zero and keep integral
doubles outside the signed 64-bit interval in double storage.

Division by zero, including negative zero, is a language error. Remainder truncates
operands toward zero with saturation at signed 64-bit extremes, propagates NaN,
returns zero for a divisor of -1 and errors on a zero divisor: `5.5 % 2` is `1`.

Equality is kind-sensitive. Total order is null, NaN, false, true, other numbers,
strings, arrays, objects. Strings and object keys compare by Unicode scalar,
arrays lexically and objects by sorted keys followed by values. Consistent NaN ordering can differ
from upstream's asymmetric NaN/null sort stability.

## Math accuracy

Math builtins use .NET math or managed approximations. Final bits can vary by
platform. Recorded comparisons of jq 1.8.2 and Lokad.Jq rendered `27 | cbrt` as
`3` on Windows amd64 and `3.0000000000000004` on Linux amd64.
`log1p`, `expm1` and `modf` preserve signed zero. The complementary error
function computes its tail directly; its approximation still limits accuracy.
`J1` and first-order `jn` preserve zero signs; first-order `yn` returns reflected
zero poles. Higher-order zero cases can differ from the reference C library.

Bessel orders truncate toward zero with signed 64-bit recurrence indices and
reflection for negative orders/arguments. Recurrences poll cancellation.
Finite orders outside index representation yield NaN unless analytical J underflow
or Y overflow for `0 < x <= 1` applies. Base approximations and large-argument phase
accuracy limit precision; uniform relative accuracy is not guaranteed.
See NIST's [computation guidance](https://dlmf.nist.gov/10.74) and
[underflow bound](https://dlmf.nist.gov/10.14.E5). Focused tests are in
`JqTests.Values`, `JqTests.Math` and `JqTests.Bessel`.
