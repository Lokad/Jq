# Benchmarks

Recorded measurements from 2026-10-05. Later memory-governor and execution
simplifications require fresh timing measurements.

<!-- BEGIN GENERATED COMPARISON -->
## Recorded comparison

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Ratio / 95% CI | Interpretation |
| --- | ---: | --- | ---: | ---: | --- | --- |
| identity-compact/small | 16 | Qualified | 0.011 | 1.301 | 116.14 [115.01, 117.42] | Lokad.Jq faster |
| identity-pretty/small | 16 | Qualified | 0.012 | 1.309 | 107.48 [107.17, 109.16] | Lokad.Jq faster |
| ndjson-project/small | 16 | Qualified | 0.029 | 1.325 | 45.86 [45.48, 46.88] | Lokad.Jq faster |
| ndjson-select/small | 16 | Qualified | 0.024 | 1.342 | 55.45 [54.83, 56.37] | Lokad.Jq faster |
| map-construct/small | 16 | Qualified | 0.031 | 1.351 | 44.42 [43.71, 44.95] | Lokad.Jq faster |
| reduce/small | 16 | Qualified | 0.011 | 1.313 | 115.36 [114.65, 117.11] | Lokad.Jq faster |
| foreach/small | 16 | Qualified | 0.016 | 1.314 | 81.51 [80.69, 82.40] | Lokad.Jq faster |
| sort-group/small | 16 | Qualified | 0.059 | 1.331 | 22.56 [22.38, 22.85] | Lokad.Jq faster |
| entries-update/small | 16 | Qualified | 0.058 | 1.569 | 26.93 [26.62, 27.29] | Lokad.Jq faster |
| walk-paths/small | 16 | Qualified | 0.081 | 1.525 | 18.74 [18.61, 19.28] | Lokad.Jq faster |
| unicode-strings/small | 16 | Qualified | 0.041 | 1.389 | 33.77 [33.36, 34.19] | Lokad.Jq faster |
| json-roundtrip/small | 16 | Qualified | 0.036 | 1.350 | 37.02 [36.30, 37.32] | Lokad.Jq faster |
| identity-compact/medium | 256 | Qualified | 0.117 | 1.449 | 12.50 [12.33, 12.67] | Lokad.Jq faster |
| identity-pretty/medium | 256 | Qualified | 0.133 | 1.520 | 11.63 [11.53, 11.72] | Lokad.Jq faster |
| ndjson-project/medium | 256 | Qualified | 0.397 | 1.609 | 4.07 [4.05, 4.12] | Lokad.Jq faster |
| ndjson-select/medium | 256 | Qualified | 0.302 | 1.532 | 5.05 [5.01, 5.15] | Lokad.Jq faster |
| map-construct/medium | 256 | Qualified | 0.397 | 1.626 | 4.09 [4.06, 4.11] | Lokad.Jq faster |
| reduce/medium | 256 | Qualified | 0.095 | 1.323 | 13.75 [13.64, 13.94] | Lokad.Jq faster |
| foreach/medium | 256 | Qualified | 0.181 | 1.396 | 7.73 [7.65, 7.77] | Lokad.Jq faster |
| sort-group/medium | 256 | Qualified | 0.855 | 1.806 | 2.14 [2.08, 2.16] | Lokad.Jq faster |
| entries-update/medium | 256 | Qualified | 0.868 | 3.246 | 3.76 [3.74, 3.80] | Lokad.Jq faster |
| walk-paths/medium | 256 | Qualified | 1.330 | 4.090 | 3.09 [3.04, 3.13] | Lokad.Jq faster |
| unicode-strings/medium | 256 | Qualified | 0.598 | 2.031 | 3.41 [3.40, 3.49] | Lokad.Jq faster |
| json-roundtrip/medium | 256 | Qualified | 0.520 | 1.858 | 3.55 [3.53, 3.61] | Lokad.Jq faster |
| identity-compact/large | 4096 | Qualified | 2.738 | 6.043 | 2.21 [2.14, 2.31] | Lokad.Jq faster |
| identity-pretty/large | 4096 | Qualified | 2.770 | 6.776 | 2.45 [2.34, 2.51] | Lokad.Jq faster |
| ndjson-project/large | 4096 | Qualified | 6.495 | 6.941 | 1.07 [1.06, 1.07] | Lokad.Jq faster |
| ndjson-select/large | 4096 | Qualified | 4.766 | 4.888 | 1.03 [1.02, 1.03] | Lokad.Jq faster |
| map-construct/large | 512 | Qualified | 0.823 | 2.013 | 2.44 [2.41, 2.47] | Lokad.Jq faster |
| reduce/large | 4096 | Qualified | 1.506 | 2.259 | 1.50 [1.45, 1.51] | Lokad.Jq faster |
| foreach/large | 4096 | Qualified | 2.848 | 2.896 | 1.02 [1.01, 1.03] | Lokad.Jq faster |
| sort-group/large | 512 | Qualified | 1.758 | 2.200 | 1.25 [1.24, 1.27] | Lokad.Jq faster |
| entries-update/large | 512 | Qualified | 1.684 | 4.754 | 2.81 [2.78, 2.85] | Lokad.Jq faster |
| walk-paths/large | 512 | Qualified | 2.589 | 6.398 | 2.47 [2.46, 2.54] | Lokad.Jq faster |
| unicode-strings/large | 1024 | Qualified | 2.357 | 4.018 | 1.70 [1.69, 1.73] | Lokad.Jq faster |
| json-roundtrip/large | 512 | Qualified | 1.007 | 2.351 | 2.33 [2.32, 2.36] | Lokad.Jq faster |
| startup-empty/control | 0 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |
| small-request/control | 1 | Unqualified | — | — | — | Need at least nine valid pairs with both samples at least 20 ms. |

