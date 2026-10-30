# Benchmarks

Hosted jq execution on .NET 11: argument binding and compilation, UTF-8 parsing,
evaluation, rendering and consumed output. `Lokad.Jq / jq` divides the mean times;
lower is better.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |
| --- | ---: | --- | ---: | ---: | ---: |
| nested-extraction/large | 1024 | Qualified | 51.066 | 51.612 | 0.989 |
| api-shaping/large | 1024 | Qualified | 5.373 | 5.622 | 0.956 |
| snapshot-differences/large | 1024 | Qualified | 6.127 | 7.017 | 0.873 |
| incident-logs/large | 4096 | Qualified | 17.996 | 21.126 | 0.852 |
| ndjson-project/large | 4096 | Qualified | 5.542 | 6.607 | 0.839 |
| nested-extraction/medium | 128 | Qualified | 6.396 | 7.649 | 0.836 |
| foreach/large | 4096 | Qualified | 2.337 | 2.961 | 0.789 |
| ndjson-select/large | 4096 | Qualified | 3.381 | 4.694 | 0.720 |
| configuration-updates/large | 1024 | Qualified | 5.400 | 7.670 | 0.704 |
| inventory-enrichment/large | 1024 | Qualified | 2.784 | 4.177 | 0.666 |
| regex-log-cleanup/large | 1024 | Qualified | 13.063 | 22.459 | 0.582 |
| unicode-strings/large | 1024 | Qualified | 2.235 | 3.880 | 0.576 |
| reduce/large | 4096 | Qualified | 1.122 | 2.321 | 0.483 |
| identity-compact/large | 4096 | Qualified | 2.767 | 5.893 | 0.469 |
| identity-pretty/large | 4096 | Qualified | 2.943 | 6.580 | 0.447 |
| operational-summary/large | 1024 | Qualified | 1.744 | 4.119 | 0.423 |
| regex-log-cleanup/medium | 128 | Qualified | 1.707 | 4.285 | 0.398 |
| entries-update/large | 512 | Qualified | 1.856 | 4.820 | 0.385 |
| json-roundtrip/large | 512 | Qualified | 0.916 | 2.392 | 0.383 |
| nested-extraction/small | 16 | Qualified | 0.776 | 2.229 | 0.348 |
| walk-paths/large | 512 | Qualified | 2.185 | 6.389 | 0.342 |
| sort-group/large | 512 | Qualified | 0.747 | 2.258 | 0.331 |
| snapshot-differences/medium | 128 | Qualified | 0.647 | 2.061 | 0.314 |
| api-shaping/medium | 128 | Qualified | 0.592 | 1.898 | 0.312 |
| entries-update/medium | 256 | Qualified | 0.923 | 3.155 | 0.293 |
| configuration-updates/medium | 128 | Qualified | 0.617 | 2.128 | 0.290 |
| unicode-strings/medium | 256 | Qualified | 0.553 | 1.989 | 0.278 |
| walk-paths/medium | 256 | Qualified | 1.082 | 3.892 | 0.278 |
| incident-logs/medium | 128 | Qualified | 0.591 | 2.156 | 0.274 |
| map-construct/large | 512 | Qualified | 0.505 | 1.996 | 0.253 |
| json-roundtrip/medium | 256 | Qualified | 0.458 | 1.829 | 0.250 |
| ndjson-project/medium | 256 | Qualified | 0.348 | 1.638 | 0.213 |
| sort-group/medium | 256 | Qualified | 0.372 | 1.804 | 0.206 |
| inventory-enrichment/medium | 128 | Qualified | 0.343 | 1.713 | 0.200 |
| operational-summary/medium | 128 | Qualified | 0.272 | 1.747 | 0.156 |
| map-construct/medium | 256 | Qualified | 0.253 | 1.654 | 0.153 |
| ndjson-select/medium | 256 | Qualified | 0.212 | 1.531 | 0.138 |
| regex-log-cleanup/small | 16 | Qualified | 0.273 | 1.991 | 0.137 |
| foreach/medium | 256 | Qualified | 0.146 | 1.402 | 0.104 |
| identity-pretty/medium | 256 | Qualified | 0.159 | 1.556 | 0.102 |
| identity-compact/medium | 256 | Qualified | 0.150 | 1.502 | 0.100 |
| snapshot-differences/small | 16 | Qualified | 0.095 | 1.541 | 0.062 |
| configuration-updates/small | 16 | Qualified | 0.092 | 1.561 | 0.059 |
| incident-logs/small | 16 | Qualified | 0.088 | 1.588 | 0.055 |
| api-shaping/small | 16 | Qualified | 0.077 | 1.482 | 0.052 |
| reduce/medium | 256 | Qualified | 0.070 | 1.373 | 0.051 |
| walk-paths/small | 16 | Qualified | 0.071 | 1.585 | 0.045 |
| operational-summary/small | 16 | Qualified | 0.056 | 1.446 | 0.039 |
| entries-update/small | 16 | Qualified | 0.060 | 1.538 | 0.039 |
| inventory-enrichment/small | 16 | Qualified | 0.049 | 1.425 | 0.035 |
| unicode-strings/small | 16 | Qualified | 0.038 | 1.387 | 0.028 |
| sort-group/small | 16 | Qualified | 0.036 | 1.376 | 0.026 |
| json-roundtrip/small | 16 | Qualified | 0.031 | 1.353 | 0.023 |
| ndjson-project/small | 16 | Qualified | 0.024 | 1.331 | 0.018 |
| map-construct/small | 16 | Qualified | 0.019 | 1.347 | 0.014 |
| ndjson-select/small | 16 | Qualified | 0.017 | 1.336 | 0.013 |
| foreach/small | 16 | Qualified | 0.013 | 1.305 | 0.010 |
| identity-pretty/small | 16 | Qualified | 0.012 | 1.315 | 0.009 |
| identity-compact/small | 16 | Qualified | 0.012 | 1.306 | 0.009 |
| reduce/small | 16 | Qualified | 0.008 | 1.310 | 0.006 |
| small-request/control | 1 | Unqualified | — | — | — |
| startup-empty/control | 0 | Unqualified | — | — | — |

