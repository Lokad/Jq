# Architecture

`Jq.TryParse(JqCommandInvocation)` recognizes jq names and borrows an immutable
invocation. `Jq.ExecuteAsync` binds options/JSON arguments and drives the evaluator:

1. `JqCommandLineParser` separates variables, positional values, format options,
   filter files, and input file operands. Option binding is implemented locally
   without a command-line parsing dependency or source generator.
2. `Lexer` and `JqParser` construct `JqFilter` nodes. Filter compilation currently
   occurs during execution, not during command-name recognition. Main-program
   tokens are reused between import scanning and parsing within that execution;
   scopes, module inputs and budgets remain fresh on every call.
3. `JqExecutor` loads input and filter files through `IJqHost`, creates a fresh
   `JqBudget`/`JqContext`, enumerates filter results, and writes UTF-8 output.
4. `JqRuntime` manipulates `JsonNode` values. C# null represents JSON null;
   zero emitted values represents an empty jq stream. Do not conflate the two.
5. `JqRegexCache`, `TestFilter`, and `GsubFilter` use managed `Lokad.Utf8Regex.Pcre2`
   patterns with per-search work, depth, workspace and time limits. Subjects are
   encoded as UTF-8; engine byte/UTF-16 positions become scalar-based jq results.
   `test` uses `IsMatch`; capture-producing operations use `MatchDetailed`.

The interpreter is the initial engine. Parser/runtime types stay internal.
Paths, invocations, descriptor handles, IO results, and `IJqHost` form the public
embedding boundary. Environment variables are invocation snapshots exposed through
`$ENV` and `env`; `PWD` also selects the current directory. An optional `JqClock`
supplies the clock and timezone for time builtins, without ambient host state.

Modules are supplied by the application through `IJqHost`. `import`, `include`
and `modulemeta` use hosted paths; `-L` and import metadata control search
directories. Automatic `~/.jq` import, home-directory lookup and expansion, and
`$ORIGIN`/executable-origin lookup are intentionally unsupported.

Catchable jq errors are separate from cancellation, host contract failures, and
resource exhaustion. Regex work/time, pattern length and matching resource limits
terminate execution with status 5; `try` and optional suppression cannot catch
them. Invalid regex syntax remains a catchable language error. Variable snapshot
allocation and bounded file reads use the same terminal quota path.

`ExecuteAsync` accepts an immutable `JqExecutionPolicy` for stricter allowances.
Input/output/allocation-work/value-work counters are cumulative; regex work/time limits
apply to each engine search. Every execution creates independent work counters;
command binding keeps its separate default work allowance within the same
execution memory scope. Structural and regex
workspace ceilings stay fixed. Utf8Regex 0.3.0 lacks cancellation-token overloads;
cancellation is checked before and after bounded synchronous regex calls.
See [REGEX.md](REGEX.md) for the engine profile and cancellation limits.
See [EXECUTION_POLICY.md](EXECUTION_POLICY.md) for the exact accounting contract.

Explicit execution overloads borrow an `IMemoryBudget` sharing the host's root
allowance. Memory admission owns live storage estimates with execution, record,
buffer, result and cache lifetimes; it does not copy a remaining balance into
per-invocation caps. Current coverage and conservative retention caveats are
listed in EXECUTION_POLICY.md. `MaximumAllocationWorkBytes` is an independent
cumulative work guard; its deprecated `MaximumAllocationBytes` alias is retained
until the next breaking API release.

Coarse owners separate logical storage demand from bounded, already-admitted
credit, so small local charges need not call the host each time. Safe record
boundaries retain at most 4 KiB of reusable credit. Regex caches retain at most
one idle subject buffer and one detailed-result allowance, capped at 4 KiB each;
nested operations keep distinct owners. Cursor reconciliation retains at most
256 bytes of existing credit beyond actual retained trees. After 32 successful
serializations, each budget may reuse one returned buffer capped at 4 KiB of
admission; active results keep distinct owners. Cold results refund immediately.
Direct buffers still admit replacement
peaks and refund them after copying. EXECUTION_POLICY.md describes exact fallback
under root pressure and the retained-headroom tradeoff.

Try-style invocation factories admit copied argument/environment vectors and
derived directory storage before allocation. The invocation owns a scope and
borrows the supplied budget; default execution overloads forward that budget.
Dispose only after all borrowing commands finish. Binding JSON values and path
work allocate during execution, so their admission survives the parsed result,
evaluation and cleanup. Legacy constructors create caller-owned snapshots without
admission; their execution can still receive an explicit shared budget.

## IO ownership

The caller owns stdin/stdout/stderr handles. The runtime borrows them and never
closes them. A successful `OpenReadAsync` transfers one descriptor to the
execution, which closes it in a finally block even on cancellation or rejection.
Cleanup uses `CancellationToken.None`, retries a reported unsuccessful close
once, and preserves an earlier exception. A thrown close exception is terminal.

`ReadBytesAsync` fills caller-owned memory and must obey its size. EOF may accompany
the last bytes. `AppendWhileOpenAsync` must copy memory if retaining it beyond
completion and may remain incomplete to apply backpressure. It distinguishes
success, downstream closure, and failure; the evaluator stops producing output
when the descriptor cannot accept more.

JSON output reuses a writer and bounded byte buffer within each execution; raw
strings reuse separate UTF-8 storage. Record framing follows output quota checks.
The executor awaits each host append before reusing its memory, preserving
per-record backpressure and closure. Filter/diagnostic serialization uses
separate storage.

Input flows through a shared pull cursor (`JqInputCursor`) using chunked 8 KiB
requests with a one-byte overflow probe. Implicit outer iteration and explicit
`input`/`inputs` calls draw from the same cursor, so values interleave; `-`
operands reuse borrowed stdin at its current position and are never closed,
while file operands open lazily and close when exhausted, abandoned, or
disposed. JSON values and raw lines may span chunk boundaries; slurp
aggregates the cursor. Ordinary JSON preserves reader state and completed DOM
nodes across reads, so incomplete containers are not repeatedly allocated.
Numeric tokens wait for a delimiter or end of source. Extended non-finite
literals and malformed-input diagnostics use the existing whole-value reader.
With --stream, a byte-level scanner emits path and
leaf events without a document DOM; with --seq, records split at RS
separators with resync recovery. No real filesystem implementation, shell
parser, subprocess launcher, or reference-jq binary belongs in this library.

## Project structure

The library includes its own invocation, canonical path, descriptor, and byte IO
types. It has no dependency on other checkouts. The bounded read loop uses
fixed-size read requests into byte buffers. Paths use Unix-style separators and
are resolved against an explicit current directory.

The tests reference the built assembly. Most exercise the public command API;
existing parser/budget/regex white-box tests use a friend assembly declaration.
The test host implements only the small jq IO contract. Shell parsing and
pipeline orchestration are responsibilities of an embedding application.

