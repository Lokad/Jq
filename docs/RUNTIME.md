# Runtime behavior

`ExecuteAsync` uses the host's I/O, cancellation token and optional shared
`IMemoryBudget`. Immutable `JqExecutionPolicy` selects a borrowed regex engine:
`new JqExecutionPolicy { RegexEngine = engine }`. Overloads without a policy use
`JqExecutionPolicy.Default` and `JqUtf8RegexEngine.Instance`.

Execution has no cumulative fuel, total I/O ceiling or configurable string-length,
regex-work or regex-time budget. Calls without a memory hook acquire no private
fallback allowance. Streams use bounded buffers, awaited writes and downstream
closure to stop production.

## Memory admission

The host implements `IMemoryBudget`, `IMemoryScope` and `IMemoryReservation`.
Nested and overlapping work shares its allowance; scopes start empty and have no
independent cap. Major allocations preflight rough capacity estimates, including
replacement peaks while old and new storage overlap. Retained buffers, caches,
aliases and aggregates stay charged through their users. Rough estimates leave
minor storage untracked, intentionally allowing roughly 20% unaccounted memory
without a coverage guarantee for each workload. Hosts need headroom for
runtime/native memory and delayed GC; admission is not a hard process-memory cap.

`TryReserve` and `TryResize` accept nonnegative estimates immediately or return
false without changing accounting; zero is valid. Negative estimates and use of
disposed owners throw. Disposal is idempotent and non-throwing, refunds immediately
without GC, and invalidates descendants. The host serializes owner disposal with
active users and reservation resize/disposal operations.

**Every refused admission fails the command with status 5**, including padding.
jq `try` and `?` cannot catch it. No smaller retry, wait, streaming fallback, spill,
eviction or dropped optional storage follows refusal. Earlier effects remain;
closed or failed stderr cannot turn exhaustion into success. An immutable emergency
diagnostic remains available if diagnostic admission also fails.

The caller owns the supplied budget. Execution disposes its scope after evaluation,
pending I/O, enumeration and owned-descriptor cleanup finish. Cancellation alone
does not end a borrow. Only opened, owned descriptors are closed; cleanup uses an
uncancelled token and preserves the original failure. Governed invocation factories
admit before copying and return false/null on refusal without publishing a snapshot.
Keep the invocation and borrowed inputs alive through reused/concurrent executions.
Ordinary argument errors remain status 2; admission refusal remains status 5.

Coarse owners track executions, records, buffers, results and caches. Completed
temporary work reconciles demand at proven lifetime boundaries; shared values and
escaping errors retain their charge. Other construction within one record can
remain conservatively charged, so a long loop may refuse despite a bounded live
set. There is no per-object reservation, alias graph or automatic reparenting.
Identity, variable and child reads borrow values without accounting scans.

Already-admitted credit amortizes host calls and stays charged. Cold small charges
and large payload copies stay exact; repeated small headers can round in 256-byte
steps below 4 KiB and one 4 KiB block above it. Nothing is admitted eagerly.

| Owner | Maximum reusable admission |
| --- | --- |
| Outer record | 4 KiB after a safe boundary; retained values stay charged. |
| Sequence/stream cursor | Retained demand plus 256 bytes; 512 after 32 small charges. |
| Serialization | One returned buffer within 4 KiB after 32 results. |
| Regex | One idle subject and one detailed-result owner, each within 4 KiB. |
| Diagnostics | 256 bytes after 32 nonempty messages smaller than 256 bytes. |

Cold or oversized storage is refunded; overlapping borrowers keep separate owners.
Reconciliation never releases storage to retry a refusal.

## Safeguards and cancellation

Shared admission governs source/token/argument breadth, array padding, strings and
materialized buffers. Checked CLR sizes and construction depth still apply.

| Boundary | Limit |
| --- | --- |
| JSON, recursive copy, parser and module depth | 64 |
| Evaluator depth | 256 |
| Iterative paths | 10,000 segments |
| Function arity | 4,096, following the reference closure-index representation |
| Host read request | 8 KiB |
| Default regex adapter | 16,384 UTF-16 pattern units; depth 256; workspace 256 KiB |

Structural/regex exhaustion is terminal status 5, including under `--seq`,
`--stream-errors` and `-e`. Reference path-length errors and invalid regex syntax
remain catchable language errors. Copies check cancellation and depth while
constructing. Cancellation propagates as `OperationCanceledException`; host
contract failures and actual `OutOfMemoryException` remain distinct.

The selected regex adapter receives the active token and owns cooperation within
its engine. The default Utf8Regex 0.3.0 adapter checks only around synchronous calls
and cannot interrupt an active search. Its pattern guard bounds constructor
exposure without guaranteeing compile-depth safety. See [regex behavior](REGEX.md)
for engine details, including the unenforced `MaxResultBytes` setting.

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
strings, arrays, objects. Strings compare by Unicode
scalar, arrays lexically and objects by sorted keys followed by values. Consistent
NaN ordering can differ from upstream's asymmetric NaN/null sort stability.

## Math accuracy

Math builtins use .NET math or managed approximations. Final bits can vary by
platform; results are not rounded to force byte identity. Recorded comparisons of
jq 1.8.2 and Lokad.Jq rendered `27 | cbrt` as `3` on Windows amd64 and
`3.0000000000000004` on Linux amd64.

Bessel orders truncate toward zero with signed 64-bit recurrence indices and
reflection for negative orders/arguments. Recurrences poll cancellation without an
order-based work cutoff. Finite orders outside index representation yield NaN unless
analytical J underflow or Y overflow for `0 < x <= 1` applies. Base approximations
and large-argument phase accuracy limit precision; uniform relative accuracy is
not guaranteed. See NIST's [computation guidance](https://dlmf.nist.gov/10.74) and
[underflow bound](https://dlmf.nist.gov/10.14.E5). Focused tests are in
`JqTests.Values`, `JqTests.Math` and `JqTests.Bessel`.
