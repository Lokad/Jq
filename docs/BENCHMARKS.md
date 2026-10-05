# Benchmarks

The development harness covers non-regex workloads only. Regex performance
belongs to the regex engine layer; semantic and resource tests remain in Jq.
The first complete reference campaign matches jq in all 38 cases and qualifies
all 36 workload measurements. The two startup/latency controls remain unqualified:
their managed batches are shorter than the 20 ms acceptance floor at the iteration
ceiling, so no ratios are published for them. See the generated comparison below.

The machine is an Azure `Standard_F4as_v7` with four full AMD cores, 16 GiB RAM and
Ubuntu 24.04.4 LTS. Small-input ratios include jq CLI launch costs and must not be
read as pure evaluator speedups. Results apply to this bounded catalog and measured
revision; no overall speedup is calculated.

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
Windows and the AMD Linux VM agree with jq in all 38 cases under default
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

Mode: qualify. Measured revision: `ccf2dd317538dda12ea3d0b952667c495e857bac`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-05T13:44:26.2153994+00:00. Completed 38/38 requested cases.

.NET 10.0.8; SDK 10.0.204; Ubuntu 24.04.4 LTS; AMD EPYC 9V45 96-Core Processor; 4 logical processors.

Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.

Ratio is jq time divided by Lokad.Jq time; above 1 favors Lokad.Jq. CI is a paired-bootstrap 95% interval.

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Ratio / 95% CI | Interpretation |
| --- | ---: | --- | ---: | ---: | --- | --- |
| identity-compact/small | 16 | Qualified | 0.012 | 1.289 | 108.95 [108.40, 110.33] | Lokad.Jq faster |
| identity-pretty/small | 16 | Qualified | 0.013 | 1.324 | 102.74 [101.67, 103.78] | Lokad.Jq faster |
| ndjson-project/small | 16 | Qualified | 0.031 | 1.328 | 42.49 [42.24, 43.42] | Lokad.Jq faster |
| ndjson-select/small | 16 | Qualified | 0.027 | 1.326 | 49.31 [48.39, 49.48] | Lokad.Jq faster |
| map-construct/small | 16 | Qualified | 0.033 | 1.346 | 40.84 [40.53, 41.52] | Lokad.Jq faster |
| reduce/small | 16 | Qualified | 0.013 | 1.303 | 105.19 [103.78, 106.10] | Lokad.Jq faster |
| foreach/small | 16 | Qualified | 0.018 | 1.331 | 74.30 [72.80, 74.78] | Lokad.Jq faster |
| sort-group/small | 16 | Qualified | 0.065 | 1.392 | 21.38 [21.30, 21.74] | Lokad.Jq faster |
| entries-update/small | 16 | Qualified | 0.061 | 1.595 | 26.09 [25.83, 26.37] | Lokad.Jq faster |
| walk-paths/small | 16 | Qualified | 0.091 | 1.601 | 17.56 [17.40, 17.69] | Lokad.Jq faster |
| unicode-strings/small | 16 | Qualified | 0.044 | 1.364 | 30.84 [30.47, 31.48] | Lokad.Jq faster |
| json-roundtrip/small | 16 | Qualified | 0.037 | 1.316 | 35.66 [35.35, 35.80] | Lokad.Jq faster |
| identity-compact/medium | 256 | Qualified | 0.195 | 1.473 | 7.74 [7.41, 7.90] | Lokad.Jq faster |
| identity-pretty/medium | 256 | Qualified | 0.207 | 1.492 | 7.35 [7.07, 7.49] | Lokad.Jq faster |
| ndjson-project/medium | 256 | Qualified | 0.417 | 1.584 | 3.80 [3.77, 3.83] | Lokad.Jq faster |
| ndjson-select/medium | 256 | Qualified | 0.336 | 1.472 | 4.39 [4.35, 4.42] | Lokad.Jq faster |
| map-construct/medium | 256 | Qualified | 0.517 | 1.694 | 3.30 [3.26, 3.33] | Lokad.Jq faster |
| reduce/medium | 256 | Qualified | 0.114 | 1.382 | 12.17 [11.97, 12.32] | Lokad.Jq faster |
| foreach/medium | 256 | Qualified | 0.198 | 1.345 | 6.76 [6.70, 6.85] | Lokad.Jq faster |
| sort-group/medium | 256 | Qualified | 0.930 | 1.775 | 1.96 [1.91, 1.98] | Lokad.Jq faster |
| entries-update/medium | 256 | Qualified | 0.927 | 3.202 | 3.36 [3.33, 3.46] | Lokad.Jq faster |
| walk-paths/medium | 256 | Qualified | 1.401 | 3.863 | 2.73 [2.71, 2.79] | Lokad.Jq faster |
| unicode-strings/medium | 256 | Qualified | 0.614 | 1.936 | 3.16 [3.14, 3.18] | Lokad.Jq faster |
| json-roundtrip/medium | 256 | Qualified | 0.579 | 1.770 | 3.05 [3.03, 3.07] | Lokad.Jq faster |
| identity-compact/large | 4096 | Qualified | 13.674 | 5.721 | 0.42 [0.41, 0.44] | jq faster |
| identity-pretty/large | 4096 | Qualified | 13.753 | 6.415 | 0.47 [0.45, 0.49] | jq faster |
| ndjson-project/large | 4096 | Qualified | 6.761 | 6.696 | 0.99 [0.97, 0.99] | jq faster |
| ndjson-select/large | 4096 | Qualified | 5.451 | 4.827 | 0.88 [0.86, 0.89] | jq faster |
| map-construct/large | 512 | Qualified | 1.085 | 2.008 | 1.85 [1.79, 1.87] | Lokad.Jq faster |
| reduce/large | 4096 | Qualified | 1.875 | 2.260 | 1.21 [1.20, 1.23] | Lokad.Jq faster |
| foreach/large | 4096 | Qualified | 3.520 | 2.961 | 0.86 [0.85, 0.87] | jq faster |
| sort-group/large | 512 | Qualified | 2.063 | 2.353 | 1.14 [1.13, 1.16] | Lokad.Jq faster |
| entries-update/large | 512 | Qualified | 2.026 | 5.074 | 2.51 [2.47, 2.53] | Lokad.Jq faster |
| walk-paths/large | 512 | Qualified | 3.085 | 6.746 | 2.20 [2.19, 2.22] | Lokad.Jq faster |
| unicode-strings/large | 1024 | Qualified | 2.532 | 3.842 | 1.53 [1.52, 1.53] | Lokad.Jq faster |
| json-roundtrip/large | 512 | Qualified | 1.225 | 2.335 | 1.91 [1.89, 1.95] | Lokad.Jq faster |
| startup-empty/control | 0 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |
| small-request/control | 1 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |

<!-- END GENERATED COMPARISON -->

## Workload direction

The family selection is informed by the public
[jaq catalog](https://github.com/01mf02/jaq/blob/main/examples/benches.json) and
[jq-jit suite](https://github.com/m5d215/jq-jit/blob/main/bench/comprehensive.sh).
Filters, input generators and tooling are self-contained; neither suite nor its
datasets is vendored or required. Our bounded sizes and measurement method do
not establish equivalence to their published timings.
