# Packaging and CI

The library targets .NET 10 and packages as `Lokad.Jq`, initially with an
unreleased prerelease version. A Release pack produces `.nupkg` and `.snupkg`
under `artifacts/nuget/`, with README, changelog, MIT license, icon, assembly,
and XML API documentation. SourceLink is enabled as a private build dependency.
Build/test do not implicitly pack. Debug pack is rejected by a project target.

GitHub Actions restores in locked mode, builds the full solution (including
benchmarks), tests Release, and packs on Windows/Linux. Test results and packages
are workflow artifacts. There are no publishing credentials or publish jobs.
No reference repositories or sibling projects are checked out by CI.

Before any public release:

1. Set the actual Git remote and `PackageProjectUrl`/`RepositoryUrl`; none is
   invented during scaffolding. Build from a committed checkout and verify that
   SourceLink resolves every published source path to that commit.
2. Complete the intended compatibility scope and document supported hosts,
   operating systems, architectures, and native PCRE.NET runtime constraints.
3. Run locked restore/build/test/pack from a fresh checkout without `external/`,
   `PLAN.md`, or access to any other checkout.
4. Inspect package contents, transitive dependencies, licensing and native assets.
   Ensure build-only dependencies stay private and no local paths/plans/reference
   sources or test/benchmark binaries are in the package.
5. Restore the generated package into a separate consumer and execute a basic
   JSON filter, a regex case, a controlled file read, and a cancellation case.
6. Finalize public API/version/changelog, confirm repository metadata, and select
   the desired publication mechanism. Publishing is separate from CI validation.

Local validation on Windows is evidence for that machine only. Merely adding a
Linux workflow does not prove Linux execution has passed; verify the hosted CI
results when a remote is configured.
