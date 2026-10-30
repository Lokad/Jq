# Benchmarks

Hosted jq execution on .NET 11: argument binding and compilation, UTF-8 parsing,
evaluation, rendering and consumed output. `Lokad.Jq / jq` divides the mean times;
lower is better.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |
| --- | ---: | --- | ---: | ---: | ---: |
| snapshot-differences/large | 1024 | Qualified | 7.158 | 6.999 | 1.023 |
| nested-extraction/large | 1024 | Qualified | 50.644 | 51.700 | 0.980 |
| api-shaping/large | 1024 | Qualified | 5.470 | 5.617 | 0.974 |
| incident-logs/large | 4096 | Qualified | 19.762 | 21.106 | 0.936 |
| ndjson-project/large | 4096 | Qualified | 6.565 | 7.334 | 0.895 |
| configuration-updates/large | 1024 | Qualified | 6.478 | 7.673 | 0.844 |
| nested-extraction/medium | 128 | Qualified | 6.370 | 7.657 | 0.832 |
| foreach/large | 4096 | Qualified | 2.361 | 3.039 | 0.777 |
| ndjson-select/large | 4096 | Qualified | 3.945 | 5.267 | 0.749 |
| inventory-enrichment/large | 1024 | Qualified | 3.049 | 4.182 | 0.729 |
| unicode-strings/large | 1024 | Qualified | 2.353 | 3.888 | 0.605 |
| reduce/large | 4096 | Qualified | 1.444 | 2.475 | 0.583 |
| regex-log-cleanup/large | 1024 | Qualified | 13.011 | 22.435 | 0.580 |
| operational-summary/large | 1024 | Qualified | 2.097 | 4.143 | 0.506 |
| identity-compact/large | 4096 | Qualified | 3.280 | 6.561 | 0.500 |
| identity-pretty/large | 4096 | Qualified | 3.329 | 7.459 | 0.446 |
| json-roundtrip/large | 512 | Qualified | 1.030 | 2.382 | 0.432 |
| regex-log-cleanup/medium | 128 | Qualified | 1.683 | 4.279 | 0.393 |
| entries-update/large | 512 | Qualified | 1.881 | 4.816 | 0.391 |
| sort-group/large | 512 | Qualified | 0.840 | 2.259 | 0.372 |
| snapshot-differences/medium | 128 | Qualified | 0.733 | 2.058 | 0.356 |
| walk-paths/large | 512 | Qualified | 2.245 | 6.413 | 0.350 |
| nested-extraction/small | 16 | Qualified | 0.777 | 2.222 | 0.350 |
| configuration-updates/medium | 128 | Qualified | 0.707 | 2.131 | 0.332 |
| api-shaping/medium | 128 | Qualified | 0.614 | 1.900 | 0.323 |
| incident-logs/medium | 128 | Qualified | 0.666 | 2.145 | 0.311 |
| unicode-strings/medium | 256 | Qualified | 0.656 | 2.194 | 0.299 |
| entries-update/medium | 256 | Qualified | 1.035 | 3.528 | 0.293 |
| walk-paths/medium | 256 | Qualified | 1.236 | 4.340 | 0.285 |
| map-construct/large | 512 | Qualified | 0.612 | 2.158 | 0.283 |
| json-roundtrip/medium | 256 | Qualified | 0.567 | 2.053 | 0.276 |
| sort-group/medium | 256 | Qualified | 0.466 | 2.016 | 0.231 |
| ndjson-project/medium | 256 | Qualified | 0.369 | 1.626 | 0.227 |
| inventory-enrichment/medium | 128 | Qualified | 0.375 | 1.708 | 0.220 |
| operational-summary/medium | 128 | Qualified | 0.314 | 1.739 | 0.181 |
| map-construct/medium | 256 | Qualified | 0.309 | 1.835 | 0.169 |
| ndjson-select/medium | 256 | Qualified | 0.223 | 1.526 | 0.146 |
| regex-log-cleanup/small | 16 | Qualified | 0.271 | 1.993 | 0.136 |
| identity-pretty/medium | 256 | Qualified | 0.168 | 1.547 | 0.109 |
| identity-compact/medium | 256 | Qualified | 0.159 | 1.497 | 0.106 |
| foreach/medium | 256 | Qualified | 0.158 | 1.566 | 0.101 |
| snapshot-differences/small | 16 | Qualified | 0.099 | 1.496 | 0.066 |
| configuration-updates/small | 16 | Qualified | 0.099 | 1.507 | 0.066 |
| reduce/medium | 256 | Qualified | 0.095 | 1.527 | 0.062 |
| incident-logs/small | 16 | Qualified | 0.093 | 1.587 | 0.059 |
| api-shaping/small | 16 | Qualified | 0.079 | 1.482 | 0.053 |
| walk-paths/small | 16 | Qualified | 0.072 | 1.577 | 0.045 |
| operational-summary/small | 16 | Qualified | 0.061 | 1.435 | 0.042 |
| entries-update/small | 16 | Qualified | 0.060 | 1.536 | 0.039 |
| inventory-enrichment/small | 16 | Qualified | 0.054 | 1.421 | 0.038 |
| unicode-strings/small | 16 | Qualified | 0.041 | 1.383 | 0.029 |
| sort-group/small | 16 | Qualified | 0.039 | 1.366 | 0.029 |
| json-roundtrip/small | 16 | Qualified | 0.034 | 1.348 | 0.026 |
| ndjson-project/small | 16 | Qualified | 0.026 | 1.379 | 0.019 |
| map-construct/small | 16 | Qualified | 0.021 | 1.343 | 0.016 |
| ndjson-select/small | 16 | Qualified | 0.017 | 1.327 | 0.013 |
| identity-pretty/small | 16 | Qualified | 0.013 | 1.305 | 0.010 |
| foreach/small | 16 | Qualified | 0.012 | 1.304 | 0.009 |
| identity-compact/small | 16 | Qualified | 0.012 | 1.308 | 0.009 |
| reduce/small | 16 | Qualified | 0.009 | 1.298 | 0.007 |
| small-request/control | 1 | Unqualified | — | — | — |
| startup-empty/control | 0 | Unqualified | — | — | — |