<!-- END GENERATED COMPARISON -->

## Managed allocations

| Case / scale | Before bytes | After bytes | Reduction |
| --- | ---: | ---: | ---: |
| identity-compact/large | 4,601,243 | 4,601,007 | 0.0% |
| identity-pretty/large | 4,601,128 | 4,600,994 | 0.0% |
| ndjson-project/large | 17,711,784 | 15,416,300 | 13.0% |
| ndjson-select/large | 13,167,216 | 12,241,592 | 7.0% |
| map-construct/large | 2,441,612 | 2,151,796 | 11.9% |
| reduce/large | 5,784,988 | 5,160,216 | 10.8% |
| foreach/large | 8,145,960 | 7,225,308 | 11.3% |
| sort-group/large | 3,340,268 | 3,247,208 | 2.8% |
| entries-update/large | 5,263,592 | 5,158,260 | 2.0% |
| walk-paths/large | 7,132,600 | 6,785,460 | 4.9% |
| unicode-strings/large | 5,375,332 | 5,192,016 | 3.4% |
| json-roundtrip/large | 2,242,044 | 2,241,236 | 0.0% |
| wide-object/diagnostic | 4,650,824 | 3,462,632 | 25.5% |
| small-filter/diagnostic | 54,912 | 45,808 | 16.6% |
| raw-strings/diagnostic | 3,945,204 | 3,400,954 | 13.8% |
| sorted-ascii/diagnostic | 533,920 | 533,680 | 0.0% |
| scalar-pipeline/diagnostic | 9,883,450 | 9,324,750 | 5.7% |
| buffered-scalars/diagnostic | 3,188,810 | 2,893,716 | 9.3% |

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `9f223bf83648dfc637ebe7eaa21658274a67bb64`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-05T17:50:46.7722658+00:00. Completed 38/38 requested cases.

.NET 10.0.8; SDK 10.0.204; Ubuntu 24.04.4 LTS; AMD EPYC 9V45 96-Core Processor; 4 logical processors.

Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.

Ratio is jq time divided by Lokad.Jq time; above 1 favors Lokad.Jq. CI is a paired-bootstrap 95% interval.

<!-- END GENERATED COMPARISON PROVENANCE -->

## Allocation method

Median managed bytes per execution, from baseline `df53365` to `9f223bf` on
the same machine. Includes binding, compilation, execution, output checksums and
a per-call deadline; excludes input generation and report serialization.

