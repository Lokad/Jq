# Execution safeguards

`Jq.ExecuteAsync(host, policy, cancellationToken)` accepts an immutable
`JqExecutionPolicy`. The other overload uses `JqExecutionPolicy.Default`.
The policy controls string construction and each synchronous regex search.
Shared live-storage admission uses the host's `IMemoryBudget`.

```csharp
var policy = new JqExecutionPolicy
{
    MaximumStringLength = 1024 * 1024,
    MaximumRegexWork = 1_000_000,
    MaximumRegexTime = TimeSpan.FromSeconds(1),
};
var command = Jq.TryParse(invocation)
    ?? throw new InvalidOperationException("Expected a jq command.");
int status = await command.ExecuteAsync(host, policy, memoryBudget, cancellationToken);
```

Limits must be positive and no greater than the tested defaults. Invalid
configuration throws `ArgumentOutOfRangeException` during initialization.
A record copy leaves the original policy unchanged.

| Safeguard | Default and current ceiling | Scope |
| --- | --- | --- |
| `MaximumStringLength` | 8 Mi UTF-16 code units | A constructed string, key or rendered text value. |
| `MaximumRegexWork` | 10,000,000 | Engine steps for each search. |
| `MaximumRegexTime` | Five seconds | Time for each engine search, excluding compilation, replacement evaluation and host waits. |

There are no cumulative node, allocation-work or input/output byte counters.
The runtime does not replace them with execution fuel. Hosts stop commands with
cancellation and govern shared live storage with admission. Streams can process
arbitrary total bytes through bounded 8 KiB reads and awaited writes. Individual
input buffers stay bounded at 64 MiB; serialized JSON values stay within 32 MiB
with 64 MiB backing buffers. Every substantial allocation preflights its storage.
Downstream closure stops production before later reads or output.

JSON strings and object keys check decoded UTF-16 length and admit storage before
materialization, including escaped input. Parser/value/evaluation stack limits
and regex workspace bounds remain independent of cumulative work.

`Jq.TryParse` recognizes a command name and borrows an immutable invocation.
Option and JSON argument binding happens during execution and shares the active
memory scope. Ordinary argument errors report status 2; admission refusal remains
terminal status 5, including `--argjson` and `--jsonargs`. Try-style invocation
factories admit snapshots before copying, return false/null on refusal, and own
their snapshot scopes. Callers keep invocations and borrowed inputs alive until
all concurrent or reused executions and cleanup finish.

## Cooperative memory admission

`ExecuteAsync(host, memoryBudget, cancellationToken)` and
`ExecuteAsync(host, policy, memoryBudget, cancellationToken)` accept a borrowed
`IMemoryBudget` implemented by the embedding host. The library defines the
interfaces in its own namespace and introduces no governor package or host
dependency. The host forwards all cooperating libraries and nested commands to
the same allowance; scopes introduce neither a new limit nor a copied balance.

An execution creates and owns a child `IMemoryScope`. It disposes that scope
after evaluation, awaited writes and owned-descriptor cleanup finish. Cancellation
does not refund storage while a host operation still borrows it. The supplied
budget stays owned by its caller. Reservations and scopes have non-throwing,
idempotent disposal; refunds are available immediately, independently of GC.
The host must serialize owner disposal with active users.

Runtime owners use their last successful admission for local accounting rather
than repeatedly querying the host's reservation counter. Explicit counter
observations still reflect ancestor disposal. Buffer reuse sends no unchanged
capacity notifications; actual growth, replacement peaks and refunds still reach
the supplied host allowance.

Coarse value/compiler owners separate logical demand from admitted credit. Charges
of at least 256 bytes stay exact. Smaller increments stay exact on cold owners;
after 32 small charges they may use 256-byte credit steps below 4 KiB. Above
4 KiB they may reserve up to one 4 KiB block ahead; that credit
remains charged to the same host allowance and can satisfy later local charges.
The owner selects one estimate before admission. Refusal of that estimate,
including padding, terminates the command without an exact-size retry.
Reconciliation refunds discarded demand and bounds retained credit. No root
balance is copied.
HTML and implode builders preflight their exact capacity in bulk; literal runs
in time formatting are charged together. Cancellation remains checked during long text scans.

