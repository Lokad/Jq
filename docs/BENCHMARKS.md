# Benchmarks

Hosted jq execution on .NET 11: argument binding and compilation, UTF-8 parsing,
evaluation, rendering and consumed output. `Lokad.Jq / jq` divides the mean times;
lower is better.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |
| --- | ---: | --- | ---: | ---: | ---: |
| snapshot-differences/large | 1024 | Qualified | 6.743 | 7.032 | 0.959 |
| api-shaping/large | 1024 | Qualified | 5.301 | 5.628 | 0.942 |
| nested-extraction/large | 1024 | Qualified | 47.482 | 51.753 | 0.917 |
| incident-logs/large | 4096 | Qualified | 19.061 | 21.149 | 0.901 |
| ndjson-project/large | 4096 | Qualified | 5.608 | 6.607 | 0.849 |
| configuration-updates/large | 1024 | Qualified | 6.085 | 7.690 | 0.791 |
| nested-extraction/medium | 128 | Qualified | 5.951 | 7.660 | 0.777 |
| ndjson-select/large | 4096 | Qualified | 3.299 | 4.704 | 0.701 |
| inventory-enrichment/large | 1024 | Qualified | 2.923 | 4.202 | 0.696 |
| foreach/large | 4096 | Qualified | 2.033 | 2.961 | 0.687 |
| unicode-strings/large | 1024 | Qualified | 2.239 | 3.890 | 0.576 |
| regex-log-cleanup/large | 1024 | Qualified | 12.826 | 22.484 | 0.570 |
| reduce/large | 4096 | Qualified | 1.300 | 2.324 | 0.560 |
| operational-summary/large | 1024 | Qualified | 2.028 | 4.147 | 0.489 |
| identity-compact/large | 4096 | Qualified | 2.868 | 5.897 | 0.486 |
| identity-pretty/large | 4096 | Qualified | 2.961 | 6.586 | 0.450 |
| json-roundtrip/large | 512 | Qualified | 0.986 | 2.382 | 0.414 |
| regex-log-cleanup/medium | 128 | Qualified | 1.698 | 4.298 | 0.395 |
| entries-update/large | 512 | Qualified | 1.844 | 4.826 | 0.382 |
| sort-group/large | 512 | Qualified | 0.822 | 2.257 | 0.364 |
| snapshot-differences/medium | 128 | Qualified | 0.697 | 2.067 | 0.337 |
| walk-paths/large | 512 | Qualified | 2.160 | 6.419 | 0.337 |
| nested-extraction/small | 16 | Qualified | 0.715 | 2.226 | 0.321 |
| configuration-updates/medium | 128 | Qualified | 0.683 | 2.146 | 0.318 |
| api-shaping/medium | 128 | Qualified | 0.584 | 1.905 | 0.307 |
| incident-logs/medium | 128 | Qualified | 0.635 | 2.158 | 0.294 |
| entries-update/medium | 256 | Qualified | 0.911 | 3.153 | 0.289 |
| unicode-strings/medium | 256 | Qualified | 0.547 | 1.980 | 0.276 |
| walk-paths/medium | 256 | Qualified | 1.066 | 3.892 | 0.274 |
| map-construct/large | 512 | Qualified | 0.541 | 1.995 | 0.271 |
| json-roundtrip/medium | 256 | Qualified | 0.495 | 1.833 | 0.270 |
| sort-group/medium | 256 | Qualified | 0.410 | 1.797 | 0.228 |
| ndjson-project/medium | 256 | Qualified | 0.350 | 1.631 | 0.214 |
| inventory-enrichment/medium | 128 | Qualified | 0.344 | 1.720 | 0.200 |
| operational-summary/medium | 128 | Qualified | 0.302 | 1.742 | 0.173 |
| map-construct/medium | 256 | Qualified | 0.270 | 1.654 | 0.163 |
| ndjson-select/medium | 256 | Qualified | 0.208 | 1.522 | 0.137 |
| regex-log-cleanup/small | 16 | Qualified | 0.269 | 1.997 | 0.135 |
| identity-pretty/medium | 256 | Qualified | 0.163 | 1.550 | 0.105 |
| identity-compact/medium | 256 | Qualified | 0.157 | 1.505 | 0.105 |
| foreach/medium | 256 | Qualified | 0.123 | 1.407 | 0.088 |
| snapshot-differences/small | 16 | Qualified | 0.095 | 1.500 | 0.063 |
| configuration-updates/small | 16 | Qualified | 0.095 | 1.514 | 0.063 |
| incident-logs/small | 16 | Qualified | 0.092 | 1.583 | 0.058 |
| reduce/medium | 256 | Qualified | 0.077 | 1.369 | 0.056 |
| api-shaping/small | 16 | Qualified | 0.076 | 1.491 | 0.051 |
| walk-paths/small | 16 | Qualified | 0.069 | 1.573 | 0.044 |
| operational-summary/small | 16 | Qualified | 0.059 | 1.432 | 0.041 |
| entries-update/small | 16 | Qualified | 0.059 | 1.543 | 0.038 |
| inventory-enrichment/small | 16 | Qualified | 0.050 | 1.426 | 0.035 |
| sort-group/small | 16 | Qualified | 0.038 | 1.373 | 0.027 |
| unicode-strings/small | 16 | Qualified | 0.037 | 1.382 | 0.027 |
| json-roundtrip/small | 16 | Qualified | 0.033 | 1.349 | 0.025 |
| ndjson-project/small | 16 | Qualified | 0.024 | 1.320 | 0.018 |
| map-construct/small | 16 | Qualified | 0.020 | 1.339 | 0.015 |
| ndjson-select/small | 16 | Qualified | 0.017 | 1.330 | 0.013 |
| identity-pretty/small | 16 | Qualified | 0.013 | 1.306 | 0.010 |
| identity-compact/small | 16 | Qualified | 0.012 | 1.301 | 0.009 |
| foreach/small | 16 | Qualified | 0.011 | 1.306 | 0.009 |
| reduce/small | 16 | Qualified | 0.009 | 1.299 | 0.007 |
| small-request/control | 1 | Unqualified | — | — | — |
| startup-empty/control | 0 | Unqualified | — | — | — |

