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
calibrates sample batches using two consecutive runs of at least 40 ms.
The default is eleven alternating
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

Mode: qualify. Measured revision: `38fc923aeb32a18658a12c7d94e732dce219923c`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-05T15:19:04.7191350+00:00. Completed 38/38 requested cases.

.NET 10.0.8; SDK 10.0.204; Ubuntu 24.04.4 LTS; AMD EPYC 9V45 96-Core Processor; 4 logical processors.

Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.

Ratio is jq time divided by Lokad.Jq time; above 1 favors Lokad.Jq. CI is a paired-bootstrap 95% interval.

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Ratio / 95% CI | Interpretation |
| --- | ---: | --- | ---: | ---: | --- | --- |
| identity-compact/small | 16 | Qualified | 0.011 | 1.249 | 115.09 [114.11, 116.24] | Lokad.Jq faster |
| identity-pretty/small | 16 | Qualified | 0.012 | 1.243 | 104.75 [104.26, 106.69] | Lokad.Jq faster |
| ndjson-project/small | 16 | Qualified | 0.031 | 1.249 | 40.57 [40.11, 40.90] | Lokad.Jq faster |
| ndjson-select/small | 16 | Qualified | 0.026 | 1.257 | 49.41 [49.04, 50.00] | Lokad.Jq faster |
| map-construct/small | 16 | Qualified | 0.032 | 1.291 | 39.93 [39.73, 40.48] | Lokad.Jq faster |
| reduce/small | 16 | Qualified | 0.012 | 1.258 | 103.20 [101.49, 105.45] | Lokad.Jq faster |
| foreach/small | 16 | Qualified | 0.017 | 1.250 | 73.66 [72.73, 74.49] | Lokad.Jq faster |
| sort-group/small | 16 | Qualified | 0.062 | 1.329 | 21.34 [21.07, 21.59] | Lokad.Jq faster |
| entries-update/small | 16 | Qualified | 0.057 | 1.484 | 25.97 [25.81, 26.19] | Lokad.Jq faster |
| walk-paths/small | 16 | Qualified | 0.084 | 1.519 | 18.07 [17.84, 18.40] | Lokad.Jq faster |
| unicode-strings/small | 16 | Qualified | 0.041 | 1.322 | 32.25 [31.77, 32.76] | Lokad.Jq faster |
| json-roundtrip/small | 16 | Qualified | 0.035 | 1.301 | 37.62 [37.04, 37.85] | Lokad.Jq faster |
| identity-compact/medium | 256 | Qualified | 0.116 | 1.456 | 12.42 [12.27, 12.60] | Lokad.Jq faster |
| identity-pretty/medium | 256 | Qualified | 0.129 | 1.491 | 11.53 [11.35, 11.62] | Lokad.Jq faster |
| ndjson-project/medium | 256 | Qualified | 0.427 | 1.569 | 3.65 [3.63, 3.70] | Lokad.Jq faster |
| ndjson-select/medium | 256 | Qualified | 0.319 | 1.469 | 4.56 [4.54, 4.64] | Lokad.Jq faster |
| map-construct/medium | 256 | Qualified | 0.438 | 1.598 | 3.67 [3.60, 3.69] | Lokad.Jq faster |
| reduce/medium | 256 | Qualified | 0.106 | 1.319 | 12.37 [12.22, 12.59] | Lokad.Jq faster |
| foreach/medium | 256 | Qualified | 0.190 | 1.340 | 7.11 [7.04, 7.15] | Lokad.Jq faster |
| sort-group/medium | 256 | Qualified | 0.817 | 1.739 | 2.13 [2.12, 2.15] | Lokad.Jq faster |
| entries-update/medium | 256 | Qualified | 0.842 | 3.097 | 3.71 [3.63, 3.72] | Lokad.Jq faster |
| walk-paths/medium | 256 | Qualified | 1.260 | 3.850 | 3.04 [3.01, 3.10] | Lokad.Jq faster |
| unicode-strings/medium | 256 | Qualified | 0.578 | 1.948 | 3.38 [3.36, 3.47] | Lokad.Jq faster |
| json-roundtrip/medium | 256 | Qualified | 0.497 | 1.780 | 3.58 [3.48, 3.63] | Lokad.Jq faster |
| identity-compact/large | 4096 | Qualified | 2.608 | 5.731 | 2.25 [2.18, 2.40] | Lokad.Jq faster |
| identity-pretty/large | 4096 | Qualified | 2.599 | 6.424 | 2.47 [2.39, 2.52] | Lokad.Jq faster |
| ndjson-project/large | 4096 | Qualified | 6.812 | 6.633 | 0.97 [0.95, 0.99] | jq faster |
| ndjson-select/large | 4096 | Qualified | 5.047 | 4.658 | 0.92 [0.91, 0.94] | jq faster |
| map-construct/large | 512 | Qualified | 0.876 | 1.955 | 2.22 [2.20, 2.28] | Lokad.Jq faster |
| reduce/large | 4096 | Qualified | 1.676 | 2.268 | 1.35 [1.32, 1.37] | Lokad.Jq faster |
| foreach/large | 4096 | Qualified | 3.061 | 2.913 | 0.95 [0.95, 0.96] | jq faster |
| sort-group/large | 512 | Qualified | 1.717 | 2.216 | 1.30 [1.26, 1.31] | Lokad.Jq faster |
| entries-update/large | 512 | Qualified | 1.718 | 4.761 | 2.81 [2.76, 2.84] | Lokad.Jq faster |
| walk-paths/large | 512 | Qualified | 2.592 | 6.343 | 2.47 [2.45, 2.52] | Lokad.Jq faster |
| unicode-strings/large | 1024 | Qualified | 2.321 | 3.859 | 1.66 [1.65, 1.67] | Lokad.Jq faster |
| json-roundtrip/large | 512 | Qualified | 1.002 | 2.343 | 2.34 [2.27, 2.36] | Lokad.Jq faster |
| startup-empty/control | 0 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |
| small-request/control | 1 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |

