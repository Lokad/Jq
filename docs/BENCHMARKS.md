# Benchmarks

Benchmarks measure hosted jq execution: argument binding and compilation, UTF-8
input parsing, evaluation, rendering and complete output consumption.
The benchmark executable and every measurement mode run on .NET 11. The pinned
SDK currently uses RC3; measurements identify that prerelease runtime explicitly.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

Qualified timing results for the current implementation are not yet published.

<!-- END GENERATED COMPARISON -->

<!-- BEGIN GENERATED ALLOCATIONS -->
## Managed allocations

| Case / scale | Bytes / execution | Gen0 / Gen1 / Gen2 |
| --- | ---: | --- |
| nested-extraction/large | 142583569 | 203 / 12 / 4 |
| incident-logs/large | 73982040 | 103 / 10 / 0 |
| regex-log-cleanup/large | 30898240 | 43 / 0 / 0 |
| configuration-updates/large | 19429616 | 28 / 23 / 1 |
| ndjson-project/large | 18029304 | 25 / 2 / 0 |
| snapshot-differences/large | 17319744 | 24 / 19 / 1 |
| api-shaping/large | 14040656 | 19 / 17 / 0 |
| ndjson-select/large | 13741268 | 19 / 2 / 0 |
| inventory-enrichment/large | 9546808 | 14 / 6 / 0 |
| scalar-pipeline/diagnostic | 7020316 | 9 / 8 / 0 |
| walk-paths/large | 6813076 | 9 / 3 / 0 |
| foreach/large | 6760404 | 10 / 3 / 0 |
| reduce/large | 6399124 | 9 / 5 / 0 |
| unicode-strings/large | 6114516 | 9 / 0 / 0 |
| entries-update/large | 5675892 | 8 / 3 / 0 |
| operational-summary/large | 5035624 | 7 / 4 / 0 |
| wide-object/diagnostic | 4047992 | 6 / 0 / 0 |
| identity-pretty/large | 3246910 | 6 / 6 / 2 |
| identity-compact/large | 3246165 | 7 / 7 / 3 |
| map-construct/large | 2509556 | 4 / 2 / 0 |
| json-roundtrip/large | 2193788 | 3 / 0 / 0 |
| sort-group/large | 1658124 | 2 / 1 / 0 |
| buffered-scalars/diagnostic | 1479796 | 2 / 1 / 0 |
| raw-strings/diagnostic | 1182500 | 1 / 1 / 0 |
| sorted-ascii/diagnostic | 255936 | 0 / 0 / 0 |
| small-filter/diagnostic | 41216 | 0 / 0 / 0 |

Large and diagnostic cases. Bytes are the median of three warmed batches of eight executions; GC columns total those 24 executions. These are total managed allocations, not peak heap or native jq memory.

Measured revision `3956e982a033f6c6bb7883badb39b5d89d9dff48`; .NET 11.0.0-rc.3.26522.148; SDK 11.0.100-rc.3.26522.148. All 68 cases completed. Library SHA-256 `d09838200d3dc4fd4edff69fd85c67c73500f1a3075a4f050d23ce2fc4e10a3a`.

<!-- END GENERATED ALLOCATIONS -->

<!-- BEGIN GENERATED COMPARISON PROVENANCE -->
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

Timing tables show arithmetic mean milliseconds per invocation and one
`Lokad.Jq / jq` value: the ratio of those means. Lower is better; 1 is parity.
Rows sort by the unrounded ratio descending, with unqualified cases last.

The comparison is warm embedded Lokad.Jq versus a native jq process. Both lanes
include parsing, evaluation, rendering and output checksums; jq also includes
process launch and pipes. There is no startup subtraction or overall speedup.

Correctness checks compare exact rendering bytes or complete ordered JSON values,
exit status and separate stderr. Failures, mismatches and timeouts reject results.
Timing qualification requires a clean Release checkout, a quiet machine and at
least nine valid paired samples. Raw reports retain samples, qualification bounds,
source/library identity, environment and input/output digests. Only qualified
results belong in the timing table.

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
The renderer updates the marked timing and provenance sections.

Governor `counts` mode checks stdout/stderr digests, admission calls and complete
refunds under a shared 256 MiB allowance. Requested workers do not prove concurrent
occupancy. `cost` mode adds separate local timing/allocation samples; those do not
qualify general speedups. Provider storage remains outside shared accounting when
the selected adapter's provider has no host-budget hook. See
[runtime memory rules](RUNTIME.md#memory-admission) for ownership and credit bounds.
