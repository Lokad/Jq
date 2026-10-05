# Benchmarks

The development harness covers non-regex workloads only. Regex performance
belongs to the regex engine layer; semantic and resource tests remain in Jq.
No qualified performance comparison is published yet. Qualification is pending
a dedicated quiet machine; the development machine failed the background CPU gate.

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
has twelve families at three bounded sizes, plus `startup-empty` and
`small-request` controls: identity (compact/pretty), NDJSON projection/selection,
construction, reduction, foreach, sort/group, entries/updates, walk/paths,
Unicode strings and JSON round trips. All inputs are generated deterministically.
The initial Windows comparison agrees with jq in all 38 cases under default
execution limits. This is a bounded catalog, not a full compatibility claim.

Small cases use 16 records/elements and medium cases use 256. Large identity,
NDJSON, reduce and foreach cases use 4,096; Unicode uses 1,024; construction,
sort/group, updates, walk/paths and JSON round trips use 512. Construction-heavy
4,096-element probes exhausted the default cumulative accounting allowances, so
those sizes are excluded rather than increasing the policy limits. Each artifact
records the actual size and input digest. Small eligible sizes can still expose
CLI launch costs; the throughput boundary remains explicit.

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

## Timing qualification

Start with a targeted case on the dedicated machine, then select the full catalog:

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --check-machine
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ --mode qualify --case reduce --scale large --output artifacts/benchmarks/qualification-smoke.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ --mode qualify --output artifacts/benchmarks/qualification.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-report artifacts/benchmarks/qualification.json
```

Commit changes and rebuild before qualification. Assembly build revisions must
match HEAD, and the checkout must be clean apart from the generated benchmark
documentation. Qualification pauses before timing if five one-second CPU samples
exceed 3% median or 5% maximum background load. It checks again during idle gaps
between pairs, using a one-second accounting window after 500 ms of settling,
and stops if the machine becomes busy. This gate uses Windows system
CPU accounting or Linux `/proc/stat`; other systems cannot currently qualify.
CPU quietness is a necessary check, not proof that IO or virtualization is noise-free.

Each lane warms for at least 32 invocations and one second, then independently
calibrates sample batches toward at least 40 ms. The default is eleven alternating
pairs; `--pairs` accepts 9 through 31. Samples include complete output consumption,
SHA-256 and successful status/stderr validation against each lane's preflight.
Calibration/warmup are outside reported sample times. Each invocation is bounded
by 15 seconds and each case by 120 seconds; Ctrl+C cancels and reaps active children.

The paired ratio is jq time divided by Lokad.Jq time per invocation. A fixed-seed
10,000-resample bootstrap supplies a 95% median-log-ratio interval. Cases are
unqualified if samples are shorter than 20 ms, lane interquartile spread exceeds
10%, the ratio changes by more than 10% with order, or its confidence interval is
wider than 15%. Intervals crossing parity claim no winner. Startup/latency controls
keep their launch costs; no overall mixed-workload speedup is calculated.

JSON checkpoints include source/build revision, checkout state, benchmark assembly
hash, SDK/runtime, OS/CPU, jq identity, default limits, filter/input digests, output
digests, warmup counts, quiet checks and every raw paired sample. Completed rows
are saved atomically, so interrupted runs retain previous complete checkpoints.
The report renderer updates only its marked generated section. Verification,
paused, mismatched and unqualified rows cannot publish ratios.

Bounded stress checks run separately without timing or an upstream executable:

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --stress
```

They cover depth, wide construction and output/string expansion with small explicit
policies, preserving complete output prefixes. Existing ordinary tests cover
cancellation, downstream closure and owned-descriptor cleanup.

<!-- BEGIN GENERATED COMPARISON -->
## Recorded comparison

Timing qualification is pending a dedicated quiet machine. No performance ratios
have been collected or published by this campaign.
<!-- END GENERATED COMPARISON -->

## Workload direction

The family selection is informed by the public
[jaq catalog](https://github.com/01mf02/jaq/blob/main/examples/benches.json) and
[jq-jit suite](https://github.com/m5d215/jq-jit/blob/main/bench/comprehensive.sh).
Filters, input generators and tooling are self-contained; neither suite nor its
datasets is vendored or required. Our bounded sizes and measurement method do
not establish equivalence to their published timings.