These totals measure managed allocation, rather than peak heap or jq process
memory. All 44 allocation cases preserve input hashes and exact output digests.
Most cases use three warmed batches of eight; the buffered-scalar baseline uses
eleven calibrated batches. Raw samples and collection counts stay in artifacts.

## Scope and method

The machine was an Azure `Standard_F4as_v7`: four AMD cores, 16 GiB RAM,
Ubuntu 24.04.4 LTS, .NET 10.0.8 and SDK 10.0.204. All 38 non-regex comparisons
match jq 1.8.2; 36 workload timings qualify and both startup controls remain
unqualified. Small inputs include CLI launch costs. No overall speedup is calculated.

The catalog covers twelve families at three sizes plus two controls. Small and
medium sizes are 16 and 256. Large identity, NDJSON, reduction and foreach cases
use 4,096; Unicode uses 1,024; construction, sorting, updates, paths and round
trips use 512. These bounded inputs do not establish full jq compatibility.

Rendering checks compare exact bytes; other checks compare the complete ordered
JSON stream. Errors, stderr, timeouts and mismatches reject a measurement.
Both lanes consume and checksum output; jq also pays process and pipe costs.
Reference execution uses an isolated home and no `JQ_LIBRARY_PATH`.

Timing qualification requires a clean committed Release checkout whose assembly
revision matches HEAD. Background CPU must stay below 3% median and 5% maximum.
Each lane warms for at least 32 calls and one second, then calibrates with two
consecutive batches of at least 40 ms. Eleven alternating pairs are the default
(`--pairs` accepts 9-31). Each call has a 15-second deadline, each case 120 seconds.

Published ratios require samples of at least 20 ms, lane interquartile spread and
order effects within 10%, and a 95% bootstrap interval no wider than 15%.
Intervals crossing parity claim no winner. Checkpoints retain provenance, input/
output digests, quiet checks and every sample. Unqualified rows publish no ratio.

## Run the tools

Use an independently installed jq 1.8.2 for reference comparisons. The runner
never downloads jq and rejects executables under `external/`. Ordinary tests
and CI need no reference executable. Run timing probes on an otherwise idle machine.

```text
# Local BenchmarkDotNet timings, or correctness-only smoke/stress checks
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --smoke
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --stress

# Correctness, timing qualification, and report generation
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --check-machine
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ --mode qualify --output artifacts/benchmarks/qualification.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-report artifacts/benchmarks/qualification.json

# Managed allocations and shared-governor diagnostics
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --allocations
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --workers 1,4
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --mode cost --iterations 128 --workers 1,4
```

`--case NAME`, `--scale SCALE` and `--output artifacts/PATH`
filter comparison/allocation runs; governor diagnostics accept `--case` and
`--output`. Reports and traces remain ignored. The renderer replaces only the
marked comparison table and provenance sections above.

## Governor diagnostics

The default `counts` mode runs once per worker, checking independent stdout/stderr
digests, admission calls and complete refunds under a shared 256 MiB allowance.
Reports include the library hash, peak/end reservations, active scopes and completed
checkpoints. Requested workers do not prove simultaneous occupancy.

`--mode cost` adds separate uninstrumented timing/allocation lanes. Each warms for
at least eight calls and one second, then collects three alternating pairs.
`--iterations` controls those batches and requires cost mode. These local samples
do not qualify general speedups or another host's contention costs.
Provider storage is not estimated by Jq; Utf8Regex 0.3.0 has no shared-budget hook.
See [runtime memory rules](RUNTIME.md#memory-admission) for ownership and credit bounds.

## Recorded artifact checksums

- Allocation baseline: `9a6d4fb6a24e5532fee4c07830b9b5c027bf425543587d201f617a2e0d56dc68`.
- Final allocations: `13909dee06ee774485c8e55822e3a3807a47838f0b40dff0d936a1605a6cf76b`.
- Reference qualification: `14f76df94f63310b935c863d67b4752ed4a5726f77c64213022f9ada8a943cba`.
