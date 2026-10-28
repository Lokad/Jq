# Benchmarks

Hosted jq execution on .NET 11: argument binding and compilation, UTF-8 parsing,
evaluation, rendering and consumed output. `Lokad.Jq / jq` divides the mean times;
lower is better.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

| Case / scale | Size | Status | Lokad.Jq ms | jq ms | Lokad.Jq / jq |
| --- | ---: | --- | ---: | ---: | ---: |
| snapshot-differences/large | 1024 | Qualified | 9.994 | 7.746 | 1.290 |
| incident-logs/large | 4096 | Qualified | 25.579 | 23.094 | 1.108 |
| api-shaping/large | 1024 | Qualified | 6.582 | 6.085 | 1.082 |
| ndjson-select/large | 4096 | Qualified | 5.630 | 5.215 | 1.080 |
| ndjson-project/large | 4096 | Qualified | 7.656 | 7.147 | 1.071 |
| configuration-updates/large | 1024 | Qualified | 8.322 | 8.368 | 0.994 |
| nested-extraction/large | 1024 | Qualified | 53.317 | 56.701 | 0.940 |
| inventory-enrichment/large | 1024 | Qualified | 4.249 | 4.572 | 0.930 |
| foreach/large | 4096 | Qualified | 2.984 | 3.214 | 0.928 |
| reduce/large | 4096 | Qualified | 2.147 | 2.578 | 0.833 |
| nested-extraction/medium | 128 | Qualified | 6.827 | 8.354 | 0.817 |
| operational-summary/large | 1024 | Qualified | 3.076 | 4.447 | 0.692 |
| regex-log-cleanup/large | 1024 | Qualified | 14.794 | 24.461 | 0.605 |
| unicode-strings/large | 1024 | Qualified | 2.494 | 4.247 | 0.587 |
| identity-compact/large | 4096 | Qualified | 3.106 | 6.413 | 0.484 |
| map-construct/large | 512 | Qualified | 0.982 | 2.183 | 0.450 |
| snapshot-differences/medium | 128 | Qualified | 1.000 | 2.261 | 0.442 |
| identity-pretty/large | 4096 | Qualified | 3.134 | 7.157 | 0.438 |
| json-roundtrip/large | 512 | Qualified | 1.108 | 2.662 | 0.416 |
| regex-log-cleanup/medium | 128 | Qualified | 1.942 | 4.707 | 0.412 |
| entries-update/large | 512 | Qualified | 2.109 | 5.278 | 0.400 |
| sort-group/large | 512 | Qualified | 0.976 | 2.476 | 0.394 |
| configuration-updates/medium | 128 | Qualified | 0.887 | 2.361 | 0.376 |
| incident-logs/medium | 128 | Qualified | 0.881 | 2.377 | 0.371 |
| walk-paths/large | 512 | Qualified | 2.533 | 6.880 | 0.368 |
| api-shaping/medium | 128 | Qualified | 0.755 | 2.090 | 0.361 |
| nested-extraction/small | 16 | Qualified | 0.824 | 2.411 | 0.342 |
| walk-paths/medium | 256 | Qualified | 1.281 | 4.250 | 0.301 |
| entries-update/medium | 256 | Qualified | 1.026 | 3.437 | 0.298 |
| unicode-strings/medium | 256 | Qualified | 0.627 | 2.173 | 0.288 |
| inventory-enrichment/medium | 128 | Qualified | 0.515 | 1.875 | 0.275 |
| ndjson-project/medium | 256 | Qualified | 0.485 | 1.787 | 0.272 |
| json-roundtrip/medium | 256 | Qualified | 0.544 | 2.009 | 0.271 |
| map-construct/medium | 256 | Qualified | 0.480 | 1.818 | 0.264 |
| operational-summary/medium | 128 | Qualified | 0.479 | 1.910 | 0.251 |
| sort-group/medium | 256 | Qualified | 0.491 | 1.975 | 0.249 |
| ndjson-select/medium | 256 | Qualified | 0.361 | 1.670 | 0.216 |
| regex-log-cleanup/small | 16 | Qualified | 0.315 | 2.196 | 0.143 |
| foreach/medium | 256 | Qualified | 0.185 | 1.536 | 0.120 |
| identity-pretty/medium | 256 | Qualified | 0.178 | 1.667 | 0.107 |
| identity-compact/medium | 256 | Qualified | 0.174 | 1.656 | 0.105 |
| reduce/medium | 256 | Qualified | 0.127 | 1.484 | 0.085 |
| snapshot-differences/small | 16 | Qualified | 0.133 | 1.653 | 0.081 |
| configuration-updates/small | 16 | Qualified | 0.122 | 1.653 | 0.074 |
| incident-logs/small | 16 | Qualified | 0.128 | 1.756 | 0.073 |
| api-shaping/small | 16 | Qualified | 0.097 | 1.647 | 0.059 |
| operational-summary/small | 16 | Qualified | 0.084 | 1.583 | 0.053 |
| walk-paths/small | 16 | Qualified | 0.085 | 1.747 | 0.049 |
| inventory-enrichment/small | 16 | Qualified | 0.071 | 1.555 | 0.046 |
| entries-update/small | 16 | Qualified | 0.066 | 1.680 | 0.039 |
| sort-group/small | 16 | Qualified | 0.050 | 1.481 | 0.034 |
| unicode-strings/small | 16 | Qualified | 0.043 | 1.525 | 0.028 |
| json-roundtrip/small | 16 | Qualified | 0.037 | 1.475 | 0.025 |
| map-construct/small | 16 | Qualified | 0.034 | 1.478 | 0.023 |
| ndjson-project/small | 16 | Qualified | 0.032 | 1.447 | 0.022 |
| ndjson-select/small | 16 | Qualified | 0.026 | 1.464 | 0.018 |
| foreach/small | 16 | Qualified | 0.015 | 1.426 | 0.010 |
| identity-pretty/small | 16 | Qualified | 0.014 | 1.462 | 0.009 |
| identity-compact/small | 16 | Qualified | 0.013 | 1.444 | 0.009 |
| reduce/small | 16 | Qualified | 0.012 | 1.428 | 0.008 |
| small-request/control | 1 | Unqualified | — | — | — |
| startup-empty/control | 0 | Unqualified | — | — | — |