- small-request/control: Need at least nine valid pairs with both samples at least 20 ms.
- startup-empty/control: Need at least nine valid pairs with both samples at least 20 ms.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 143615741 | 211 / 12 / 4 |
| incident-logs/large | 61697784 | 89 / 8 / 0 |
| regex-log-cleanup/large | 28465176 | 41 / 0 / 0 |
| configuration-updates/large | 16192232 | 24 / 23 / 1 |
| ndjson-project/large | 12196536 | 17 / 2 / 0 |
| api-shaping/large | 12106280 | 17 / 15 / 0 |
| snapshot-differences/large | 11924864 | 18 / 17 / 1 |
| ndjson-select/large | 7380768 | 11 / 1 / 0 |
| inventory-enrichment/large | 6465868 | 9 / 5 / 0 |
| walk-paths/large | 6288788 | 9 / 5 / 0 |
| unicode-strings/large | 6114516 | 9 / 3 / 0 |
| entries-update/large | 5700452 | 8 / 3 / 0 |
| foreach/large | 4663224 | 7 / 3 / 0 |
| reduce/large | 4400248 | 6 / 3 / 0 |
| operational-summary/large | 3313128 | 5 / 4 / 0 |
| scalar-pipeline/diagnostic | 3251960 | 5 / 4 / 0 |
| identity-compact/large | 3246172 | 5 / 4 / 1 |
| identity-pretty/large | 3246160 | 6 / 6 / 2 |
| json-roundtrip/large | 2193788 | 3 / 1 / 0 |
| buffered-scalars/diagnostic | 1479796 | 2 / 0 / 0 |
| sort-group/large | 1396508 | 2 / 1 / 0 |
| map-construct/large | 1338084 | 2 / 1 / 0 |
| raw-strings/diagnostic | 1182500 | 2 / 1 / 0 |
| wide-object/diagnostic | 1039360 | 1 / 0 / 0 |
| sorted-ascii/diagnostic | 253888 | 0 / 0 / 0 |
| small-filter/diagnostic | 38672 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `466ab50244e56daab201ed07b876eafde605beeb`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `628104bdee32b945759cefdb2a7f01c19b41fd36fbdcdc87e01220589eda8af6`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `466ab50244e56daab201ed07b876eafde605beeb`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-29T16:10:04.4313172+00:00. Completed 62/62 requested cases.

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
