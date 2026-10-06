# Packaging and CI

The library targets .NET 10 and packages as `Lokad.Jq` with an
unreleased prerelease version. A Release pack produces `.nupkg` and `.snupkg`
under `artifacts/nuget/`, with README, changelog, MIT license, icon, assembly,
and XML API documentation. SourceLink is enabled as a private build dependency.
Build/test do not implicitly pack. Debug pack is rejected by a project target.
Project and repository metadata point to the public repository. Release builds
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
   operating systems, architectures, and the managed Utf8Regex regex profile.
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
against the consumer lock, audits both managed regex dependency packages for
native assets, and repeats locked restore before executing. It
verifies JSON, regex, owned file cleanup, modules, explicit environment/time,
non-finite parsing, output/regex policy, cancellation, and borrowed descriptors.
All ten checks passed against the CI-built packages at `9f223bf` on Windows and
Linux; later dependency changes require a new consumer receipt.
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

## Symbol and hosted source verification

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
against committed Git blobs or embedded generated source. Comparisons use the
committed bytes rather than worktree contents or line endings. Git is invoked
only by this development fixture; the runtime remains independent of processes
and repositories. The default mode
performs no network lookup of source URLs.

Add `-VerifyHostedSources` to the same command to download every non-embedded
document using its SourceLink URL at the expected commit. Each response must
contain exactly the committed bytes and match the PDB checksum. Downloads have
a per-document deadline and their buffers are bounded by the committed file
size. Generated documents are checked against their embedded bytes. This mode
is explicit, opt-in development tooling and does not run in ordinary tests or CI.

## Local source and package evidence

On 2026-10-05, isolated committed Git checkouts containing neither `PLAN.md` nor
`external/` passed locked restore, Release build, all 3,643 tests, and Release
pack on Windows and Ubuntu 24.04 x64 under WSL, using SDK 10.0.204. Each packed
artifact passed ten detached consumer checks with a verified lock hash and
isolated cache. Each symbol package passed assembly identity, normalized public
commit mappings and SHA-256 checksums for 64 committed and four embedded generated
source documents. Dependencies and lock files were unchanged; no development
fixture entered the package.

An earlier tracked-source archive without Git metadata also passed the local
restore/build/test/pack and detached-consumer checks. The Linux run exposed a
Windows-only cube-root test pin; the corrected test follows the platform
variation confirmed in official jq binaries, as recorded in `NUMERIC_PROFILE.md`.

The latest package audit uses artifacts from GitHub Actions run `37348034783`
for committed revision `9f223bf` on 2026-10-05. Both Windows and Ubuntu jobs
passed locked restore, Release build, all 3,727 tests, Release pack and artifact
uploads. Their respective packages passed ten detached consumer checks on
Windows and Ubuntu 24.04 x64, with fresh package caches, exact SHA-512 lock
hashes and locked restore.

Both symbol packages match their assemblies and verify 66 committed and four
embedded generated source documents. All 66 hosted SourceLink URLs resolve at
that revision with exact committed bytes and matching SHA-256 checksums. The
saved corpora retain 703/734 value matches (31 classified misses), 66/66 regex
matches and 32/32 encoding matches, with no escapes/timeouts on either platform.
Corrupted SourceLink mappings and source checksums are rejected. This rerun uses
saved vectors, rather than a new live oracle campaign. No dependencies changed
and no development fixture entered the packages. This evidence applies to that
commit; later release candidates require their own checks.
