# Benchmarks

Warm embedded execution on .NET 11, including argument binding, compilation,
UTF-8 parsing, evaluation and rendering, compared with jq CLI execution including
process startup and pipes. `Lokad.Jq / jq` divides the mean times; lower is better.
These command-level ratios do not establish that evaluation is universally faster.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |
| --- | ---: | --- | ---: | ---: | ---: |
| nested-extraction/large | 1024 | Qualified | 49.496 | 52.002 | 0.952 |
| api-shaping/large | 1024 | Qualified | 5.204 | 5.600 | 0.929 |
| snapshot-differences/large | 1024 | Qualified | 6.003 | 7.008 | 0.856 |
| ndjson-project/large | 4096 | Qualified | 5.638 | 6.633 | 0.850 |
| nested-extraction/medium | 128 | Qualified | 6.180 | 7.632 | 0.810 |
| foreach/large | 4096 | Qualified | 2.322 | 2.973 | 0.781 |
| incident-logs/large | 4096 | Qualified | 14.916 | 21.182 | 0.704 |
| ndjson-select/large | 4096 | Qualified | 3.216 | 4.711 | 0.683 |
| configuration-updates/large | 1024 | Qualified | 5.100 | 7.659 | 0.666 |
| inventory-enrichment/large | 1024 | Qualified | 2.783 | 4.187 | 0.665 |
| regex-log-cleanup/large | 1024 | Qualified | 13.251 | 22.429 | 0.591 |
| unicode-strings/large | 1024 | Qualified | 2.160 | 3.896 | 0.554 |
| reduce/large | 4096 | Qualified | 1.140 | 2.338 | 0.488 |
| identity-compact/large | 4096 | Qualified | 2.681 | 5.887 | 0.455 |
| identity-pretty/large | 4096 | Qualified | 2.782 | 6.589 | 0.422 |
| operational-summary/large | 1024 | Qualified | 1.714 | 4.133 | 0.415 |
| regex-log-cleanup/medium | 128 | Qualified | 1.761 | 4.311 | 0.408 |
| json-roundtrip/large | 512 | Qualified | 0.901 | 2.389 | 0.377 |
| entries-update/large | 512 | Qualified | 1.811 | 4.810 | 0.377 |
| nested-extraction/small | 16 | Qualified | 0.753 | 2.245 | 0.335 |
| walk-paths/large | 512 | Qualified | 2.126 | 6.449 | 0.330 |
| sort-group/large | 512 | Qualified | 0.710 | 2.262 | 0.314 |
| snapshot-differences/medium | 128 | Qualified | 0.627 | 2.071 | 0.303 |
| api-shaping/medium | 128 | Qualified | 0.577 | 1.909 | 0.302 |
| entries-update/medium | 256 | Qualified | 0.908 | 3.168 | 0.287 |
| configuration-updates/medium | 128 | Qualified | 0.583 | 2.127 | 0.274 |
| unicode-strings/medium | 256 | Qualified | 0.540 | 1.997 | 0.271 |
| walk-paths/medium | 256 | Qualified | 1.051 | 3.886 | 0.270 |
| json-roundtrip/medium | 256 | Qualified | 0.450 | 1.834 | 0.245 |
| map-construct/large | 512 | Qualified | 0.477 | 1.987 | 0.240 |
| incident-logs/medium | 128 | Qualified | 0.502 | 2.147 | 0.234 |
| ndjson-project/medium | 256 | Qualified | 0.357 | 1.645 | 0.217 |
| inventory-enrichment/medium | 128 | Qualified | 0.346 | 1.731 | 0.200 |
| sort-group/medium | 256 | Qualified | 0.356 | 1.808 | 0.197 |
| operational-summary/medium | 128 | Qualified | 0.267 | 1.735 | 0.154 |
| map-construct/medium | 256 | Qualified | 0.239 | 1.663 | 0.144 |
| regex-log-cleanup/small | 16 | Qualified | 0.283 | 2.000 | 0.142 |
| ndjson-select/medium | 256 | Qualified | 0.204 | 1.530 | 0.134 |
| foreach/medium | 256 | Qualified | 0.144 | 1.395 | 0.103 |
| identity-pretty/medium | 256 | Qualified | 0.157 | 1.552 | 0.101 |
| identity-compact/medium | 256 | Qualified | 0.149 | 1.517 | 0.098 |
| snapshot-differences/small | 16 | Qualified | 0.091 | 1.497 | 0.061 |
| configuration-updates/small | 16 | Qualified | 0.086 | 1.518 | 0.056 |
| reduce/medium | 256 | Qualified | 0.071 | 1.383 | 0.052 |
| incident-logs/small | 16 | Qualified | 0.082 | 1.597 | 0.051 |
| api-shaping/small | 16 | Qualified | 0.075 | 1.491 | 0.050 |
| walk-paths/small | 16 | Qualified | 0.069 | 1.609 | 0.043 |
| operational-summary/small | 16 | Qualified | 0.055 | 1.445 | 0.038 |
| entries-update/small | 16 | Qualified | 0.059 | 1.541 | 0.038 |
| inventory-enrichment/small | 16 | Qualified | 0.050 | 1.440 | 0.035 |
| unicode-strings/small | 16 | Qualified | 0.037 | 1.389 | 0.027 |
| sort-group/small | 16 | Qualified | 0.035 | 1.382 | 0.025 |
| json-roundtrip/small | 16 | Qualified | 0.031 | 1.350 | 0.023 |
| ndjson-project/small | 16 | Qualified | 0.025 | 1.330 | 0.019 |
| map-construct/small | 16 | Qualified | 0.019 | 1.349 | 0.014 |
| ndjson-select/small | 16 | Qualified | 0.016 | 1.349 | 0.012 |
| foreach/small | 16 | Qualified | 0.012 | 1.315 | 0.009 |
| identity-pretty/small | 16 | Qualified | 0.012 | 1.309 | 0.009 |
| identity-compact/small | 16 | Qualified | 0.012 | 1.306 | 0.009 |
| reduce/small | 16 | Qualified | 0.008 | 1.331 | 0.006 |
| small-request/control | 1 | Unqualified | — | — | — |
| startup-empty/control | 0 | Unqualified | — | — | — |

