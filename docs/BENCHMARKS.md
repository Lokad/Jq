# Benchmarks

Benchmarks measure hosted jq execution: argument binding and compilation, UTF-8
input parsing, evaluation, rendering and complete output consumption.
The benchmark executable and every measurement mode run on .NET 11. The pinned
SDK currently uses RC3; measurements identify that prerelease runtime explicitly.

<!-- BEGIN GENERATED COMPARISON -->
## Timing

Qualified timing results for the current implementation are not yet published.

<!-- END GENERATED COMPARISON -->

## Managed allocations

Allocation results for the current implementation are not yet published.
Report absolute managed bytes per execution and GC counts. These measure total
allocation, not peak heap, admitted storage or native jq process memory.

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
