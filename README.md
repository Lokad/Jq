# Lokad.Jq

Lokad.Jq is an embeddable jq runtime for .NET 10 with host-controlled I/O and
bounded execution. It executes JSON filters through a caller-supplied `IJqHost`,
which owns input, output, file access, and descriptor cleanup.

## Use

Implement `IJqHost` for your application's byte streams and virtual files, then:

```csharp
using Lokad.Jq;

static async Task<int> SelectNamesAsync(IJqHost host, IMemoryBudget memoryBudget, CancellationToken cancellationToken)
{
    if (!JqCommandInvocation.TryCreateWithStandardDescriptors(
        "jq", ["-r", ".items[].name"], [], memoryBudget, out var invocation))
        return 5;
    using (invocation)
    {
        var command = Jq.TryParse(invocation)
            ?? throw new InvalidOperationException("Expected a jq command.");
        return await command.ExecuteAsync(host, cancellationToken);
    }
}
```

Supply UTF-8 JSON on `JqFileDescriptor.StdIn`; output and diagnostics go to
`StdOut` and `StdErr`. Explicit descriptors and a current directory can be
passed to the `JqCommandInvocation` constructor. Arguments exclude the command
name and are already tokenized. No shell quoting or expansion happens here.
`TryParse` returns null for another executable name; jq argument/filter errors
are reported when executing. Option/JSON argument binding and filter compilation
happen during each execution. Cancellation propagates as cancellation.

The host implements `IMemoryBudget` and shares one allowance across cooperating
consumers. A governed invocation owns admission for its copied argument/environment
vectors and derived directory; argument strings and explicit host inputs stay
borrowed from the caller. Keep their owners and the invocation alive through all
reused/concurrent commands, then dispose the invocation. Existing execution
overloads automatically borrow its budget. Explicit execution hooks also work
with caller-owned invocations constructed by the legacy APIs.

Pass a `JqExecutionPolicy` to `ExecuteAsync(host, policy, cancellationToken)`
to reduce per-execution resource allowances. Existing calls retain the defaults;
see [execution policy](docs/EXECUTION_POLICY.md) for accounting and fixed ceilings.

The cumulative `MaximumAllocationWorkBytes` guard limits charged work. Shared
storage admission uses a governed invocation or an explicit `IMemoryBudget`
execution overload; its estimates and lifetime boundaries are described in the
execution policy. Coarse owners batch small charges using bounded admitted
credit; retained buffers and caches stay charged until their safe refund boundary.
Every refused admission, including padded growth, fails the command with status 5
without retrying. Earlier output remains; cleanup refunds relinquished storage
so later commands can reuse the allowance.
The old
`MaximumAllocationBytes` spelling is a deprecated work-policy alias.

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
replacement. Regex uses the managed `Lokad.Utf8Regex.Pcre2` package and follows
its supported PCRE2 profile; see [regex behavior](docs/REGEX.md).

- [Compatibility](docs/COMPATIBILITY.md): supported scope and intentional differences.
- [Compatibility matrix](docs/COMPATIBILITY_MATRIX.md): feature coverage, tests and known gaps.
- [Reference comparisons](docs/UPSTREAM_VECTOR_CAMPAIGN.md): differential results and evidence limits.
- [Architecture](docs/ARCHITECTURE.md): execution model, host contract and I/O ownership.
- [Regex behavior](docs/REGEX.md): engine syntax, flags, captures and cancellation boundaries.
- [Execution policy](docs/EXECUTION_POLICY.md): resource allowances and fixed limits.
- [Benchmarks](docs/BENCHMARKS.md): workloads, governor cost diagnostics and opt-in upstream comparisons.
- [Numeric profile](docs/NUMERIC_PROFILE.md): number semantics, precision and rendering.
- [Packaging](docs/PACKAGING.md): builds, CI, package verification and release checks.
- [Changelog](CHANGELOG.md): changes and validation history.
- [Provenance](docs/PROVENANCE.md): source origins and dependency/reference distinctions.
- [License](LICENSE.txt): MIT.