- small-request/control: Need at least nine valid pairs with both samples at least 20 ms.
- startup-empty/control: Need at least nine valid pairs with both samples at least 20 ms.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 138309284 | 203 / 12 / 4 |
| incident-logs/large | 36894712 | 53 / 6 / 0 |
| regex-log-cleanup/large | 27039000 | 38 / 0 / 0 |
| configuration-updates/large | 12247960 | 19 / 15 / 1 |
| api-shaping/large | 11440764 | 16 / 14 / 0 |
| ndjson-project/large | 10820876 | 16 / 1 / 0 |
| snapshot-differences/large | 10490324 | 15 / 14 / 1 |
| ndjson-select/large | 6382220 | 10 / 1 / 0 |
| inventory-enrichment/large | 5882860 | 9 / 4 / 0 |
| walk-paths/large | 5801364 | 8 / 3 / 0 |
| unicode-strings/large | 5637076 | 8 / 2 / 0 |
| entries-update/large | 5131496 | 8 / 3 / 0 |
| foreach/large | 4532416 | 6 / 2 / 0 |
| reduce/large | 3941768 | 6 / 2 / 0 |
| identity-pretty/large | 2845228 | 6 / 4 / 2 |
| identity-compact/large | 2845201 | 6 / 5 / 2 |
| operational-summary/large | 2803828 | 4 / 2 / 0 |
| scalar-pipeline/diagnostic | 2719996 | 4 / 3 / 0 |
| json-roundtrip/large | 1891028 | 3 / 1 / 0 |
| buffered-scalars/diagnostic | 1381804 | 2 / 0 / 0 |
| map-construct/large | 965924 | 2 / 0 / 0 |
| sort-group/large | 903396 | 1 / 1 / 0 |
| wide-object/diagnostic | 684368 | 1 / 0 / 0 |
| raw-strings/diagnostic | 658480 | 1 / 0 / 0 |
| sorted-ascii/diagnostic | 227544 | 0 / 0 / 0 |
| small-filter/diagnostic | 38256 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `5890277bb5227710f99a92709bdc60248dd3268a`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `dbfd6091f2841cc8448b912d3d62150e56f41e4a10e426408c0d914d87f9adee`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `5890277bb5227710f99a92709bdc60248dd3268a`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-31T17:39:20.7024613+00:00. Completed 62/62 requested cases.

.NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148; Ubuntu 24.04.5 LTS; AMD EPYC 9V45 96-Core Processor; 4 logical processors.

Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.

Regex provider: Lokad.Utf8Regex.Pcre2 0.4.0+3d120bdcbbd49e25e9a3c6af7d408e34d239adca; SHA-256 acab389c8aa10e04b8b6034427cb152d73421d479f08a0b1229d7e3f443c9d2a.

Times are arithmetic mean milliseconds per invocation. Lokad.Jq / jq divides those means; lower is better, with 1 at parity. Rows descend by this ratio; unqualified rows follow.

<!-- END GENERATED COMPARISON PROVENANCE -->

## Coverage

The comparison catalog has twenty families at three sizes, plus startup and
single-request controls. Basic cases cover compact/pretty identity, NDJSON projection/selection,
object construction, reduction, foreach, sorting/grouping, entry updates, tree
walking/paths, Unicode strings and JSON round trips.

Compound cases cover paged API shaping, NDJSON incident summaries, indexed
inventory enrichment, grouped operational reports, layered configuration updates,
snapshot differences, nested TSV extraction and adapted regex log cleanup. Their
fixtures include missing fields, duplicate IDs, skewed groups and Unicode. Inputs
and independently constructed expectations are prepared outside measurement.

Local BenchmarkDotNet cases also cover command recognition and reused commands.
Separate `TokenBenchmarks` vary large JSON string size, escapes and host read
fragmentation; they do not change the comparison corpus above.
Allocation diagnostics add wide objects, raw strings, sorted ASCII, scalar
pipelines and buffered scalars. Governor workloads exercise memory admission,
retention/refunds and streaming boundaries separately from timing comparisons.

## Reading results

Timing uses three warm-up passes, each with at least 32 invocations and one
second per lane, followed by two seconds of settling. Calibration follows these
passes; each case retains eleven alternating pairs. The campaign keeps the first
completed result for each case and retries quiet-gate pauses only. Raw checkpoints
retain their original timestamps and hashes. Allocation results use workstation GC.

Correctness checks compare exact rendering bytes or complete ordered JSON values,
exit status and separate stderr. Failures, mismatches and timeouts reject results.
Timing qualification requires a clean Release checkout, a quiet machine and at
least nine valid paired samples. Raw reports retain samples, qualification bounds,
source/library identity, environment and input/output digests. Only qualified
rows receive timings and ratios.

## Run

Use an independently installed jq 1.8.2 for opt-in reference comparisons. The
runner never downloads jq and rejects executables under external/. Ordinary
checks need no reference executable. Run timing measurements on an idle machine.

```text
# Local timings and correctness checks
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --smoke
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --stress

# Reference correctness, qualified timing and documentation rendering
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --check-machine
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --campaign --jq PATH_TO_JQ --output artifacts/benchmarks/campaign
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-report artifacts/benchmarks/campaign/qualification.json

# Absolute managed allocations and governor diagnostics
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --allocations
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-allocations artifacts/benchmarks/allocations.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --workers 1,4
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --mode cost --iterations 128 --workers 1,4
```

Comparison/allocation runs accept `--case NAME`, `--scale SCALE` and `--output`;
governor runs accept `--case` and `--output`. Reports and traces remain ignored.
The renderers update the marked result and provenance sections.
`--campaign` retains immutable attempts and retries quiet pauses. Repeat the same
command after interruption to resume; a changed source, binary, runtime, provider,
reference or workload requires a new directory. Completed unqualified cases stay
unqualified; exit 1 can therefore accompany a complete campaign. Raw schema 7
names paired-median qualification bounds separately from the published mean ratio.

Governor `counts` mode checks stdout/stderr digests, admission calls and complete
refunds under a shared 256 MiB allowance. Requested workers do not prove concurrent
occupancy. `cost` mode adds separate local timing/allocation samples; those do not
qualify general speedups. Provider storage remains outside shared accounting when
the selected adapter's provider has no host-budget hook. See
[runtime memory rules](RUNTIME.md#memory-admission) for ownership and credit bounds.
