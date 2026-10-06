# Code, dependencies, and inspection references

Lokad.Jq is a standalone C# implementation maintained by Lokad and licensed under
MIT. Product sources are under `src/`; tests and benchmarks have separate
projects. No source files or project references point outside this repository.

The runtime depends on the public NuGet package `Lokad.Utf8Regex.Pcre2` 0.3.0,
which brings `Lokad.Utf8Regex` 0.3.0 transitively. Both are managed packages;
there are no native PCRE runtime assets or dependencies on sibling checkouts.
Command argument parsing is
implemented in this repository. SourceLink is a private build dependency.
Package versions and resolved dependency graphs are recorded in
`Directory.Packages.props` and each project's `packages.lock.json`.

## Optional local references

These public repositories live only below ignored `/external/`:

| Directory | Source | Pinned revision | Purpose |
| --- | --- | --- | --- |
| `external/jq` | https://github.com/jqlang/jq | `jq-1.8.2`, `34f7186b86743a083a589741b6cea95293524108` | Authoritative language, builtins, CLI, docs, and tests |
| `external/gojq` | https://github.com/itchyny/gojq | `v0.12.19`, `b7ebffbfc038677520df0bae4c8c2d877f88ffea` | Independent interpreter/compiler design; known divergences are not normative |
| `external/oniguruma` | https://github.com/kkos/oniguruma | `4ef89209a239c1aea328cf13c05a2807e5c146d1` | Regex semantics at jq's pinned submodule revision |

They are for inspection only. They are not product source, submodules of this
repository, vendored code, build inputs, package dependencies, or test prerequisites.
Do not copy implementation sources or bulk test fixtures into this project from
them. Write independent C# implementation and focused local regression cases.
Consult the original license if later proposing any upstream content import.
