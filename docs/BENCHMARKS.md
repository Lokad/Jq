# Benchmarks

The development harness covers non-regex workloads only. Regex performance
belongs to the regex engine layer; semantic and resource tests remain in Jq.
The latest reference campaign matches jq in all 38 cases and qualifies
all 36 workload measurements, each favoring Lokad.Jq in the declared comparison.
The two startup/latency controls remain unqualified:
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

Actual managed allocations can also be collected without a reference process:

```text
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --allocations
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --allocations --case wide-object --scale diagnostic
```

This records process-wide `GC.GetTotalAllocatedBytes(true)` deltas and generation
collection counts in three warmed batches of eight executions, validating every
status, stderr and complete output digest. Binding, compilation, execution, host
consumption and a linked per-call deadline are included; input generation,
preflight and artifact serialization are excluded. Run without other work in the
benchmark process. These are actual managed bytes, separate from cumulative
execution-policy allowances, native memory and qualified CPU timing. Small batch
variation can include runtime bookkeeping. Source/build provenance, library and
benchmark hashes, inputs, outputs and every batch stay in the ignored artifact.

Six `diagnostic` workloads supplement the unchanged 38-case comparison catalog:
eight-field objects, a small function/filter, long raw strings, sorted ASCII JSON,
a scalar pipeline and buffered scalar inputs. They are bounded probes for
allocation attribution; they do not expand the public jq comparison results.
The command accepts `--case`, `--scale` and `--output artifacts/PATH`.

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