- small-request/control: Need at least nine valid pairs with both samples at least 20 ms.
- startup-empty/control: Need at least nine valid pairs with both samples at least 20 ms.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 138358430 | 202 / 12 / 4 |
| incident-logs/large | 48980816 | 70 / 7 / 0 |
| regex-log-cleanup/large | 26923776 | 39 / 0 / 0 |
| configuration-updates/large | 12887351 | 21 / 16 / 2 |
| api-shaping/large | 11427148 | 16 / 15 / 0 |
| ndjson-project/large | 10820876 | 15 / 1 / 0 |
| snapshot-differences/large | 10585892 | 16 / 13 / 1 |
| ndjson-select/large | 6704652 | 9 / 1 / 0 |
| inventory-enrichment/large | 5882876 | 8 / 4 / 0 |
| walk-paths/large | 5801356 | 9 / 3 / 0 |
| unicode-strings/large | 5637044 | 8 / 2 / 0 |
| entries-update/large | 5168360 | 8 / 3 / 0 |
| foreach/large | 4434112 | 7 / 2 / 0 |
| reduce/large | 3843464 | 5 / 1 / 0 |
| scalar-pipeline/diagnostic | 2982132 | 4 / 3 / 0 |
| identity-compact/large | 2845232 | 5 / 5 / 2 |
| identity-pretty/large | 2844936 | 6 / 4 / 2 |
| operational-summary/large | 2814940 | 4 / 2 / 0 |
| json-roundtrip/large | 1886932 | 2 / 0 / 0 |
| buffered-scalars/diagnostic | 1381804 | 2 / 0 / 0 |
| map-construct/large | 1084708 | 2 / 1 / 0 |
| sort-group/large | 909800 | 1 / 0 / 0 |
| wide-object/diagnostic | 684368 | 1 / 0 / 0 |
| raw-strings/diagnostic | 658480 | 1 / 0 / 0 |
| sorted-ascii/diagnostic | 227544 | 0 / 0 / 0 |
| small-filter/diagnostic | 38312 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `5e972acb8a4beeccc9e1ce006b56c67b6bbd21f2`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `d124c01dd420f8087f56c372d8580073534f2ee9bb27856c7c3fbffb12d6abc9`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `5e972acb8a4beeccc9e1ce006b56c67b6bbd21f2`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-30T17:55:12.8260778+00:00. Completed 62/62 requested cases.

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
