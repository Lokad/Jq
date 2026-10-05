# Benchmarks

The development harness covers non-regex workloads only. Regex performance
belongs to the regex engine layer; semantic and resource tests remain in Jq.
No qualified performance comparison is published yet.

## Local measurements and correctness

BenchmarkDotNet reports managed allocations and local execution timings:

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'
```

The twelve local cases validate status, separate stderr, output length and
SHA-256 on every execution. A correctness-only smoke command avoids timing:

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --smoke
```

Command binding is measured separately. Reusing a command still compiles its
filter on every execution. Output consumption and checksum costs are included.
The verified-consumption host replaces earlier discard/capture hosts, so older
timings are not directly comparable to measurements with this harness.

## Upstream comparison

Supply an independently installed jq 1.8.2 executable explicitly. The tool records
its version, binary SHA-256 and build configuration. It never downloads jq and
rejects paths under `external/`. Ordinary tests and CI do not require jq.

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ --case reduce --scale small
```

These commands check correctness only and collect no timings. The initial catalog
has twelve families at 16, 128 and 512 records/elements, plus `startup-empty` and
`small-request` controls: identity (compact/pretty), NDJSON projection/selection,
construction, reduction, foreach, sort/group, entries/updates, walk/paths,
Unicode strings and JSON round trips. All inputs are generated deterministically.
The initial Windows comparison agrees with jq in all 38 cases under default
execution limits. This is a bounded catalog, not a full compatibility claim.

Rendering cases compare exact output bytes. Value cases compare the complete
ordered JSON stream with exact numeric comparison and decoded string identity;
JSON spelling and object key order may differ. Duplicate keys are rejected by
the comparison helper. Nonzero status, stderr, quota exhaustion, timeout and
mismatched output cannot become successful performance measurements.

Both lanes receive the same arguments, including `-b` for Windows LF output and
`-M` for monochrome output. The reference process uses an absent home directory
and no `JQ_LIBRARY_PATH`, preventing automatic home-module content from affecting
the catalog. All input/output is handled as bytes; stdout and stderr are drained
concurrently with bounds, and cancelled/timed-out children are killed and reaped.

The intended timing boundary is warm embedded Lokad.Jq versus invoking the jq
CLI: binding, compilation, parsing, evaluation, rendering and output consumption
are included; CLI launch and pipe costs are also included for jq. Input generation
and full result comparisons occur before timing. This measures application
throughput, not pure evaluator speed. Startup controls are reported separately
and never subtracted. Managed GC allocations do not measure jq process memory.

Verification JSON defaults to ignored `artifacts/benchmarks/verification.json`;
`--output PATH` selects another destination under `artifacts/`. Raw measurements and
traces stay ignored. Timing qualification requires a quiet machine and a clean,
committed Release checkout; correctness verification can run on a busy machine.

## Workload direction

The family selection is informed by the public
[jaq catalog](https://github.com/01mf02/jaq/blob/main/examples/benches.json) and
[jq-jit suite](https://github.com/m5d215/jq-jit/blob/main/bench/comprehensive.sh).
Filters, input generators and tooling are self-contained; neither suite nor its
datasets is vendored or required. Our bounded sizes and measurement method do
not establish equivalence to their published timings.