Mode: qualify. Measured revision: `9f223bf83648dfc637ebe7eaa21658274a67bb64`. jq: `jq-1.8.2`, SHA-256 `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

Recorded UTC: 2026-10-05T17:50:46.7722658+00:00. Completed 38/38 requested cases.

.NET 10.0.8; SDK 10.0.204; Ubuntu 24.04.4 LTS; AMD EPYC 9V45 96-Core Processor; 4 logical processors.

Warm embedded Lokad.Jq versus jq CLI; includes binding/compilation, parsing, evaluation, rendering and SHA-256 consumption; jq includes launch and pipes. No startup subtraction.

Ratio is jq time divided by Lokad.Jq time; above 1 favors Lokad.Jq. CI is a paired-bootstrap 95% interval.

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

## Earlier optimization campaign

The initial baseline used revision `ccf2dd317538dda12ea3d0b952667c495e857bac` on
the same AMD Linux VM, SDK/runtime and jq executable. The first follow-up at
`38fc923` included stateful ordinary JSON input, reusable JSON output storage and shared surviving
`select` inputs. Parser progress avoids rebuilding incomplete document prefixes;
output still uses one awaited host append per result. Numeric/regex policies,
execution ceilings, workloads and the timing boundary remain unchanged.

The five original jq-faster cases are compared below. These are historical
medians from the initial and first follow-up campaigns. The generated table
above and allocation campaign below report the latest accepted runtime.

| Large case (4,096 items) | Initial Lokad.Jq ms | First follow-up Lokad.Jq ms | First follow-up jq ms | First follow-up result |
| --- | ---: | ---: | ---: | --- |
| identity-compact | 13.674 | 2.608 | 5.731 | Lokad.Jq faster |
| identity-pretty | 13.753 | 2.599 | 6.424 | Lokad.Jq faster |
| ndjson-project | 6.761 | 6.812 | 6.633 | jq faster |
| ndjson-select | 5.451 | 5.047 | 4.658 | jq faster |
| foreach | 3.520 | 3.061 | 2.913 | jq faster |

That campaign matched all 38 cases and qualified 36 workload measurements.
Lokad.Jq was faster in 33 measured workloads and jq in three. Both controls were
unqualified because their managed sample batches fell below the duration floor. The original
baseline calibrated with one 40 ms batch; the current protocol confirms that
target with two consecutive batches, retaining the 20 ms eligibility floor and
all noise/deadline thresholds. An initial undersized calibration checkpoint and
all diagnostic/profiling runs are excluded from the published performance claims.

## Measured allocation campaign

The second campaign delivered object construction without repeated prefix
clones, one lazy iterator for identity/literal/variable leaves, main-program
token reuse and reusable raw UTF-8 output storage. Each runtime increment
earned its commit through repeated before/after measurements, correctness
checks and regression guards. Public API, numerical policies, resource
allowances and the JsonNode model remain unchanged.

Final runtime: `9f223bf83648dfc637ebe7eaa21658274a67bb64`; allocation baseline:
`df5336598efbb21202544b2d34fb0c04ca7c912a`. SDK 10.0.204, .NET 10.0.8,
the same four-core AMD Linux VM. All 44 frozen allocation checks preserve
exact output digests and input hashes. The final clean committed checkout
also passes 3,727 tests, Release build/pack, benchmark smoke and stress checks.

The table reports median actual managed bytes per execution, including
binding, compilation, execution, SHA-256 consumption and a per-call deadline.
Generation collection counts and individual samples remain in the artifacts.
Three warmed batches of eight are used; the added buffered-scalar baseline
comes from eleven calibrated batches with the same counter and execution
boundary. Input generation, preflight and artifact serialization are excluded.
The maintained `--allocations` command uses a linked deadline; small fixed
bookkeeping differences from the frozen diagnostic driver are possible.
These are allocation totals, not peak heap, native jq memory or policy charges.

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

The final jq 1.8.2 qualification matches all 38 cases and qualifies all 36
workload timings, each favoring Lokad.Jq in the declared comparison of the
embedded runtime with the CLI. Both startup controls remain unqualified. In this campaign,
the three previously jq-faster large cases now favor Lokad.Jq: projection
6.495 versus 6.941 ms, selection 4.766 versus 4.888 ms, and foreach 2.848
versus 2.896 ms. Their paired intervals exclude parity; the latter margins
are small. The generated table supplies the full intervals and provenance.

Withheld experiments are part of the record:

- Broad input ValueTask conversion saved 5.35% on selection and 22.65% on
  scalar-input allocations against the accepted runtime, but timing varied
  across ordinary and reversed lanes, including an 8.6% scalar slowdown.
  The unresolved regression prevents acceptance. Existing input Tasks remain.
- Stack-span `implode` variants saved 5.3% allocations but showed slower or
  inconsistent timing. The original implementation remains.
- An early object prototype and an alternative owned-builder variant were
  superseded by the stronger measured implementation that borrows fields privately.
- CPU-affinity trials changed runtime behavior and failed quietness checks;
  their incomplete results support no performance claim. Controls using the same
  binary exposed timing variation between processes; failed/inconclusive runs are retained.

Direct input staging, sorted writer changes, property-name caches and broader
Unicode/sorting rewrites remain deferred pending focused attribution and
equivalent quota/ordering evidence. The sorted diagnostic retains a real clone
cost; its ASCII-only strings do not exercise Unicode escape normalization.
Linux results cannot justify changing Windows newline normalization.

Raw artifacts remain ignored. SHA-256 receipts:

- Allocation baseline (43 original/frozen cases):
  `9a6d4fb6a24e5532fee4c07830b9b5c027bf425543587d201f617a2e0d56dc68`.
- Final allocation checks (44 cases):
  `13909dee06ee774485c8e55822e3a3807a47838f0b40dff0d936a1605a6cf76b`.
- Final reference qualification:
  `14f76df94f63310b935c863d67b4752ed4a5726f77c64213022f9ada8a943cba`.

## Workload direction

The family selection is informed by the public
[jaq catalog](https://github.com/01mf02/jaq/blob/main/examples/benches.json) and
[jq-jit suite](https://github.com/m5d215/jq-jit/blob/main/bench/comprehensive.sh).
Filters, input generators and tooling are self-contained; neither suite nor its
datasets is vendored or required. Our bounded sizes and measurement method do
not establish equivalence to their published timings.