- small-request/control: Need at least nine valid pairs with both samples at least 20 ms.
- startup-empty/control: Need at least nine valid pairs with both samples at least 20 ms.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 143615770 | 210 / 12 / 4 |
| incident-logs/large | 61697808 | 89 / 9 / 0 |
| regex-log-cleanup/large | 28465200 | 41 / 0 / 0 |
| configuration-updates/large | 16192256 | 24 / 23 / 1 |
| ndjson-project/large | 12196560 | 18 / 1 / 0 |
| api-shaping/large | 12106304 | 18 / 16 / 0 |
| snapshot-differences/large | 11924888 | 18 / 17 / 1 |
| ndjson-select/large | 7380764 | 11 / 1 / 0 |
| inventory-enrichment/large | 6465892 | 9 / 4 / 0 |
| walk-paths/large | 6288812 | 9 / 5 / 0 |
| unicode-strings/large | 6114540 | 9 / 3 / 0 |
| entries-update/large | 5700476 | 8 / 3 / 0 |
| foreach/large | 4663248 | 7 / 3 / 0 |
| reduce/large | 4400272 | 7 / 3 / 0 |
| operational-summary/large | 3313124 | 5 / 3 / 0 |
| scalar-pipeline/diagnostic | 3251984 | 5 / 4 / 0 |
| identity-compact/large | 3246189 | 6 / 6 / 2 |
| identity-pretty/large | 3246181 | 6 / 5 / 2 |
| json-roundtrip/large | 2193812 | 4 / 2 / 0 |
| buffered-scalars/diagnostic | 1479820 | 2 / 0 / 0 |
| sort-group/large | 1396532 | 2 / 1 / 0 |
| map-construct/large | 1338108 | 2 / 1 / 0 |
| raw-strings/diagnostic | 1182496 | 1 / 1 / 0 |
| wide-object/diagnostic | 1039384 | 2 / 0 / 0 |
| sorted-ascii/diagnostic | 253912 | 0 / 0 / 0 |
| small-filter/diagnostic | 38696 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `e52f5a5aaf4d93f834793e7dfa2d6e3bf7c9d5f8`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `7488114da728ae448a400445134d8e5f200c852d9c4d876e942575378446415e`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `e52f5a5aaf4d93f834793e7dfa2d6e3bf7c9d5f8`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-30T09:36:48.0989091+00:00. Completed 62/62 requested cases.

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
