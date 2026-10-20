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
to select a borrowed regex engine. Existing calls retain the default adapter;
see [runtime behavior](docs/RUNTIME.md) for accounting and structural safeguards.

Shared storage admission uses a governed invocation or an explicit `IMemoryBudget`
execution overload; the runtime guide describes its estimates and lifetime
boundaries. Coarse owners batch small charges using bounded admitted
credit; retained buffers and caches stay charged until their safe refund boundary.
Every refused admission, including padded growth, fails the command with status 5
without retrying. Earlier output remains; cleanup refunds relinquished storage
so later commands can reuse the allowance.
Execution has no cumulative fuel or input/output byte ceiling. Use host
cancellation to stop long-running commands; stack and buffer safeguards remain.
String allocations use shared admission. Regex cooperation belongs to the
selected adapter; the default engine cannot interrupt an active search.

## Develop

```text
dotnet restore Lokad.Jq.slnx --locked-mode
dotnet build Lokad.Jq.slnx -c Release --no-restore
dotnet test Lokad.Jq.slnx -c Release --no-build
dotnet pack src/Lokad.Jq/Lokad.Jq.csproj -c Release --no-build --no-restore
dotnet run -c Release --project benchmarks/Lokad.Jq.Benchmarks -- --filter '*'
```

The SDK version is pinned in [global.json](global.json). Benchmarks are opt-in.

Release packages and symbols go to `artifacts/nuget/`. Verify package contents,
dependencies and public API behavior in an isolated consumer:

```powershell
./tools/VerifyPackage.ps1 -PackagePath ./artifacts/nuget/Lokad.Jq.0.1.0.nupkg
```

The script also supports symbol, hosted-source and caller-supplied corpus checks.

## Documentation

Compatibility with upstream jq is incomplete; Lokad.Jq is not yet a drop-in
replacement. Regex uses the managed `Lokad.Utf8Regex.Pcre2` package and follows
its supported PCRE2 profile; see [regex behavior](docs/REGEX.md).

- [Compatibility](docs/COMPATIBILITY.md): feature inventory, tests, supported scope and differences.
- [Architecture](docs/ARCHITECTURE.md): execution model, host contract and I/O ownership.
- [Regex behavior](docs/REGEX.md): engine syntax, flags, captures and cancellation boundaries.
- [Runtime behavior](docs/RUNTIME.md): memory admission, safeguards, cancellation and number semantics.
- [Benchmarks](docs/BENCHMARKS.md): workloads, governor cost diagnostics and opt-in upstream comparisons.
- [Changelog](CHANGELOG.md): changes and validation history.
- [License](LICENSE.txt): MIT.