- small-request/control: Need at least nine valid pairs with both samples at least 20 ms.
- startup-empty/control: Need at least nine valid pairs with both samples at least 20 ms.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 142583559 | 209 / 12 / 4 |
| incident-logs/large | 73982040 | 107 / 11 / 0 |
| regex-log-cleanup/large | 30898240 | 44 / 0 / 0 |
| configuration-updates/large | 19429616 | 28 / 22 / 1 |
| ndjson-project/large | 18029304 | 26 / 2 / 0 |
| snapshot-differences/large | 17319744 | 26 / 24 / 1 |
| api-shaping/large | 14040684 | 20 / 18 / 0 |
| ndjson-select/large | 13741268 | 20 / 2 / 0 |
| inventory-enrichment/large | 9546836 | 13 / 6 / 0 |
| scalar-pipeline/diagnostic | 7020316 | 11 / 9 / 0 |
| walk-paths/large | 6813076 | 10 / 5 / 0 |
| foreach/large | 6760404 | 10 / 5 / 0 |
| reduce/large | 6399124 | 10 / 4 / 0 |
| unicode-strings/large | 6114516 | 9 / 3 / 0 |
| entries-update/large | 5675892 | 8 / 4 / 0 |
| operational-summary/large | 5035624 | 7 / 5 / 0 |
| wide-object/diagnostic | 4047992 | 6 / 0 / 0 |
| identity-pretty/large | 3246161 | 6 / 4 / 2 |
| identity-compact/large | 3246146 | 6 / 4 / 2 |
| map-construct/large | 2509556 | 4 / 2 / 0 |
| json-roundtrip/large | 2193788 | 3 / 1 / 0 |
| sort-group/large | 1658124 | 2 / 0 / 0 |
| buffered-scalars/diagnostic | 1479796 | 2 / 0 / 0 |
| raw-strings/diagnostic | 1182472 | 1 / 1 / 0 |
| sorted-ascii/diagnostic | 255936 | 0 / 0 / 0 |
| small-filter/diagnostic | 41216 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `5f023686e45ceb6e2f0789fb7e1ff96164a4a0de`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `2ce601c45009edc9977122eb168624b56fd5101286f885e46ff4f06123b5420d`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
## Comparison provenance

Mode: qualify. Measured revision: `5f023686e45ceb6e2f0789fb7e1ff96164a4a0de`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-28T15:33:17.1649274+00:00. Completed 62/62 requested cases.

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
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --compare --jq PATH_TO_JQ --mode qualify --output artifacts/benchmarks/qualification.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-report artifacts/benchmarks/qualification.json

# Absolute managed allocations and governor diagnostics
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --allocations
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --render-allocations artifacts/benchmarks/allocations.json
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --workers 1,4
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --governor --mode cost --iterations 128 --workers 1,4
```

Comparison/allocation runs accept `--case NAME`, `--scale SCALE` and `--output`;
governor runs accept `--case` and `--output`. Reports and traces remain ignored.
The renderers update the marked result and provenance sections.

Governor `counts` mode checks stdout/stderr digests, admission calls and complete
refunds under a shared 256 MiB allowance. Requested workers do not prove concurrent
occupancy. `cost` mode adds separate local timing/allocation samples; those do not
qualify general speedups. Provider storage remains outside shared accounting when
the selected adapter's provider has no host-budget hook. See
[runtime memory rules](RUNTIME.md#memory-admission) for ownership and credit bounds.
