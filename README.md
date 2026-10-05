# Lokad.Jq

Lokad.Jq is an embeddable jq runtime for .NET 10 with host-controlled I/O and
bounded execution. It executes JSON filters through a caller-supplied `IJqHost`,
which owns input, output, file access, and descriptor cleanup.

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

The SDK version is pinned in [global.json](global.json). Benchmarks are opt-in.

## Documentation

Compatibility with upstream jq is incomplete; Lokad.Jq is not yet a drop-in
replacement. The runtime currently uses PCRE.NET, which includes native regex
components.

- [Compatibility](docs/COMPATIBILITY.md): supported scope and intentional differences.
- [Compatibility matrix](docs/COMPATIBILITY_MATRIX.md): feature coverage, tests and known gaps.
- [Reference comparisons](docs/UPSTREAM_VECTOR_CAMPAIGN.md): differential results and evidence limits.
- [Architecture](docs/ARCHITECTURE.md): execution model, host contract and I/O ownership.
- [Execution policy](docs/EXECUTION_POLICY.md): resource allowances and fixed limits.
- [Benchmarks](docs/BENCHMARKS.md): non-regex workloads and opt-in upstream comparisons.
- [Numeric profile](docs/NUMERIC_PROFILE.md): number semantics, precision and rendering.
- [Packaging](docs/PACKAGING.md): builds, CI, package verification and release checks.
- [Changelog](CHANGELOG.md): changes and validation history.
- [Provenance](docs/PROVENANCE.md): source origins and dependency/reference distinctions.
- [License](LICENSE.txt): MIT.
