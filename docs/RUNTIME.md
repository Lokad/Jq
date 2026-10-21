# Runtime behavior

`Jq.TryParse(JqCommandInvocation)` recognizes the command name and borrows the
invocation. `ExecuteAsync` binds arguments, compiles the filter and streams results
through `IJqHost`, using the supplied cancellation token and optional shared
`IMemoryBudget`. Each execution has fresh evaluation state.

## Host I/O

The embedding application supplies byte I/O, files and modules through `IJqHost`.
Paths use Unix-style separators and resolve against the invocation's current
directory. Its environment snapshot supplies `$ENV` and `env`; `PWD` selects the
default current directory. `JqClock` supplies time and timezone inputs. Hosted
module search uses `-L` and import metadata.

Standard descriptors are borrowed. Successful `OpenReadAsync` calls transfer an
owned descriptor to execution, which calls `CloseDescriptorAsync` when exhausted
or abandoned, including on cancellation or failure. Cleanup uses
`CancellationToken.None`, retries a reported unsuccessful close once and preserves
an earlier failure.

`ReadBytesAsync` copies at most the requested size and releases its buffer borrow
on completion. Reads must make progress or report EOF/failure; EOF may accompany
the last bytes. `AppendWhileOpenAsync` may stay pending to apply backpressure and
must copy bytes retained beyond completion.
Execution awaits writes before reusing buffers and stops production on downstream
closure or failure. Cancellation alone does not end a pending buffer borrow.

Outer iteration and `input`/`inputs` share one pull cursor. File operands open
lazily; `-` uses borrowed stdin at its current position. UTF-8 JSON values and raw
lines may span reads. LF splits raw input; CR is data. Slurp aggregates the cursor;
`--stream` emits path/leaf events, and `--seq` separates records at RS and recovers
after malformed records.

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

The caller owns the supplied budget. Execution disposes its scope after evaluation,
pending I/O, enumeration and owned-descriptor cleanup finish. Governed invocation
factories admit before copying and return false/null on refusal.
Keep the invocation and borrowed inputs alive through reused/concurrent executions.
Ordinary argument errors remain status 2; admission refusal remains status 5.

Coarse owners track executions, records, buffers, results and caches. Completed
temporary work reconciles demand at proven lifetime boundaries; shared values and
escaping errors retain their charge. Other construction within one record can
remain conservatively charged, so a long loop may refuse despite a bounded live
set.

Reads and pass-through results borrow existing values. Writes copy borrowed
branches; private accumulators such as `add` and `fromstream` update their owned
containers directly. Emitted stream values stay independent of later updates.
Data imports parse directly into their owning array. Cached function modules and
module metadata reuse loaded sources while preserving search precedence; data
imports read fresh contents. Module exports stay charged with their cache; linked
bindings and temporary merge metadata admit separately. Interpolation fragments
borrow visible compiler scopes and keep their declarations private.

User-function arguments and pipeline sources stream once in demand order.
Direct filter-parameter forwarding reuses the captured argument without adding
another environment wrapper. Lexical scope batches admit before construction and
release completed demand. Suspended continuations and captures retain scope
admission until completion or abandonment; proven single-result tails run flat. Recursive calls under handlers, optional
suppression or labels retain their scopes and use the evaluation depth safeguard.
Early output closure stops deferred work.
Iterative tree/path operations retain ancestor cursors rather than whole traversal
inventories. Recursive consumers still enforce their construction depth.

Comparisons, ordering, serialization and regex subjects reuse admitted scratch
where its lifetime is known. Replacements cover overlapping old/new capacity.
Fresh strings, date remainders and constructed error messages admit before copying;
long scans observe cancellation. Shared error payloads keep their existing charge.

Already-admitted credit amortizes host calls and stays charged. Cold small charges
and large payload copies stay exact; repeated small headers can round in 256-byte
steps below 4 KiB and one 4 KiB block above it. Credit is acquired on demand.

| Owner | Maximum reusable admission |
| --- | --- |
| Outer record | 4 KiB after a safe boundary; retained values stay charged. |
| Sequence/stream cursor | Retained demand plus 256 bytes; 512 after 32 small charges. |
| Serialization | One returned buffer within 4 KiB after 32 results. |
| Regex subject | One returned UTF-8 buffer within 4 KiB. |
| Diagnostics | 256 bytes after 32 nonempty messages smaller than 256 bytes. |

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
tree. Iterative paths and `walk` can process deeper runtime trees within shared
admission; a later recursive consumer still applies its own guard.

Structural/regex exhaustion is terminal status 5, including under `--seq`,
`--stream-errors` and `-e`. Reference path-length errors and invalid regex syntax
remain catchable language errors. Copies check cancellation and depth while
constructing. Cancellation propagates as `OperationCanceledException`; host
contract failures and actual `OutOfMemoryException` remain distinct.

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
double extremes. NaN keeps number type and never equals anything, including itself.
Input and `tonumber` accept signed `nan`, `inf` and `infinity`, ignoring ASCII case;
`tonumber` permits no surrounding whitespace.

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

Bessel orders truncate toward zero with signed 64-bit recurrence indices and
reflection for negative orders/arguments. Recurrences poll cancellation.
Finite orders outside index representation yield NaN unless analytical J underflow
or Y overflow for `0 < x <= 1` applies. Base approximations and large-argument phase
accuracy limit precision; uniform relative accuracy is not guaranteed.
See NIST's [computation guidance](https://dlmf.nist.gov/10.74) and
[underflow bound](https://dlmf.nist.gov/10.14.E5). Focused tests are in
`JqTests.Values`, `JqTests.Math` and `JqTests.Bessel`.