<!-- END GENERATED COMPARISON -->

## Optimization follow-up

The initial baseline used revision `ccf2dd317538dda12ea3d0b952667c495e857bac` on
the same AMD Linux VM, SDK/runtime and jq executable. The current report includes
stateful ordinary JSON input, reusable JSON output storage and shared surviving
`select` inputs. Parser progress avoids rebuilding incomplete document prefixes;
output still uses one awaited host append per result. Numeric/regex policies,
execution ceilings, workloads and the timing boundary remain unchanged.

The five original jq-faster cases are compared below. Before/current times are
medians from separate campaigns; paired confidence intervals apply to the current
Lokad.Jq-versus-jq comparisons in the generated table above.

| Large case (4,096 items) | Initial Lokad.Jq ms | Current Lokad.Jq ms | Current jq ms | Current result |
| --- | ---: | ---: | ---: | --- |
| identity-compact | 13.674 | 2.608 | 5.731 | Lokad.Jq faster |
| identity-pretty | 13.753 | 2.599 | 6.424 | Lokad.Jq faster |
| ndjson-project | 6.761 | 6.812 | 6.633 | jq faster |
| ndjson-select | 5.451 | 5.047 | 4.658 | jq faster |
| foreach | 3.520 | 3.061 | 2.913 | jq faster |

All 38 cases match the reference; 36 workload measurements qualify. Lokad.Jq is
faster in 33 measured workloads and jq in three. Both controls remain unqualified
because their managed sample batches fall below the duration floor. The original
baseline calibrated with one 40 ms batch; the current protocol confirms that
target with two consecutive batches, retaining the 20 ms eligibility floor and
all noise/deadline thresholds. An initial undersized calibration checkpoint and
all diagnostic/profiling runs are excluded from the published performance claims.

## Workload direction

The family selection is informed by the public
[jaq catalog](https://github.com/01mf02/jaq/blob/main/examples/benches.json) and
[jq-jit suite](https://github.com/m5d215/jq-jit/blob/main/bench/comprehensive.sh).
Filters, input generators and tooling are self-contained; neither suite nor its
datasets is vendored or required. Our bounded sizes and measurement method do
not establish equivalence to their published timings.