`TryReserve` and `TryResize` admit nonnegative estimates immediately or refuse
without changing existing charges. Negative estimates throw; admission through
disposed owners throws. Refusal reports terminal status 5 and cannot be caught
by jq handlers. Work stops, users and cleanup are awaited, and relinquished
storage is refunded so later commands can reuse the allowance. Retained storage
stays charged. Refusal does not trigger streaming, spilling, eviction, dropping
optional storage, waiting or retries. Earlier output and side effects remain;
there is no rollback or safe replay guarantee.
If pending stderr closes or reports a write failure while reporting exhaustion,
the resource status remains 5 and no further diagnostic append is attempted.
Cancellation and thrown host failures still propagate separately.

Current coverage includes reusable JSON/raw output backing capacity, information
output, cursor read/accumulation buffers, streaming token capacity and hosted
filter/module/variable-file read buffers. Growth reserves old and new storage
together until replacement or copying finishes. Retained capacity stays charged
between records. Cursor buffers survive pending reads and owned-descriptor
cleanup; a hosted read result owns its accumulated capacity until decoding ends.
Raw output starts with exact capacity and doubles later growth up to its output
ceiling, keeping that backing capacity charged. Refused growth fails before
replacement or output, even if an exact-size allocation could have fit.
Temporary read storage is released after the awaited read loop completes. Regex
compiled-pattern and pooled-workspace estimates remain charged under one cache
owner until context disposal and completion of its borrowers. Subject encoding
and detailed capture contexts have operation owners. The cache can retain one
idle UTF-8 subject buffer and one idle capture allowance, each at most 4 KiB and
still charged. Reuse avoids admission calls for unchanged capacity. Subjects start
at exact capacity, then double growth within the existing 4 KiB idle-owner ceiling
(including metadata). Larger subjects stay exact. Refused growth terminates
without retrying or evicting cached storage to continue. Subject growth
includes old-plus-new storage. Larger owners and failed operations refund
on return. Nested operations use separate owners, and closing the cache waits for
active borrowers before refunding their compiled-pattern admission.

Execution snapshots and filter/module compilation use a persistent storage owner.
UTF-16 materialization, decoded JSON nodes/backing slots, container clones and
array/object construction use coarse value owners. The outer-record owner survives
its generators, variable aliases, nested input, awaited writes and terminal
cleanup. At a safe next-record boundary its logical demand resets, refunding
all but at most 4 KiB of already-admitted reusable credit. Small records retain
only their existing admission; there is no eager block allocation. That credit
stays charged until reused or execution disposal, avoiding a fresh host reservation
for each input. Slurp keeps the whole aggregate charged. Sequence queues
and streaming paths/pending events have a separate cursor owner; record refunds
preserve the data it still retains, plus at most 256 bytes of already-admitted
reusable credit on cold owners. After 32 small storage charges, that bound may
rise to 512 bytes. Warming alone admits no additional storage. Larger discarded
estimates still refund at each safe boundary, and terminal cleanup refunds all
remaining admission. Module compilation during `modulemeta` uses
the persistent owner so cached definitions survive record refunds.

Serialization results own their byte capacity until consumption finishes.
After 32 successful serializations, a budget may retain one returned buffer with
at most 4 KiB of admitted capacity.
Before handing off a hot result whose current buffer fits that ceiling, finished
writer borrows end and replaced capacity is refunded. Actual growth still admits
the old-plus-new peak first. This lets a fitting grown buffer remain reusable;
the retained admission stays charged even where it was previously discarded.
Cold results refund immediately. Borrowed results remain distinct; failed or
oversized buffers release, and closing the budget releases idle capacity while
active result owners retain their own admission until returned. Output rendering
and queued copies use separate storage. Reuse still checks cancellation on every serialization.
Queued diagnostic copies have a separate owner that survives record refunds and
all awaited appends. Cold drains refund completely. After 32 nonempty messages
smaller than 256 bytes, drains may retain at most 256 bytes of existing credit.
Hot small-message growth can round admission to 256-byte steps, with at most
255 bytes above actual queued demand. Refused padding terminates before the
new message is encoded or queued, without a smaller admission. Larger copies
still preflight their full demand, and context disposal releases all credit.
Suppressing stdout can queue many messages before a drain; every queued payload
stays charged until all its host borrows end. Empty diagnostic chunks preserve
their host append and cancellation behavior without making zero-byte admissions
or unchanged resize notifications. Small standalone scalar wrappers
are untracked; managed scalar clones borrow immutable payloads already charged
to their source owner. Container copies reserve backing slots and stored wrappers.

