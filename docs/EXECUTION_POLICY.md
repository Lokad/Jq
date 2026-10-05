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
| `MaximumRegexWork` | 10,000,000 | Cumulative regex callout and movement charges across patterns and inputs. |
| `MaximumRegexTime` | Five seconds | Cumulative matching time, excluding filter replacement evaluation and host waits. |

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

Command recognition and argument binding happen in `Jq.TryParse`, before an
execution policy is selected. That phase retains its separate default allowance
and exit-2 argument-error contract, including JSON-valued arguments. Execution
snapshots, compilation and IO then use the supplied policy. This separation
keeps command reuse independent of host and execution state.

## Fixed safety ceilings

JSON/value/parser/module depth remains 64; evaluation depth remains 256. Filter
length remains 1 Mi characters, with 4,096 tokens/arguments. A serialized JSON
value is bounded to 32 MiB with a 64 MiB buffer ceiling. Native regex patterns
remain bounded to 16,384 characters, 64 KiB compiled storage, 256 KiB match heap,
100,000 per-match steps, and native depth 256. These limits are not raised by an
execution policy. Increasing them requires separate stack and allocation evidence.

## Failure and cleanup

Execution quota exhaustion terminates with status 5, regardless of the active
stage or `-e`. `try`, `?`, and other language handlers cannot catch it; later
input records and files are abandoned. Cancellation propagates as
`OperationCanceledException`, including cancellation before setup. Host contract
failures propagate separately. Catchable language failures, such as invalid
regex syntax, retain their jq handler behavior.

Only opened, owned descriptors are closed. Borrowed descriptors stay with the
caller. Cleanup uses an uncancelled token and preserves the original failure.
Host backpressure and downstream closure apply to every output.
