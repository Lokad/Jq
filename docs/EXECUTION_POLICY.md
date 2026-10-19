# Execution policy

`JqExecutionPolicy` is immutable. Execution overloads without a policy use
`JqExecutionPolicy.Default`; an optional host `IMemoryBudget` governs shared storage.

Use `new JqExecutionPolicy { RegexEngine = engine }` to select a borrowed regex
adapter. The default is `JqUtf8RegexEngine.Instance`. There are no configurable
string-length, regex-work or regex-time allowances. Shared admission governs
substantial string storage alongside containers, buffers and compiled handles;
checked sizes and CLR representability still apply. Calls without a supplied
memory budget acquire no private fallback allowance.

There is no cumulative execution fuel or input/output byte ceiling. Use host
cancellation for long-running commands. Streams use bounded buffers and awaited
host writes; downstream closure stops production before later reads or writes.

## Shared memory admission

The library-local `IMemoryBudget`, `IMemoryScope` and `IMemoryReservation`
interfaces forward to the host's shared allowance. Scopes have no independent
cap and reserve nothing on creation. Nested cooperating work must receive the
same budget, with parent storage still charged.

`TryReserve` and `TryResize` accept nonnegative estimates immediately or return
false without changing existing accounting. Negative estimates throw; admission
through disposed owners throws. Disposal is idempotent and non-throwing, refunds
immediately without GC, and invalidates descendants. The host must serialize
owner disposal with active users and reservation resize/disposal operations.

**Every refused admission fails the command with status 5**, including optional
padding. No smaller retry, wait, streaming fallback, spill, eviction or dropped
optional storage follows refusal. jq `try` and `?` cannot catch it. Stop work,
await users and cleanup, and refund relinquished storage; retained capacity stays
charged. Earlier output and effects remain, without rollback or replay guarantees.
A closed or failed stderr descriptor cannot turn exhaustion into success.
A small immutable emergency diagnostic can report exhaustion if payload admission
also fails.

The caller owns the supplied budget. Execution owns its child scope and disposes
it after evaluation, pending reads/writes and owned-descriptor cleanup finish.
Cancellation alone does not end a buffer borrow. Only opened, owned descriptors
are closed; cleanup uses an uncancelled token and preserves the original failure.

Governed invocation factories admit before copying and return false/null on
refusal before publishing a snapshot. The caller stops on false and keeps the
invocation, borrowed inputs and their owners alive through all reused/concurrent
executions. Argument binding and compilation run inside execution storage;
ordinary argument errors remain status 2, admission refusal remains status 5.
There is no public deferred value stream or automatic ownership reparenting.

Accounting uses coarse execution, record, buffer, result and cache owners.
Substantial allocations preflight estimated capacity, including old-plus-new
replacement peaks. Aliases, slurp aggregates, queued records, cached definitions
and active output/regex leases stay charged through their users. Discarded
single-evaluation temporaries may remain conservatively charged until its boundary.
A long loop inside one record can therefore refuse despite a bounded live set;
this retention gap is separate from the removed cumulative fuel policies.
Successful `any`/`all` predicates reuse one temporary owner after each predicate
finishes; source state remains charged under its parent. A failure retains the
owner because its payload can escape to a handler. No per-object reservation or
alias-graph system is introduced. Scalar-to-scalar `reduce` updates reconcile
only their own completed demand; shared string backing keeps its charge, while
containers and opaque backing retain conservative ownership. Sources, collected
answers and persistent caches remain charged; failed updates never reclaim early.

Estimates cover major storage; small objects, metadata and minor temporaries may
remain untracked. Roughly 20% unaccounted memory is intentional, without a
coverage guarantee for each workload. Hosts need headroom for runtime/native memory and
delayed collection. Admission is separate from managed-allocation measurements
and does not establish a hard process-memory cap.

## Bounded reuse

Already-admitted credit amortizes host calls and stays charged until relinquished.
Cold small charges and known increments of at least 256 bytes stay exact; repeated
small charges can round in 256-byte steps below 4 KiB and one 4 KiB block above it.
Owner-local cached sizes avoid counter reads and unchanged resize notifications.

| Owner | Maximum reusable admission |
| --- | --- |
| Outer record | 4 KiB after a safe record boundary; retained aliases/aggregates remain charged. |
| Sequence/stream cursor | Retained values plus 256 bytes; 512 after 32 small charges. |
| Serialization | One returned buffer within 4 KiB after 32 successful results. |
| Regex | One idle subject and one detailed-result owner, each within 4 KiB. |
| Diagnostics | 256 bytes after 32 nonempty messages smaller than 256 bytes. |

These are retention ceilings, with no eager grant or new execution cap.
Cold/oversized results and discarded demand refund; overlapping borrowers retain
separate owners. Safe reconciliation never drops storage to retry a refusal.

## Structural limits and cancellation

- JSON, recursive-copy, parser and module depth: 64; evaluator depth: 256.
  Iterative path operations retain the separate 10,000-segment path bound;
  reference path-length errors remain catchable language errors.
- Source, token and argument breadth uses shared admission, without fixed count
  caps. Function arity follows the reference closure-index representation (4,096).
  Recursive tail rewriting retains a depth guard; object/interpolation breadth
  and definition continuations use heap iteration.
- Materialized input and JSON buffers use shared admission and CLR representation
  bounds. Host read requests are at most 8 KiB. Decoded string/key storage
  are preflighted before materialization, including escaped JSON.
- Regex pattern: 16,384 UTF-16 units; search depth: 256; workspace: 256 KiB.
  Compilation/storage estimates are conservative, not a hard compiled-size cap;
  the engine's reserved `MaxResultBytes` setting is not an enforced safeguard.

Identity, variable and child reads share values without tree scans. Copies check
cancellation and depth while constructing; cursor queues retain construction
estimates. Structural/regex exhaustion is terminal status 5, including under
`--seq`, `--stream-errors` and `-e`. Invalid regex syntax stays catchable.

Cancellation propagates as `OperationCanceledException`; host contract failures
and actual `OutOfMemoryException` remain distinct and still unwind ownership.
The selected regex adapter receives the active token and is responsible for
cooperation within engine work. The default Utf8Regex 0.3.0 adapter can only check
around synchronous calls; it cannot interrupt a search in progress. There are no
work/time policies. See [regex behavior](REGEX.md).
