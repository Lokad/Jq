# Architecture

`Jq.TryParse(JqCommandInvocation)` recognizes jq names and binds command
arguments. `Jq.ExecuteAsync` drives the existing command evaluator:

1. `JqCommandLineParser` separates variables, positional values, format options,
   filter files, and input file operands. `JqArgs` uses Lokad.Cli generation.
2. `JqLexer` and `JqParser` construct `JqFilter` nodes. Filter compilation currently
   occurs during execution, not during command-name recognition.
3. `JqExecutor` loads input and filter files through `IJqHost`, creates a fresh
   `JqBudget`/`JqContext`, enumerates filter results, and writes UTF-8 output.
4. `JqRuntime` manipulates `JsonNode` values. C# null represents JSON null;
   zero emitted values represents an empty jq stream. Do not conflate the two.
5. `JqRegexCache`, `TestFilter`, and `GsubFilter` use PCRE.NET with bounded work,
   native memory allowances, cancellation callbacks, and a time allowance.

The interpreter is the initial engine. Parser/runtime types stay internal.
Paths, invocations, descriptor handles, IO results, and `IJqHost` form the public
embedding boundary. Environment variables are invocation snapshots; only PWD
currently affects execution by selecting the current directory.

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

Input is currently fully buffered per source using bounded 8 KiB requests with
a one-byte overflow probe. Results are streamed from the filter but this does
not make input parsing incremental. No real filesystem implementation, shell
parser, subprocess launcher, or reference-jq binary belongs in this library.

## Project structure

The library includes its own invocation, canonical path, descriptor, and byte IO
types. It has no dependency on other checkouts. The bounded read loop uses a
MemoryStream and fixed-size read requests. Paths use Unix-style separators and
are resolved against an explicit current directory.

The tests reference the built assembly. Most exercise the public command API;
existing parser/budget/regex white-box tests use a friend assembly declaration.
The test host implements only the small jq IO contract. Shell parsing and
pipeline orchestration are responsibilities of an embedding application.
