# Lokad.Jq

Lokad.Jq is an embeddable jq runtime for .NET 10. It executes JSON filters through
a caller-supplied `IJqHost`, which supplies input, output and file access.

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
name and are tokenized by the embedding application.
`TryParse` returns null for another executable name; jq argument/filter errors
are reported when executing. Option/JSON argument binding and filter compilation
happen during each execution. Cancellation propagates as cancellation.

The host implements `IMemoryBudget` and shares one allowance across cooperating
consumers. A governed invocation owns admission for its copied argument/environment
vectors and derived directory; argument strings and explicit host inputs stay
borrowed from the caller. Keep their owners and the invocation alive through all
reused/concurrent commands, then dispose the invocation. Execution automatically
borrows its budget; an explicit execution overload also accepts a budget for
invocations created through the constructors.

Every refused memory admission fails the command with status 5. Earlier output
remains; cleanup refunds relinquished storage. Use host cancellation to stop
long-running commands.

Pass a `JqExecutionPolicy` to `ExecuteAsync(host, policy, cancellationToken)`
to select a borrowed regex engine. The default adapter checks cancellation around
synchronous matching; an active search must finish first. See
[runtime behavior](docs/RUNTIME.md) for host I/O, memory admission and safeguards.

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
- [Regex behavior](docs/REGEX.md): engine syntax, flags, captures and cancellation boundaries.
- [Runtime behavior](docs/RUNTIME.md): host I/O, ownership, memory admission, safeguards and numbers.
- [Benchmarks](docs/BENCHMARKS.md): workloads, governor cost diagnostics and opt-in upstream comparisons.
- [Changelog](CHANGELOG.md): changes and validation history.
- [License](LICENSE.txt): MIT.
