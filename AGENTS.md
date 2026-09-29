# Lokad.Jq agent guide

This repository contains an embeddable jq runtime for .NET. It starts with the
initial partial implementation; feature-complete jq compatibility is future
work. Read `README.md`, `docs/ARCHITECTURE.md`, and
`docs/COMPATIBILITY.md` before changing behavior. If local `PLAN.md` exists,
read it for the detailed implementation handoff.

## Delivery

Deliver implementation increments through incremental commits. Each commit
must represent a coherent, working improvement with relevant passing tests.
Do not accumulate the whole implementation in one final commit. Use
`Jq: imperative summary` (or `Tests:`, `Build:`, `Docs:` as appropriate).
Record what passed and any remaining compatibility gaps in the commit body
when useful. Keep history linear and preserve other people's changes.

## Commands

- `dotnet restore Lokad.Jq.slnx --locked-mode`
- `dotnet build Lokad.Jq.slnx -c Release --no-restore`
- `dotnet test Lokad.Jq.slnx -c Release --no-build`
- `dotnet pack src/Lokad.Jq/Lokad.Jq.csproj -c Release --no-build --no-restore`
- Benchmarks: `dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'`

When dependencies intentionally change, restore without locked mode, inspect the
lock-file diff, then repeat locked restore. CI must work without `external/`.

## Boundaries and compatibility

- All program IO is mediated through `IJqHost`. Do not add ambient filesystem,
  console, process, environment, timezone, or network access to the evaluator.
  Future time/environment/module capabilities must be explicit host inputs.
- Preserve borrowed/owned descriptor lifetimes, cancellation, backpressure,
  and downstream closure. Input uses UTF-8 bytes; LF splits raw input, CR is data.
- Keep limits preflighted before allocation, mutation, and observable writes.
  Never swallow cancellation or limit failures as jq language errors.
- Upstream jq is the semantic authority. The copied regression tests preserve
  an extraction baseline, not a proof of jq compatibility. Correct deliberate
  divergences with focused oracle evidence and update the compatibility notes.
- Search existing runtime/helpers/tests before introducing parallel logic.
  Keep the package independent of other checkouts.
- The repositories under `/external/` are for inspection only. Do not vendor,
  link, compile, package, invoke, or depend on them from production, ordinary
  tests, benchmarks, or CI. An independently installed reference executable may
  later be used by an explicit opt-in differential-test tool.

## C# and tests

- Target net10.0 with nullable reference types. No default parameter values or
  null-forgiving operators in new code. Prefer explicit result types and
  non-nullable contracts; genuine JSON null remains part of the value model.
- Expected failure paths use Try-style APIs; single-caller helpers should be
  local functions. Use ConfigureAwait(false) in asynchronous library code.
- Do not expose parser/evaluator internals to grow a speculative public API.
  Friend access for existing tests is in `Properties/AssemblyInfo.cs`.
- Test exact values, output bytes, diagnostics/status, and relevant host effects.
  Focus resource/cancellation tests on stable boundaries and bounded workloads.
- Package dependencies are allowlisted and centrally versioned. Preserve
  lock files. Keep dependencies private when only needed for build/test tooling.

## Artifacts

Keep `/PLAN.md`, `/external/`, `/tmp/`, build outputs, traces, benchmark results,
and package artifacts ignored. Public architecture, compatibility, provenance,
and packaging documentation belongs under `docs/` and stays reviewable.
Do not claim a release, full compatibility, or public package availability until
the corresponding checks have actually passed.

All content must be suitable for a public repository, including ignored local
notes. Do not include private repository names, paths, hashes, project details,
internal workflows, or identifying fixtures. Use self-contained examples and
public references only.
