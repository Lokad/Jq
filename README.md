# Lokad.Jq

Lokad.Jq is an embeddable jq runtime for .NET 10.
It executes JSON filters through a caller-supplied `IJqHost`, which owns input,
output, file access, and descriptor cleanup. The library does not install a real
filesystem host or invoke an external jq executable.

This implementation is incomplete. It is not yet a drop-in
replacement for upstream jq.
See [compatibility](docs/COMPATIBILITY.md) for the baseline and
[architecture](docs/ARCHITECTURE.md) for the host contract.

## Use

Implement `IJqHost` for your application's byte streams and virtual files, then:

```csharp
using Lokad.Jq;

static async Task<int> SelectNamesAsync(IJqHost host, CancellationToken cancellationToken)
{
    var invocation = JqCommandInvocation.CreateWithStandardDescriptors(
        "jq", ["-r", ".items[].name"], []);
    var command = Jq.TryParse(invocation)
        ?? throw new InvalidOperationException("Expected a jq command.");
    return await command.ExecuteAsync(host, cancellationToken);
}
```

Supply UTF-8 JSON on `JqFileDescriptor.StdIn`; output and diagnostics go to
`StdOut` and `StdErr`. Explicit descriptors and a current directory can be
passed to the `JqCommandInvocation` constructor. Arguments exclude the command
name and are already tokenized. No shell quoting or expansion happens here.
`TryParse` returns null for another executable name; jq argument/filter errors
are reported when executing. Cancellation propagates as cancellation.

Pass a `JqExecutionPolicy` to `ExecuteAsync(host, policy, cancellationToken)`
to reduce per-execution resource allowances. Existing calls retain the defaults;
see [execution policy](docs/EXECUTION_POLICY.md) for accounting and fixed ceilings.

## Develop

```text
dotnet restore Lokad.Jq.slnx --locked-mode
dotnet build Lokad.Jq.slnx -c Release --no-restore
dotnet test Lokad.Jq.slnx -c Release --no-build
dotnet pack src/Lokad.Jq/Lokad.Jq.csproj -c Release --no-build --no-restore
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'
```

The SDK policy is in `global.json`. Dependencies come from nuget.org, use central
versions and committed lock files, and are checked against an explicit allowlist.
Production dependencies remain `Lokad.Cli` and `PCRE.NET`. PCRE.NET contains native regex components; this is not
yet a fully managed runtime. Benchmarks are opt-in.

GitHub Actions builds, tests, and packs on Windows and Linux. It does not publish
packages. NuGet outputs live in `artifacts/nuget/`; only Release pack is accepted.
An uncommitted checkout without a remote produces SourceLink warnings. Before
publication, configure the real public Git remote and package project/repository
URLs, validate SourceLink from a committed checkout, and complete the release
checklist in [PACKAGING.md](docs/PACKAGING.md).

`external/` contains optional local inspection repositories. It is ignored,
never compiled, and never needed for restore, tests, packaging, or CI. Local
handoff notes in `PLAN.md` are also ignored. Implement changes through small,
validated incremental commits; see [AGENTS.md](AGENTS.md).

## License

MIT; see [LICENSE.txt](LICENSE.txt). See [PROVENANCE.md](docs/PROVENANCE.md)
for extraction origins and dependency/reference distinctions.
