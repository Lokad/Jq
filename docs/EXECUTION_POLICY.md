# Execution policy

`Jq.ExecuteAsync(host, policy, cancellationToken)` accepts an immutable
`JqExecutionPolicy`. The existing overload uses `JqExecutionPolicy.Default`.
Each execution gets fresh counters, including repeated or concurrent execution
of the same command with the same policy.

```csharp
var policy = new JqExecutionPolicy
{
    MaximumInputBytes = 1024 * 1024,
    MaximumOutputBytes = 2 * 1024 * 1024,
    MaximumValueNodes = 100_000,
};
var command = Jq.TryParse(invocation)
    ?? throw new InvalidOperationException("Expected a jq command.");
int status = await command.ExecuteAsync(host, policy, cancellationToken);
```

Allowances must be positive and no greater than the tested defaults. Invalid
configuration throws `ArgumentOutOfRangeException` during initialization. A
record copy, such as `policy with { MaximumOutputBytes = 1024 }`, leaves the
original policy unchanged. There is no unlimited setting.

| Allowance | Default and current ceiling | Accounting |
| --- | --- | --- |
| `MaximumInputBytes` | 16 MiB | Cumulative bytes read from stdin, input files, filter files, file variables, and modules. |
| `MaximumOutputBytes` | 32 MiB | Cumulative stdout bytes, including newlines, NUL/RS framing, and information output. |
| `MaximumAllocationWorkBytes` | 256 MiB | Cumulative conservative allocation-work charges, including intermediate and discarded work. |
| `MaximumValueNodes` | 262,144 | Cumulative value and evaluation charges, including discarded work. |
| `MaximumStringLength` | 8 Mi UTF-16 code units | Each charged string or text buffer, including keys, rendered JSON, and decoded filter-file text. |
| `MaximumRegexWork` | 10,000,000 | Engine work steps per search; a fresh allowance for each `IsMatch` or `MatchDetailed` call. |
| `MaximumRegexTime` | Five seconds | Time per engine search; excludes compilation, filter replacement evaluation and host waits. |

Allocation-work accounting measures charged work. Its charges are not refunded
when values are discarded. Small
allowances can expire during variable snapshots or compilation, before any
input is read. The output allowance covers stdout; queued `debug`/`stderr` bytes
consume allocation work. Terminal diagnostics remain writable after
exhaustion so the host can observe its cause.

`MaximumAllocationBytes` is a deprecated compatibility alias for
`MaximumAllocationWorkBytes`, with the same backing value and validation. It
introduces no separate memory balance or backend. Remove the alias in the next
breaking API release; maintained examples, runtime consumers and ordinary tests
use the canonical work property. Work exhaustion reports
`allocation work allowance exceeded`; shared admission refusal reports
`memory budget exceeded`. Both remain terminal status 5 and bypass jq handlers.
Calls without a host memory hook enforce only the independent work/I/O/structural
policies; they provide no shared live-storage admission.

Input reads use bounded requests and a one-byte overflow probe. Filter/module
and variable-file reads use the remaining cumulative allowance. A rejected
output is not appended: complete records already emitted remain visible.
Downstream closure ends production before subsequent outputs or charges.
JSON strings and object keys preflight their decoded UTF-16 length and string
allocation before materialization, including Unicode and other JSON escapes.

`Jq.TryParse` recognizes a command name and borrows the immutable invocation.
Binding options/JSON arguments happens afresh during execution, with a separate
default work allowance and the existing exit-2 argument-error contract. Its
storage admission uses the same execution scope as evaluation and remains live
through parsed-variable use and cleanup. Admission refusal reports status 5,
including `--argjson` and `--jsonargs`, rather than becoming a JSON argument error.
Execution snapshots, compilation and IO then use the supplied work policy.

`JqCommandInvocation.TryCreateWithStandardDescriptors` and `TryCreate` reserve
copied argument/environment vectors and derived-directory estimates before
allocation. Refusal returns false with a null invocation. The returned invocation
owns its scope; the supplied budget and immutable input strings remain borrowed.
Keep input owners and the invocation live through every borrowing command's
completion, then dispose it. Disposal is idempotent and neither cancels work nor
disposes the root. Existing execution overloads borrow its associated budget;
explicit execution budgets must share that allowance. Legacy constructors do
not perform shared admission for their caller-owned snapshots.

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
If optional padding is refused, one immediate exact-size attempt preserves the
required allocation's outcome, then that owner uses exact growth without further
padding probes. Refusal of required storage remains terminal. Reconciliation
refunds discarded demand and bounds retained credit. No root balance is copied.
HTML and implode builders preflight their exact capacity in bulk; literal runs
in time formatting are charged together. Cancellation and work guards remain
independent, including bounded checks during long text scans.

`TryReserve` and `TryResize` admit nonnegative estimates immediately or refuse
without changing existing charges. Negative estimates throw; admission through
disposed owners throws. Refusal reports terminal status 5 and cannot be caught
by jq handlers. Complete earlier output remains visible.

Current coverage includes reusable JSON/raw output backing capacity, information
output, cursor read/accumulation buffers, streaming token capacity and hosted
filter/module/variable-file read buffers. Growth reserves old and new storage
together until replacement or copying finishes. Retained capacity stays charged
between records. Cursor buffers survive pending reads and owned-descriptor
cleanup; a hosted read result owns its accumulated capacity until decoding ends.
Raw output starts with exact capacity and doubles later growth up to its output
ceiling, keeping that backing capacity charged. If optional growth is refused,
one exact replacement is attempted and further growth stays exact for that
renderer. Both attempts preserve the old-plus-new replacement peak; a successful
smaller replacement does not replay output or consume output allowance twice.
Temporary read storage is released after the awaited read loop completes. Regex
compiled-pattern and pooled-workspace estimates remain charged under one cache
owner until context disposal and completion of its borrowers. Subject encoding
and detailed capture contexts have operation owners. The cache can retain one
idle UTF-8 subject buffer and one idle capture allowance, each at most 4 KiB and
still charged. Reuse avoids admission calls for unchanged capacity. Subjects start
at exact capacity, then double growth within the existing 4 KiB idle-owner ceiling
(including metadata). Larger subjects stay exact. Refused optional growth falls
back once to exact capacity and disables padding for that owner. Subject growth
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
reusable credit. No credit is prepaid on an empty cursor. Larger discarded
estimates still refund at each safe boundary, and terminal cleanup refunds all
remaining admission. Module compilation during `modulemeta` uses
the persistent owner so cached definitions survive record refunds.

Serialization results own their byte capacity until consumption finishes.
Queued diagnostic copies have a separate owner that survives record refunds and
is released after all awaited appends finish. Empty diagnostic chunks preserve
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
runtime/native memory and delayed collection. The cumulative work policy above
remains independent. Estimated reserved bytes are separate from actual managed
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
jq value/allocation-work counters still bound produced results. Host contract
failures propagate separately. Catchable language failures, such as invalid
regex syntax, retain their jq handler behavior.
Actual `OutOfMemoryException` propagates separately from admission refusal,
including file-variable reads; owned admission and descriptors still unwind.
Data imports and streaming numeric decoding propagate quotas unchanged; input
depth exhaustion also remains terminal under `--seq` and `--stream-errors`.

Only opened, owned descriptors are closed. Borrowed descriptors stay with the
caller. Cleanup uses an uncancelled token and preserves the original failure.
Host backpressure and downstream closure apply to every output.