Terminal diagnostic and halt payloads reserve their UTF-8 output before encoding,
without concatenating a large intermediate message. That storage stays charged
until the host append completes. If admission refuses, a small immutable library
diagnostic reports `memory budget exceeded` and status 5, including a refused halt
payload. This emergency diagnostic is intentionally outside workload accounting
so exhaustion can remain observable.

Path update/deletion/slice copies and padding reserve before copying or mutating
containers. Collection results, precomputed keys and stable-sort workspaces have
coarse record estimates, including stored scalar wrappers. Unicode matching,
path slices and trimming reserve large scratch lists/builders before allocation.
Module metadata copies use record storage; newly compiled cache keys and
definitions keep persistent admission across later records.
Constant module metadata copies, generated JSON paths/stream events, walk and
flatten scratch state, entry/arithmetic copies, string-to-array wrappers and
affix substrings also reserve their substantial backing storage before creation.

Within a single outer evaluation, discarded value and builder temporaries may
remain conservatively charged until that evaluation finishes. TODO: use narrower
existing generator/aggregate boundaries where profiling shows a material gap,
without introducing per-object reservations or alias-graph tracking.

Admission estimates major storage capacity rather than measuring the whole heap.
Small objects, metadata and minor temporaries can remain uninstrumented; roughly
20% unaccounted storage is an intentional design allowance, without a coverage
percentage guarantee for each workload. The host needs separate headroom for
runtime/native memory and delayed collection. Estimated reserved bytes are
separate from actual managed
allocation measurements and do not establish a hard process-memory cap.

There is no returned public value stream or command-dispatch callback in the
current host API. Execution consumes its deferred evaluator internally and awaits
all host borrows. An embedding host starting cooperating commands must pass the
same budget, keep parent storage charged, and await child completion/cleanup
before disposing any scope it creates. No charge is transferred to GC or
automatically reparented.

## Fixed safety ceilings

JSON/value/parser/module depth remains 64; evaluation depth remains 256. Filter
length remains 1 Mi characters, with 4,096 tokens/arguments. A serialized JSON
value is bounded to 32 MiB with a 64 MiB buffer ceiling. Regex patterns are
bounded to 16,384 UTF-16 code units; the engine receives a 256 KiB working-memory
limit and depth limit 256, in addition to the configured per-search work/time
limits. Increasing these ceilings requires separate stack and allocation evidence.
Compilation receives conservative allocation charges (64 KiB plus workspace
and 64 bytes per pattern code unit); this is not a hard compiled-storage cap.
UTF-8 subject buffers and detailed capture capacity are charged before engine
calls, with extracted strings charged before materialization. The engine
`MaxResultBytes` setting is reserved and is not used as an enforced guard.
See [REGEX.md](REGEX.md) for the engine profile.
Reference path-length validation remains a catchable language error, as in
upstream jv_aux.c; it is separate from execution quota exhaustion.

## Failure and cleanup

Execution quota exhaustion terminates with status 5, regardless of the active
stage or `-e`. `try`, `?`, and other language handlers cannot catch it; later
input records and files are abandoned. Cancellation propagates as
`OperationCanceledException`, including cancellation before setup. Utf8Regex
0.3.0 has no cancellation-token API: cancellation is checked before and after
compilation and each bounded synchronous search, and cannot interrupt a running
engine call. Its work/time allowances are per search rather than a cumulative
execution deadline. A global operation can therefore take multiple allowances;
shared admission bounds retained storage and cancellation stops further work.
Host contract failures propagate separately. Catchable language failures, such as invalid
regex syntax, retain their jq handler behavior.
Actual `OutOfMemoryException` propagates separately from admission refusal,
including file-variable reads; owned admission and descriptors still unwind.
Data imports and streaming numeric decoding propagate quotas unchanged; input
depth exhaustion also remains terminal under `--seq` and `--stream-errors`.

Only opened, owned descriptors are closed. Borrowed descriptors stay with the
caller. Cleanup uses an uncancelled token and preserves the original failure.
Host backpressure and downstream closure apply to every output.
