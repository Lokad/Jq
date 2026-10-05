# Packaging and CI

The library targets .NET 10 and packages as `Lokad.Jq` with an
unreleased prerelease version. A Release pack produces `.nupkg` and `.snupkg`
under `artifacts/nuget/`, with README, changelog, MIT license, icon, assembly,
and XML API documentation. SourceLink is enabled as a private build dependency.
Build/test do not implicitly pack. Debug pack is rejected by a project target.
Project and repository metadata use `https://github.com/lokad/Jq`, the supplied
future public location. The repository is not published yet. Release builds
normalize PDB source paths; `.gitattributes` keeps C# source bytes at LF so GitHub
downloads can match their checksums.

GitHub Actions restores in locked mode, builds the full solution (including
benchmarks), tests Release, and packs on Windows/Linux. Test results and packages
are workflow artifacts. There are no publishing credentials or publish jobs.
No reference repositories or sibling projects are checked out by CI.

Before any public release:

1. Use the configured public repository URL as the Git remote. Build from a
   committed checkout, verify the symbol package locally, and after the repository
   exists verify that SourceLink downloads every published source path at that
   commit. Local mappings and checksums alone do not prove hosted resolution.
2. Complete the intended compatibility scope and document supported hosts,
   operating systems, architectures, and native PCRE.NET runtime constraints.
3. Run locked restore/build/test/pack from a fresh checkout without `external/`,
   `PLAN.md`, or access to any other checkout.
4. Inspect package contents, transitive dependencies, licensing and native assets.
   Ensure build-only dependencies stay private and no local paths/plans/reference
   sources or test/benchmark binaries are in the package.
5. Restore the generated package into a separate consumer kept outside the
   repository directory tree (so repository build props and the package
   allowlist do not apply to it) and execute a basic JSON filter, a regex
   case, a controlled file read, and a cancellation case.
6. Finalize public API/version/changelog, confirm repository metadata, and select
   the desired publication mechanism. Publishing is separate from CI validation.

## Reproducible package consumer

After Release pack, run the opt-in PowerShell verification command:

```powershell
./tools/VerifyPackage.ps1 -PackagePath ./artifacts/nuget/Lokad.Jq.0.1.0-preview.1.nupkg
```

The command inspects package contents and dependencies, copies the committed
smoke fixture outside the repository tree, creates a fresh isolated package
cache, restores the exact artifact version, checks its SHA-512 content hash
against the consumer lock, and repeats locked restore before executing. It
verifies JSON, regex, owned file cleanup, modules, explicit environment/time,
non-finite parsing, output/regex policy, cancellation, and borrowed descriptors.
All ten checks passed locally on Windows after the execution-policy increment.
The isolated workspace is retained for inspection. The project template is not
part of the solution and never references the production source project.

Supply separately prepared JSONL corpora to rerun a value campaign:

```powershell
./tools/VerifyPackage.ps1 -PackagePath ./artifacts/nuget/Lokad.Jq.0.1.0-preview.1.nupkg `
    -VectorPath /path/to/values.jsonl -ExpectedMisses 31
```

`VectorPath` and `ExpectedMisses` accept matching arrays for multiple corpora.
The schema and comparison rules are in `UPSTREAM_VECTOR_CAMPAIGN.md`; vectors
are caller-supplied and no inspection checkout is consulted. This mode gates
the expected mismatch count and rejects escapes/timeouts; it does not certify
compatibility. Value and decimal comparison controls detect corrupted expectations.
Neither the package proof nor vector mode runs in ordinary tests or CI.

## Local symbol verification

From a committed Git checkout with its public remote configured, build and pack
Release, then include the symbol package and expected revision:

```powershell
$revision = git rev-parse HEAD
./tools/VerifyPackage.ps1 -PackagePath ./artifacts/nuget/Lokad.Jq.0.1.0-preview.1.nupkg `
    -SymbolPackagePath ./artifacts/nuget/Lokad.Jq.0.1.0-preview.1.snupkg `
    -SourceRoot . -ExpectedCommit $revision
```

This checks package repository metadata, the PDB's assembly identity, normalized
source paths, the public commit mapping, and every document's SHA-256 checksum
against committed Git blobs or embedded generated source. Dirty source files and
CRLF/LF differences are rejected. Git is invoked only by this development fixture;
the runtime remains independent of processes and repositories. It performs no
network lookup of source URLs. Hosted source resolution and Windows/Linux CI
passage remain separate release checks.

## Local source-only proof

On 2026-10-05, a tracked-source archive containing neither `PLAN.md` nor
`external/` passed locked restore, Release build, all 3,616 tests, and Release
pack on Windows. The package from that archive also passed all ten standalone
consumer checks with a verified lock hash and isolated cache. Dependencies and
lock files were unchanged; no development fixture entered the package. This
proves the local source/package path, while SourceLink requires a real Git
checkout with the actual public remote.

Local validation on Windows is evidence for that machine only. Merely adding a
Linux workflow does not prove Linux execution has passed; verify the hosted CI
results when a remote is configured.
