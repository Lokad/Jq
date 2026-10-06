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
| `MaximumAllocationBytes` | 256 MiB | Cumulative conservative allocation charges, including intermediate and discarded values. |
| `MaximumValueNodes` | 262,144 | Cumulative value and evaluation charges, including discarded work. |
| `MaximumStringLength` | 8 Mi UTF-16 code units | Each charged string or text buffer, including keys, rendered JSON, and decoded filter-file text. |
| `MaximumRegexWork` | 10,000,000 | Engine work steps per search; a fresh allowance for each `IsMatch` or `MatchDetailed` call. |
| `MaximumRegexTime` | Five seconds | Time per engine search; excludes compilation, filter replacement evaluation and host waits. |

Allocation accounting measures charged work, rather than retained heap or exact
process memory. Charges are not refunded when values are discarded. Small
allowances can expire during variable snapshots or compilation, before any
input is read. The output allowance covers stdout; queued `debug`/`stderr` bytes
consume the allocation allowance. Terminal diagnostics remain writable after
exhaustion so the host can observe its cause.

Input reads use bounded requests and a one-byte overflow probe. Filter/module
and variable-file reads use the remaining cumulative allowance. A rejected
output is not appended: complete records already emitted remain visible.
Downstream closure ends production before subsequent outputs or charges.
JSON strings and object keys preflight their decoded UTF-16 length and string
allocation before materialization, including Unicode and other JSON escapes.

Command recognition and argument binding happen in `Jq.TryParse`, before an
execution policy is selected. That phase retains its separate default allowance
and exit-2 argument-error contract, including JSON-valued arguments. Execution
snapshots, compilation and IO then use the supplied policy. This separation
keeps command reuse independent of host and execution state.

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
jq value/allocation counters still bound produced results. Host contract
failures propagate separately. Catchable language failures, such as invalid
regex syntax, retain their jq handler behavior.
Data imports and streaming numeric decoding propagate quotas unchanged; input
depth exhaustion also remains terminal under `--seq` and `--stream-errors`.

Only opened, owned descriptors are closed. Borrowed descriptors stay with the
caller. Cleanup uses an uncancelled token and preserves the original failure.
Host backpressure and downstream closure apply to every output.
